using System;
using System.Collections.Generic;
using Godot;

namespace Propane.Navigation;

/// <summary>
/// Circles on the ground that routes keep out of (tank piles and venting tanks), refilled each tick and shared by
/// every bot that plans in the world. A sparse bucket grid finds the circles near a point, avoiding
/// a scan of every tank for each route node. Filling and asking allocate nothing once the buffers have grown.
/// </summary>
public sealed class NavigationHazards
{
    private const float BaseBucket = 4f;
    private readonly List<NavigationHazard> hazards = new(512);
    private double bucket = BaseBucket;
    private readonly Dictionary<long, int> buckets = new(512 * 16);
    private Entry[] entries = new Entry[512 * 16];
    private struct Entry
    {
        public int Hazard;
        public int Next;
    }
    private bool built;

    public int Count => hazards.Count;
    public NavigationHazard this[int index] => hazards[index];

    public void Clear()
    {
        hazards.Clear();
        built = false;
    }

    public void Add(Vector3 center, float radius)
    {
        // A body flung out of the world reports no usable position; it is no hazard to anyone either.
        if (!float.IsFinite(center.X) || !float.IsFinite(center.Z) || !float.IsFinite(radius) || !(radius > 0)) return;
        hazards.Add(new NavigationHazard(center, radius));
        built = false;
    }

    /// <summary>Indexes the circles added since <see cref="Clear"/>; asking builds it when needed.</summary>
    public void Build()
    {
        built = true;
        buckets.Clear();
        bucket = BaseBucket;
        foreach (var h in hazards) bucket = Math.Max(bucket, h.Radius);
        // A circle covers at most three buckets per axis (reserve a fourth for numerical boundary rounding). Memory and lookup cost depend on the population,
        // never on how far a tank has flown or the aspect ratio of its bounding box.
        var capacity = checked(hazards.Count * 16);
        buckets.EnsureCapacity(capacity);
        if (entries.Length < capacity) entries = new Entry[Math.Max(capacity, entries.Length * 2)];
        var used = 0;
        for (var index = 0; index < hazards.Count; index++)
        {
            var h = hazards[index];
            var x0 = Coordinate((double)h.Center.X - h.Radius);
            var x1 = Coordinate((double)h.Center.X + h.Radius);
            var z0 = Coordinate((double)h.Center.Z - h.Radius);
            var z1 = Coordinate((double)h.Center.Z + h.Radius);
            for (var z = z0; z <= z1; z++)
            {
                for (var x = x0; x <= x1; x++)
                {
                    var key = Key(x, z);
                    entries[used] = new Entry { Hazard = index, Next = buckets.GetValueOrDefault(key, -1) };
                    buckets[key] = used++;
                }
            }
        }
    }

    private int Coordinate(double value) => (int)Math.Clamp(Math.Floor(value / bucket), int.MinValue + 1d, int.MaxValue - 1d);

    private static long Key(int x, int z) => ((long)x << 32) | (uint)z;

    private int BucketAt(float x, float z)
    {
        if (!built) Build();
        return buckets.GetValueOrDefault(Key(Coordinate(x), Coordinate(z)), -1);
    }

    /// <summary>
    /// Outside every circle. With <paramref name="escapeFrom"/>, a point inside one is still safe if it is no closer
    /// to that circle's center than <paramref name="escapeFrom"/> is: a bot already in danger must be able to walk out.
    /// </summary>
    public bool Safe(Vector3 point, Vector3? escapeFrom = null)
    {
        for (var i = BucketAt(point.X, point.Z); i >= 0; i = entries[i].Next)
        {
            var h = hazards[entries[i].Hazard];
            var dx = point.X - h.Center.X;
            var dz = point.Z - h.Center.Z;
            var d = dx * dx + dz * dz;
            if (d >= h.Radius * h.Radius) continue;
            if (escapeFrom is not { } escape) return false;
            var ex = escape.X - h.Center.X;
            var ez = escape.Z - h.Center.Z;
            if (d + 0.001f < ex * ex + ez * ez) return false;
        }
        return true;
    }

    /// <summary>Points every <paramref name="step"/> meters from one end to the other, ends included, are safe.</summary>
    public bool SafeAlong(Vector3 from, Vector3 to, float step, Vector3? escapeFrom = null)
    {
        if (hazards.Count == 0) return true;
        var samples = Math.Max(1, (int)MathF.Ceiling(from.DistanceTo(to) / step));
        for (var i = 0; i <= samples; i++)
        {
            if (!Safe(from.Lerp(to, (float)i / samples), escapeFrom)) return false;
        }
        return true;
    }

    /// <summary>The nearest circle containing a point, by distance to its center, or -1.</summary>
    public int Containing(Vector3 point)
    {
        var best = -1;
        var bestDistance = float.MaxValue;
        for (var i = BucketAt(point.X, point.Z); i >= 0; i = entries[i].Next)
        {
            var h = hazards[entries[i].Hazard];
            var dx = point.X - h.Center.X;
            var dz = point.Z - h.Center.Z;
            var d = dx * dx + dz * dz;
            if (d < h.Radius * h.Radius && (d < bestDistance || d == bestDistance && entries[i].Hazard < best))
            { best = entries[i].Hazard; bestDistance = d; }
        }
        return best;
    }
}
