using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Godot;
using Propane.Core;
using Propane.Diagnostics;

namespace Propane.Fx;

/// <summary>At crowded counts, approximate overlapping vent lights with energy-preserving spatial clusters.</summary>
public partial class VentLightBudget : Node3D
{
    private static readonly ConditionalWeakTable<WorldContext, VentLightBudget> worlds = new();
    public required WorldContext World { get; init; }
    private readonly List<OmniLight3D> sources = new();
    private readonly List<OmniLight3D> proxies = new();
    private Vector3[] positions = Array.Empty<Vector3>();
    private float[] energies = Array.Empty<float>();
    private int[] groups = Array.Empty<int>();
    private int[] seeds = Array.Empty<int>();
    private Vector3[] centers = Array.Empty<Vector3>();
    private float[] totals = Array.Empty<float>();

    public static VentLightBudget Register(WorldContext world, OmniLight3D light)
    {
        world.EnsureAlive();
        var budget = worlds.GetValue(world, static owner =>
        {
            var node = new VentLightBudget { World = owner, Name = "VentLights", ProcessPriority = 100 };
            // This service outlives each suburb's content. RetireContent must only remove its sources.
            owner.EffectsRoot.AddChild(node);
            return node;
        });
        budget.sources.Add(light);
        light.TreeExiting += () => budget.Remove(light);
        light.TreeEntered += () => { if (world.IsAlive && !budget.sources.Contains(light)) budget.sources.Add(light); };
        return budget;
    }

    public override void _ExitTree()
    {
        if (worlds.TryGetValue(World, out var current) && ReferenceEquals(current, this)) worlds.Remove(World);
    }

    public void Remove(OmniLight3D light) => sources.Remove(light);

    public override void _Process(double delta)
    {
        using var profile = EffectProfile.Measure(EffectProfile.System.VentLights);
        var count = sources.Count;
        var limit = Math.Clamp(World.Tuning.VentLightClusterCount, 1, 64);
        var crowded = count > Math.Max(limit, World.Tuning.VentLightMergeThreshold);
        if (!crowded)
        {
            foreach (var source in sources) source.Visible = true;
            foreach (var proxy in proxies) proxy.Visible = false;
            return;
        }
        if (positions.Length < count)
        {
            positions = new Vector3[count];
            energies = new float[count];
            groups = new int[count];
        }
        if (seeds.Length != limit)
        {
            seeds = new int[limit]; centers = new Vector3[limit]; totals = new float[limit];
        }
        while (proxies.Count < limit)
        {
            var proxy = new OmniLight3D { ShadowEnabled = false, Visible = false };
            AddChild(proxy); proxies.Add(proxy);
        }
        for (var i = 0; i < count; i++)
        {
            positions[i] = sources[i].GlobalPosition;
            energies[i] = sources[i].LightEnergy;
        }
        var camera = World.Viewport.GetCamera3D();
        Cluster(positions.AsSpan(0, count), energies.AsSpan(0, count),
            camera?.GlobalPosition ?? positions[0], seeds, centers, totals, World.Tuning.VentLightMergeRadius, groups.AsSpan(0, count));
        // Write the final state once. Toggling distant sources off and back on dirties renderer visibility.
        for (var i = 0; i < count; i++) sources[i].Visible = groups[i] < 0;
        for (var i = limit; i < proxies.Count; i++) proxies[i].Visible = false;
        for (var i = 0; i < limit; i++)
        {
            var proxy = proxies[i];
            var source = sources[seeds[i]];
            proxy.GlobalPosition = centers[i];
            proxy.LightEnergy = totals[i];
            proxy.LightColor = source.LightColor;
            proxy.OmniRange = source.OmniRange;
            proxy.OmniAttenuation = source.OmniAttenuation;
            proxy.LightCullMask = source.LightCullMask;
            proxy.Visible = totals[i] > 0;
        }
    }

    /// <summary>Keep the nearest source as a seed, spread the others, then sum energy at each weighted center.</summary>
    internal static void Cluster(ReadOnlySpan<Vector3> positions, ReadOnlySpan<float> energies, Vector3 camera,
        Span<int> seeds, Span<Vector3> centers, Span<float> totals, float radius, Span<int> groups)
    {
        var first = 0;
        for (var i = 1; i < positions.Length; i++)
            if (positions[i].DistanceSquaredTo(camera) < positions[first].DistanceSquaredTo(camera)) first = i;
        seeds[0] = first;
        for (var seed = 1; seed < seeds.Length; seed++)
        {
            var farthest = 0;
            var maximum = -1f;
            for (var i = 0; i < positions.Length; i++)
            {
                var distance = float.PositiveInfinity;
                for (var j = 0; j < seed; j++) distance = Math.Min(distance, positions[i].DistanceSquaredTo(positions[seeds[j]]));
                if (distance > maximum) { maximum = distance; farthest = i; }
            }
            seeds[seed] = farthest;
        }
        centers.Clear(); totals.Clear();
        for (var i = 0; i < positions.Length; i++)
        {
            var nearest = 0;
            for (var j = 1; j < seeds.Length; j++)
                if (positions[i].DistanceSquaredTo(positions[seeds[j]]) < positions[i].DistanceSquaredTo(positions[seeds[nearest]])) nearest = j;
            // Distant sources keep their own light: never move a sparse vent's illumination to a remote cluster.
            groups[i] = positions[i].DistanceSquaredTo(positions[seeds[nearest]]) <= Math.Max(0, radius) * Math.Max(0, radius) ? nearest : -1;
            if (groups[i] >= 0) { centers[nearest] += positions[i] * energies[i]; totals[nearest] += energies[i]; }
        }
        for (var i = 0; i < seeds.Length; i++)
            centers[i] = totals[i] > 0 ? centers[i] / totals[i] : positions[seeds[i]];
    }
}
