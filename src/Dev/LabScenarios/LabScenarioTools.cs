using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using Propane.Bots;
using Propane.Lab;
using Propane.Modes;
using Propane.Player;
using Propane.Tank;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev.LabCases;

internal static class LabScenarioTools
{
    /// <summary>
    /// Logs each bot's idle share (<see cref="BotActivity"/>); <c>--max-idle=F</c> and <c>--max-idle-worst=F</c> fail the
    /// run when the bots' mean or worst share exceeds them.
    /// </summary>
    internal static void CheckIdle(string label, IReadOnlyDictionary<int, BotActivity> activity)
    {
        if (activity.Count == 0) return;
        foreach (var (actor, bot) in activity.OrderBy(pair => pair.Key)) GD.Print($"[lab idle] {label} bot {actor}: {bot.Report()}");
        var mean = activity.Values.Average(bot => bot.IdleShare);
        var worst = activity.Values.Max(bot => bot.IdleShare);
        GD.Print(FormattableString.Invariant($"[lab idle] {label}: bots={activity.Count} mean={mean * 100:0.0}% worst={worst * 100:0.0}% longest={activity.Values.Max(bot => bot.LongestIdle)} s"));
        if (DevArgs.Get("max-idle") != null)
            Assert(mean <= DevArgs.GetFloat("max-idle", 1), FormattableString.Invariant($"{label}: bots idle {mean * 100:0.0}% of play on average"));
        if (DevArgs.Get("max-idle-worst") != null)
            Assert(worst <= DevArgs.GetFloat("max-idle-worst", 1), FormattableString.Invariant($"{label}: a bot idles {worst * 100:0.0}% of play"));
    }

    internal static void ReportPoints(LabSession lab, string mode, int seed, IReadOnlyDictionary<int, int> scores,
        IReadOnlyDictionary<int, int> earned)
    {
        if (DevArgs.Get("compare-points") == null) return;
        var hard = lab.Session.Actors.Where(a => a.Character.InputOverride is IBotGoalPolicy { PointsDriven: true }).Select(a => a.Character.ActorId).ToArray();
        var normal = lab.Session.Actors.Where(a => a.Character.InputOverride is IBotGoalPolicy { PointsDriven: false }).Select(a => a.Character.ActorId).ToArray();
        Assert(hard.Length > 0 && normal.Length > 0, "points comparison requires both policies");
        GD.Print($"[lab points] mode={mode} seed={seed} hard={hard.Length} normal={normal.Length} " +
            $"hard_score={hard.Sum(a => scores.GetValueOrDefault(a))} normal_score={normal.Sum(a => scores.GetValueOrDefault(a))} " +
            $"hard_earned={hard.Sum(a => earned.GetValueOrDefault(a))} normal_earned={normal.Sum(a => earned.GetValueOrDefault(a))}");
    }

    internal static void ReportSkills(LabSession lab, int seed, IReadOnlyDictionary<int, int> scores)
    {
        if (DevArgs.Get("compare-skills") == null) return;
        var bots = lab.Session.Actors.Select(a => a.Character).ToArray();
        int[] Group(BotDifficulty difficulty) => bots.Where(a => a.InputOverride is IBotGoalPolicy brain &&
            !brain.PointsDriven && BotProfile.Nearest(brain.Skill, lab.World.Tuning) == difficulty).Select(a => a.ActorId).ToArray();
        var normal = Group(BotDifficulty.Normal);
        var easy = Group(BotDifficulty.Easy);
        Assert(normal.Length > 0 && easy.Length > 0 && normal.Length + easy.Length == bots.Length,
            "skill comparison requires Normal and Easy objective-policy bots on every actor");
        GD.Print($"[lab skills] seed={seed} normal={normal.Length} easy={easy.Length} " +
            $"normal_score={normal.Sum(a => scores.GetValueOrDefault(a))} easy_score={easy.Sum(a => scores.GetValueOrDefault(a))}");
    }

    internal static async Task FireAt(PlayerCharacter actor, Func<Vector3> point)
    {
        actor.InputOverride = new OneShot(actor, point);
        for (var i = 0; i < 15; i++) await actor.ToSignal(actor.GetTree(), SceneTree.SignalName.ProcessFrame);
        actor.InputOverride = new IdleInput();
    }

    internal sealed class IdleInput(bool aim = false) : IPlayerInputSource { public PlayerIntent Read() => new() { Aim = aim }; }

    internal sealed class OneShot(PlayerCharacter actor, Func<Vector3> point) : IPlayerInputSource
    {
        private readonly BotAim aim = new();
        private bool fired;
        public PlayerIntent Read()
        {
            if (fired) return new PlayerIntent { Aim = true };
            aim.Track(actor, point(), 1f / Engine.PhysicsTicksPerSecond, 1, smooth: false);
            fired = true;
            return new PlayerIntent { Aim = true, FirePressed = true };
        }
    }

    internal static bool RingEliminationsAgree(RingOutRules ring, IReadOnlyList<int> starters, Func<int, bool> spectating)
    {
        var eliminated = ring.Eliminations.Select(e => e.ActorId).ToHashSet();
        // The last actors out together share first place and still become spectators.
        return eliminated.Count == ring.Eliminations.Count && eliminated.SetEquals(starters.Where(spectating)) &&
            starters.Where(a => !eliminated.Contains(a)).All(a => ring.Place(a) == 1) &&
            ring.Eliminations.All(e => ring.Place(e.ActorId) == e.Place);
    }

    internal static int TrackingCount(PrototypeRunner runner) =>
        (int?)typeof(PrototypeRunner).GetProperty("TrackingCount", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(runner) ?? -1;
}
