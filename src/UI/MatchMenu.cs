using Godot;

namespace Propane.UI;

/// <summary>Esc in a match: the controls, and resume, leave or quit. The match keeps running behind it.</summary>
public partial class MatchMenu : CanvasLayer
{
    public event System.Action? ResumeRequested;

    public event System.Action? LeaveRequested;

    public override void _Ready()
    {
        Layer = 30;
        Visible = false;

        var shade = new ColorRect { Color = new Color(0.95f, 0.95f, 0.96f, 0.35f) };
        shade.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        shade.GuiInput += e =>
        {
            if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
            {
                ResumeRequested?.Invoke();
            }
        };
        AddChild(shade);

        var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(center);
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", UiStyle.Panel(0.96f));
        center.AddChild(panel);
        var layout = new VBoxContainer { CustomMinimumSize = new Vector2(460, 0) };
        layout.AddThemeConstantOverride("separation", 10);
        panel.AddChild(layout);

        var title = UiStyle.Text("PROPANE", 46, UiStyle.Ink, HorizontalAlignment.Center);
        title.AddThemeFontOverride("font", UiStyle.BoldFont);
        layout.AddChild(title);
        layout.AddChild(UiStyle.Text("The match is still on", 18, UiStyle.SoftInk, HorizontalAlignment.Center));
        layout.AddChild(new HSeparator());
        foreach (var (keys, action) in new[]
                 {
                     ("W A S D", "Move"), ("Shift", "Sprint"), ("Space", "Jump"), ("Mouse", "Look"), ("Left click", "Fire"),
                     ("Right click", "Aim (tighter spread)"), ("R", "Reload"), ("F11  or  Alt+Enter", "Fullscreen"),
                 })
        {
            var row = new HBoxContainer();
            var key = UiStyle.Text(keys, 19);
            key.AddThemeFontOverride("font", UiStyle.BoldFont);
            key.CustomMinimumSize = new Vector2(210, 0);
            row.AddChild(key);
            row.AddChild(UiStyle.Text(action, 19, UiStyle.SoftInk));
            layout.AddChild(row);
        }
        layout.AddChild(new HSeparator());
        var rules = UiStyle.Text("Every tank you blow up is banked. A bullet hit spills one behind you; getting thrown spills five.", 16,
            UiStyle.SoftInk, HorizontalAlignment.Center);
        rules.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        layout.AddChild(rules);
        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        buttons.AddThemeConstantOverride("separation", 10);
        var resume = UiStyle.Button("Resume");
        resume.Pressed += () => ResumeRequested?.Invoke();
        var leave = UiStyle.Button("Leave match");
        leave.Pressed += () => LeaveRequested?.Invoke();
        var quit = UiStyle.Button("Quit");
        quit.Pressed += () => GetTree().Quit();
        buttons.AddChild(resume);
        buttons.AddChild(leave);
        buttons.AddChild(quit);
        layout.AddChild(buttons);
    }
}
