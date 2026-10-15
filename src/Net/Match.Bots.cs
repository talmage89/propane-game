using System.Collections.Generic;
using System.Diagnostics;
using Propane.Bots;
using Propane.Core;
using Propane.Fx;
using Propane.Player;
using Propane.Tank;

namespace Propane.Net;

public partial class Match
{
    /// <summary>
    /// One bot actor this peer owns. It is simulated exactly like the owner's player: the same character, owner
    /// physics and messages. Only its intent differs, which the mode's goal policy chooses from this peer's view of
    /// the match. It has no camera, audio listener or HUD.
    /// </summary>
    private sealed class OwnedBot(Match match, PlayerCharacter actor, IBotGoalPolicy brain, BotServices services) : IBotWorld
    {
        public readonly List<PlayerCharacter> Others = new();
        public readonly BotActivity Activity = new();
        public PlayerCharacter Actor => actor;
        public IBotGoalPolicy Brain => brain;
        public WorldContext World => match.World;
        public PlayerCharacter Player => actor;
        public bool IsPlaying => match.IsPlaying;
        public float TimeLeft => match.timeLeft;
        public int MyScore => match.scores.GetValueOrDefault(actor.ActorId);
        public IReadOnlyDictionary<int, int> Scores => match.scores;
        public double Now => match.Clock.Seconds;
        public double LastHitTime => actor.Runtime.LastHitTime;
        public int LastHitBy => actor.Runtime.LastHitBy;
        // Everyone else in the match, this peer's other actors included; refreshed when the roster shrinks.
        public IReadOnlyList<PlayerCharacter> RemoteBodies => Others;
        public IReadOnlyList<AmmoPickup> Pickups => match.pickups;
        public BotServices Services => services;
        public Modes.IModeView? View => match.ModeView;
        public KingBotView? King => match.BotKing;
    }

    internal Dictionary<uint, PropaneTank>.ValueCollection BotTanks => tanks.Values;
    /// <summary>Read the received zone at this peer's clock without allocating KingState.At for each brain.</summary>
    internal KingBotView? BotKing => kingState != null
        ? KingBotView.Read(kingState, System.Math.Max(0, Setup.Length - timeLeft)) : null;
    private readonly List<OwnedBot> bots = new();
    private BotServices? botServices;
    private int botTurn;

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
    private void AttachBots()
    {
        if (mode.CreateBot is not { } create) return;
        foreach (var owned in Session.OwnedActors)
        {
            if (profiles[owned.Character.ActorId].Kind == ActorKind.Bot) botServices ??= new BotServices(World, plan.Bounds);
            var profile = profiles[owned.Character.ActorId];
            if (profile.Kind != ActorKind.Bot) continue;
            // Seeded per match and actor, so a bot never draws from the world's shared random stream.
            var brain = create((ulong)(uint)Setup.Id << 32 | (uint)profile.Id);
            brain.Skill = BotProfile.For(profile.ControllerConfig.Difficulty, World.Tuning).Skill;
            owned.Character.InputOverride = brain;
            var view = new OwnedBot(this, owned.Character, brain, botServices!);
            brain.Prepare(view);
            bots.Add(view);
        }
        RefreshBotPeers();
        BotCost = (bots.Count, 0, 0, 0, 0, 0, 0, 0, 0);
        Session.OnStopping(() =>
        {
            foreach (var bot in bots) bot.Brain.Dispose();
            bots.Clear();
            botServices?.Dispose();
            botServices = null;
        });
    }

    private void RefreshBotPeers()
    {
        if (botServices != null)
        {
            foreach (var actor in Session.Actors) botServices.PrepareActor(actor.Character);
        }
        foreach (var bot in bots)
        {
            bot.Others.Clear();
            foreach (var actor in Session.Actors)
            {
                if (actor.Character != bot.Actor) bot.Others.Add(actor.Character);
            }
        }
    }

    private void ThinkBots(float dt)
    {
        if (bots.Count == 0 || botServices == null) return;
        var started = Stopwatch.GetTimestamp();
        var allocated = System.GC.GetAllocatedBytesForCurrentThread();
        var navigation = EffectProfile.Enabled ? EffectProfile.Peek(EffectProfile.System.BotNavigation) : 0;
        var queries = botServices.NavigationQueries;
        var rays = botServices.RayLines;
        botServices.BeginTick(tanks.Values);
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
