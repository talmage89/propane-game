using Godot;
using Propane.Core;

namespace Propane.UI;

/// <summary>
/// Esc or Start/Options in a match (or the lobby world): the controls, and resume, leave or quit. The world keeps
/// running behind it.
/// </summary>
public partial class MatchMenu : CanvasLayer
{
    /// <summary>The lobby world's menu: it leaves the lobby, and points at the lobby panel instead of the rules.</summary>
    public bool Lobby { get; init; }

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
        layout.AddChild(UiStyle.Text(Lobby ? "You are in the lobby" : "The match is still on", 18, UiStyle.SoftInk, HorizontalAlignment.Center));
        layout.AddChild(new HSeparator());
        layout.AddChild(new ControlsReference { Match = true, Lobby = Lobby });
        layout.AddChild(new HSeparator());
        var rules = UiStyle.Text(Lobby ? "Nothing counts here: tanks come back and the rifle never runs dry. The lobby panel has names, colors, bots and the match settings."
                : "Every tank you blow up is banked. A bullet hit spills one behind you; getting thrown spills five.", 16,
            UiStyle.SoftInk, HorizontalAlignment.Center);
        rules.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        layout.AddChild(rules);
        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        buttons.AddThemeConstantOverride("separation", 10);
        var resume = UiStyle.Button("Resume");
        resume.Pressed += () => ResumeRequested?.Invoke();
        var leave = UiStyle.Button(Lobby ? "Leave lobby" : "Leave match");
        leave.Pressed += () => LeaveRequested?.Invoke();
        var quit = UiStyle.Button("Quit");
        quit.Pressed += () => ShutdownGuard.Quit(GetTree());
        buttons.AddChild(resume);
        buttons.AddChild(leave);
        buttons.AddChild(quit);
        layout.AddChild(buttons);
        AddChild(new MenuNavigation { Screen = this, Root = layout, Back = () => ResumeRequested?.Invoke() });
    }
}
