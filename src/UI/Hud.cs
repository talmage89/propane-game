using System.Collections.Generic;
using Godot;

namespace Propane.UI;

/// <summary>
/// The whole HUD: how many tanks are left, a crosshair whose marks show the current spread, and small arrows toward
/// every group of tanks.
/// </summary>
public partial class Hud : CanvasLayer
{
    private Label count = null!;
    private Label caption = null!;
    private Crosshair crosshair = null!;
    private TankArrows arrows = null!;
    private float pop;

    public override void _Ready()
    {
        Layer = 10;
        var root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(root);

        var top = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.Center };
        top.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
        top.Position = new Vector2(-120, 22);
        top.Size = new Vector2(240, 150);
        top.AddThemeConstantOverride("separation", -8);
        root.AddChild(top);

        count = new Label { HorizontalAlignment = HorizontalAlignment.Center, PivotOffset = new Vector2(120, 50), Size = new Vector2(240, 100) };
        count.AddThemeFontOverride("font", UiStyle.BoldFont);
        count.AddThemeFontSizeOverride("font_size", 84);
        count.AddThemeColorOverride("font_color", new Color(0.12f, 0.13f, 0.15f));
        count.AddThemeColorOverride("font_outline_color", new Color(1, 1, 1, 0.85f));
        count.AddThemeConstantOverride("outline_size", 10);
        top.AddChild(count);

        caption = new Label { Text = "TANKS LEFT", HorizontalAlignment = HorizontalAlignment.Center };
        caption.AddThemeFontOverride("font", UiStyle.BoldFont);
        caption.AddThemeFontSizeOverride("font_size", 19);
        caption.AddThemeColorOverride("font_color", new Color(0.12f, 0.13f, 0.15f, 0.75f));
        caption.AddThemeColorOverride("font_outline_color", new Color(1, 1, 1, 0.7f));
        caption.AddThemeConstantOverride("outline_size", 6);
        top.AddChild(caption);

        arrows = new TankArrows { MouseFilter = Control.MouseFilterEnum.Ignore };
        arrows.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(arrows);

        crosshair = new Crosshair { MouseFilter = Control.MouseFilterEnum.Ignore };
        crosshair.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(crosshair);
    }

    public override void _Process(double delta)
    {
        pop = Mathf.MoveToward(pop, 0, (float)delta * 3f);
        var s = 1f + pop * pop * 0.45f;
        count.Scale = new Vector2(s, s);
    }

    public void SetTanks(int remaining, bool animate)
    {
        count.Text = remaining.ToString();
        caption.Text = remaining == 1 ? "TANK LEFT" : "TANKS LEFT";
        if (animate)
        {
            pop = 1f;
        }
    }

    /// <summary>Crosshair spread as a screen radius in pixels; hidden while ragdolled.</summary>
    public void SetCrosshair(float radiusPixels, bool visible, float aim)
    {
        crosshair.Radius = radiusPixels;
        crosshair.Aim = aim;
        crosshair.Visible = visible;
        crosshair.QueueRedraw();
    }

    /// <summary>Points the edge arrows at these groups of tanks (world positions), or hides them.</summary>
    public void SetTankArrows(Camera3D camera, IReadOnlyList<Vector3> groups, bool visible)
    {
        arrows.Camera = camera;
        arrows.Groups = groups;
        arrows.Visible = visible;
        arrows.QueueRedraw();
    }

    /// <summary>Shows or hides the tanks-left count (a match shows the banked score instead).</summary>
    public void ShowTankCount(bool visible)
    {
        count.Visible = visible;
        caption.Visible = visible;
    }

    public void ShowHud(bool visible)
    {
        Visible = visible;
    }
}

/// <summary>A dot with four marks set out at the spread radius, drawn with a dark outline for any background.</summary>
public partial class Crosshair : Control
{
    public float Radius { get; set; } = 20f;

    public float Aim { get; set; }

    public override void _Draw()
    {
        var center = Size / 2f;
        var radius = Mathf.Max(Radius, 4f);
        var light = new Color(1, 1, 1, 0.9f);
        var dark = new Color(0.05f, 0.06f, 0.08f, 0.55f);
        DrawCircle(center, 3.2f, dark);
        DrawCircle(center, 2f, light);
        // Marks tighten in when aiming.
        var tick = Mathf.Lerp(8f, 5f, Aim);
        foreach (var dir in new[] { Vector2.Up, Vector2.Down, Vector2.Left, Vector2.Right })
        {
            var a = center + dir * radius;
            var b = center + dir * (radius + tick);
            DrawLine(a, b, dark, 3.6f, true);
            DrawLine(a, b, light, 1.7f, true);
        }
    }
}

/// <summary>
/// Small arrows toward every group of tanks: on the screen edge when the group is out of view, hovering over it when
/// in view. Faint when far, solid when close.
/// </summary>
public partial class TankArrows : Control
{
    private const float Inset = 34f;
    private const float ArrowLength = 13f;
    private const float ArrowWidth = 9f;
    private const float OnScreenGap = 6f;
    private const float HoverHeight = 0.6f;
    private const float MinOpacity = 0.3f;
    private const float NearDistance = 8f;
    private const float FarDistance = 110f;

    public Camera3D? Camera { get; set; }

    public IReadOnlyList<Vector3> Groups { get; set; } = System.Array.Empty<Vector3>();

    public override void _Draw()
    {
        if (Camera == null || !IsInstanceValid(Camera))
        {
            return;
        }
        var center = Size / 2f;
        var half = center - new Vector2(Inset, Inset);
        var toCamera = Camera.GlobalTransform.AffineInverse();
        // Pixels per unit of camera-space slope, from the vertical field of view.
        var focal = center.Y / Mathf.Tan(Mathf.DegToRad(Camera.Fov) / 2f);
        foreach (var group in Groups)
        {
            var local = toCamera * group;
            Vector2 tip;
            Vector2 dir;
            // In view, the arrow hovers over a point clear above the tanks' tops.
            var above = toCamera * (group + Vector3.Up * HoverHeight);
            var screen = center + new Vector2(above.X, -above.Y) / Mathf.Max(-above.Z, 0.001f) * focal;
            if (local.Z < -0.1f && Mathf.Abs(screen.X - center.X) < half.X && Mathf.Abs(screen.Y - center.Y) < half.Y)
            {
                // In view: hover just above the group, pointing down at it.
                dir = Vector2.Down;
                tip = screen - new Vector2(0, OnScreenGap);
            }
            else
            {
                // Out of view: on the edge, pointing toward it. The camera-space offset gives the right direction even
                // for groups behind the camera.
                dir = new Vector2(local.X, -local.Y);
                if (local.Z > 0 && dir.LengthSquared() < 0.01f)
                {
                    dir = Vector2.Down;
                }
                dir = dir.Normalized();
                var reach = Mathf.Min(half.X / Mathf.Max(Mathf.Abs(dir.X), 0.0001f), half.Y / Mathf.Max(Mathf.Abs(dir.Y), 0.0001f));
                tip = center + dir * reach;
            }
            // Faint when far, solid when close.
            var closeness = 1f - Mathf.SmoothStep(NearDistance, FarDistance, local.Length());
            var alpha = Mathf.Lerp(MinOpacity, 1f, closeness);
            var side = new Vector2(-dir.Y, dir.X) * ArrowWidth / 2f;
            var points = new[] { tip, tip - dir * ArrowLength + side, tip - dir * ArrowLength * 0.62f, tip - dir * ArrowLength - side };
            DrawColoredPolygon(points, new Color(0.05f, 0.06f, 0.08f, alpha * 0.45f));
            DrawPolyline(new[] { points[0], points[1], points[2], points[3], points[0] }, new Color(1, 1, 1, alpha), 1.6f, true);
        }
    }
}
