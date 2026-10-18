using Propane.Bots;
using Propane.Core;

namespace Propane.Lab;

/// <summary>Live local difficulty by attachment slot, independent of the runner's rotating think turn.</summary>
internal static class LabBotDifficulty
{
    public static void Configure(IBotGoalPolicy brain, int index, Tuning tuning)
    {
        var selected = index >= tuning.LabHardOffset && index < tuning.LabHardOffset + tuning.LabHardBots;
        var hard = selected || BotProfile.Nearest(tuning.LabBotSkill, tuning) == BotDifficulty.Hard;
        brain.Skill = selected ? tuning.BotHardSkill : tuning.LabBotSkill;
        brain.PointsDriven = hard && tuning.BotHardChasesPoints;
    }
}
