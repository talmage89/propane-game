using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.World.Plan;

namespace Propane.Tank;

/// <summary>
/// Finds clear, flat ground for a whole new pile near a point, with the world's own physics: every tank of the
/// formation has floor support at one height, nothing solid, loose, thrown or standing occupies the pile's footprint,
/// and the pile keeps its distance from players. King of the Cul-de-sac's fresh piles are placed with it, by the Lab
/// and by a match's world authority (the server runs no physics, so it only validates the result).
/// </summary>
public static class PileSite
{
    private const uint Obstacles = Layers.World | Layers.Props | Layers.Tanks | Layers.Debris | Layers.Player | Layers.Ragdoll;
    /// <summary>Candidate rings step outward this far apart, so a site is found close to its spot.</summary>
    private const float RingStep = 1.5f;

    /// <summary>
    /// The square pile's transforms on the clear site nearest <paramref name="spot"/> within <paramref name="reach"/>
    /// meters, or null when none is clear. <paramref name="clearance"/> keeps that much open ground around the pile
    /// and twice that from every position in <paramref name="players"/>.
    /// </summary>
    public static IReadOnlyList<Transform3D>? Find(WorldContext world, Vector3 spot, int count, float reach, float clearance,
        IReadOnlyCollection<Vector3> players)
    {
        if (count <= 0) return null;
        var tuning = world.Tuning;
        var local = PileBuilder.Build(PileFormation.Square, count,
            new Vector3(tuning.PileSpacing, tuning.PileLayerHeight, tuning.PileDepthSpacing), tuning.PileUprightLayerHeight);
        var extent = local.Max(t => new Vector2(t.Origin.X, t.Origin.Z).Length()) + PileBuilder.Radius;
        var height = local.Max(t => t.Origin.Y) + PileBuilder.Height;
        var space = world.Physics.DirectSpaceState;
        // The footprint, raised clear of the floor it stands on, padded by the clearance all round.
        using var footprint = new CylinderShape3D { Radius = extent + clearance, Height = height + clearance };
        foreach (var candidate in Candidates(spot, reach))
        {
            if (Floor(space, candidate) is not { } floor) continue;
            if (players.Any(p => new Vector2(p.X - floor.X, p.Z - floor.Z).Length() < extent + clearance * 2)) continue;
            var placed = local.Select(t => new Transform3D(t.Basis, t.Origin + floor)).ToArray();
            // Every foot on the same flat floor: no step, curb or slab edge under part of the pile.
            if (placed.Any(t => Floor(space, t.Origin) is not { } under || Mathf.Abs(under.Y - floor.Y) > 0.03f)) continue;
            using var query = new PhysicsShapeQueryParameters3D
            {
                Shape = footprint,
                Transform = new Transform3D(Basis.Identity, floor + Vector3.Up * (0.05f + (height + clearance) / 2)),
                CollisionMask = Obstacles,
            };
            if (space.IntersectShape(query, 1).Count == 0) return placed;
        }
        return null;
    }

    /// <summary>A pile's middle on the floor it stands on: where it materializes from.</summary>
    public static Vector3 Center(IReadOnlyList<Transform3D> pile)
    {
        var sum = pile.Aggregate(Vector3.Zero, (total, t) => total + t.Origin) / pile.Count;
        return new Vector3(sum.X, pile.Min(t => t.Origin.Y), sum.Z);
    }

    /// <summary>The spot first, then rings outward to the reach, each sampled about a ring step apart.</summary>
    private static IEnumerable<Vector3> Candidates(Vector3 spot, float reach)
    {
        yield return spot;
        for (var radius = RingStep; radius <= reach + 0.001f; radius += RingStep)
        {
            var directions = Mathf.Max(8, Mathf.CeilToInt(Mathf.Tau * radius / RingStep));
            for (var i = 0; i < directions; i++)
            {
                var angle = i * Mathf.Tau / directions;
                yield return spot + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * radius;
            }
        }
    }

    /// <summary>Level floor under a point: walkable ground, not a roof or a slope.</summary>
    private static Vector3? Floor(PhysicsDirectSpaceState3D space, Vector3 at)
    {
        using var ray = PhysicsRayQueryParameters3D.Create(at + Vector3.Up * 4, at + Vector3.Down * 4, Layers.Floor);
        var hit = space.IntersectRay(ray);
        return hit.Count > 0 && hit["normal"].AsVector3().Y >= 0.99f ? hit["position"].AsVector3() : null;
    }
}
