using System.Collections.Generic;
using Godot;
using Propane.Core;

namespace Propane.UI;

/// <summary>
/// The match HUD's live standings in the top-left corner: rank, color, name and score for the top rows, the local
/// player's row highlighted (below a divider when outside them), rows sliding to a new rank, a climb flashing and a
/// gain popping "+N" beside the score. Drawn, not laid out, so sixteen players re-ranking cost no node churn.
/// </summary>
public partial class LeaderboardPanel : Control
{
    public required WorldContext World { get; init; }

    private Tuning Tuning => World.Tuning;

    private const float Width = 316f;
    private const float Pad = 12f;
    private const float HeaderHeight = 26f;
    private const float RowHeight = 36f;
    private const int RankSize = 20;
    private const int NameSize = 21;
    private const int ScoreSize = 24;
    private const int CaptionSize = 16;
    private const int PopupSize = 22;
    private const float RankWidth = 30f;
    private const float SwatchSize = 16f;
    private const float ScoreWidth = 64f;

    private static readonly Color Warn = new(0.85f, 0.2f, 0.15f);
    private static readonly StyleBoxFlat Background = new()
    {
        BgColor = new Color(0.97f, 0.97f, 0.98f, 0.8f),
        CornerRadiusTopLeft = 10,
        CornerRadiusTopRight = 10,
        CornerRadiusBottomLeft = 10,
        CornerRadiusBottomRight = 10,
        ShadowColor = new Color(0, 0, 0, 0.12f),
        ShadowSize = 10,
    };

    private readonly Leaderboard board = new();
    private readonly Dictionary<int, (string Name, string Fitted)> fitted = new();
    private string caption = "";
    private float height;

    public Leaderboard Board => board;

    /// <summary>The panel's drawn rectangle, in the HUD's coordinates (the "+N" popups sit just right of it).</summary>
    public Rect2 Bounds => new(Position, new Vector2(Width, height));

    /// <summary>Ranks the standings; <paramref name="rows"/> 0 fades the panel out.</summary>
    public void Set(IReadOnlyList<LeaderboardEntry> roster, IReadOnlyDictionary<int, int> scores, int local, int rows, string column)
    {
        caption = column.ToUpperInvariant();
        board.ChainWindow = Tuning.ScorePopupTime * 0.7f;
        board.Update(roster, scores, local, rows);
    }

    public override void _Process(double delta)
    {
        var dt = (float)delta;
        board.Advance(dt, Tuning.LeaderboardSlideTime, Tuning.LeaderboardFlashTime);
        var target = board.TopCount == 0 ? 0f : HeaderHeight + RowHeight * (board.HasDetached ? board.DetachedSlot + 1 : board.TopCount) + Pad;
        height = height <= 0 ? target : Mathf.Lerp(height, target, 1f - Mathf.Exp(-4.6f * dt / Mathf.Max(Tuning.LeaderboardSlideTime, 0.01f)));
        Position = new Vector2(Tuning.LeaderboardMarginX, Tuning.LeaderboardMarginY);
        Visible = board.Motions.Count > 0;
        if (Visible)
        {
            QueueRedraw();
        }
        else
        {
            height = 0;
        }
    }

    public override void _Draw()
    {
        var alpha = 0f;
        foreach (var motion in board.Motions)
        {
            alpha = Mathf.Max(alpha, motion.Alpha);
        }
        var panel = new Rect2(0, 0, Width, Mathf.Max(height, HeaderHeight + Pad));
        DrawStyleBox(Background, panel);
        var font = UiStyle.BoldFont;
        DrawString(font, new Vector2(Pad, Pad + Baseline(font, CaptionSize, HeaderHeight - Pad)), caption, HorizontalAlignment.Left, Width - Pad * 2, CaptionSize,
            new Color(UiStyle.SoftInk, UiStyle.SoftInk.A * alpha));

        if (board.HasDetached)
        {
            var y = HeaderHeight + RowHeight * (board.TopCount + Leaderboard.DividerGap / 2f);
            DrawLine(new Vector2(Pad, y), new Vector2(Width - Pad, y), new Color(UiStyle.Ink, 0.25f * alpha), 2f);
        }

        // Rows leaving the board fade under the rest; rows climbing past others draw last, over an opaque backing,
        // so a slide never mixes two rows' text.
        for (var pass = 0; pass < 3; pass++)
        {
            foreach (var motion in board.Motions)
            {
                if (pass == (!motion.Shown ? 0 : motion.Slot <= motion.Target ? 1 : 2)) DrawRow(font, motion);
            }
        }
    }

    private void DrawRow(Font font, LeaderboardMotion motion)
    {
        var row = motion.Row;
        var top = HeaderHeight + RowHeight * motion.Slot;
        var a = motion.Alpha * (row.Entry.Present ? 1f : 0.5f);
        var band = new Rect2(4, top + 2, Width - 8, RowHeight - 4);
        if (motion.Shown && motion.Slot != motion.Target)
        {
            DrawRect(band, new Color(Background.BgColor, motion.Alpha));
        }
        if (row.Local)
        {
            DrawRect(band, new Color(UiStyle.Accent, 0.2f * motion.Alpha));
            DrawRect(new Rect2(band.Position, new Vector2(4, band.Size.Y)), new Color(UiStyle.Accent, motion.Alpha));
        }
        if (motion.Flash > 0)
        {
            DrawRect(band, new Color(UiStyle.Accent, 0.35f * motion.Flash * motion.Flash * motion.Alpha));
        }

        var x = Pad;
        DrawString(font, new Vector2(x, top + Baseline(font, RankSize, RowHeight)), motion.RankText,
            HorizontalAlignment.Right, RankWidth, RankSize, new Color(UiStyle.SoftInk, UiStyle.SoftInk.A * a));
        x += RankWidth + 10;
        var swatch = new Rect2(x, top + (RowHeight - SwatchSize) / 2f, SwatchSize, SwatchSize);
        DrawRect(swatch.Grow(1), new Color(UiStyle.Ink, a));
        DrawRect(swatch, new Color(row.Entry.Color, a));
        x += SwatchSize + 10;
        var nameWidth = Width - Pad - ScoreWidth - x;
        DrawString(font, new Vector2(x, top + Baseline(font, NameSize, RowHeight)), Fit(font, row.Entry, nameWidth), HorizontalAlignment.Left, nameWidth, NameSize,
            new Color(UiStyle.Ink, a));

        var scoreColor = UiStyle.Ink.Lerp(Warn, motion.Drop);
        var popLife = Mathf.Max(Tuning.ScorePopupTime, 0.1f);
        var pop = Mathf.Clamp(1f - motion.PopAge / 0.35f, 0, 1);
        var size = Mathf.RoundToInt(ScoreSize * (1f + pop * pop * 0.3f));
        DrawString(font, new Vector2(Width - Pad - ScoreWidth, top + Baseline(font, size, RowHeight)), motion.ScoreText, HorizontalAlignment.Right, ScoreWidth, size,
            new Color(scoreColor, a));

        if (motion.PopAge < popLife && motion.PopAmount > 0)
        {
            // "+N" outside the panel, in the HUD popup's accent and white outline, rising as it fades.
            var t = motion.PopAge / popLife;
            var at = new Vector2(Width + 8, top + Baseline(font, PopupSize, RowHeight) - t * 12f);
            var fade = (1f - Mathf.SmoothStep(0.6f, 1f, t)) * motion.Alpha;
            DrawStringOutline(font, at, motion.PopText, HorizontalAlignment.Left, -1, PopupSize, 7, new Color(1, 1, 1, 0.85f * fade));
            DrawString(font, at, motion.PopText, HorizontalAlignment.Left, -1, PopupSize, new Color(UiStyle.Accent, fade));
        }
    }

    // The baseline that centers a line of this size in a box of this height.
    private static float Baseline(Font font, int size, float box) => (box + font.GetAscent(size) - font.GetDescent(size)) / 2f;

    // A name trimmed with an ellipsis to fit, cached until the name changes.
    private string Fit(Font font, LeaderboardEntry entry, float width)
    {
        if (fitted.TryGetValue(entry.Id, out var cached) && cached.Name == entry.Name)
        {
            return cached.Fitted;
        }
        var text = entry.Name;
        if (font.GetStringSize(text, HorizontalAlignment.Left, -1, NameSize).X > width)
        {
            while (text.Length > 1 && font.GetStringSize(text + "…", HorizontalAlignment.Left, -1, NameSize).X > width)
            {
                text = text[..^1];
            }
            text = text.TrimEnd() + "…";
        }
        fitted[entry.Id] = (entry.Name, text);
        return text;
    }
}
