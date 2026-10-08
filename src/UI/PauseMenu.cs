using Godot;

namespace Propane.UI;

/// <summary>Esc pauses the game: controls reference, resume and quit.</summary>
public partial class PauseMenu : CanvasLayer
{
    public event System.Action? ResumeRequested;

    public override void _Ready()
    {
        Layer = 30;
        ProcessMode = ProcessModeEnum.Always;
        Visible = false;

        var shade = new ColorRect { Color = new Color(0.95f, 0.95f, 0.96f, 0.55f) };
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
        layout.AddChild(UiStyle.Text("Paused", 18, UiStyle.SoftInk, HorizontalAlignment.Center));
        layout.AddChild(new HSeparator());
        foreach (var (keys, action) in new[]
                 {
                     ("W A S D", "Move"), ("Shift", "Sprint"), ("Space", "Jump"), ("Mouse", "Look"), ("Left click", "Fire"),
                     ("Right click", "Aim (tighter spread)"), ("R", "New suburb"), ("F1  or  `", "Tuning panel"), ("F11  or  Alt+Enter", "Fullscreen"),
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
        layout.AddChild(UiStyle.Text("One shot punctures a tank. A second sets it off.", 16, UiStyle.SoftInk, HorizontalAlignment.Center));
        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        buttons.AddThemeConstantOverride("separation", 10);
        var resume = UiStyle.Button("Resume");
        resume.Pressed += () => ResumeRequested?.Invoke();
        var quit = UiStyle.Button("Quit");
        quit.Pressed += () => GetTree().Quit();
        buttons.AddChild(resume);
        buttons.AddChild(quit);
        layout.AddChild(buttons);
    }
}
