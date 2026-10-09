using System.Collections.Generic;
using Godot;
using Propane.Core;
using Propane.Player;

namespace Propane.UI;

/// <summary>A score floating over another player: where, what and in which colour.</summary>
public readonly record struct ScoreTag(Vector2 Screen, int Score, Color Color, float Scale);

/// <summary>
/// The match HUD, over the single-player one (crosshair and tank arrows): the match clock, the tanks you have
/// banked with "+N" as you score, ammo, a hit marker when your shot hits a player, arrows toward whoever shot you,
/// every other visible player's score over their head, and the countdown.
/// </summary>
public partial class MatchHud : CanvasLayer
{
    private static readonly Color Dark = new(0.12f, 0.13f, 0.15f);
    private static readonly Color Warn = new(0.85f, 0.2f, 0.15f);

    private Label timer = null!;
    private Label score = null!;
    private Label caption = null!;
    private Label popup = null!;
    private Label magazine = null!;
    private Label reserve = null!;
    private Label ammoNote = null!;
    private Label ammoPopup = null!;
    private Label countdown = null!;
    private Label notice = null!;
    private Overlay overlay = null!;
    private float scorePop;
    private float popupAge = 99f;
    private int popupAmount;
    private float ammoPopupAge = 99f;
    private float countdownAge = 99f;
    private string countdownText = "";
    private float reloadProgress;

    public override void _Ready()
    {
        Layer = 11;
        var root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(root);

        var top = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.Begin };
        top.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
        top.Position = new Vector2(-160, 14);
        top.Size = new Vector2(320, 190);
        top.AddThemeConstantOverride("separation", -10);
        root.AddChild(top);
        timer = HudLabel(38, Dark, 8);
        top.AddChild(timer);
        score = HudLabel(84, Dark, 10);
        score.PivotOffset = new Vector2(160, 50);
        top.AddChild(score);
        caption = HudLabel(19, new Color(Dark, 0.75f), 6);
        caption.Text = "TANKS BANKED";
        top.AddChild(caption);

        popup = HudLabel(44, UiStyle.Accent, 9);
        popup.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
        popup.Position = new Vector2(70, 60);
        popup.Size = new Vector2(200, 60);
        popup.HorizontalAlignment = HorizontalAlignment.Left;
        root.AddChild(popup);

        var ammo = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.End };
        ammo.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
        ammo.Position = new Vector2(-300, -170);
        ammo.Size = new Vector2(270, 150);
        ammo.AddThemeConstantOverride("separation", -6);
        root.AddChild(ammo);
        ammoPopup = HudLabel(24, new Color(0.25f, 0.55f, 0.2f), 6);
        ammoPopup.HorizontalAlignment = HorizontalAlignment.Right;
        ammo.AddChild(ammoPopup);
        ammoNote = HudLabel(19, Dark, 6);
        ammoNote.HorizontalAlignment = HorizontalAlignment.Right;
        ammo.AddChild(ammoNote);
        var counts = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.End };
        counts.AddThemeConstantOverride("separation", 10);
        ammo.AddChild(counts);
        magazine = HudLabel(64, Dark, 9);
        magazine.HorizontalAlignment = HorizontalAlignment.Right;
        counts.AddChild(magazine);
        reserve = HudLabel(30, new Color(Dark, 0.7f), 7);
        reserve.VerticalAlignment = VerticalAlignment.Bottom;
        reserve.SizeFlagsVertical = Control.SizeFlags.ShrinkEnd;
        counts.AddChild(reserve);

        countdown = HudLabel(150, Dark, 16);
        countdown.SetAnchorsPreset(Control.LayoutPreset.Center);
        countdown.Position = new Vector2(-300, -190);
        countdown.Size = new Vector2(600, 200);
        countdown.PivotOffset = new Vector2(300, 100);
        root.AddChild(countdown);

        notice = HudLabel(24, Dark, 7);
        notice.SetAnchorsPreset(Control.LayoutPreset.Center);
        notice.Position = new Vector2(-400, 60);
        notice.Size = new Vector2(800, 40);
        root.AddChild(notice);

        overlay = new Overlay { MouseFilter = Control.MouseFilterEnum.Ignore };
        overlay.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(overlay);
        SetScore(0, false);
        SetTimer(-1);
    }

    public override void _Process(double delta)
    {
        var dt = (float)delta;
        var tuning = Tuning.Current;
        scorePop = Mathf.MoveToward(scorePop, 0, dt * 3f);
        var s = 1f + scorePop * scorePop * 0.45f;
        score.Scale = new Vector2(s, s);

        popupAge += dt;
        var popupLife = Mathf.Max(tuning.ScorePopupTime, 0.1f);
        popup.Visible = popupAge < popupLife;
        popup.Modulate = new Color(1, 1, 1, 1f - Mathf.SmoothStep(0.6f, 1f, popupAge / popupLife));
        popup.Position = new Vector2(popup.Position.X, 60 - popupAge / popupLife * 26f);

        ammoPopupAge += dt;
        ammoPopup.Modulate = new Color(1, 1, 1, ammoPopupAge < 1.6f ? 1f - Mathf.SmoothStep(1f, 1.6f, ammoPopupAge) : 0f);

        countdownAge += dt;
        countdown.Text = countdownText;
        countdown.Visible = countdownAge < 1f && countdownText.Length > 0;
        var c = Mathf.Clamp(countdownAge, 0, 1);
        countdown.Scale = Vector2.One * (1.25f - 0.25f * Mathf.SmoothStep(0, 0.25f, c));
        countdown.Modulate = new Color(1, 1, 1, 1f - Mathf.SmoothStep(0.6f, 1f, c));

        overlay.ReloadProgress = reloadProgress;
        overlay.Tick(dt);
    }

    public void SetTimer(float seconds)
    {
        timer.Visible = seconds >= 0;
        if (seconds < 0)
        {
            return;
        }
        var whole = Mathf.CeilToInt(seconds);
        timer.Text = $"{whole / 60}:{whole % 60:00}";
        // The last ten seconds turn red.
        timer.AddThemeColorOverride("font_color", seconds <= 10.5f && seconds > 0 ? Warn : Dark);
    }

    public void SetScore(int banked, bool animate)
    {
        score.Text = banked.ToString();
        caption.Text = banked == 1 ? "TANK BANKED" : "TANKS BANKED";
        if (animate)
        {
            scorePop = 1f;
        }
    }

    /// <summary>"+N" by the score; points that keep coming (a chain) add to the one showing.</summary>
    public void AddScorePopup(int amount)
    {
        popupAmount = popupAge < Tuning.Current.ScorePopupTime * 0.7f ? popupAmount + amount : amount;
        popupAge = 0f;
        popup.Text = $"+{popupAmount}";
    }

    public void SetAmmo(AmmoState? ammo)
    {
        magazine.GetParent<Control>().GetParent<Control>().Visible = ammo != null;
        if (ammo == null)
        {
            return;
        }
        magazine.Text = ammo.Magazine.ToString();
        reserve.Text = $"/ {ammo.Reserve}";
        reloadProgress = ammo.Reloading ? ammo.ReloadProgress : 0f;
        var empty = ammo.Magazine == 0;
        magazine.AddThemeColorOverride("font_color", empty ? Warn : Dark);
        ammoNote.Text = ammo.Reloading ? "RELOADING" : empty && ammo.Reserve == 0 ? "NO AMMO" : empty ? "R TO RELOAD" : "";
        ammoNote.AddThemeColorOverride("font_color", ammo.Reloading ? Dark : Warn);
    }

    public void AddAmmoPopup(int rounds)
    {
        ammoPopup.Text = rounds > 0 ? $"+{rounds} AMMO" : "AMMO FULL";
        ammoPopupAge = 0f;
    }

    /// <summary>Shows a countdown step ("3", "2", "1", "GO").</summary>
    public void ShowCountdown(string text)
    {
        if (text == countdownText && countdownAge < 1f)
        {
            return;
        }
        countdownText = text;
        countdownAge = 0f;
    }

    public void SetNotice(string text) => notice.Text = text;

    public void ShowHitMarker() => overlay.HitMarker = Tuning.Current.HitMarkerTime;

    /// <summary>An arrow round the crosshair toward where a shot that hit you came from.</summary>
    public void AddHitDirection(Vector3 from) => overlay.Hits.Add((from, 0f));

    public void SetView(Camera3D camera, Vector3 player)
    {
        overlay.Camera = camera;
        overlay.Player = player;
    }

    public void SetTags(List<ScoreTag> tags) => overlay.Tags = tags;

    private static Label HudLabel(int size, Color color, int outline)
    {
        var label = new Label { HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
        label.AddThemeFontOverride("font", UiStyle.BoldFont);
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color);
        label.AddThemeColorOverride("font_outline_color", new Color(1, 1, 1, 0.85f));
        label.AddThemeConstantOverride("outline_size", outline);
        return label;
    }

    /// <summary>Everything drawn rather than laid out: the hit marker, incoming-shot arrows, score tags and the reload ring.</summary>
    private partial class Overlay : Control
    {
        public Camera3D? Camera;
        public Vector3 Player;
        public float HitMarker;
        public float ReloadProgress;
        public readonly List<(Vector3 From, float Age)> Hits = new();
        public List<ScoreTag> Tags = new();

        public void Tick(float dt)
        {
            HitMarker = Mathf.Max(0, HitMarker - dt);
            var life = Mathf.Max(Tuning.Current.HitDirectionTime, 0.05f);
            for (var i = Hits.Count - 1; i >= 0; i--)
            {
                var (from, age) = Hits[i];
                if (age + dt >= life)
                {
                    Hits.RemoveAt(i);
                }
                else
                {
                    Hits[i] = (from, age + dt);
                }
            }
            QueueRedraw();
        }

        public override void _Draw()
        {
            var center = Size / 2f;
            var light = new Color(1, 1, 1, 0.95f);
            var dark = new Color(0.05f, 0.06f, 0.08f, 0.6f);
            var tuning = Tuning.Current;

            foreach (var tag in Tags)
            {
                var font = UiStyle.BoldFont;
                var size = Mathf.RoundToInt(34 * tag.Scale);
                var text = tag.Score.ToString();
                var width = font.GetStringSize(text, HorizontalAlignment.Left, -1, size).X;
                var at = tag.Screen - new Vector2(width / 2f, 0);
                DrawStringOutline(font, at, text, HorizontalAlignment.Left, -1, size, Mathf.Max(4, size / 5), new Color(0.05f, 0.06f, 0.08f, 0.85f));
                DrawString(font, at, text, HorizontalAlignment.Left, -1, size, tag.Color);
            }

            if (HitMarker > 0)
            {
                var a = Mathf.Clamp(HitMarker / Mathf.Max(tuning.HitMarkerTime, 0.01f), 0, 1);
                var hit = new Color(1, 1, 1, a);
                foreach (var dir in new[] { new Vector2(1, 1), new Vector2(-1, 1), new Vector2(1, -1), new Vector2(-1, -1) })
                {
                    var n = dir.Normalized();
                    DrawLine(center + n * 9f, center + n * 19f, new Color(dark, a * 0.6f), 5f, true);
                    DrawLine(center + n * 9f, center + n * 19f, hit, 2.5f, true);
                }
            }

            if (ReloadProgress > 0)
            {
                DrawArc(center, 28f, -Mathf.Pi / 2, -Mathf.Pi / 2 + Mathf.Tau, 48, new Color(dark, 0.35f), 4f, true);
                DrawArc(center, 28f, -Mathf.Pi / 2, -Mathf.Pi / 2 + Mathf.Tau * ReloadProgress, 48, light, 3f, true);
            }

            if (Camera != null && IsInstanceValid(Camera))
            {
                var life = Mathf.Max(tuning.HitDirectionTime, 0.05f);
                var basis = Camera.GlobalBasis;
                foreach (var (from, age) in Hits)
                {
                    // Direction to the shooter on the ground plane, relative to where the camera faces.
                    var to = from - Player;
                    var right = basis.X;
                    var forward = -basis.Z;
                    var x = to.Dot(new Vector3(right.X, 0, right.Z).Normalized());
                    var y = to.Dot(new Vector3(forward.X, 0, forward.Z).Normalized());
                    var dir = new Vector2(x, -y);
                    if (dir.LengthSquared() < 0.0001f)
                    {
                        continue;
                    }
                    dir = dir.Normalized();
                    var alpha = 1f - Mathf.SmoothStep(0.5f, 1f, age / life);
                    var side = new Vector2(-dir.Y, dir.X);
                    var tip = center + dir * 118f;
                    var points = new[] { tip, center + dir * 88f + side * 26f, center + dir * 96f, center + dir * 88f - side * 26f };
                    DrawColoredPolygon(points, new Color(0.85f, 0.15f, 0.12f, 0.75f * alpha));
                    DrawPolyline(new[] { points[0], points[1], points[2], points[3], points[0] }, new Color(1, 1, 1, 0.8f * alpha), 1.5f, true);
                }
            }
        }
    }
}
