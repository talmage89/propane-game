using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Modes;
using Propane.Player;
using Propane.Tank;

namespace Propane.Net;

/// <summary>Session-owned Bank scores and owner spill policy; predictions/action sequences remain actor-owned.</summary>
public sealed class BankClientAdapter : SilentClientAdapter
{
    public BankClientAdapter(IClientModeReplica replica) : base(replica) { }

    public override void Apply(ModeEnvelope envelope)
    {
        switch (envelope.Payload)
        {
            case TankBankScore score:
                ApplyScore(envelope.ActorId, score.Score, score.Delta);
                break;
            case TankBankBanks banks:
                foreach (var (actor, bank, _) in banks.Banks) ApplyScore(actor, bank, 0);
                break;
            case TankBankSpill spill:
                // The committed spill reaches everyone, its owner included: confirmation clears pending ids without
                // spawning them twice.
                if (replica.Actor(envelope.ActorId) is not { } source || source.LifeId != envelope.LifeId) break;
                if (!source.IsRemote) replica.Replication(source.Runtime).DropPredictions.Accepted(spill.Tanks.Select(t => t.Id));
                foreach (var body in spill.Tanks) { SpawnDroppedTank(body.Id, envelope.ActorId, body.Transform, body.Linear, body.Angular); }
                break;
            case TankBankRefused refusedSpill:
                foreach (var id in replica.OwnedActors.SelectMany(a => replica.Replication(a).DropPredictions.Refused(refusedSpill.Ids)))
                {
                    replica.RemovePredictedTank(id);
                }
                break;
        }
    }

    public override void Thrown(PlayerCharacter actor, Vector3 at) =>
        DropTanks(actor, replica.World.Tuning.RagdollDropCount, SpillReason.Thrown, at + Vector3.Up * 0.35f, Vector3.Zero);

    private void ApplyScore(int actorId, int score, int delta)
    {
        scores[actorId] = score;
        if (actorId == replica.SelectedActor?.ActorId)
        {
            replica.ScoreChanged(delta);
        }
        if (delta < 0 && replica.Actor(actorId) is { IsRemote: false } scored)
            replica.Replication(scored.Runtime).DropPredictions.ScoreConfirmed(delta);
    }

    public override void HitByShot(PlayerCharacter victim)
    {
        var tuning = replica.World.Tuning;
        if (!OpenHitGrace(victim)) return;
        Vector3 origin;
        Vector3 away;
        if (victim.IsRagdolled)
        {
            origin = victim.PelvisPosition + Vector3.Up * 0.3f;
            away = Vector3.Zero;
        }
        else
        {
            var back = -new Vector3(-Mathf.Sin(victim.FacingYaw), 0, -Mathf.Cos(victim.FacingYaw));
            origin = victim.GlobalPosition + Vector3.Up * 0.45f + back * tuning.HitDropDistance;
            away = back;
        }
        DropTanks(victim, tuning.HitDropCount, SpillReason.Hit, origin, away);
    }

    private void DropTanks(PlayerCharacter actor, int wanted, SpillReason reason, Vector3 origin, Vector3 away)
    {
        if (!replica.IsPlaying)
        {
            return;
        }
        var count = TankBankRules.SpillCount(wanted, scores.GetValueOrDefault(actor.ActorId), replica.Replication(actor.Runtime).DropPredictions.UnconfirmedCount);
        if (count <= 0)
        {
            return;
        }
        var tuning = replica.World.Tuning;
        var rng = new RandomNumberGenerator();
        var ticket = checked(++replica.Replication(actor.Runtime).ActionSequence);
        var placements = new List<BodyState>(count);
        var start = rng.RandfRange(0, Mathf.Tau);
        var safeFrom = actor.IsRagdolled ? actor.PelvisPosition + Vector3.Up * 0.2f : actor.GlobalPosition + Vector3.Up * 0.9f;
        for (var i = 0; i < count; i++)
        {
            // Spread on a sunflower spiral, so any number lands evenly within the scatter radius.
            var angle = start + i * 2.39996f;
            var spread = count == 1 ? 0.15f : Mathf.Sqrt((i + 0.5f) / count);
            var offset = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * tuning.DropScatter * spread;
            var position = SafeDropPoint(safeFrom, origin + offset);
            var outward = (offset.LengthSquared() > 0.0001f ? offset.Normalized() : Vector3.Zero) + away;
            outward = outward.LengthSquared() > 0.0001f ? outward.Normalized() : Vector3.Zero;
            var velocity = outward * tuning.DropToss + Vector3.Up * tuning.DropToss * 0.6f;
            var spin = new Vector3(rng.RandfRange(-1, 1), rng.RandfRange(-1, 1), rng.RandfRange(-1, 1)) * 2f;
            var transform = new Transform3D(new Basis(Vector3.Up, rng.RandfRange(0, Mathf.Tau)), position);
            var id = replica.Replication(actor.Runtime).AllocateDropId();
            replica.Replication(actor.Runtime).DropPredictions.Add(id);
            SpawnDroppedTank(id, actor.ActorId, transform, velocity, spin);
            placements.Add(new BodyState(id, transform, velocity, spin, 1, 0));
        }
        if (replica.World.IsAlive)
        {
            replica.SendMode(actor, ticket, ModeSchemas.TankBankKinds.Spill, new TankBankSpill(reason, placements));
        }
    }

    private Vector3 SafeDropPoint(Vector3 clear, Vector3 wanted)
    {
        var space = replica.World.Physics.DirectSpaceState;
        var mask = Layers.World | Layers.Props | Layers.Floor;
        var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(clear, wanted, mask));
        if (hit.Count > 0)
        {
            var toward = (wanted - clear).Normalized();
            wanted = hit["position"].AsVector3() - toward * 0.3f;
        }
        var ground = space.IntersectRay(PhysicsRayQueryParameters3D.Create(wanted + Vector3.Up * 0.5f, wanted + Vector3.Down * 3f, mask));
        if (ground.Count > 0)
        {
            wanted.Y = Mathf.Max(wanted.Y, ground["position"].AsVector3().Y + 0.05f);
        }
        return wanted;
    }

    private void SpawnDroppedTank(uint id, int owner, Transform3D transform, Vector3 velocity, Vector3 spin)
    {
        if (replica.HasTank(id) || replica.Exploded(id))
        {
            return;
        }
        var tank = PropaneTank.Create(replica.World);
        tank.NetId = id;
        tank.Name = $"Dropped_{id:x}";
        replica.AddTank(tank);
        tank.GlobalTransform = transform;
        tank.LinearVelocity = velocity;
        tank.AngularVelocity = spin;
        tank.MakeInvulnerable(replica.World.Tuning.DropInvulnerableTime, replica.Profile(owner).Appearance.Color);
        replica.TrackTank(tank, replica.Profile(owner).OwnerPeerId);
    }
}
