using Godot;
using Propane.Core;

namespace Propane.Bots;

/// <summary>A bot's difficulty, as the roster's controller configuration carries it (0–2).</summary>
public enum BotDifficulty : byte { Easy, Normal, Hard }

/// <summary>
/// A named difficulty profile of the local bot dials. Skill blends the Easy/Hard endpoints of aim error, reaction
/// and aim turning; the brain's goals, navigation and the actor's physics are the same at every difficulty.
/// </summary>
public readonly record struct BotProfile(BotDifficulty Difficulty, float Skill)
{
    public static readonly BotDifficulty[] All = { BotDifficulty.Easy, BotDifficulty.Normal, BotDifficulty.Hard };
    public string Name => Difficulty.ToString();

    /// <summary>Meters of aim wobble around a fought player's aim point, before the brain's settling factor.</summary>
    public float AimError(Tuning tuning) => Mathf.Lerp(tuning.BotAimErrorEasy, tuning.BotAimErrorHard, Skill);

    /// <summary>Seconds before the bot fires at a player it has just started fighting.</summary>
    public float Reaction(Tuning tuning) => Mathf.Lerp(tuning.BotReactionEasy, tuning.BotReactionHard, Skill);

    public static BotProfile For(BotDifficulty difficulty, Tuning tuning) => new(difficulty, difficulty switch
    {
        BotDifficulty.Easy => tuning.BotEasySkill,
        BotDifficulty.Hard => tuning.BotHardSkill,
        _ => tuning.BotNormalSkill,
    });

    /// <summary>A wire difficulty byte; out-of-range values never reach here, since the codec rejects them.</summary>
    public static BotProfile For(byte difficulty, Tuning tuning) =>
        For(difficulty <= (byte)BotDifficulty.Hard ? (BotDifficulty)difficulty : BotDifficulty.Normal, tuning);

    /// <summary>The nearest named profile to a free skill value, such as the Lab's tuning slider.</summary>
    public static BotDifficulty Nearest(float skill, Tuning tuning)
    {
        var best = BotDifficulty.Normal;
        foreach (var difficulty in All)
        {
            if (Mathf.Abs(For(difficulty, tuning).Skill - skill) < Mathf.Abs(For(best, tuning).Skill - skill)) best = difficulty;
        }
        return best;
    }
}
