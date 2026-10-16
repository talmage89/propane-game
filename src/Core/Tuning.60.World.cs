using Godot;

namespace Propane.Core;

public partial class Tuning
{
    public const int TankSupplyMinimum = 1;
    public const int TankSupplyMaximum = 8;

    // ---------------------------------------------------------------- World
    [ExportGroup("World")]
    [Export(PropertyHint.Range, "1,80,1")] public int TankCount { get; set; } = 30;
    /// <summary>Scales suburb supply and pile sizes in single player and matches; rebuild to apply.</summary>
    [Export(PropertyHint.Range, "1,8,1")] public int TankCountMultiplier { get; set; } = 1;
    /// <summary>Tanks stand in groups of between these sizes, close enough to set each other off.</summary>
    [Export(PropertyHint.Range, "1,16,1")] public int ClusterSizeMin { get; set; } = 1;
    [Export(PropertyHint.Range, "1,16,1")] public int ClusterSizeMax { get; set; } = 10;
    [Export(PropertyHint.Range, "0,15,0.1")] public float ClearCelebrationTime { get; set; } = 3f;
    [Export(PropertyHint.Range, "0.3,10,0.1")] public float TransitionTime { get; set; } = 2.4f;
    [Export] public int FixedSeed { get; set; } = 0;

    [ExportGroup("Tank Gather")]
    [Export(PropertyHint.Range, "0.5,8,0.1")] public float GatherRadius { get; set; } = 3f;
    [Export(PropertyHint.Range, "2,64,1")] public int GatherMinimum { get; set; } = 2;
    [Export(PropertyHint.Range, "0,120,0.5")] public float GatherDelay { get; set; } = 20f;
    [Export(PropertyHint.Range, "0.05,2,0.05")] public float GatherScanInterval { get; set; } = 0.25f;
    [Export(PropertyHint.Range, "0.5,10,0.1")] public float GatherDuration { get; set; } = 3f;
    [Export(PropertyHint.Range, "0.1,4,0.1")] public float GatherLift { get; set; } = 1.5f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float GatherShimmer { get; set; } = 0.12f;
    [Export(PropertyHint.Range, "0,20,0.1")] public float GatherShimmerSpeed { get; set; } = 4f;
    [Export(PropertyHint.Range, "0,80,1")] public float GatherShimmerBands { get; set; } = 18f;
    [Export(PropertyHint.Range, "1,20,1")] public float GatherShimmerSharpness { get; set; } = 6f;
    [Export(PropertyHint.Range, "0.01,0.5,0.01")] public float GatherRestSpeed { get; set; } = 0.06f;
    [Export(PropertyHint.Range, "0.001,0.2,0.001")] public float GatherRestDistance { get; set; } = 0.015f;
    [Export(PropertyHint.Range, "0.1,10,0.1")] public float GatherRestAngle { get; set; } = 1f;
    [Export(PropertyHint.Range, "0.01,1,0.01")] public float GatherRestSpin { get; set; } = 0.1f;
    [Export(PropertyHint.Range, "0.01,1,0.01")] public float GatherPileTolerance { get; set; } = 0.15f;
    [Export(PropertyHint.Range, "1,90,1")] public float GatherPileAngle { get; set; } = 15f;
    [Export(PropertyHint.Range, "0,3,0.1")] public float GatherCenterSearch { get; set; } = 1f;
    [Export(PropertyHint.Range, "1,8,1")] public int GatherSearchRings { get; set; } = 2;
    [Export(PropertyHint.Range, "4,16,1")] public int GatherSearchDirections { get; set; } = 8;
    [Export(PropertyHint.Range, "0,0.2,0.005")] public float GatherClearance { get; set; } = 0.025f;
    [Export(PropertyHint.Range, "0,0.2,0.005")] public float GatherGroundTolerance { get; set; } = 0.025f;
    [Export(PropertyHint.Range, "0.9,1,0.005")] public float GatherGroundNormal { get; set; } = 0.99f;
    [Export(PropertyHint.Range, "1,100,1")] public float GatherGroundRay { get; set; } = 10f;

    [ExportGroup("Tank Piles")]
    [Export(PropertyHint.Range, "0.5,4,0.05")] public float PileSiteSpacing { get; set; } = 1f;
    [Export(PropertyHint.Range, "0,10,0.1")] public float PileRowsWeight { get; set; } = 1f;
    [Export(PropertyHint.Range, "0,10,0.1")] public float PileSquareWeight { get; set; } = 1f;
    [Export(PropertyHint.Range, "0,10,0.1")] public float PileTriangularWeight { get; set; } = 1f;
    [Export(PropertyHint.Range, "0,10,0.1")] public float PileClusterWeight { get; set; } = 1f;
    [Export(PropertyHint.Range, "0.32,0.5,0.001")] public float PileSpacing { get; set; } = 0.36f;
    /// <summary>Rise of the side-laid rows.</summary>
    [Export(PropertyHint.Range, "0.15,0.35,0.001")] public float PileLayerHeight { get; set; } = 0.249f;
    /// <summary>Upright pyramid rise: the authored collision collar is 0.462 m above the foot.</summary>
    [Export(PropertyHint.Range, "0.36,0.6,0.001")] public float PileUprightLayerHeight { get; set; } = 0.462f;
    [Export(PropertyHint.Range, "0.468,0.8,0.001")] public float PileDepthSpacing { get; set; } = 0.49f;

    // ---------------------------------------------------------------- HUD
    [ExportGroup("HUD")]
    /// <summary>Small arrows toward every group of tanks: on the screen edge, or over the group when in view.</summary>
    [Export] public bool TankArrows { get; set; } = true;
}
