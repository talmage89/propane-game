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

    /// <summary>
    /// Pushes bodies, throws blast receivers and, when <paramref name="chain"/> is on, sets off tanks in chain range.
    /// </summary>
    public static void Detonate(WorldContext world, Vector3 center, PropaneTank? source, bool chain = true)
    {
        world.EnsureAlive();
        var tuning = world.Tuning;
        var radius = tuning.BlastRadius;
        var space = world.Physics.DirectSpaceState;
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
        var pushed = new List<RigidBody3D>();
        foreach (var hit in QueryAll(space, query))
        {
            if (hit["collider"].As<GodotObject>() is not RigidBody3D body || !seen.Add(body.GetInstanceId()))
            {
                continue;
            }
            if (Push(space, body, center, radius, tuning, rng, chain, source?.BlastRequest ?? 0))
            {
                pushed.Add(body);
            }
        }

        foreach (var receiver in new List<IBlastReceiver>(world.BlastReceivers))
        {
            if (!world.IsAlive) { return; }
            if (!world.IsRegistered(receiver)) { continue; }
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

        world.Events.RaiseExplosion(center);
        if (chain)
        {
            world.Events.RaiseLocalBlast(center, pushed, source?.BlastRequest ?? 0);
        }
    }

    /// <summary>Full pages are ambiguous: exclude their bodies and keep querying until a short page proves completion.</summary>
    internal static IEnumerable<Godot.Collections.Dictionary> QueryAll(PhysicsDirectSpaceState3D space, PhysicsShapeQueryParameters3D query)
    {
        while (true)
        {
            var page = space.IntersectShape(query, MaxQueryResults);
            var excluded = query.Exclude;
            foreach (var hit in page)
            {
                var rid = hit["rid"].AsRid();
                if (!excluded.Contains(rid)) { excluded.Add(rid); }
                yield return hit;
            }
            query.Exclude = excluded;
            if (page.Count < MaxQueryResults) { yield break; }
        }
    }

    /// <summary>Pushes one body; returns whether it moved it.</summary>
    private static bool Push(PhysicsDirectSpaceState3D space, RigidBody3D body, Vector3 center, float radius,
        Tuning tuning, RandomNumberGenerator rng, bool chain, uint cause)
    {
        var state = PhysicsServer3D.BodyGetDirectState(body.GetRid());
        var massCenter = state != null ? body.GlobalPosition + state.CenterOfMass : body.GlobalPosition;
        var distance = center.DistanceTo(massCenter);
        if (distance > radius)
        {
            return false;
        }

        if (chain && body is PropaneTank tank && distance <= tuning.ChainRadius)
        {
            tank.ChainHit(center, distance, cause);
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
                return false;
            }
            breakaway.Release();
        }

        if (body is PropaneTank pushedTank) { pushedTank.Disturb(); }
        body.Sleeping = false;
        body.ApplyCentralImpulse(PushDirection(center, massCenter, tuning, rng) * deltaV * body.Mass);
        var spin = new Vector3(rng.RandfRange(-1, 1), rng.RandfRange(-1, 1), rng.RandfRange(-1, 1));
        body.AngularVelocity += spin * tuning.BlastSpin * falloff * Mathf.Min(massFactor, 1.5f);
        return true;
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
