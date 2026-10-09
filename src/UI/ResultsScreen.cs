using System.Collections.Generic;
using Godot;
using Propane.Net;

namespace Propane.UI;

/// <summary>The final standings when a match ends, shown until everyone goes back to the lobby.</summary>
public partial class ResultsScreen : CanvasLayer
{
    private VBoxContainer rows = null!;
    private Label title = null!;
    private Label footer = null!;
    private float left;

    public override void _Ready()
    {
        Layer = 25;
        Visible = false;
        var shade = new ColorRect { Color = new Color(0.95f, 0.95f, 0.96f, 0.45f), MouseFilter = Control.MouseFilterEnum.Ignore };
        shade.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(shade);
        var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(center);
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", UiStyle.Panel(0.96f));
        center.AddChild(panel);
        var layout = new VBoxContainer { CustomMinimumSize = new Vector2(520, 0) };
        layout.AddThemeConstantOverride("separation", 10);
        panel.AddChild(layout);
        title = UiStyle.Text("", 44, UiStyle.Ink, HorizontalAlignment.Center);
        title.AddThemeFontOverride("font", UiStyle.BoldFont);
        layout.AddChild(title);
        layout.AddChild(UiStyle.Text("Tanks banked", 18, UiStyle.SoftInk, HorizontalAlignment.Center));
        layout.AddChild(new HSeparator());
        rows = new VBoxContainer();
        rows.AddThemeConstantOverride("separation", 6);
        layout.AddChild(rows);
        layout.AddChild(new HSeparator());
        footer = UiStyle.Text("", 16, UiStyle.SoftInk, HorizontalAlignment.Center);
        layout.AddChild(footer);
    }

    public override void _Process(double delta)
    {
        if (!Visible)
        {
            return;
        }
        left = Mathf.Max(0, left - (float)delta);
        footer.Text = $"Back to the lobby in {Mathf.CeilToInt(left)}";
    }

    public void Show(IReadOnlyList<Standing> standings, int me, float seconds)
    {
        foreach (var child in rows.GetChildren())
        {
            child.QueueFree();
        }
        var best = standings.Count > 0 ? standings[0].Score : 0;
        var winners = 0;
        foreach (var s in standings)
        {
            winners += s.Score == best ? 1 : 0;
        }
        var meWon = standings.Count > 0 && System.Linq.Enumerable.FirstOrDefault(standings, s => s.Id == me)?.Score == best;
        title.Text = standings.Count == 0 ? "MATCH OVER" : meWon ? winners > 1 ? "TIED FOR FIRST" : "YOU WIN" : "MATCH OVER";

        var rank = 0;
        var previous = int.MinValue;
        for (var i = 0; i < standings.Count; i++)
        {
            var s = standings[i];
            if (s.Score != previous)
            {
                rank = i + 1;
                previous = s.Score;
            }
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 12);
            var place = UiStyle.Text($"{rank}", 24, UiStyle.SoftInk);
            place.CustomMinimumSize = new Vector2(32, 0);
            row.AddChild(place);
            row.AddChild(new ColorRect { Color = Protocol.PlayerColor(s.Color), CustomMinimumSize = new Vector2(18, 18), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter });
            var name = UiStyle.Text(s.Name + (s.Id == me ? "  (you)" : "") + (s.Present ? "" : "  (left)"), 24, s.Present ? UiStyle.Ink : UiStyle.SoftInk);
            name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            if (s.Score == best)
            {
                name.AddThemeFontOverride("font", UiStyle.BoldFont);
            }
            row.AddChild(name);
            var points = UiStyle.Text(s.Score.ToString(), 28, UiStyle.Ink, HorizontalAlignment.Right);
            points.AddThemeFontOverride("font", UiStyle.BoldFont);
            row.AddChild(points);
            rows.AddChild(row);
        }
        left = seconds;
        Visible = true;
    }
}
