namespace Propane.Core;

// Dissolve Run, the Lab's solo prototype: an endless street that builds ahead and dissolves behind. Local Lab dials
// like every prototype's: DissolveRunRules copies the rules' ones by name (DissolveRunRules.Tuners); the segment and
// layout dials apply on reset, the front and score dials live.
public partial class Tuning
{
    // The front: where it starts, how fast it comes, and how much each detonated tank pushes it back.
    [LabDial(5, 60, 1, Scenario = "dissolve")] public float DissolveFrontStartGap { get; set; } = 25f;
    [LabDial(0, 5, 0.1)] public float DissolveFrontSpeed { get; set; } = 1.5f;
    /// <summary>What the front's speed ramps toward; kept under SprintSpeed so a player who keeps moving can outrun it.</summary>
    [LabDial(0.5, 9, 0.1)] public float DissolveFrontMaxSpeed { get; set; } = 5f;
    /// <summary>The ramp's time constant: the speed closes about 63% of the gap to the maximum in this long.</summary>
    [LabDial(10, 600, 10)] public float DissolveFrontRampSeconds { get; set; } = 180f;
    [LabDial(0, 10, 0.5)] public float DissolveFrontPush { get; set; } = 1.5f;
    /// <summary>The front never trails the furthest point reached by more than this: it keeps the street behind you short.</summary>
    [LabDial(20, 200, 5)] public float DissolveFrontMaxGap { get; set; } = 60f;
    /// <summary>How long a caught player falls before the results.</summary>
    [LabDial(0.5, 8, 0.5)] public float DissolveFallSeconds { get; set; } = 2.5f;

    // Score: meters, plus points per tank, plus n squared for a chain of n (at least DissolveChainMin tanks).
    [LabDial(0, 100, 1)] public int DissolveTankPoints { get; set; } = 10;
    [LabDial(1, 10, 1)] public int DissolveChainMin { get; set; } = 2;
    /// <summary>A chain closes once none of its tanks has gone up for this long (longer than a chain fuse).</summary>
    [LabDial(0.2, 5, 0.1)] public float DissolveChainWindow { get; set; } = 1f;

    // The window: how far ahead the street has materialized, and how far beyond it segments are built.
    [LabDial(20, 120, 5, Scenario = "dissolve")] public float DissolveAhead { get; set; } = 60f;
    [LabDial(0, 60, 5, Scenario = "dissolve")] public float DissolveBuildLead { get; set; } = 20f;
    /// <summary>How far the front must pass a segment's end before it is freed.</summary>
    [LabDial(0, 60, 5, Scenario = "dissolve")] public float DissolveRetireLag { get; set; } = 10f;
    /// <summary>Width of the floor's collision strips, which the front takes away one at a time.</summary>
    [LabDial(0.5, 10, 0.5, Scenario = "dissolve")] public float DissolveFloorStrip { get; set; } = 2f;
    [LabDial(10, 55, 1, Scenario = "dissolve")] public float DissolveStartOffset { get; set; } = 30f;
    [LabDial(10, 300, 10, Scenario = "dissolve")] public float DissolveVoidDepth { get; set; } = 150f;

    // Segments: strips of street from the generator's parts, with fewer tanks the further the run goes.
    [LabDial(40, 100, 5, Scenario = "dissolve")] public float DissolveSegmentLength { get; set; } = 60f;
    /// <summary>The strip's width before it is cropped to its lots; it must hold a road and the deepest lots.</summary>
    [LabDial(84, 120, 2, Scenario = "dissolve")] public float DissolveStreetWidth { get; set; } = 90f;
    [LabDial(1, 40, 1, Scenario = "dissolve")] public int DissolveTanksStart { get; set; } = 16;
    [LabDial(1, 40, 1, Scenario = "dissolve")] public int DissolveTanksMin { get; set; } = 4;
    /// <summary>Tanks a segment loses per segment along the run, down to DissolveTanksMin.</summary>
    [LabDial(0, 5, 0.1, Scenario = "dissolve")] public float DissolveTanksDrop { get; set; } = 0.5f;
    [LabDial(1, 10, 1, Scenario = "dissolve")] public int DissolvePileMin { get; set; } = 2;
    [LabDial(1, 14, 1, Scenario = "dissolve")] public int DissolvePileMax { get; set; } = 8;
    /// <summary>Every this many segments, a jackpot pyramid stands in the street (0: none).</summary>
    [LabDial(0, 20, 1, Scenario = "dissolve")] public int DissolveJackpotEvery { get; set; } = 10;
    [LabDial(10, 40, 1, Scenario = "dissolve")] public int DissolveJackpotTanks { get; set; } = 30;
    [LabDial(0, 6, 1, Scenario = "dissolve")] public int DissolveAmmoCans { get; set; } = 2;
    /// <summary>1 plays today's seed (the UTC date), the same run for everyone that day; 0 a fresh seed each run.</summary>
    [LabDial(0, 1, 1, Scenario = "dissolve")] public int DissolveDaily { get; set; }
}
