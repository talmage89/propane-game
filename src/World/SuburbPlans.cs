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

    public static SuburbSettings SinglePlayer(Tuning tuning) => Piles(tuning) with
    {
        TankCount = tuning.TankCount * tuning.TankCountMultiplier,
        ClusterSizeMin = tuning.ClusterSizeMin * tuning.TankCountMultiplier,
        ClusterSizeMax = Mathf.Max(tuning.ClusterSizeMin, tuning.ClusterSizeMax) * tuning.TankCountMultiplier,
    };

    /// <summary>A player-scaled suburb: piles near the center, a clear start for every player, and ammo pickups.</summary>
    public static SuburbSettings Match(Tuning tuning, int players) => Piles(tuning) with
    {
        MapSize = tuning.MpMapSizeBase + tuning.MpMapSizePerPlayer * Mathf.Max(players, 1),
        TankCount = (tuning.MpTankCount + tuning.MpTanksPerPlayer * Mathf.Max(players, 1)) * tuning.TankCountMultiplier,
        ClusterSizeMin = (tuning.MpClusterSizeMin + (int)(tuning.MpPileSizePerPlayer * Mathf.Max(players, 1))) * tuning.TankCountMultiplier,
        ClusterSizeMax = (Mathf.Max(tuning.MpClusterSizeMin, tuning.MpClusterSizeMax) + (int)(tuning.MpPileSizePerPlayer * Mathf.Max(players, 1))) * tuning.TankCountMultiplier,
        CenterBias = tuning.MpCenterBias,
        CenterRadius = tuning.MpCenterRadius,
        PlayerSpawns = Mathf.Max(players, 1),
        AmmoSpots = tuning.PickupCount + tuning.PickupsPerPlayer * Mathf.Max(players, 1),
        StartSpacing = tuning.MpStartSpacing,
        RoadSpacing = tuning.MpRoadSpacing,
    };

    /// <summary>Sample the live Dissolve segment dials at session construction.</summary>
    public static SegmentSettings Segments(Tuning tuning) => new(SuburbPlans.SinglePlayer(tuning) with
    {
        MapSize = tuning.DissolveStreetWidth,
        StripLength = tuning.DissolveSegmentLength,
        ClusterSizeMin = Math.Min(tuning.DissolvePileMin, tuning.DissolvePileMax),
        ClusterSizeMax = tuning.DissolvePileMax,
        PlayerSpawns = 0,
        CenterBias = 0,
    }, tuning.DissolveSegmentLength, tuning.DissolveTanksStart, Math.Min(tuning.DissolveTanksMin, tuning.DissolveTanksStart),
        tuning.DissolveTanksDrop, tuning.DissolveJackpotEvery, tuning.DissolveJackpotTanks, tuning.DissolveAmmoCans);

    private static SuburbSettings Piles(Tuning tuning) => new()
    {
        PileSiteSpacing = tuning.PileSiteSpacing,
        RowsWeight = tuning.PileRowsWeight,
        SquareWeight = tuning.PileSquareWeight,
        TriangularWeight = tuning.PileTriangularWeight,
        TightWeight = tuning.PileClusterWeight,
        PileSpacing = tuning.PileSpacing,
        PileLayerHeight = tuning.PileLayerHeight,
        PileUprightLayerHeight = tuning.PileUprightLayerHeight,
        PileDepthSpacing = tuning.PileDepthSpacing,
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
