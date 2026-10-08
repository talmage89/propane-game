using System.Collections.Generic;
using Godot;
using Propane.Core;

namespace Propane.Tank;

/// <summary>The physical side of an explosion: pushes bodies, sets off nearby tanks and throws the player.</summary>
public static class Blast
{
    /// <summary>Mass at which a body gets exactly <see cref="Tuning.BlastSpeed"/>. Lighter bodies fly faster.</summary>
    private const float ReferenceMass = 17f;
    private const int MaxQueryResults = 512;

    public static void Detonate(Node3D context, Vector3 center, PropaneTank? source)
    {
        var tuning = Tuning.Current;
        var radius = tuning.BlastRadius;
        var space = context.GetWorld3D().DirectSpaceState;
        var rng = new RandomNumberGenerator();
        var exclude = new Godot.Collections.Array<Rid>();
        if (source != null)
        {
            exclude.Add(source.GetRid());
        }

        var query = new PhysicsShapeQueryParameters3D
        {
            Shape = new SphereShape3D { Radius = radius },
            Transform = new Transform3D(Basis.Identity, center),
            CollisionMask = Layers.Pushable,
            CollideWithBodies = true,
            CollideWithAreas = false,
            Exclude = exclude,
        };
        var seen = new HashSet<ulong>();
        foreach (var hit in space.IntersectShape(query, MaxQueryResults))
        {
            if (hit["collider"].As<GodotObject>() is not RigidBody3D body || !seen.Add(body.GetInstanceId()))
            {
                continue;
            }
            Push(space, body, center, radius, tuning, rng);
        }

        foreach (var node in context.GetTree().GetNodesInGroup(IBlastReceiver.Group))
        {
            if (node is not IBlastReceiver receiver)
            {
                continue;
            }
            var target = receiver.BlastTargetPosition;
            var distance = center.DistanceTo(target);
            var reach = Mathf.Max(radius, Mathf.Max(tuning.RagdollRadius, tuning.StaggerRadius));
            if (distance > reach)
            {
                continue;
            }
            var falloff = Mathf.Pow(Mathf.Clamp(1f - distance / reach, 0, 1), tuning.BlastFalloff);
            if (IsOccluded(space, center, target))
            {
                falloff *= tuning.BlastOcclusion;
            }
            receiver.ReceiveBlast(new BlastInfo(center, PushDirection(center, target, tuning, rng), distance, falloff));
        }

        GameEvents.RaiseExplosion(center);
    }

    private static void Push(PhysicsDirectSpaceState3D space, RigidBody3D body, Vector3 center, float radius,
        Tuning tuning, RandomNumberGenerator rng)
    {
        var state = PhysicsServer3D.BodyGetDirectState(body.GetRid());
        var massCenter = state != null ? body.GlobalPosition + state.CenterOfMass : body.GlobalPosition;
        var distance = center.DistanceTo(massCenter);
        if (distance > radius)
        {
            return;
        }

        if (body is PropaneTank tank && distance <= tuning.ChainRadius)
        {
            tank.ChainHit(center, distance);
        }

        var falloff = Mathf.Pow(1f - distance / radius, tuning.BlastFalloff);
        if (IsOccluded(space, center, massCenter))
        {
            falloff *= tuning.BlastOcclusion;
        }
        var massFactor = Mathf.Clamp(Mathf.Pow(ReferenceMass / Mathf.Max(body.Mass, 0.01f), tuning.BlastMassInfluence), 0.01f, 4f);
        var deltaV = tuning.BlastSpeed * falloff * massFactor;

        if (body is Breakaway breakaway && breakaway.Anchored)
        {
            if (deltaV < tuning.BreakawaySpeed)
            {
                return;
            }
            breakaway.Release();
        }

        body.Sleeping = false;
        body.ApplyCentralImpulse(PushDirection(center, massCenter, tuning, rng) * deltaV * body.Mass);
        var spin = new Vector3(rng.RandfRange(-1, 1), rng.RandfRange(-1, 1), rng.RandfRange(-1, 1));
        body.AngularVelocity += spin * tuning.BlastSpin * falloff * Mathf.Min(massFactor, 1.5f);
    }

    private static Vector3 PushDirection(Vector3 center, Vector3 target, Tuning tuning, RandomNumberGenerator rng)
    {
        var away = target - center;
        if (away.LengthSquared() < 0.0001f)
        {
            away = new Vector3(rng.RandfRange(-1, 1), 0, rng.RandfRange(-1, 1));
        }
        away = away.Normalized();
        return (away + Vector3.Up * tuning.BlastUpwardBias).Normalized();
    }

    private static bool IsOccluded(PhysicsDirectSpaceState3D space, Vector3 from, Vector3 to)
    {
        var ray = PhysicsRayQueryParameters3D.Create(from, to, Layers.BlastOccluders);
        return space.IntersectRay(ray).Count > 0;
    }
}
