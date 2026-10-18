namespace Propane.World.Plan;

/// <summary>Authored model keys, selection weights and pickup dimensions; no resource loading.</summary>
public static class PlanModels
{
    public const float CarScale = 1.85f;

    public static readonly string[] CarModels =
    {
        "sedan", "sedan-sports", "suv", "suv-luxury", "hatchback-sports", "van", "truck", "sedan", "suv",
    };

    public const string PickupModel = "truck";

    // The pickup's bed, measured from the Kenney truck model (units before CarScale).
    public const float PickupBedFloorModel = 0.6f;
    public const float PickupBedWallTopModel = 0.8f;
    public const float PickupBedFrontModel = -0.3f;
    public const float PickupBedBackModel = -1.45f;
    public const float PickupBedHalfWidthModel = 0.72f;
    public const float PickupBedWallModel = 0.2f;

    /// <summary>Height of the bed floor above the ground, in meters.</summary>
    public const float PickupBedFloor = PickupBedFloorModel * CarScale;

    /// <summary>Distance from the truck's origin back to the middle of its bed, in meters.</summary>
    public const float PickupBedCenterBehind = -(PickupBedFrontModel + PickupBedBackModel) * 0.5f * CarScale;

    public static readonly string[] TreeModels =
    {
        "tree_default", "tree_oak", "tree_detailed", "tree_fat", "tree_simple", "tree_tall", "tree_default_dark",
        "tree_oak_dark",
    };

    public static readonly string[] BushModels = { "plant_bush", "plant_bushDetailed", "plant_bushLarge", "plant_bushSmall", "plant_bushTriangle" };

    public static readonly string[] FlowerModels = { "flower_purpleA", "flower_redA", "flower_yellowA", "flower_purpleB", "flower_redB", "flower_yellowB" };
}
