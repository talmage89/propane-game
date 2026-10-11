using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Propane.Tank;

public enum GatherPhase : byte { Cancel, Glide, Settled }

/// <summary>Pure connectivity and undisturbed group clocks; the world supplies eligibility and disturbance revisions.</summary>
public sealed class GatherGroups
{
    public readonly record struct Sample(ulong Id, Vector3 Position, bool Eligible, ulong Revision);
    private Dictionary<string, float> clocks = new();

    public IReadOnlyList<Sample[]> Advance(IEnumerable<Sample> samples, float radius, int minimum, float delay, float delta)
    {
        var eligible = samples.Where(s => s.Eligible).OrderBy(s => s.Id).ToArray();
        var seen = new HashSet<ulong>();
        var next = new Dictionary<string, float>();
        var ready = new List<Sample[]>();
        foreach (var seed in eligible)
        {
            if (!seen.Add(seed.Id)) { continue; }
            var group = new List<Sample> { seed };
            for (var i = 0; i < group.Count; i++)
            {
                foreach (var sample in eligible)
                {
                    if (!seen.Contains(sample.Id) && group[i].Position.DistanceSquaredTo(sample.Position) <= radius * radius)
                    {
                        seen.Add(sample.Id);
                        group.Add(sample);
                    }
                }
            }
            if (group.Count < minimum) { continue; }
            var ordered = group.OrderBy(s => s.Id).ToArray();
            var key = string.Join(",", ordered.Select(s => $"{s.Id}:{s.Revision}"));
            // First observation establishes the clock; time before a member joined never counts.
            var age = clocks.TryGetValue(key, out var previous) ? previous + delta : 0;
            next[key] = age;
            if (age >= delay) { ready.Add(ordered); }
        }
        clocks = next;
        return ready;
    }

    /// <summary>Lift, traverse and settle independently, with zero speed at each join.</summary>
    public static Vector3 Path(Vector3 from, Vector3 to, float lift, float progress)
    {
        if (progress <= 0) { return from; }
        if (progress >= 1) { return to; }
        var stage = progress * 3;
        var fraction = stage - Mathf.Floor(stage);
        var smooth = fraction * fraction * (3 - 2 * fraction);
        var height = Mathf.Max(from.Y, to.Y) + lift;
        if (stage < 1) { return from.Lerp(new Vector3(from.X, height, from.Z), smooth); }
        if (stage < 2) { return new Vector3(from.X, height, from.Z).Lerp(new Vector3(to.X, height, to.Z), smooth); }
        return new Vector3(to.X, height, to.Z).Lerp(to, smooth);
    }

    /// <summary>Search nearest to the centroid first. World collision checks cover the destination and complete path.</summary>
    public static Vector3? SelectCenter(IReadOnlyList<Vector3> positions, float searchRadius, int rings, int directions,
        Func<Vector3, Vector3?> validate)
    {
        var center = positions.Aggregate(Vector3.Zero, (a, b) => a + b) / positions.Count;
        if (validate(center) is { } clear) { return clear; }
        for (var ring = 1; ring <= rings; ring++)
        {
            for (var direction = 0; direction < directions; direction++)
            {
                var angle = direction * Mathf.Tau / directions;
                var candidate = center + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * (searchRadius * ring / rings);
                if (validate(candidate) is { } found) { return found; }
            }
        }
        return null;
    }
}
