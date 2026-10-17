using System;
using System.Collections.Generic;
using Godot;

namespace Propane.World.Plan;

/// <summary>Inputs the generator needs about the asset catalog, so it can be tested without loading models.</summary>
public interface IPlanCatalog
{
    /// <summary>House footprints (width across the front, depth), indexed like the house catalog.</summary>
    IReadOnlyList<Vector2> HouseFootprints { get; }

    int HousePaletteCount { get; }

    /// <summary>Scaled footprint (width along X, depth along Z) of a prop model key such as "prop:gas_grill".</summary>
    Vector2 PropFootprint(string model, float scale);
}

/// <summary>Generator settings. Distances in meters.</summary>
public sealed record SuburbSettings
{
    public float MapSize { get; init; } = 190f;
    public int TankCount { get; init; } = 30;

    /// <summary>Tanks stand in groups of this many, close enough that one going up sets off the rest.</summary>
    public int ClusterSizeMin { get; init; } = 1;

    public int ClusterSizeMax { get; init; } = 10;

    public float RoadSpacing { get; init; } = 70f;
    public float PileSiteSpacing { get; init; } = 1f;
    public float RowsWeight { get; init; } = 1f;
    public float SquareWeight { get; init; } = 1f;
    public float TriangularWeight { get; init; } = 1f;
    public float TightWeight { get; init; } = 1f;
    public float PileSpacing { get; init; } = 0.36f;
    public float PileLayerHeight { get; init; } = 0.249f;
    public float PileUprightLayerHeight { get; init; } = 0.462f;
    public float PileDepthSpacing { get; init; } = 0.49f;
    public float StartSpacing { get; init; } = 12f;

    public float CarriagewayHalfWidth { get; init; } = 4f;
    public float VergeWidth { get; init; } = 1.3f;
    public float SidewalkWidth { get; init; } = 1.8f;
    public float LotFrontageMin { get; init; } = 17f;
    public float LotFrontageMax { get; init; } = 23f;
    public float LotDepthMin { get; init; } = 23f;
    public float LotDepthMax { get; init; } = 34f;
    public float CulDeSacRadius { get; init; } = 10.5f;

    /// <summary>How strongly tank groups are pulled toward the map's center (0 spreads them evenly, as in single player).</summary>
    public float CenterBias { get; init; }

    /// <summary>Radius of the central area that tank groups and ammo favor when <see cref="CenterBias"/> is above 0.</summary>
    public float CenterRadius { get; init; } = 40f;

    /// <summary>Separate player spawn points to choose, for a multiplayer match. 0 for single player.</summary>
    public int PlayerSpawns { get; init; }

    /// <summary>Ammo pickup spots to choose, for a multiplayer match.</summary>
    public int AmmoSpots { get; init; }

    /// <summary>
    /// Above 0, a strip instead of a square map: one straight through road along X, this long, with lots on both sides
    /// and <see cref="StripSeamGap"/> of lawn kept free at each end, so strips laid end to end join street to street.
    /// <see cref="MapSize"/> is then the strip's width before cropping. 0 (every shipped suburb) leaves output unchanged.
    /// </summary>
    public float StripLength { get; init; }

    public float StripSeamGap { get; init; } = 1.5f;

    public float RightOfWayHalfWidth => CarriagewayHalfWidth + VergeWidth + SidewalkWidth;
}

/// <summary>
/// Builds a <see cref="SuburbPlan"/> from a seed: a road layout from one of a few templates, house lots packed
/// along every road frontage and around cul-de-sacs, houses, driveways, backyard fences with gate openings, yard
/// and street props, and tank spots chosen to spread groups of tanks across the map.
/// </summary>
public sealed class SuburbGenerator
{
    private readonly SuburbSettings settings;
    private readonly SuburbLayout layout;
    private readonly SuburbFurnishing furnishing;
    private readonly SuburbSupply supply;

    public SuburbGenerator(SuburbSettings settings, IPlanCatalog catalog, int seed)
    {
        this.settings = settings;
        var rng = new Random(seed);
        layout = new SuburbLayout(settings, rng);
        furnishing = new SuburbFurnishing(settings, catalog, rng, layout.RoadAreas);
        supply = new SuburbSupply(settings, rng, furnishing.Occupied, furnishing.Obstacles,
            furnishing.TankSpots, furnishing.FenceSegments);
        Seed = seed;
    }

    public int Seed { get; }

    public SuburbPlan Generate()
    {
        var half = settings.MapSize / 2f;
        var plan = new SuburbPlan { Seed = Seed, Bounds = new Rect2(-half, -half, settings.MapSize, settings.MapSize) };
        if (settings.StripLength > 0)
        {
            // Lots pack inside the seam gaps; the crop restores the strip's full length.
            var length = settings.StripLength - settings.StripSeamGap * 2f;
            plan.Bounds = new Rect2(-length / 2f, -half, length, settings.MapSize);
        }

        layout.Generate(plan);
        furnishing.Generate(plan);
        supply.Generate(plan);
        // Multiplayer additions come last, so single-player suburbs stay identical for a given seed.
        if (settings.PlayerSpawns > 0)
        {
            supply.ChooseSpawns(settings.PlayerSpawns);
        }
        if (settings.AmmoSpots > 0)
        {
            supply.ChooseAmmoSpots(settings.AmmoSpots);
        }
        return plan;
    }

    /// <summary>World yaw that turns a model's +Z front toward a map-space direction.</summary>
    public static float YawFacing(Vector2 direction) => Mathf.Atan2(direction.X, direction.Y);

    /// <summary>The map-space direction a model's +Z front faces at a yaw.</summary>
    public static Vector2 FacingVector(float yaw) => new(Mathf.Sin(yaw), Mathf.Cos(yaw));
}
