using System.Collections.Generic;
using Godot;

namespace Propane.World.Plan;

// The plan is pure data in map coordinates: Vector2(x, y) maps to world (X, Z). The builder turns it into nodes.

/// <summary>An oriented rectangle: centre, half extents along its own axes, and rotation (radians, about up).</summary>
public readonly record struct OrientedRect(Vector2 Center, Vector2 HalfExtents, float Angle)
{
    public Vector2 AxisX => new(Mathf.Cos(Angle), Mathf.Sin(Angle));

    public Vector2 AxisY => new(-Mathf.Sin(Angle), Mathf.Cos(Angle));

    public Vector2[] Corners()
    {
        var x = AxisX * HalfExtents.X;
        var y = AxisY * HalfExtents.Y;
        return new[] { Center - x - y, Center + x - y, Center + x + y, Center - x + y };
    }

    public OrientedRect Shrunk(float margin) => this with { HalfExtents = HalfExtents - new Vector2(margin, margin) };

    public OrientedRect Grown(float margin) => this with { HalfExtents = HalfExtents + new Vector2(margin, margin) };

    /// <summary>Separating-axis overlap test.</summary>
    public bool Overlaps(OrientedRect other)
    {
        foreach (var axis in new[] { AxisX, AxisY, other.AxisX, other.AxisY })
        {
            var (minA, maxA) = Project(this, axis);
            var (minB, maxB) = Project(other, axis);
            if (maxA <= minB || maxB <= minA)
            {
                return false;
            }
        }
        return true;
    }

    public bool Contains(Vector2 point)
    {
        var d = point - Center;
        return Mathf.Abs(d.Dot(AxisX)) <= HalfExtents.X && Mathf.Abs(d.Dot(AxisY)) <= HalfExtents.Y;
    }

    /// <summary>Converts a point in this rectangle's local frame (origin at centre) to map space.</summary>
    public Vector2 ToMap(Vector2 local) => Center + AxisX * local.X + AxisY * local.Y;

    private static (float, float) Project(OrientedRect rect, Vector2 axis)
    {
        var min = float.MaxValue;
        var max = float.MinValue;
        foreach (var corner in rect.Corners())
        {
            var p = corner.Dot(axis);
            min = Mathf.Min(min, p);
            max = Mathf.Max(max, p);
        }
        return (min, max);
    }
}

/// <summary>A straight road between two points along an axis.</summary>
public sealed record RoadSegment(Vector2 A, Vector2 B)
{
    public Vector2 Direction => (B - A).Normalized();

    public float Length => A.DistanceTo(B);

    /// <summary>The road's right of way (carriageway, verges and sidewalks).</summary>
    public OrientedRect RightOfWay(float halfWidth) =>
        new((A + B) * 0.5f, new Vector2(Length * 0.5f, halfWidth), Mathf.Atan2(Direction.Y, Direction.X));
}

/// <summary>A cul-de-sac turnaround at the end of a road.</summary>
public sealed record CulDeSac(Vector2 Center, float Radius);

public enum HouseSide
{
    Left,
    Right,
}

/// <summary>A house lot. Its local frame: +Y points from the street into the lot, X across the frontage.</summary>
public sealed class Lot
{
    public required OrientedRect Bounds { get; init; }

    public required int Seed { get; init; }

    /// <summary>Index into the house catalog.</summary>
    public int HouseModel { get; set; } = -1;

    public int HousePalette { get; set; }

    /// <summary>House footprint in map space.</summary>
    public OrientedRect House { get; set; }

    /// <summary>Distance from the lot front to where the side fences meet the house.</summary>
    public float ReturnDepth { get; set; }

    public HouseSide DrivewaySide { get; set; }

    /// <summary>Where the street is, in map space: the midpoint of the lot's front edge.</summary>
    public Vector2 FrontCenter => Bounds.ToMap(new Vector2(0, -Bounds.HalfExtents.Y));

    /// <summary>Direction from the lot into the street.</summary>
    public Vector2 TowardStreet => -Bounds.AxisY;

    /// <summary>Converts a point in lot space (x across, y = depth from the front edge) to map space.</summary>
    public Vector2 LotToMap(float x, float depth) => Bounds.ToMap(new Vector2(x, depth - Bounds.HalfExtents.Y));
}

/// <summary>A straight fence run with openings already removed.</summary>
public sealed record FenceRun(Vector2 A, Vector2 B);

public enum PropBody
{
    /// <summary>Never moves (trees, lamps, houses).</summary>
    Static,

    /// <summary>A loose rigid body from the start.</summary>
    Loose,

    /// <summary>Frozen until a blast is strong enough.</summary>
    Breakaway,
}

/// <summary>A model placed in the world.</summary>
public sealed record PropPlacement(string Model, Vector2 Position, float Yaw, float Scale, PropBody Body, float Mass,
    Color Tint, float Elevation = 0f);

/// <summary>Flat ground features drawn over the lawn (driveways, walks, patios).</summary>
public enum PavingKind
{
    Driveway,
    Walk,
    Patio,
}

public sealed record Paving(OrientedRect Area, PavingKind Kind);

/// <summary>A player start: map position and facing (as <see cref="SuburbGenerator.YawFacing"/>).</summary>
public sealed record SpawnPoint(Vector2 Position, float Yaw);

/// <summary>Where a tank goes and why (for debugging the generator).</summary>
public sealed record TankPlacement(Vector2 Position, float Yaw, float Elevation, string Reason);

public sealed class SuburbPlan
{
    public required int Seed { get; init; }

    /// <summary>The playable chunk. Starts as the full map and is cropped to the built-up area.</summary>
    public required Rect2 Bounds { get; set; }

    public List<RoadSegment> Roads { get; } = new();

    public List<CulDeSac> CulDeSacs { get; } = new();

    public List<Lot> Lots { get; } = new();

    public List<FenceRun> Fences { get; } = new();

    public List<Paving> Pavings { get; } = new();

    public List<PropPlacement> Props { get; } = new();

    public List<TankPlacement> Tanks { get; } = new();

    public Vector2 Spawn { get; set; }

    public float SpawnYaw { get; set; }

    /// <summary>Separate starts for each player in a match (empty in single player).</summary>
    public List<SpawnPoint> Spawns { get; } = new();

    /// <summary>Ammo pickup positions for a match (empty in single player).</summary>
    public List<Vector2> AmmoSpots { get; } = new();
}
