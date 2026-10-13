using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Bots;
using Propane.Core;
using Propane.Modes;
using Propane.Net;
using Propane.Player;
using Propane.Tank;

namespace Propane.Lab;

/// <summary>
/// Offline runner for the Lab's prototype modes. Every actor simulates here; the mode's pure rules decide, and this
/// node supplies their clock and the physical facts: where each actor is (sampled every step, as a server samples
/// the positions its clients already send), who knocked whom down (melee, or a blast whose chain they started), and,
/// for a banking mode, attributed detonations, hits and throws. It carries out what the rules decide and hands the
/// rest (eliminations, zone moves, the end) to the scenario's arena through <see cref="Decided"/>.
/// </summary>
public partial class PrototypeRunner : Node
{
    public required LabSession Lab { get; init; }
    public required ModeDefinition Mode { get; init; }
    /// <summary>Points the rules may use, such as the piles a zone sits on.</summary>
    public IReadOnlyList<ModeSpot> Spots { get; init; } = Array.Empty<ModeSpot>();
    /// <summary>Test seam: the player's actor is driven by the mode's brain too, so a whole round plays headless.</summary>
    public bool DrivePlayer { get; init; }
    public event Action<ModeCommand>? Decided;

    private readonly Dictionary<int, int> scores = new();
    private readonly Dictionary<PropaneTank, int> directShots = new();
    private readonly HashSet<PropaneTank> drops = new();
    private readonly List<(IBotGoalPolicy Brain, PlayerCharacter Actor)> brains = new();
    private readonly HashSet<int> spectating = new();
    private (int Actor, ulong Frame) blastCause;
    private uint nextTicket;
    private double elapsed;
    private double length;
    private ModeActor[] roster = Array.Empty<ModeActor>();

    public IModeRules Rules { get; private set; } = null!;
    public IModeHudPresenter Hud { get; private set; } = null!;
    public ModePhase Phase { get; private set; } = ModePhase.Round;
    public bool IsPlaying => Phase == ModePhase.Round;
    public double Elapsed => elapsed;
    public float TimeLeft => Mode.Timer == RoundTimer.Countdown ? (float)Math.Max(0, length - elapsed) : 0;
    public IReadOnlyDictionary<int, int> Scores => scores;
    public IReadOnlyList<ModeActor> Roster => roster;
    public int Thinks { get; private set; }
    public double ThinkSeconds { get; private set; }
    public ModeClock Clock => new(Phase, 1, elapsed, IsPlaying ? (Mode.Timer == RoundTimer.Countdown ? length : double.PositiveInfinity) : elapsed);
    public bool Spectating(int actorId) => spectating.Contains(actorId);
    public string BotStatus(int actorId) => brains.FirstOrDefault(b => b.Actor.ActorId == actorId).Brain?.Status ?? "";
    private WorldContext World => Lab.World;
    private bool Banking => Rules is KingRules;
    private TankBankRules? Bank => (Rules as KingRules)?.Bank;

    public override void _Ready()
    {
        var tuning = World.Tuning;
        length = Mode.Id == ModeRegistry.KingId ? tuning.KingRoundSeconds : 0;
        roster = Lab.Session.Actors.Select(a => new ModeActor(a.Character.ActorId, a.Character.Name)).ToArray();
        foreach (var actor in roster) scores[actor.ActorId] = 0;
        Rules = Mode.CreateRules();
        Hud = Mode.CreateHud();
        Rules.Initialize(new ModeSetup(roster,
            new ModeWorld(Lab.EnvironmentKind == LabEnvironment.Void ? ModeWorldSource.Void : ModeWorldSource.SuburbPlan, 1, Lab.LiveTanks.Count(), Spots),
            Mode.CopyTuners(tuning), World.Random.Randi()));
        foreach (var tank in Lab.LiveTanks) Track(tank);
        foreach (var actor in Lab.Session.Actors.Select(a => a.Character)) Watch(actor);
        foreach (var actor in Lab.Session.Actors.Select(a => a.Character))
            if (DrivePlayer || actor != Lab.Player) AttachBot(actor);
        Apply(Rules.Handle(new PhaseEntered(ModePhase.Round, new RoundInfo(1, 1, Mode.Timer, 0, length)), Clock));
        Lab.Session.OnStopping(() =>
        {
            foreach (var (brain, actor) in brains) { actor.InputOverride = null; brain.Dispose(); }
            brains.Clear(); directShots.Clear(); drops.Clear();
            Decided = null;
        });
    }

    private void AttachBot(PlayerCharacter actor)
    {
        var brain = Mode.CreateBot!(World.Random.Randi());
        brain.Skill = World.Tuning.LabBotSkill;
        actor.InputOverride = brain;
        brains.Add((brain, actor));
    }

    public override void _PhysicsProcess(double delta)
    {
        if (!IsPlaying) return;
        elapsed += delta;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var (brain, actor) in brains)
        {
            brain.Skill = World.Tuning.LabBotSkill;
            brain.Think(new BotWorld(this, actor), (float)delta);
            Thinks++;
        }
        ThinkSeconds += System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds;
        Apply(Rules.Handle(new TunersChanged(Mode.CopyTuners(World.Tuning)), Clock)); // Session tuning is live.
        var samples = Lab.Session.Actors.Select(a => a.Character).Where(a => !spectating.Contains(a.ActorId)).Select(Sample).ToArray();
        Apply(Rules.Handle(new ActorsSampled(samples), Clock));
        if (IsPlaying && Mode.Timer == RoundTimer.Countdown && elapsed >= length) End();
    }

    private static ActorSample Sample(PlayerCharacter actor)
    {
        var at = actor.IsRagdolled ? actor.PelvisPosition : actor.GlobalPosition;
        return new ActorSample(actor.ActorId, at.X, at.Y, at.Z, actor.IsRagdolled);
    }

    private void Apply(IReadOnlyList<ModeCommand> commands)
    {
        foreach (var command in commands)
        {
            switch (command)
            {
                case ScoreChanged changed:
                    scores[changed.ActorId] = changed.Score;
                    break;
                case ActorEliminated eliminated:
                    spectating.Add(eliminated.ActorId);
                    break;
                case RoundOver:
                    End();
                    break;
            }
            Decided?.Invoke(command);
        }
    }

    /// <summary>The round is decided: the rules close it, everyone stops, and the controls panel shows the result.</summary>
    private void End()
    {
        if (!IsPlaying) return;
        Phase = ModePhase.RoundEnd;
        Apply(Rules.Handle(new PhaseEntered(ModePhase.RoundEnd, new RoundInfo(1, 1, Mode.Timer, 0, length)), Clock));
        Phase = ModePhase.Results;
        foreach (var actor in Lab.Session.Actors) actor.Character.InputLocked = true;
        World.SetPaused(true);
        if (Lab.CreateLocalView) World.Defer(() => Lab.SetMenu(true), whilePaused: true);
    }

    // ------------------------------------------------------------------ Physical facts

    private void Watch(PlayerCharacter actor)
    {
        void Impact(ShotReport shot)
        {
            if (!IsPlaying) return;
            if (shot.Collider is PropaneTank { Invulnerable: false } tank && tank.State != PropaneTank.TankState.Exploded)
                directShots[tank] = actor.ActorId;
            else if (PlayerCharacter.Owning(shot.Collider) is { } victim && victim != actor) Hit(actor, victim);
        }
        void Thrown(Vector3 at, Vector3 velocity, Vector3 spin)
        {
            if (!IsPlaying) return;
            // A blast throws its receivers inside the detonation's own step; anything else (a melee hit names its
            // attacker right after) starts with nobody to blame.
            var by = blastCause.Frame == Engine.GetPhysicsFrames() ? blastCause.Actor : 0;
            Apply(Rules.Handle(new ActorThrown(actor.ActorId, by), Clock));
            if (Banking) Spill(actor, SpillReason.Thrown, World.Tuning.RagdollDropCount, at + Vector3.Up * 0.35f, Vector3.Zero);
        }
        void Melee(MeleeHit hit)
        {
            if (IsPlaying && hit.Collider is PlayerCharacter victim && victim != actor && victim.IsRagdolled)
                Apply(Rules.Handle(new ActorThrown(victim.ActorId, actor.ActorId), Clock));
        }
        actor.ShotImpacting += Impact;
        actor.Ragdolled += Thrown;
        actor.MeleeHit += Melee;
        Lab.Session.OnUnsubscribing(() => { actor.ShotImpacting -= Impact; actor.Ragdolled -= Thrown; actor.MeleeHit -= Melee; });
    }

    /// <summary>Credits detonations as the Lab duel does: a chain to its initiator, a lone tank to its last shooter.</summary>
    internal void Track(PropaneTank tank)
    {
        void Punctured(PropaneTank changed) => directShots.Remove(changed);
        void Detonated(PropaneTank changed)
        {
            var scorer = changed.ChainCause != 0 ? (int)changed.ChainCause : directShots.GetValueOrDefault(changed);
            directShots.Remove(changed);
            changed.BlastRequest = (uint)scorer;
            blastCause = (scorer, Engine.GetPhysicsFrames());
            if (IsPlaying && Banking && scores.ContainsKey(scorer))
                Apply(Rules.Handle(new TankDetonated(scorer, drops.Remove(changed) ? TankOrigin.Drop : TankOrigin.Map), Clock));
            changed.Punctured -= Punctured;
            changed.Detonated -= Detonated;
        }
        tank.Punctured += Punctured;
        tank.Detonated += Detonated;
        Lab.Session.OnUnsubscribing(() =>
        {
            if (IsInstanceValid(tank)) { tank.Punctured -= Punctured; tank.Detonated -= Detonated; }
        });
    }

    private void Hit(PlayerCharacter shooter, PlayerCharacter victim)
    {
        victim.Runtime.LastHitTime = World.Clock.Now;
        victim.Runtime.LastHitBy = shooter.ActorId;
        if (!Banking) return;
        var grace = victim.Runtime.GraceUntil;
        if (!TankBankRules.TryOpenGrace(World.Clock.Now, ref grace, World.Tuning.HitGraceTime)) return;
        victim.Runtime.GraceUntil = grace;
        var back = new Vector3(Mathf.Sin(victim.FacingYaw), 0, Mathf.Cos(victim.FacingYaw));
        var origin = victim.IsRagdolled ? victim.PelvisPosition + Vector3.Up * 0.3f :
            victim.GlobalPosition + Vector3.Up * 0.45f + back * World.Tuning.HitDropDistance;
        Spill(victim, SpillReason.Hit, World.Tuning.HitDropCount, origin, victim.IsRagdolled ? Vector3.Zero : back);
    }

    /// <summary>A spill ticket, as in the Lab duel: the rules reserve the bank, the tanks appear, then the debit commits.</summary>
    private void Spill(PlayerCharacter actor, SpillReason reason, int wanted, Vector3 origin, Vector3 away)
    {
        if (!IsPlaying || Bank is not { } bank) return;
        var count = TankBankRules.SpillCount(wanted, scores.GetValueOrDefault(actor.ActorId), bank.Reserved(actor.ActorId));
        if (count <= 0) return;
        var ticket = new SpillTicket(actor.ActorId, actor.LifeId, ++nextTicket);
        if (!Rules.Handle(new SpillRequested(ticket, reason, count), Clock).OfType<SpillReserved>().Any()) return;
        LocalBank.SpawnDrops(Lab, actor, count, origin, away, tank => { drops.Add(tank); Track(tank); });
        Apply(Rules.Handle(new SpillRegistered(ticket), Clock));
    }

    /// <summary>The HUD data for the local player, from the rules this runner holds.</summary>
    public ModeHud Present(IReadOnlyList<Vector3> objectives) => Hud.Present(new ModeHudInput(Lab.Player.ActorId, Phase, TimeLeft, scores,
        objectives, true, Rules, Clock));

    private sealed class BotWorld(PrototypeRunner runner, PlayerCharacter actor) : IBotWorld
    {
        public WorldContext World => runner.World;
        public PlayerCharacter Player => actor;
        public bool IsPlaying => runner.IsPlaying;
        public float TimeLeft => runner.TimeLeft;
        public int MyScore => runner.scores.GetValueOrDefault(actor.ActorId);
        public IReadOnlyDictionary<int, int> Scores => runner.scores;
        public double Now => World.Clock.Now;
        public double LastHitTime => actor.Runtime.LastHitTime;
        public int LastHitBy => actor.Runtime.LastHitBy;
        // Spectators are out of play: nobody chases or shoots them.
        public IEnumerable<PlayerCharacter> RemoteBodies => runner.Lab.Session.Actors.Select(a => a.Character)
            .Where(a => a != actor && !runner.spectating.Contains(a.ActorId));
        public IReadOnlyList<AmmoPickup> Pickups => Array.Empty<AmmoPickup>();
        public IEnumerable<PropaneTank> LiveTanks => runner.Lab.LiveTanks;
        public IModeRules? Rules => runner.Rules;
        public ModeClock Clock => runner.Clock;
    }
}
