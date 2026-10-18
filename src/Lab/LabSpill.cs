using System;
using Godot;
using Propane.Core;
using Propane.Player;
using Propane.Tank;

namespace Propane.Lab;

/// <summary>Offline Lab spill placement only; callers retain reservation/commit, tuning cadence and attribution.</summary>
internal static class LabSpill
{
    public static (Vector3 Origin, Vector3 Away) HitPlacement(PlayerCharacter victim, Tuning tuning)
    {
        var back = new Vector3(Mathf.Sin(victim.FacingYaw), 0, Mathf.Cos(victim.FacingYaw));
        var origin = victim.IsRagdolled ? victim.PelvisPosition + Vector3.Up * 0.3f :
            victim.GlobalPosition + Vector3.Up * 0.45f + back * tuning.HitDropDistance;
        return (origin, victim.IsRagdolled ? Vector3.Zero : back);
    }

    /// <summary>
    /// Spawns a spill's tanks around the victim: scattered on a spiral, kept clear of walls and on the ground, tossed
    /// outward, invulnerable for a moment and tinted with the victim's color. Shared by the Lab's banking runners.
    /// </summary>
    public static void SpawnDrops(LabSession lab, PlayerCharacter actor, int count, Vector3 origin, Vector3 away, Action<PropaneTank> track)
    {
        var world = lab.World;
        var tuning = world.Tuning;
        var rng = world.Random;
        var start = rng.RandfRange(0, Mathf.Tau);
        var clear = actor.IsRagdolled ? actor.PelvisPosition + Vector3.Up * 0.2f : actor.GlobalPosition + Vector3.Up * 0.9f;
        for (var i = 0; i < count; i++)
        {
            var angle = start + i * 2.39996f;
            var spread = count == 1 ? 0.15f : Mathf.Sqrt((i + 0.5f) / count);
            var offset = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * tuning.DropScatter * spread;
            var position = SafeDropPoint(world, clear, origin + offset);
            var outward = (offset.LengthSquared() > 0.0001f ? offset.Normalized() : Vector3.Zero) + away;
            outward = outward.LengthSquared() > 0.0001f ? outward.Normalized() : Vector3.Zero;
            var tank = PropaneTank.Create(world);
            tank.Transform = new Transform3D(new Basis(Vector3.Up, rng.RandfRange(0, Mathf.Tau)), position);
            tank.LinearVelocity = outward * tuning.DropToss + Vector3.Up * tuning.DropToss * 0.6f;
            tank.AngularVelocity = new Vector3(rng.RandfRange(-1, 1), rng.RandfRange(-1, 1), rng.RandfRange(-1, 1)) * 2;
            world.ContentRoot.AddChild(tank);
            tank.MakeInvulnerable(tuning.DropInvulnerableTime, actor.BodyColor);
            lab.TrackDrop(tank);
            track(tank);
        }
    }

    private static Vector3 SafeDropPoint(WorldContext world, Vector3 clear, Vector3 wanted)
    {
        var space = world.Physics.DirectSpaceState;
        var mask = Layers.World | Layers.Props | Layers.Floor;
        var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(clear, wanted, mask));
        if (hit.Count > 0) wanted = hit["position"].AsVector3() - (wanted - clear).Normalized() * 0.3f;
        var ground = space.IntersectRay(PhysicsRayQueryParameters3D.Create(wanted + Vector3.Up * 0.5f, wanted + Vector3.Down * 3, mask));
        if (ground.Count > 0) wanted.Y = Mathf.Max(wanted.Y, ground["position"].AsVector3().Y + 0.05f);
        return wanted;
    }
}
