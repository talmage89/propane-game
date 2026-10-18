using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Propane.Bots;
using Propane.Core;
using Propane.Diagnostics;
using Propane.Player;
using Propane.Tank;
using Propane.World;

namespace Propane.Net;

/// <summary>Session-owned brains, rotating think order, shared world services and cost/activity samples.
/// Match stops this set before replication, and ActorRegistration removes actor caches before native free.
/// Think consumes physics delta; combat observations use the injected monotonic network clock.</summary>
internal sealed class OwnedBotSet : IDisposable
{
    private readonly WorldContext World;
    private readonly IReadOnlyList<ActorRuntime> actors;
    private readonly IReadOnlyDictionary<int, PlayerProfile> profiles;
    private readonly Func<ulong?, IBotGoalPolicy>? createBot;
    private readonly Godot.Rect2 bounds;
    private readonly MatchSetup Setup;
    private readonly IMonotonicClock clock;
    private readonly Func<bool> playing;
    private readonly Func<float> remaining;
    private IReadOnlyDictionary<int, int> Scores;
    private static readonly IReadOnlyDictionary<int, int> EmptyScores = new Dictionary<int, int>();
    private IModeClient? clientMode;
    private readonly Func<Dictionary<uint, PropaneTank>.ValueCollection> tanks;
    private readonly IReadOnlyList<AmmoPickup> pickups;
    private bool IsPlaying => playing();
    private float timeLeft => remaining();
    private Modes.IModeView? ModeView => clientMode?.ViewAt(System.Math.Max(0, Setup.Length - timeLeft));
    private bool disposed;

    public OwnedBotSet(WorldContext world, IReadOnlyList<ActorRuntime> actors, IReadOnlyDictionary<int, PlayerProfile> profiles,
        Func<ulong?, IBotGoalPolicy>? createBot, Godot.Rect2 bounds, MatchSetup setup, IMonotonicClock clock,
        Func<bool> playing, Func<float> remaining, IModeClient clientMode, IReadOnlyDictionary<int, int> scores,
        Func<Dictionary<uint, PropaneTank>.ValueCollection> tanks, IReadOnlyList<AmmoPickup> pickups)
    {
        World = world;
        this.actors = actors;
        this.profiles = profiles;
        this.createBot = createBot;
        this.bounds = bounds;
        Setup = setup;
        this.clock = clock;
        this.playing = playing;
        this.remaining = remaining;
        this.clientMode = clientMode;
        Scores = scores;
        this.tanks = tanks;
        this.pickups = pickups;
    }

    public void RetireActor(PlayerCharacter actor)
    {
        RemoveBot(actor);
        botServices?.RemoveActor(actor);
        foreach (var bot in bots)
        {
            bot.Others.Remove(actor);
            bot.Brain.ForgetActor(actor);
        }
    }

    public void AttachLobbyBot(PlayerCharacter actor, PlayerProfile profile)
    {
        if (createBot is not { } create) return;
        botServices ??= new BotServices(World, bounds);
        var brain = create((ulong)(uint)Setup.Id << 32 | (uint)profile.Id);
        brain.Skill = BotProfile.For(profile.ControllerConfig.Difficulty, World.Tuning).Skill;
        actor.InputOverride = brain;
        var view = new OwnedBot(this, actor, brain, botServices);
        brain.Prepare(view);
        bots.Add(view);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var bot in bots) bot.Brain.Dispose();
        bots.Clear();
        botServices?.Dispose();
        botServices = null;
        clientMode = null;
        Scores = EmptyScores;
    }

    /// <summary>
    /// One bot actor this peer owns. It is simulated exactly like the owner's player: the same character, owner
    /// physics and messages. Only its intent differs, which the mode's goal policy chooses from this peer's view of
    /// the match. It has no camera, audio listener or HUD.
    /// </summary>
    private sealed class OwnedBot(OwnedBotSet match, PlayerCharacter actor, IBotGoalPolicy brain, BotServices services) : IBotWorld
    {
        public readonly List<PlayerCharacter> Others = new();
        public readonly BotActivity Activity = new();
        public PlayerCharacter Actor => actor;
        public IBotGoalPolicy Brain => brain;
        public WorldContext World => match.World;
        public PlayerCharacter Player => actor;
        public bool IsPlaying => match.IsPlaying;
        public float TimeLeft => match.timeLeft;
        public int MyScore => match.Scores.GetValueOrDefault(actor.ActorId);
        public IReadOnlyDictionary<int, int> Scores => match.Scores;
        public double Now => match.clock.Seconds;
        public double LastHitTime => actor.Runtime.LastHitTime;
        public int LastHitBy => actor.Runtime.LastHitBy;
        // Everyone else in the match, this peer's other actors included; refreshed when the roster shrinks.
        public IReadOnlyList<PlayerCharacter> RemoteBodies => Others;
        public IReadOnlyList<AmmoPickup> Pickups => match.pickups;
        public BotServices Services => services;
        public Modes.IModeView? View => match.ModeView;
        public KingBotView? King => match.BotKing;
    }


    /// <summary>Read the received zone at this peer's clock without allocating KingState.At for each brain.</summary>
    internal KingBotView? BotKing => clientMode?.KingSnapshot is { } state
        ? KingBotView.Read(state, System.Math.Max(0, Setup.Length - timeLeft)) : null;
    private readonly List<OwnedBot> bots = new();
    private BotServices? botServices;
    private int botTurn;

    public void RemoveBot(PlayerCharacter actor)
    {
        foreach (var bot in bots.FindAll(bot => bot.Actor == actor))
        {
            bot.Brain.Dispose();
            bots.Remove(bot);
            bot.Others.Clear();
        }
    }

    /// <summary>
    /// Owned bot actors and their brains' cost on this peer: think calls, physics ticks with bots, seconds (in all and
    /// in the worst tick), managed bytes allocated, and the navigation physics queries and sight rays they made.
    /// </summary>
    internal (int Bots, long Thinks, long Ticks, double Seconds, double Worst, long Bytes, long Queries, long Rays, double NavigationSeconds) BotCost { get; private set; }

    internal (long Ticks, double Seconds, double Worst, long Bytes, long AllocatingTicks, long PeakBytes) WarmBotCost { get; private set; }
    private long playingBotTicks;
    private readonly long[] botTickHistogram = new long[1001]; // 0.01 ms bins; last holds >=10 ms.
    internal double BotTickPercentile(double fraction)
    {
        var wanted = (long)System.Math.Ceiling(WarmBotCost.Ticks * fraction);
        long count = 0;
        for (var i = 0; i < botTickHistogram.Length; i++)
        {
            count += botTickHistogram[i];
            if (count >= wanted) return i / 100d;
        }
        return 0;
    }

    /// <summary>The goal policy driving an owned bot actor, if this peer owns one with that id.</summary>
    internal IBotGoalPolicy? BotBrain(int actorId)
    {
        foreach (var bot in bots) { if (bot.Actor.ActorId == actorId) return bot.Brain; }
        return null;
    }

    /// <summary>How often the owned bots' shared navigation budgets ran out (see <see cref="Navigation.NavigationGrid.Saturation"/>).</summary>
    internal (long Ticks, long QueryLimited, long ExpansionLimited) BotNavigationSaturation => botServices?.Grid?.Saturation ?? default;

    /// <summary>Whether an owned bot actor makes progress, if this peer owns one with that id.</summary>
    internal BotActivity? BotActivityOf(int actorId)
    {
        foreach (var bot in bots) { if (bot.Actor.ActorId == actorId) return bot.Activity; }
        return null;
    }

    /// <summary>Gives each owned bot actor its mode's goal policy, at the difficulty frozen in its roster profile.</summary>
    public void AttachBots()
    {
        if (createBot is not { } create) return;
        foreach (var owned in actors.Where(actor => !actor.Character.IsRemote))
        {
            if (profiles[owned.Character.ActorId].Kind == ActorKind.Bot) botServices ??= new BotServices(World, bounds);
            var profile = profiles[owned.Character.ActorId];
            if (profile.Kind != ActorKind.Bot) continue;
            // Seeded per match and actor, so a bot never draws from the world's shared random stream.
            var brain = create((ulong)(uint)Setup.Id << 32 | (uint)profile.Id);
            brain.Skill = BotProfile.For(profile.ControllerConfig.Difficulty, World.Tuning).Skill;
            brain.PointsDriven = profile.ControllerConfig.Difficulty == (byte)BotDifficulty.Hard && World.Tuning.BotHardChasesPoints;
            owned.Character.InputOverride = brain;
            var view = new OwnedBot(this, owned.Character, brain, botServices!);
            brain.Prepare(view);
            bots.Add(view);
        }
        RefreshBotPeers();
        BotCost = (bots.Count, 0, 0, 0, 0, 0, 0, 0, 0);

    }

    public void RefreshBotPeers()
    {
        if (botServices != null)
        {
            foreach (var actor in actors) botServices.PrepareActor(actor.Character);
        }
        foreach (var bot in bots)
        {
            bot.Others.Clear();
            foreach (var actor in actors)
            {
                if (actor.Character != bot.Actor) bot.Others.Add(actor.Character);
            }
        }
    }

    public void ThinkBots(float dt)
    {
        if (bots.Count == 0 || botServices == null) return;
        var started = Stopwatch.GetTimestamp();
        var allocated = System.GC.GetAllocatedBytesForCurrentThread();
        var navigation = EffectProfile.Enabled ? EffectProfile.Peek(EffectProfile.System.BotNavigation) : 0;
        var queries = botServices.NavigationQueries;
        var rays = botServices.RayLines;
        botServices.BeginTick(tanks());
        // Who thinks first takes turns, so no bot always meets a budget the others have spent.
        botTurn = (botTurn + 1) % bots.Count;
        for (var i = 0; i < bots.Count; i++)
        {
            var bot = bots[(botTurn + i) % bots.Count];
            bot.Brain.Think(bot, dt);
        }
        var seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        var bytes = System.GC.GetAllocatedBytesForCurrentThread() - allocated;
        SampleBotActivity(dt);
        if (IsPlaying && ++playingBotTicks > 1200) // Exclude ten simulated seconds of cache/buffer warm-up.
        {
            var warm = WarmBotCost;
            WarmBotCost = (warm.Ticks + 1, warm.Seconds + seconds, System.Math.Max(warm.Worst, seconds), warm.Bytes + bytes,
                warm.AllocatingTicks + (bytes > 0 ? 1 : 0), System.Math.Max(warm.PeakBytes, bytes));
            botTickHistogram[System.Math.Min(botTickHistogram.Length - 1, (int)(seconds * 100000))]++;
        }
        var cost = BotCost;
        BotCost = (cost.Bots, cost.Thinks + bots.Count, cost.Ticks + 1, cost.Seconds + seconds, System.Math.Max(cost.Worst, seconds),
            cost.Bytes + bytes, cost.Queries + botServices.NavigationQueries - queries,
            cost.Rays + botServices.RayLines - rays,
            cost.NavigationSeconds + (EffectProfile.Enabled ? (EffectProfile.Peek(EffectProfile.System.BotNavigation) - navigation) / 1000 : 0));
    }

    private void SampleBotActivity(float dt)
    {
        var king = BotKing;
        foreach (var bot in bots) bot.Activity.Sample(IsPlaying, bot.Actor, bot.Brain, king, dt);
    }
}
