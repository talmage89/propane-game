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
    private sealed class OwnedBot(Match match, PlayerCharacter actor, IBotGoalPolicy brain) : IBotWorld
    {
        public readonly List<PlayerCharacter> Others = new();
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
        public IEnumerable<PlayerCharacter> RemoteBodies => Others;
        public IReadOnlyList<AmmoPickup> Pickups => match.pickups;
        public IEnumerable<PropaneTank> LiveTanks => match.LiveTanks;
        public Modes.IModeView? View => match.ModeView;
    }

    private readonly List<OwnedBot> bots = new();

    /// <summary>Owned bot actors and their brains' cost on this peer: think calls, seconds, managed bytes and inclusive navigation seconds. Navigation timing requires EffectProfile.Enabled.</summary>
    internal (int Bots, long Thinks, double Seconds, long Bytes, double NavigationSeconds) BotCost { get; private set; }

    /// <summary>The goal policy driving an owned bot actor, if this peer owns one with that id.</summary>
    internal IBotGoalPolicy? BotBrain(int actorId)
    {
        foreach (var bot in bots) { if (bot.Actor.ActorId == actorId) return bot.Brain; }
        return null;
    }

    /// <summary>Gives each owned bot actor its mode's goal policy, at the difficulty frozen in its roster profile.</summary>
    private void AttachBots()
    {
        if (mode.CreateBot is not { } create) return;
        foreach (var owned in Session.OwnedActors)
        {
            var profile = profiles[owned.Character.ActorId];
            if (profile.Kind != ActorKind.Bot) continue;
            // Seeded per match and actor, so a bot never draws from the world's shared random stream.
            var brain = create((ulong)(uint)Setup.Id << 32 | (uint)profile.Id);
            brain.Skill = BotProfile.For(profile.ControllerConfig.Difficulty, World.Tuning).Skill;
            owned.Character.InputOverride = brain;
            bots.Add(new OwnedBot(this, owned.Character, brain));
        }
        RefreshBotPeers();
        BotCost = (bots.Count, 0, 0, 0, 0);
        Session.OnStopping(() =>
        {
            foreach (var bot in bots) bot.Brain.Dispose();
            bots.Clear();
        });
    }

    private void RefreshBotPeers()
    {
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
        if (bots.Count == 0) return;
        var started = Stopwatch.GetTimestamp();
        var allocated = System.GC.GetAllocatedBytesForCurrentThread();
        var navigation = EffectProfile.Enabled ? EffectProfile.Peek(EffectProfile.System.BotNavigation) : 0;
        foreach (var bot in bots) bot.Brain.Think(bot, dt);
        var cost = BotCost;
        BotCost = (cost.Bots, cost.Thinks + bots.Count, cost.Seconds + Stopwatch.GetElapsedTime(started).TotalSeconds,
            cost.Bytes + System.GC.GetAllocatedBytesForCurrentThread() - allocated,
            cost.NavigationSeconds + (EffectProfile.Enabled ? (EffectProfile.Peek(EffectProfile.System.BotNavigation) - navigation) / 1000 : 0));
    }
}
