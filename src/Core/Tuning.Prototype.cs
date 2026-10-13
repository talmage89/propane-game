namespace Propane.Core;

// The Lab's prototype modes (Ring Out, King of the Cul-de-sac, Short Fuse). Like every Lab dial these are local: they never
// enter a match snapshot or a saved .tres, so trying a rule changes nobody else's game. The mode rules copy the
// ones they read by name (RingOutRules.Tuners, KingRules.Tuners, ShortFuseRules.Tuners); layout dials apply on reset.
public partial class Tuning
{
    [LabDial(1, 5)] public int LabPrototypeBots { get; set; } = 3;
    [LabDial(0.5, 6, 0.5)] public float LabWarningPulseHz { get; set; } = 2f;

    // Ring Out: the slab, its dissolving rim, and when a fall counts.
    [LabDial(3, 7, 2, Scenario = "ringout")] public int RingOutLots { get; set; } = 5;
    [LabDial(10, 24, 1, Scenario = "ringout")] public float RingOutLotSize { get; set; } = 16f;
    [LabDial(1, 8, 1, Scenario = "ringout")] public int RingOutBandLots { get; set; } = 4;
    [LabDial(3, 60, 1, Scenario = "ringout")] public float RingOutBandSeconds { get; set; } = 15f;
    [LabDial(0.5, 10, 0.5, Scenario = "ringout")] public float RingOutWarningSeconds { get; set; } = 4f;
    [LabDial(1, 15, 0.5, Scenario = "ringout")] public float RingOutCreditSeconds { get; set; } = 6f;
    [LabDial(0.5, 10, 0.5, Scenario = "ringout")] public float RingOutFallDepth { get; set; } = 2f;
    [LabDial(0.2, 2, 0.1, Scenario = "ringout")] public float RingOutStandHeight { get; set; } = 0.6f;
    [LabDial(30, 600, 10, Scenario = "ringout")] public float RingOutRoundLimit { get; set; } = 180f;
    [LabDial(1, 8, 1, Scenario = "ringout")] public int RingOutPiles { get; set; } = 4;
    [LabDial(1, 12, 1, Scenario = "ringout")] public int RingOutPileCount { get; set; } = 6;
    [LabDial(4, 40, 1, Scenario = "ringout")] public float RingOutPileDistance { get; set; } = 13f;
    [LabDial(0.2, 3, 0.1, Scenario = "ringout")] public float RingOutDissolveSeconds { get; set; } = 0.8f;
    [LabDial(10, 300, 10, Scenario = "ringout")] public float RingOutVoidDepth { get; set; } = 150f;

    // King of the Cul-de-sac: the zone, its minting and its moves.
    [LabDial(3, 12, 0.5, Scenario = "king")] public float KingZoneRadius { get; set; } = 6f;
    [LabDial(1, 8, 0.5, Scenario = "king")] public float KingZoneHeight { get; set; } = 3f;
    [LabDial(0.5, 10, 0.5, Scenario = "king")] public float KingMintSeconds { get; set; } = 2f;
    [LabDial(10, 120, 5, Scenario = "king")] public float KingMoveSeconds { get; set; } = 45f;
    [LabDial(0.5, 10, 0.5, Scenario = "king")] public float KingWarningSeconds { get; set; } = 3f;
    [LabDial(5, 40, 1, Scenario = "king")] public float KingMoveMin { get; set; } = 15f;
    [LabDial(5, 60, 1, Scenario = "king")] public float KingMoveMax { get; set; } = 25f;
    [LabDial(30, 600, 30, Scenario = "king")] public float KingRoundSeconds { get; set; } = 180f;
    [LabDial(3, 12, 1, Scenario = "king")] public int KingSpots { get; set; } = 8;
    [LabDial(10, 40, 1, Scenario = "king")] public float KingSpotRing { get; set; } = 20f;
    [LabDial(1, 12, 1, Scenario = "king")] public int KingPileCount { get; set; } = 6;
    [LabDial(5, 80, 5, Scenario = "king")] public float KingBeamHeight { get; set; } = 40f;
    [LabDial(0.05, 0.75, 0.05, Scenario = "king")] public float KingBeamAlpha { get; set; } = 0.3f;
    [LabDial(0.2, 4, 0.1, Scenario = "king")] public float KingMaterializeSeconds { get; set; } = 1.2f;

    // Short Fuse: health, damage, credit and rounds. Blast damage reaches RagdollRadius, as far as a blast throws.
    [LabDial(30, 300, 5, Scenario = "shortfuse")] public float ShortFuseRoundSeconds { get; set; } = 105f;
    /// <summary>The slab's side; its tanks and piles are stocked for the players as in a match.</summary>
    [LabDial(100, 240, 10, Scenario = "shortfuse")] public float ShortFuseMapSize { get; set; } = 150f;
    [LabDial(1, 7, 1, Scenario = "shortfuse")] public int ShortFuseWinsToMatch { get; set; } = 3;
    [LabDial(10, 300, 10, Scenario = "shortfuse")] public float ShortFuseMaxHealth { get; set; } = 100f;
    [LabDial(0, 50, 1, Scenario = "shortfuse")] public float ShortFuseBulletDamage { get; set; } = 7f;
    [LabDial(0, 300, 5, Scenario = "shortfuse")] public float ShortFuseBlastCenterDamage { get; set; } = 90f;
    [LabDial(0, 300, 5, Scenario = "shortfuse")] public float ShortFuseBlastEdgeDamage { get; set; } = 30f;
    [LabDial(0, 50, 0.5, Scenario = "shortfuse")] public float ShortFuseRegenPerSecond { get; set; } = 5f;
    [LabDial(0, 20, 0.5, Scenario = "shortfuse")] public float ShortFuseRegenDelay { get; set; } = 5f;
    [LabDial(0, 5, 0.25, Scenario = "shortfuse")] public float ShortFuseGetUpGrace { get; set; } = 0.75f;
    [LabDial(0, 30, 0.5, Scenario = "shortfuse")] public float ShortFuseCreditSeconds { get; set; } = 8f;
    /// <summary>How late a damage report may arrive; a death is declared once this has passed it.</summary>
    [LabDial(0, 2, 0.05, Scenario = "shortfuse")] public float ShortFuseReportWindow { get; set; } = 0.25f;
    /// <summary>How long a round's result shows before the next suburb assembles.</summary>
    [LabDial(1, 15, 0.5, Scenario = "shortfuse")] public float ShortFuseRoundEndSeconds { get; set; } = 4f;
    /// <summary>How long a dead body lies before it leaves the round.</summary>
    [LabDial(0, 5, 0.25, Scenario = "shortfuse")] public float ShortFuseCorpseSeconds { get; set; } = 1.5f;
}
