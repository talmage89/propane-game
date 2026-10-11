using Godot;

namespace Propane.UI;

/// <summary>Esc or Start/Options pauses the game: controls reference, resume and quit.</summary>
public partial class PauseMenu : CanvasLayer
{
    public event System.Action? ResumeRequested;

    /// <summary>Back to the main menu. The button only shows when something listens.</summary>
    public event System.Action? MainMenuRequested;

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
        layout.AddChild(new ControlsReference());
        layout.AddChild(new HSeparator());
        layout.AddChild(UiStyle.Text("One shot punctures a tank. A second sets it off.", 16, UiStyle.SoftInk, HorizontalAlignment.Center));
        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        buttons.AddThemeConstantOverride("separation", 10);
        var resume = UiStyle.Button("Resume");
        resume.Pressed += () => ResumeRequested?.Invoke();
        var quit = UiStyle.Button("Quit");
        quit.Pressed += () => GetTree().Quit();
        buttons.AddChild(resume);
        if (MainMenuRequested != null)
        {
            var menu = UiStyle.Button("Main menu");
            menu.Pressed += () => MainMenuRequested?.Invoke();
            buttons.AddChild(menu);
        }
        buttons.AddChild(quit);
        layout.AddChild(buttons);
        AddChild(new MenuNavigation { Screen = this, Root = layout, Back = () => ResumeRequested?.Invoke() });
    }
}
