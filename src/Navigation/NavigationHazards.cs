using System;
using System.Collections.Generic;
using Godot;

namespace Propane.Navigation;

/// <summary>
/// Circles on the ground that routes keep out of (tank piles and venting tanks), refilled each tick and shared by
/// every bot that plans in the world. A bucket grid finds the few circles near a point, so testing one costs the
/// same with ten tanks or a thousand. Filling and asking allocate nothing once the buffers have grown.
/// </summary>
public sealed class NavigationHazards
{
    private const float BaseBucket = 4f;
    private const int MaxBuckets = 1 << 16;
    private readonly List<NavigationHazard> hazards = new(512);
    private float bucket = BaseBucket;
    private int[] starts = new int[MaxBuckets + 1];
    private int[] entries = new int[512 * 25];
    private int minX, minZ, columns, rows;
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
        if (!float.IsFinite(center.X) || !float.IsFinite(center.Z) || !(radius > 0)) return;
        hazards.Add(new NavigationHazard(center, radius));
        built = false;
    }

    /// <summary>Indexes the circles added since <see cref="Clear"/>; asking builds it when needed.</summary>
    public void Build()
    {
        built = true;
        columns = rows = 0;
        if (hazards.Count == 0) return;
        float x0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, z1 = float.MinValue;
        foreach (var h in hazards)
        {
            x0 = Math.Min(x0, h.Center.X - h.Radius); z0 = Math.Min(z0, h.Center.Z - h.Radius);
            x1 = Math.Max(x1, h.Center.X + h.Radius); z1 = Math.Max(z1, h.Center.Z + h.Radius);
        }
        // Coarser buckets when something has flown far away, so the index stays small.
        bucket = BaseBucket;
        while ((x1 - x0) / bucket * ((z1 - z0) / bucket) > MaxBuckets) bucket *= 2;
        minX = (int)MathF.Floor(x0 / bucket);
        minZ = (int)MathF.Floor(z0 / bucket);
        columns = (int)MathF.Floor(x1 / bucket) - minX + 1;
        rows = (int)MathF.Floor(z1 / bucket) - minZ + 1;
        if (starts.Length < columns * rows + 1) starts = new int[Math.Max(columns * rows + 1, starts.Length * 2)];
        Array.Clear(starts, 0, columns * rows + 1);
        // Two passes: count each bucket's circles, then place them, so the index is one flat array.
        for (var pass = 0; pass < 2; pass++)
        {
            for (var index = 0; index < hazards.Count; index++)
            {
                var h = hazards[index];
                var bx0 = (int)MathF.Floor((h.Center.X - h.Radius) / bucket) - minX;
                var bx1 = (int)MathF.Floor((h.Center.X + h.Radius) / bucket) - minX;
                var bz0 = (int)MathF.Floor((h.Center.Z - h.Radius) / bucket) - minZ;
                var bz1 = (int)MathF.Floor((h.Center.Z + h.Radius) / bucket) - minZ;
                for (var bz = bz0; bz <= bz1; bz++)
                {
                    for (var bx = bx0; bx <= bx1; bx++)
                    {
                        var cell = bz * columns + bx;
                        if (pass == 0) starts[cell + 1]++;
                        else entries[starts[cell]++] = index;
                    }
                }
            }
            if (pass == 0)
            {
                for (var i = 0; i < columns * rows; i++) starts[i + 1] += starts[i];
                if (entries.Length < starts[columns * rows]) entries = new int[Math.Max(starts[columns * rows], entries.Length * 2)];
            }
        }
        // Placing advanced each start to the next bucket's; shift them back.
        for (var i = columns * rows; i > 0; i--) starts[i] = starts[i - 1];
        starts[0] = 0;
    }

    private bool Bucketed(float x, float z, out int first, out int last)
    {
        if (!built) Build();
        first = last = 0;
        var bx = (int)MathF.Floor(x / bucket) - minX;
        var bz = (int)MathF.Floor(z / bucket) - minZ;
        if (bx < 0 || bz < 0 || bx >= columns || bz >= rows) return false;
        first = starts[bz * columns + bx];
        last = starts[bz * columns + bx + 1];
        return first < last;
    }

    /// <summary>
    /// Outside every circle. With <paramref name="escapeFrom"/>, a point inside one is still safe if it is no closer
    /// to that circle's center than <paramref name="escapeFrom"/> is: a bot already in danger must be able to walk out.
    /// </summary>
    public bool Safe(Vector3 point, Vector3? escapeFrom = null)
    {
        if (!Bucketed(point.X, point.Z, out var first, out var last)) return true;
        for (var i = first; i < last; i++)
        {
            var h = hazards[entries[i]];
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
        if (!Bucketed(point.X, point.Z, out var first, out var last)) return -1;
        var best = -1;
        var bestDistance = float.MaxValue;
        for (var i = first; i < last; i++)
        {
            var h = hazards[entries[i]];
            var dx = point.X - h.Center.X;
            var dz = point.Z - h.Center.Z;
            var d = dx * dx + dz * dz;
            if (d < h.Radius * h.Radius && d < bestDistance) { best = entries[i]; bestDistance = d; }
        }
        return best;
    }
}
