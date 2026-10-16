using Godot;

namespace Propane.Core;

public partial class Tuning
{
    // Crowd tactics are shared bot tuners for the next minor release. Existing local dials stay local.
    [ExportGroup("Bots")]
    [Export(PropertyHint.Range, "4,16,1")] public int BotKingCrowdPlayers { get; set; } = 4;
    [Export(PropertyHint.Range, "0.1,2,0.1")] public float BotKingCrowdInterval { get; set; } = 0.4f;
    [Export(PropertyHint.Range, "0,10,0.1")] public float BotKingContestMin { get; set; } = 1f;
    [Export(PropertyHint.Range, "0,10,0.1")] public float BotKingContestMax { get; set; } = 3f;
    [Export(PropertyHint.Range, "0,30,0.5")] public float BotKingRaidMin { get; set; } = 6f;
    [Export(PropertyHint.Range, "0,30,0.5")] public float BotKingRaidMax { get; set; } = 14f;
    // Additional waiting per rival: large rosters should not all charge back at the same cadence.
    [Export(PropertyHint.Range, "0,2,0.05")] public float BotKingCrowdScale { get; set; } = 0.15f;

    // Local brain dials deliberately have no Export attribute: ValueNames/Snapshot must remain unchanged for
    // patch compatibility. DevArgs --tune.Bot... and C# callers can tune them. The difficulty profiles below are
    // local too: a bot's difficulty changes its brain on its creator's game, never the match's physics or schema.
    public float BotRouteSpacing { get; set; } = 1.5f;
    // Feet within this height of a cached ground plane share its answers.
    public float BotLayerTolerance { get; set; } = 0.35f;
    // Reserve metadata for occasional standing heights on cars and debris before brains start thinking.
    public int BotLayerReserve { get; set; } = 8;
    public int BotSearchPerTick { get; set; } = 24;
    public int BotSearchBudget { get; set; } = 900;
    // Shared by every bot one process runs in a world, per physics tick: navigation physics queries and route
    // search expansions. Searches past them wait for the next tick, so many bots cost no more per tick than a few.
    public int BotQueriesPerTick { get; set; } = 128;
    public int BotExpansionsPerTick { get; set; } = 160;
    // Pursue a richer rival after the tank supply has been cleared, instead of waiting outside rifle range.
    public bool BotHuntWhenEmpty { get; set; } = true;
    // Easy keeps looking for company when no banking objective is available, even against an empty rival.
    public bool BotEasySeekRivals { get; set; } = true;
    public float BotEasyRoamRadius { get; set; } = 12f;
    public float BotEasyRoamSpreadDegrees { get; set; } = 90f;
    public float BotEasyRoamSeconds { get; set; } = 4f;
    // Periodic observations leave steering and aiming at the physics rate.
    public float BotSightInterval { get; set; } = 0.1f;
    // King contests move through short sight gaps at melee range; caching visibility lost its banking strength.
    public float BotKingSightInterval { get; set; } = 0f;
    public float BotPrizeInterval { get; set; } = 0.4f;
    public float BotWatchInterval { get; set; } = 0.2f;
    public float BotCoverInterval { get; set; } = 0.5f;
    // Crowded King zones need recovery space: a knocked-down bot briefly regroups outside before returning.
    public int BotKingRegroupPlayers { get; set; } = 8;
    public float BotKingRegroupMin { get; set; } = 3f;
    public float BotKingRegroupMax { get; set; } = 12f;
    // Meters a regrouping bot moves along the zone's edge before choosing its next point.
    public float BotKingRegroupStep { get; set; } = 6f;
    // Prototype motors retain their original bounded walking recovery when a planner cannot make progress.
    public float BotDirectStallTime { get; set; } = 1.2f;
    public float BotDirectTime { get; set; } = 1f;
    public float BotDirectProgressDistance { get; set; } = 0.5f;
    // Seconds that navigation answers near an explosion expire while thrown props settle.
    public float BotDisturbSettle { get; set; } = 3f;
    // A goal the search cannot reach still yields a route to the nearest explored point, when that is at least this
    // many meters nearer than the start (0: off); the bot searches again from its end.
    public float BotPartialRouteGain { get; set; } = 3f;
    // After this many expansions, a search that has found a point that much nearer settles for it (0: never early).
    public int BotPartialSearchExpansions { get; set; } = 240;
    // A failed search that expanded no more than this many points started on an island the lattice has no way off
    // (a car roof, debris): the bot steps straight toward its goal for BotDirectTime.
    public int BotIslandDirections { get; set; } = 16;
    public int BotIslandExpansions { get; set; } = 24;
    // Keep walking the previous route while the search for a moved goal runs, instead of standing still.
    public bool BotReplanWhileMoving { get; set; } = true;
    public float BotWaypointRadius { get; set; } = 0.45f;
    public float BotGoalRadius { get; set; } = 0.6f;
    public float BotReplanInterval { get; set; } = 0.4f;
    public float BotProgressWindow { get; set; } = 1.25f;
    public float BotProgressDistance { get; set; } = 0.7f;
    public int BotRouteRetries { get; set; } = 3;
    public float BotJumpMargin { get; set; } = 0.06f;
    public float BotLaunchSpeedTolerance { get; set; } = 0.1f;
    public float BotJumpMinimumRange { get; set; } = 0.8f;
    public float BotFlightTimeoutFactor { get; set; } = 1.5f;
    public float BotFleeDistance { get; set; } = 10f;
    public int BotFleeDirections { get; set; } = 8;
    public float BotVentClearance { get; set; } = 1f;
    public float BotJumpCost { get; set; } = 0.25f;
    public float BotCoverDetourCost { get; set; } = 3f;
    public float BotTankClearance { get; set; } = 1.8f;
    public float BotTargetRetryTime { get; set; } = 8f;
    public float BotStuckAimTime { get; set; } = 4f;
    public float BotTurnSharpness { get; set; } = 12f;
    public float BotSprintDistance { get; set; } = 8f;
    public float BotRecoilControlSpeed { get; set; } = 2.8f;
    public float BotAimTurnSharpness { get; set; } = 18f;
    public float BotAimErrorEasy { get; set; } = 1.1f;
    public float BotAimErrorHard { get; set; } = 0.22f;
    public float BotReactionEasy { get; set; } = 0.5f;
    public float BotReactionHard { get; set; } = 0.2f;
    // Easy, Normal and Hard pick the brain's skill, which blends the aim error, reaction and aim turning endpoints above.
    public float BotEasySkill { get; set; } = 0.2f;
    public float BotNormalSkill { get; set; } = 0.6f;
    // Hard's local score-swing estimates. Capture discounts spilled tanks we may not recover; denial favors leaders.
    public bool BotHardChasesPoints { get; set; } = true;
    public float BotPointsCapture { get; set; } = 0.25f;
    public float BotPointsDeny { get; set; } = 0.8f;
    public float BotPointsLeader { get; set; } = 1f;
    public float BotPointsAhead { get; set; } = 0.7f;
    public float BotPointsBehind { get; set; } = 0.15f;
    public float BotPointsRisk { get; set; } = 0.35f;
    public int BotPointsPileCap { get; set; } = 5;
    public float BotPointsShotRange { get; set; } = 22f;
    public float BotPointsShotSeconds { get; set; } = 1.5f;
    public float BotPointsFightSeconds { get; set; } = 3f;
    public float BotPointsHitsPerSecond { get; set; } = 1f;
    public float BotPointsHysteresis { get; set; } = 0.3f;
    public float BotPointsInterval { get; set; } = 0.4f;
    public float BotPointsProtectDistance { get; set; } = 16f;
    public float BotPointsZone { get; set; } = 2f;
    // First N attached Lab actors use Hard; the rest retain LabBotSkill (also allows policy-only A/B trials).
    public int LabHardBots { get; set; } = 0;
    public int LabHardOffset { get; set; } = 0;
    public float BotHardSkill { get; set; } = 1f;
}
