using System.Collections.Generic;
using Godot;

namespace Propane.UI;

/// <summary>A player the leaderboard may rank, in roster order: id, name, color and whether they are still in the match.</summary>
public readonly record struct LeaderboardEntry(int Id, string Name, Color Color, bool Present = true);

/// <summary>
/// A ranked row. <paramref name="Rank"/> is shared on ties (1, 1, 3), as on the results screen. <paramref name="Local"/>
/// marks the local player's row; <paramref name="Detached"/> marks it when it sits below the divider, outside the top rows.
/// </summary>
public readonly record struct LeaderboardRow(int Rank, LeaderboardEntry Entry, int Score, bool Local, bool Detached);

/// <summary>Where a row is drawn and what it is animating: its slot (in rows, fractional while sliding) and its effects.</summary>
public sealed class LeaderboardMotion
{
    public int Id;
    public float Slot;
    public float Alpha;
    /// <summary>1 when the row climbs a rank, fading to 0.</summary>
    public float Flash;
    /// <summary>1 when the score drops, fading to 0.</summary>
    public float Drop;
    /// <summary>Seconds since the last gain; the "+N" shows while it is under the popup time.</summary>
    public float PopAge = 99f;
    public int PopAmount;
    /// <summary>The slot the row is sliding to.</summary>
    public float Target { get; internal set; }
    /// <summary>The row as last ranked: a row fading out after leaving the board still draws it.</summary>
    public LeaderboardRow Row { get; internal set; }
    /// <summary>Whether the row is on the board now (false while it fades out).</summary>
    public bool Shown { get; internal set; }
    /// <summary>Rank, score and "+N" as text, rebuilt only when they change.</summary>
    public string RankText { get; internal set; } = "";
    public string ScoreText { get; internal set; } = "";
    public string PopText { get; internal set; } = "";
    internal int Rank;
    internal int Score;
}

/// <summary>
/// The live standings the match HUD shows: the top rows by score, ties in roster order (the results screen's order),
/// and the local player's own row below a divider when they are outside them. <see cref="Update"/> ranks and notes
/// what changed (a climb flashes, a gain pops "+N", a loss tints); <see cref="Advance"/> slides rows to their slots.
/// Pure: no nodes, and no allocation once its buffers hold the roster.
/// </summary>
public sealed class Leaderboard
{
    /// <summary>The gap, in rows, between the top rows and the detached local row.</summary>
    public const float DividerGap = 0.4f;

    private readonly List<LeaderboardRow> rows = new();
    private readonly List<int> order = new();
    private readonly List<LeaderboardMotion> motions = new();
    private readonly Dictionary<int, LeaderboardMotion> byId = new();
    private IReadOnlyList<LeaderboardEntry> roster = System.Array.Empty<LeaderboardEntry>();
    private IReadOnlyDictionary<int, int> scores = new Dictionary<int, int>();
    private readonly System.Comparison<int> compare;
    private bool started;

    public Leaderboard() => compare = Compare;

    /// <summary>Gains within this many seconds of the last add to the "+N" showing, like the HUD's own popup.</summary>
    public float ChainWindow { get; set; } = 1f;

    /// <summary>The rows to show, top first; the detached local row, if any, is last.</summary>
    public IReadOnlyList<LeaderboardRow> Rows => rows;

    /// <summary>Every row being drawn, including rows fading out after leaving the board.</summary>
    public IReadOnlyList<LeaderboardMotion> Motions => motions;

    /// <summary>The number of top rows (not counting a detached local row).</summary>
    public int TopCount { get; private set; }

    public bool HasDetached => rows.Count > TopCount;

    /// <summary>The slot the detached local row slides to: below the top rows and the divider.</summary>
    public float DetachedSlot => TopCount + DividerGap;

    public LeaderboardMotion? Motion(int id) => byId.GetValueOrDefault(id);

    /// <summary>Forgets every row, so the next standings appear in place (after the board was hidden).</summary>
    public void Reset()
    {
        rows.Clear();
        motions.Clear();
        byId.Clear();
        TopCount = 0;
        started = false;
    }

    /// <summary>
    /// Ranks <paramref name="entries"/> (in roster order) by <paramref name="points"/>, keeps the top
    /// <paramref name="count"/> and the local player's row, and records rank and score changes since the last call.
    /// </summary>
    public void Update(IReadOnlyList<LeaderboardEntry> entries, IReadOnlyDictionary<int, int> points, int local, int count)
    {
        roster = entries;
        scores = points;
        order.Clear();
        for (var i = 0; i < roster.Count; i++)
        {
            order.Add(i);
        }
        order.Sort(compare);

        rows.Clear();
        TopCount = Mathf.Clamp(count, 0, order.Count);
        var rank = 0;
        var previous = int.MinValue;
        for (var i = 0; i < order.Count; i++)
        {
            var entry = roster[order[i]];
            var score = scores.GetValueOrDefault(entry.Id);
            if (i == 0 || score != previous)
            {
                rank = i + 1;
                previous = score;
            }
            var mine = entry.Id == local;
            if (i < TopCount || (mine && TopCount > 0))
            {
                rows.Add(new LeaderboardRow(rank, entry, score, mine, i >= TopCount));
            }
        }
        Track();
    }

    /// <summary>
    /// Moves every row toward its slot over about <paramref name="slideTime"/>, fades rows in and out, and ages the
    /// flash, the drop tint and the "+N" popups.
    /// </summary>
    public void Advance(float dt, float slideTime, float flashTime)
    {
        var slide = Mathf.Max(slideTime, 0.01f);
        // Exponential approach: within 1% of the slot after slideTime.
        var follow = 1f - Mathf.Exp(-4.6f * dt / slide);
        for (var i = motions.Count - 1; i >= 0; i--)
        {
            var motion = motions[i];
            var target = motion.Target = motion.Shown ? TargetSlot(motion.Id) : TopCount;
            motion.Slot = Mathf.Lerp(motion.Slot, target, follow);
            if (Mathf.Abs(motion.Slot - target) < 0.001f)
            {
                motion.Slot = target;
            }
            motion.Alpha = Mathf.MoveToward(motion.Alpha, motion.Shown ? 1f : 0f, dt / slide);
            motion.Flash = Mathf.MoveToward(motion.Flash, 0f, dt / Mathf.Max(flashTime, 0.01f));
            motion.Drop = Mathf.MoveToward(motion.Drop, 0f, dt / Mathf.Max(flashTime, 0.01f));
            motion.PopAge += dt;
            if (!motion.Shown && motion.Alpha <= 0f)
            {
                motions.RemoveAt(i);
                byId.Remove(motion.Id);
            }
        }
    }

    // Score descending; ties keep roster order, so the order is the same on every peer and never flickers.
    private int Compare(int a, int b)
    {
        var byScore = scores.GetValueOrDefault(roster[b].Id).CompareTo(scores.GetValueOrDefault(roster[a].Id));
        return byScore != 0 ? byScore : a.CompareTo(b);
    }

    private float TargetSlot(int id)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Entry.Id == id)
            {
                return rows[i].Detached ? DetachedSlot : i;
            }
        }
        return TopCount;
    }

    private void Track()
    {
        foreach (var motion in motions)
        {
            motion.Shown = false;
        }
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var slot = row.Detached ? DetachedSlot : i;
            if (!byId.TryGetValue(row.Entry.Id, out var motion))
            {
                motion = new LeaderboardMotion { Id = row.Entry.Id, Slot = started ? TopCount : slot, Alpha = started ? 0f : 1f, Rank = row.Rank, Score = row.Score };
                byId[row.Entry.Id] = motion;
                motions.Add(motion);
                motion.RankText = Text(row.Rank);
                motion.ScoreText = Text(row.Score);
            }
            if (row.Rank < motion.Rank)
            {
                motion.Flash = 1f;
            }
            if (row.Score > motion.Score)
            {
                motion.PopAmount = motion.PopAge < ChainWindow ? motion.PopAmount + row.Score - motion.Score : row.Score - motion.Score;
                motion.PopAge = 0f;
                motion.PopText = "+" + Text(motion.PopAmount);
            }
            else if (row.Score < motion.Score)
            {
                motion.Drop = 1f;
            }
            if (row.Rank != motion.Rank) motion.RankText = Text(row.Rank);
            if (row.Score != motion.Score) motion.ScoreText = Text(row.Score);
            motion.Rank = row.Rank;
            motion.Score = row.Score;
            motion.Row = row;
            motion.Shown = true;
        }
        // The first rows to show appear in place; later arrivals rise in.
        started |= rows.Count > 0;
    }

    private static string Text(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
