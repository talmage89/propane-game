using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Bots;
using Propane.Core;
using Propane.Net;
using Propane.Player;
using Propane.Tank;

namespace Propane.Lab;

/// <summary>Offline Tank Bank adapter. Both actors simulate here; no server or prediction is needed.</summary>
public partial class LocalBank : Node
{
    public required LabSession Lab { get; init; }
    private readonly Dictionary<int, int> scores = new() { [1] = 0, [2] = 0 };
    private readonly Dictionary<int, int> earned = new() { [1] = 0, [2] = 0 };
    public IReadOnlyDictionary<int, int> Earned => earned;
    private readonly Dictionary<PropaneTank, int> directShots = new();
    private readonly List<AmmoPickup> pickups = new();
    private readonly List<(BankingBot Brain, PlayerCharacter Actor)> brains = new();
    public IReadOnlyDictionary<int, int> Scores => scores;
    public IReadOnlyList<AmmoPickup> Pickups => pickups;
    public float TimeLeft { get; private set; }
    public bool IsPlaying => TimeLeft > 0;
    public int BankedTotal { get; private set; }
    public int SpilledTotal { get; private set; }
    public int Hits { get; private set; }
    public int InitialSupply { get; private set; }
    public string BotStatus => brains[0].Brain.Status;
    private WorldContext World => Lab.World;

    public override void _Ready()
    {
        TimeLeft = World.Tuning.LabDuelSeconds;
        InitialSupply = Lab.LiveTanks.Count();
        foreach (var tank in Lab.LiveTanks) Track(tank);
        foreach (var actor in Lab.Session.Actors.Select(a => a.Character))
        {
            void Impact(ShotReport shot)
            {
                if (!IsPlaying) return;
                if (shot.Collider is PropaneTank { Invulnerable: false } tank && tank.State != PropaneTank.TankState.Exploded)
                    directShots[tank] = actor.PeerId;
                else if (PlayerCharacter.Owning(shot.Collider) is { } victim && victim != actor) Hit(actor, victim);
            }
            void Thrown(Vector3 at, Vector3 velocity, Vector3 spin) => Spill(actor, World.Tuning.RagdollDropCount,
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
            brains.Clear(); directShots.Clear(); pickups.Clear();
        });
    }

    public void AttachBot(PlayerCharacter actor)
    {
        if (!Lab.Session.Actors.Any(a => a.Character == actor) || brains.Any(b => b.Actor == actor))
            throw new ArgumentException("Bot must control an unassigned actor in this session", nameof(actor));
        var brain = new BankingBot(World.Random.Randi()) { Skill = World.Tuning.LabBotSkill };
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
            if (IsPlaying && scores.ContainsKey(scorer)) { scores[scorer]++; earned[scorer]++; BankedTotal++; }
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
        victim.Runtime.LastHitBy = shooter.PeerId;
        if (World.Clock.Now < victim.Runtime.GraceUntil) return;
        victim.Runtime.GraceUntil = World.Clock.Now + World.Tuning.HitGraceTime;
        Hits++;
        var back = new Vector3(Mathf.Sin(victim.FacingYaw), 0, Mathf.Cos(victim.FacingYaw));
        var origin = victim.IsRagdolled ? victim.PelvisPosition + Vector3.Up * 0.3f :
            victim.GlobalPosition + Vector3.Up * 0.45f + back * World.Tuning.HitDropDistance;
        Spill(victim, World.Tuning.HitDropCount, origin, victim.IsRagdolled ? Vector3.Zero : back);
    }

    private void Spill(PlayerCharacter actor, int wanted, Vector3 origin, Vector3 away)
    {
        if (!IsPlaying) return;
        var count = Mathf.Min(wanted, scores[actor.PeerId]);
        if (count <= 0) return;
        scores[actor.PeerId] -= count;
        SpilledTotal += count;
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
            Track(tank);
        }
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

    private sealed class BotWorld(LocalBank bank, PlayerCharacter actor) : IBankingBotWorld
    {
        public WorldContext World => bank.World;
        public PlayerCharacter Player => actor;
        public bool IsPlaying => bank.IsPlaying;
        public float TimeLeft => bank.TimeLeft;
        public int MyScore => bank.scores[actor.PeerId];
        public IReadOnlyDictionary<int, int> Scores => bank.scores;
        public double Now => World.Clock.Now;
        public double LastHitTime => actor.Runtime.LastHitTime;
        public int LastHitBy => actor.Runtime.LastHitBy;
        public IEnumerable<PlayerCharacter> RemoteBodies => bank.Lab.Session.Actors.Select(a => a.Character).Where(a => a != actor);
        public IReadOnlyList<AmmoPickup> Pickups => bank.pickups;
        public IEnumerable<PropaneTank> LiveTanks => bank.Lab.LiveTanks;
    }
}
