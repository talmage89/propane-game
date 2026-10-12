namespace Propane.Core;

public partial class Tuning
{
    // Local brain dials deliberately have no Export attribute: ValueNames/Snapshot must remain unchanged for
    // patch compatibility. DevArgs --tune.Bot... and C# callers can tune them; a later runtime bot package can
    // provide local difficulty profiles without changing the match's movement physics or network schema.
    public float BotRouteSpacing { get; set; } = 1.5f;
    public int BotSearchPerTick { get; set; } = 12;
    public int BotSearchBudget { get; set; } = 900;
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
    public float BotTurnSharpness { get; set; } = 12f;
    public float BotSprintDistance { get; set; } = 8f;
    public float BotRecoilControlSpeed { get; set; } = 2.8f;
    public float BotAimTurnSharpness { get; set; } = 18f;
    public float BotAimErrorEasy { get; set; } = 1.1f;
    public float BotAimErrorHard { get; set; } = 0.22f;
    public float BotReactionEasy { get; set; } = 0.5f;
    public float BotReactionHard { get; set; } = 0.2f;
}
