using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Godot;

namespace Propane.Bots;

/// <summary>
/// Whether a bot makes progress, for logs and tests. Play is cut into windows of <see cref="Window"/> seconds; a window
/// is idle when the bot stayed upright, ended it less than <see cref="Travel"/> meters from where it began, and never
/// fired, meleed or stood in a scoring zone. It counts idle windows (and those spent pushing against something),
/// the longest idle run, and the time spent in each status by its format, without allocating per tick.
/// </summary>
public sealed class BotActivity
{
    public const float Window = 1f;
    public const float Travel = 1f;

    private readonly Dictionary<object, int> statusTicks = new(32, ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, int> idleStatus = new(32, ReferenceEqualityComparer.Instance);
    private Vector3 start;
    private float elapsed = -1;
    private bool down, acted, scored;
    private int pushing, ticks;
    private int run;

    public int Windows { get; private set; }
    public int IdleWindows { get; private set; }
    /// <summary>Idle windows in which the bot asked to move most of the time: blocked rather than standing.</summary>
    public int BlockedWindows { get; private set; }
    public int LongestIdle { get; private set; }
    public double IdleShare => Windows == 0 ? 0 : (double)IdleWindows / Windows;

    /// <summary>One physics tick of a bot actor, reading its last intent; <paramref name="zone"/> is King's, if any.</summary>
    public void Sample(bool playing, Player.PlayerCharacter actor, IBotGoalPolicy brain, KingBotView? zone, float dt)
    {
        var at = actor.GlobalPosition;
        var intent = actor.Runtime.Intent;
        var scoring = zone is { } king && !actor.IsRagdolled &&
            (at.X - king.Spot.X) * (at.X - king.Spot.X) + (at.Z - king.Spot.Z) * (at.Z - king.Spot.Z) <= king.Radius * king.Radius;
        Sample(playing, at, actor.IsRagdolled, intent.FireHeld || intent.FirePressed || intent.MeleePressed,
            intent.Move.LengthSquared() > 0.04f, scoring, brain.StatusKind, dt);
    }

    /// <summary>One physics tick of a bot. Outside play the open window is dropped.</summary>
    public void Sample(bool playing, Vector3 position, bool ragdolled, bool acting, bool moving, bool scoring, string kind, float dt)
    {
        if (!playing)
        {
            elapsed = -1;
            return;
        }
        statusTicks[kind] = statusTicks.GetValueOrDefault(kind) + 1;
        if (elapsed < 0)
        {
            start = position;
            elapsed = 0;
            down = acted = scored = false;
            pushing = ticks = 0;
        }
        down |= ragdolled;
        acted |= acting;
        scored |= scoring;
        ticks++;
        if (moving) pushing++;
        elapsed += dt;
        if (elapsed < Window) return;
        Windows++;
        var travel = new Vector2(position.X - start.X, position.Z - start.Z).Length();
        if (!down && !acted && !scored && travel < Travel)
        {
            IdleWindows++;
            if (pushing * 2 > ticks) BlockedWindows++;
            idleStatus[kind] = idleStatus.GetValueOrDefault(kind) + 1;
            LongestIdle = Math.Max(LongestIdle, ++run);
        }
        else
        {
            run = 0;
        }
        elapsed = -1;
    }

    /// <summary>One line: idle share, blocked windows, longest idle run, and the commonest statuses overall and when idle.</summary>
    public string Report(int top = 4)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"idle={IdleShare * 100:0.0}% ({IdleWindows}/{Windows} s, blocked {BlockedWindows}) longest={LongestIdle} s");
        Append(text, " statuses:", statusTicks, top);
        Append(text, " idle in:", idleStatus, top);
        return text.ToString();
    }

    /// <summary>A status format up to its first value, e.g. "moving to a vantage".</summary>
    private static string Short(string kind)
    {
        var cut = kind.IndexOf('{', StringComparison.Ordinal);
        return (cut < 0 ? kind : kind[..cut]).Trim();
    }

    private static void Append(StringBuilder text, string label, Dictionary<object, int> counts, int top)
    {
        var total = Math.Max(1, counts.Values.Sum());
        text.Append(label);
        foreach (var (kind, count) in counts.OrderByDescending(pair => pair.Value).Take(top))
            text.Append(CultureInfo.InvariantCulture, $" [{Short((string)kind)}] {count * 100 / total}%");
    }
}
