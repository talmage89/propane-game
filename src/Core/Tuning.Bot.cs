namespace Propane.Core;

public partial class Tuning
{
    // Local brain dials deliberately have no Export attribute: ValueNames/Snapshot must remain unchanged for
    // patch compatibility. DevArgs --tune.Bot... and C# callers can tune them. The difficulty profiles below are
    // local too: a bot's difficulty changes its brain on its creator's game, never the match's physics or schema.
    public float BotRouteSpacing { get; set; } = 1.5f;
    // Feet within this height of a cached ground plane share its answers.
    public float BotLayerTolerance { get; set; } = 0.35f;
    public int BotSearchPerTick { get; set; } = 24;
    public int BotSearchBudget { get; set; } = 900;
    // Shared by every bot one process runs in a world, per physics tick: navigation physics queries and route
    // search expansions. Searches past them wait for the next tick, so many bots cost no more per tick than a few.
    public int BotQueriesPerTick { get; set; } = 128;
    public int BotExpansionsPerTick { get; set; } = 160;
    // Pursue a richer rival after the tank supply has been cleared, instead of waiting outside rifle range.
    public bool BotHuntWhenEmpty { get; set; } = true;
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
    public float BotKingRegroupMax { get; set; } = 30f;
    // Prototype motors retain their original bounded walking recovery when a planner cannot make progress.
    public float BotDirectStallTime { get; set; } = 1.2f;
    public float BotDirectTime { get; set; } = 1f;
    public float BotDirectProgressDistance { get; set; } = 0.5f;
    // Seconds that navigation answers near an explosion expire while thrown props settle.
    public float BotDisturbSettle { get; set; } = 3f;
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
    public float BotHardSkill { get; set; } = 1f;
}
