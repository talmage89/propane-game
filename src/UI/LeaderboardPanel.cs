using System;
using System.Collections.Generic;
using Godot;
using Propane.Core;

namespace Propane.UI;

/// <summary>
/// The match HUD's live standings in the top-left corner: rank, color, name and score for the top rows, the local
/// player's row highlighted (below a divider when outside them), rows sliding to a new rank, a climb flashing and a
/// gain popping "+N" beside the score. Drawn, not laid out, so sixteen players re-ranking cost no node churn. The top
/// rows draw clipped to their section, so a row leaving slides out under the divider while its replacement slides in;
/// the local row and the popups draw unclipped above them.
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
    private Layer top = null!;
    private Layer front = null!;
    private string caption = "";
    private int local;
    private float height;
    private float fade;
    private bool hiding = true;

    public Leaderboard Board => board;

    /// <summary>The panel's drawn rectangle, in the HUD's coordinates (the "+N" popups sit just right of it).</summary>
    public Rect2 Bounds => new(Position, new Vector2(Width, height));

    public override void _Ready()
    {
        Visible = false;
        top = new Layer { Paint = DrawTop, ClipContents = true, MouseFilter = MouseFilterEnum.Ignore, Position = new Vector2(0, HeaderHeight) };
        AddChild(top);
        front = new Layer { Paint = DrawFront, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(front);
    }

    /// <summary>Ranks the standings; <paramref name="rows"/> 0 fades the whole panel out, and it returns fresh.</summary>
    public void Set(IReadOnlyList<LeaderboardEntry> roster, IReadOnlyDictionary<int, int> scores, int localActor, int rows, string column)
    {
        hiding = rows <= 0;
        if (hiding)
        {
            return;
        }
        if (fade <= 0)
        {
            board.Reset();
            height = 0;
        }
        caption = column.ToUpperInvariant();
        local = localActor;
        board.ChainWindow = Tuning.ScorePopupTime * 0.7f;
        board.Update(roster, scores, localActor, rows);
    }

    public override void _Process(double delta)
    {
        var dt = (float)delta;
        var slide = Mathf.Max(Tuning.LeaderboardSlideTime, 0.01f);
        fade = Mathf.MoveToward(fade, hiding ? 0f : 1f, dt / slide);
        Visible = fade > 0 && board.TopCount > 0;
        if (!Visible)
        {
            return;
        }
        board.Advance(dt, slide, Tuning.LeaderboardFlashTime);
        var target = HeaderHeight + RowHeight * (board.HasDetached ? board.DetachedSlot + 1 : board.TopCount) + Pad;
        height = height <= 0 ? target : Mathf.Lerp(height, target, 1f - Mathf.Exp(-4.6f * dt / slide));
        Position = new Vector2(Tuning.LeaderboardMarginX, Tuning.LeaderboardMarginY);
        Modulate = new Color(1, 1, 1, fade);
        top.Size = new Vector2(Width, RowHeight * board.TopCount);
        QueueRedraw();
        top.QueueRedraw();
        front.QueueRedraw();
    }

    public override void _Draw()
    {
        DrawStyleBox(Background, new Rect2(0, 0, Width, height));
        var font = UiStyle.BoldFont;
        DrawString(font, new Vector2(Pad, Pad + Baseline(font, CaptionSize, HeaderHeight - Pad)), caption, HorizontalAlignment.Left, Width - Pad * 2, CaptionSize,
            UiStyle.SoftInk);
        if (board.HasDetached)
        {
            var y = HeaderHeight + RowHeight * (board.TopCount + Leaderboard.DividerGap / 2f);
            DrawLine(new Vector2(Pad, y), new Vector2(Width - Pad, y), new Color(UiStyle.Ink, 0.25f), 2f);
        }
    }

    // The other players' rows, clipped to the top section: rows leaving first, then the rest, then climbers over them.
    private void DrawTop(Layer layer)
    {
        var font = UiStyle.BoldFont;
        for (var pass = 0; pass < 3; pass++)
        {
            foreach (var motion in board.Motions)
            {
                if (motion.Id != local && pass == (!motion.Shown ? 0 : motion.Slot <= motion.Target ? 1 : 2))
                {
                    DrawRow(layer, font, motion, 0f);
                }
            }
        }
    }

    // The local row, which crosses the divider, and every "+N".
    private void DrawFront(Layer layer)
    {
        var font = UiStyle.BoldFont;
        if (board.Motion(local) is { Shown: true } mine)
        {
            DrawRow(layer, font, mine, HeaderHeight);
        }
        var popLife = Mathf.Max(Tuning.ScorePopupTime, 0.1f);
        foreach (var motion in board.Motions)
        {
            if (!motion.Shown || motion.PopAge >= popLife || motion.PopAmount <= 0)
            {
                continue;
            }
            // Beside the score, in the HUD popup's accent and white outline, rising as it fades; a row still rising
            // into the top section pops at its last slot.
            var slot = motion.Id == local ? motion.Slot : Mathf.Min(motion.Slot, board.TopCount - 1);
            var t = motion.PopAge / popLife;
            var at = new Vector2(Width + 8, HeaderHeight + RowHeight * slot + Baseline(font, PopupSize, RowHeight) - t * 12f);
            var alpha = 1f - Mathf.SmoothStep(0.6f, 1f, t);
            layer.DrawStringOutline(font, at, motion.PopText, HorizontalAlignment.Left, -1, PopupSize, 7, new Color(1, 1, 1, 0.85f * alpha));
            layer.DrawString(font, at, motion.PopText, HorizontalAlignment.Left, -1, PopupSize, new Color(UiStyle.Accent, alpha));
        }
    }

    private void DrawRow(Layer layer, Font font, LeaderboardMotion motion, float offset)
    {
        var row = motion.Row;
        var top = offset + RowHeight * motion.Slot;
        var a = motion.Alpha * (row.Entry.Present ? 1f : 0.5f);
        var band = new Rect2(4, top + 2, Width - 8, RowHeight - 4);
        // Opaque under a moving row, so it hides what it passes, and under the local row, which others pass beneath.
        if (motion.Shown && (row.Local || motion.Slot != motion.Target))
        {
            layer.DrawRect(band, new Color(Background.BgColor, motion.Alpha));
        }
        if (row.Local)
        {
            layer.DrawRect(band, new Color(UiStyle.Accent, 0.2f * motion.Alpha));
            layer.DrawRect(new Rect2(band.Position, new Vector2(4, band.Size.Y)), new Color(UiStyle.Accent, motion.Alpha));
        }
        if (motion.Flash > 0)
        {
            layer.DrawRect(band, new Color(UiStyle.Accent, 0.35f * motion.Flash * motion.Flash * motion.Alpha));
        }

        var x = Pad;
        layer.DrawString(font, new Vector2(x, top + Baseline(font, RankSize, RowHeight)), motion.RankText, HorizontalAlignment.Right, RankWidth, RankSize,
            new Color(UiStyle.SoftInk, UiStyle.SoftInk.A * a));
        x += RankWidth + 10;
        var swatch = new Rect2(x, top + (RowHeight - SwatchSize) / 2f, SwatchSize, SwatchSize);
        layer.DrawRect(swatch.Grow(1), new Color(UiStyle.Ink, a));
        layer.DrawRect(swatch, new Color(row.Entry.Color, a));
        x += SwatchSize + 10;
        var nameWidth = Width - Pad - ScoreWidth - x;
        layer.DrawString(font, new Vector2(x, top + Baseline(font, NameSize, RowHeight)), Fit(font, row.Entry, nameWidth), HorizontalAlignment.Left, nameWidth,
            NameSize, new Color(UiStyle.Ink, a));

        var pop = Mathf.Clamp(1f - motion.PopAge / 0.35f, 0, 1);
        var size = Mathf.RoundToInt(ScoreSize * (1f + pop * pop * 0.3f));
        layer.DrawString(font, new Vector2(Width - Pad - ScoreWidth, top + Baseline(font, size, RowHeight)), motion.ScoreText, HorizontalAlignment.Right, ScoreWidth,
            size, new Color(UiStyle.Ink.Lerp(Warn, motion.Drop), a));
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

    /// <summary>A drawing surface for one part of the panel.</summary>
    private sealed partial class Layer : Control
    {
        public Action<Layer>? Paint;

        public override void _Draw() => Paint?.Invoke(this);
    }
}
