using System;
using Godot;
using Propane.Core;
using Propane.World.Plan;

namespace Propane.World;

/// <summary>Generator settings for single player and for a match, and generation with rerolls of sparse layouts.</summary>
public static class SuburbPlans
{
    /// <summary>A layout with fewer lots than this is rerolled (up to a few times), so every suburb is worth exploring.</summary>
    public const int MinimumLots = 18;

    private const int Rerolls = 6;

    public static SuburbSettings SinglePlayer(Tuning tuning) => new()
    {
        TankCount = tuning.TankCount,
        ClusterSizeMin = tuning.ClusterSizeMin,
        ClusterSizeMax = Mathf.Max(tuning.ClusterSizeMin, tuning.ClusterSizeMax),
    };

    /// <summary>A match suburb: fewer, bigger piles near the center, a start for every player, and ammo pickups.</summary>
    public static SuburbSettings Match(Tuning tuning, int players) => new()
    {
        TankCount = tuning.MpTankCount,
        ClusterSizeMin = tuning.MpClusterSizeMin,
        ClusterSizeMax = Mathf.Max(tuning.MpClusterSizeMin, tuning.MpClusterSizeMax),
        CenterBias = tuning.MpCenterBias,
        CenterRadius = tuning.MpCenterRadius,
        PlayerSpawns = Mathf.Max(players, 1),
        AmmoSpots = tuning.PickupCount,
    };

    /// <summary>
    /// Generates a plan from <paramref name="seed"/>. With <paramref name="nextSeed"/>, a sparse layout is rerolled
    /// with fresh seeds and the fullest attempt kept.
    /// </summary>
    public static SuburbPlan Generate(SuburbSettings settings, int seed, Func<int>? nextSeed = null)
    {
        var catalog = new GameCatalog();
        var plan = new SuburbGenerator(settings, catalog, seed).Generate();
        for (var attempt = 0; attempt < Rerolls && nextSeed != null && plan.Lots.Count < MinimumLots; attempt++)
        {
            var candidate = new SuburbGenerator(settings, catalog, nextSeed()).Generate();
            if (candidate.Lots.Count > plan.Lots.Count)
            {
                plan = candidate;
            }
        }
        return plan;
    }
}
