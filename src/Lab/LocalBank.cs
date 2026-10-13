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
/// Offline Tank Bank runner. Both actors simulate here; no server or prediction is needed. The same
/// <see cref="TankBankRules"/> as a server match decide banks and spills; this node supplies their clock, the
/// physical facts (attributed detonations, hits and throws) and the spilled tanks themselves.
/// </summary>
public partial class LocalBank : Node
{
    public required LabSession Lab { get; init; }
    private readonly ModeDefinition mode = ModeRegistry.TankBank;
    private readonly TankBankRules rules = new();
    private readonly Dictionary<int, int> scores = new() { [1] = 0, [2] = 0 };
    public IReadOnlyDictionary<int, int> Earned => scores.Keys.ToDictionary(id => id, id => rules.StatsFor(id).Gained);
    private readonly Dictionary<PropaneTank, int> directShots = new();
    private readonly HashSet<PropaneTank> drops = new();
    private readonly List<AmmoPickup> pickups = new();
    private readonly List<(IBotGoalPolicy Brain, PlayerCharacter Actor)> brains = new();
    private uint nextTicket;
    public IReadOnlyDictionary<int, int> Scores => scores;
    public IReadOnlyList<AmmoPickup> Pickups => pickups;
    public float TimeLeft { get; private set; }
    public bool IsPlaying => TimeLeft > 0;
    public int BankedTotal => scores.Keys.Sum(id => rules.StatsFor(id).Gained);
    public int SpilledTotal => rules.Spilled;
    public TankBankRules Rules => rules;
    public int Hits { get; private set; }
    public int InitialSupply { get; private set; }
    public string BotStatus => brains[0].Brain.Status;
    public int BotBlacklisted => (brains[0].Brain as BankingBot)?.BlacklistedTargets ?? 0;
    private WorldContext World => Lab.World;

    public override void _Ready()
    {
        TimeLeft = World.Tuning.LabDuelSeconds;
        InitialSupply = Lab.LiveTanks.Count();
        rules.Initialize(new ModeSetup(new[] { new ModeActor(1, "You"), new ModeActor(2, "Bot") },
            new ModeWorld(Lab.EnvironmentKind == LabEnvironment.Void ? ModeWorldSource.Void : ModeWorldSource.SuburbPlan, 1, InitialSupply),
            mode.CopyTuners(World.Tuning), 0));
        foreach (var tank in Lab.LiveTanks) Track(tank);
        foreach (var actor in Lab.Session.Actors.Select(a => a.Character))
        {
            void Impact(ShotReport shot)
            {
                if (!IsPlaying) return;
                if (shot.Collider is PropaneTank { Invulnerable: false } tank && tank.State != PropaneTank.TankState.Exploded)
                    directShots[tank] = actor.ActorId;
                else if (PlayerCharacter.Owning(shot.Collider) is { } victim && victim != actor) Hit(actor, victim);
            }
            void Thrown(Vector3 at, Vector3 velocity, Vector3 spin) => Spill(actor, SpillReason.Thrown, World.Tuning.RagdollDropCount,
                at + Vector3.Up * 0.35f, Vector3.Zero);
            actor.ShotImpacting += Impact;
            actor.Ragdolled += Thrown;
            Lab.Session.OnUnsubscribing(() => { actor.ShotImpacting -= Impact; actor.Ragdolled -= Thrown; });
        }
        AttachBot(Lab.Opponent!);
        var positions = Lab.AmmoPositions ??
            new[] { new Vector3(World.Tuning.LabArenaRadius * 0.8f, 0.05f, 0), new Vector3(-World.Tuning.LabArenaRadius * 0.8f, 0.05f, 0) };
        foreach (var position in positions)
        {
            var pickup = new AmmoPickup { Index = pickups.Count, Position = position, Name = $"Ammo_{pickups.Count}" };
            World.ContentRoot.AddChild(pickup);
            pickups.Add(pickup);
        }
        Lab.Session.OnStopping(() =>
        {
            foreach (var (brain, actor) in brains) { actor.InputOverride = null; brain.Dispose(); }
            brains.Clear(); directShots.Clear(); drops.Clear(); pickups.Clear();
        });
    }

    public void AttachBot(PlayerCharacter actor)
    {
        if (!Lab.Session.Actors.Any(a => a.Character == actor) || brains.Any(b => b.Actor == actor))
            throw new ArgumentException("Bot must control an unassigned actor in this session", nameof(actor));
        var brain = mode.CreateBot!(World.Random.Randi());
        brain.Skill = World.Tuning.LabBotSkill;
        actor.InputOverride = brain;
        brains.Add((brain, actor));
    }

    public override void _PhysicsProcess(double delta)
    {
        if (!IsPlaying) return;
        TimeLeft = Mathf.Max(0, TimeLeft - (float)delta);
        foreach (var (brain, actor) in brains)
        {
            brain.Skill = World.Tuning.LabBotSkill;
            brain.Think(new BotWorld(this, actor), (float)delta);
        }
        foreach (var actor in Lab.Session.Actors.Select(a => a.Character))
        {
            if (actor.IsRagdolled || actor.Ammo == null) continue;
            foreach (var pickup in pickups)
            {
                var offset = actor.GlobalPosition - pickup.GlobalPosition;
                if (!pickup.Available || new Vector2(offset.X, offset.Z).Length() > AmmoPickup.TakeRadius ||
                    Mathf.Abs(offset.Y) > 1.6f || actor.Ammo.AddReserve(World.Tuning.PickupAmount) == 0)
                {
                    continue;
                }

                pickup.Take();
                World.Schedule(pickup, World.Tuning.PickupRespawnTime, () => pickup.Reveal(World.Tuning.PickupRevealTime));
            }
        }
        if (!IsPlaying)
        {
            foreach (var actor in Lab.Session.Actors) actor.Character.InputLocked = true;
            World.SetPaused(true);
            if (Lab.CreateLocalView) World.Defer(() => Lab.SetMenu(true), whilePaused: true);
        }
    }

    private void Track(PropaneTank tank)
    {
        void Punctured(PropaneTank changed) => directShots.Remove(changed);
        void Detonated(PropaneTank changed)
        {
            var scorer = changed.ChainCause != 0 ? (int)changed.ChainCause : directShots.GetValueOrDefault(changed);
            directShots.Remove(changed);
            changed.BlastRequest = (uint)scorer;
            if (IsPlaying && scores.ContainsKey(scorer))
                Apply(rules.Handle(new TankDetonated(scorer, drops.Remove(changed) ? TankOrigin.Drop : TankOrigin.Map), Clock));
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
        var grace = victim.Runtime.GraceUntil;
        if (!TankBankRules.TryOpenGrace(World.Clock.Now, ref grace, World.Tuning.HitGraceTime)) return;
        victim.Runtime.GraceUntil = grace;
        Hits++;
        var back = new Vector3(Mathf.Sin(victim.FacingYaw), 0, Mathf.Cos(victim.FacingYaw));
        var origin = victim.IsRagdolled ? victim.PelvisPosition + Vector3.Up * 0.3f :
            victim.GlobalPosition + Vector3.Up * 0.45f + back * World.Tuning.HitDropDistance;
        Spill(victim, SpillReason.Hit, World.Tuning.HitDropCount, origin, victim.IsRagdolled ? Vector3.Zero : back);
    }

    /// <summary>The rules' clock: one round that is open while the duel's time remains.</summary>
    private ModeClock Clock => new(IsPlaying ? ModePhase.Round : ModePhase.Results, 1, 0, TimeLeft);

    private void Apply(IReadOnlyList<ModeCommand> commands)
    {
        foreach (var command in commands)
            if (command is ScoreChanged changed) scores[changed.ActorId] = changed.Score;
    }

    /// <summary>A spill ticket: the rules reserve the bank, the tanks appear, then the debit commits.</summary>
    private void Spill(PlayerCharacter actor, SpillReason reason, int wanted, Vector3 origin, Vector3 away)
    {
        if (!IsPlaying) return;
        var count = TankBankRules.SpillCount(wanted, scores[actor.ActorId], rules.Reserved(actor.ActorId));
        if (count <= 0) return;
        rules.Handle(new TunersChanged(mode.CopyTuners(World.Tuning)), Clock); // Session tuning is live.
        var ticket = new SpillTicket(actor.ActorId, actor.LifeId, ++nextTicket);
        if (!rules.Handle(new SpillRequested(ticket, reason, count), Clock).OfType<SpillReserved>().Any()) return;
        var tuning = World.Tuning;
        var rng = World.Random;
        var start = rng.RandfRange(0, Mathf.Tau);
        var clear = actor.IsRagdolled ? actor.PelvisPosition + Vector3.Up * 0.2f : actor.GlobalPosition + Vector3.Up * 0.9f;
        for (var i = 0; i < count; i++)
        {
            var angle = start + i * 2.39996f;
            var spread = count == 1 ? 0.15f : Mathf.Sqrt((i + 0.5f) / count);
            var offset = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * tuning.DropScatter * spread;
            var position = SafeDropPoint(clear, origin + offset);
            var outward = (offset.LengthSquared() > 0.0001f ? offset.Normalized() : Vector3.Zero) + away;
            outward = outward.LengthSquared() > 0.0001f ? outward.Normalized() : Vector3.Zero;
            var tank = PropaneTank.Create(World);
            tank.Transform = new Transform3D(new Basis(Vector3.Up, rng.RandfRange(0, Mathf.Tau)), position);
            tank.LinearVelocity = outward * tuning.DropToss + Vector3.Up * tuning.DropToss * 0.6f;
            tank.AngularVelocity = new Vector3(rng.RandfRange(-1, 1), rng.RandfRange(-1, 1), rng.RandfRange(-1, 1)) * 2;
            World.ContentRoot.AddChild(tank);
            tank.MakeInvulnerable(tuning.DropInvulnerableTime, actor.BodyColor);
            Lab.TrackDrop(tank);
            drops.Add(tank);
            Track(tank);
        }
        Apply(rules.Handle(new SpillRegistered(ticket), Clock));
    }

    private Vector3 SafeDropPoint(Vector3 clear, Vector3 wanted)
    {
        var space = World.Physics.DirectSpaceState;
        var mask = Layers.World | Layers.Props | Layers.Floor;
        var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(clear, wanted, mask));
        if (hit.Count > 0) wanted = hit["position"].AsVector3() - (wanted - clear).Normalized() * 0.3f;
        var ground = space.IntersectRay(PhysicsRayQueryParameters3D.Create(wanted + Vector3.Up * 0.5f, wanted + Vector3.Down * 3, mask));
        if (ground.Count > 0) wanted.Y = Mathf.Max(wanted.Y, ground["position"].AsVector3().Y + 0.05f);
        return wanted;
    }

    private sealed class BotWorld(LocalBank bank, PlayerCharacter actor) : IBotWorld
    {
        public WorldContext World => bank.World;
        public PlayerCharacter Player => actor;
        public bool IsPlaying => bank.IsPlaying;
        public float TimeLeft => bank.TimeLeft;
        public int MyScore => bank.scores[actor.ActorId];
        public IReadOnlyDictionary<int, int> Scores => bank.scores;
        public double Now => World.Clock.Now;
        public double LastHitTime => actor.Runtime.LastHitTime;
        public int LastHitBy => actor.Runtime.LastHitBy;
        public IEnumerable<PlayerCharacter> RemoteBodies => bank.Lab.Session.Actors.Select(a => a.Character).Where(a => a != actor);
        public IReadOnlyList<AmmoPickup> Pickups => bank.pickups;
        public IEnumerable<PropaneTank> LiveTanks => bank.Lab.LiveTanks;
    }
}
