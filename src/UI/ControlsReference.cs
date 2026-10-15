using Godot;

namespace Propane.UI;

/// <summary>The pause reference follows the last active device, including a device change while it is open.</summary>
public partial class ControlsReference : VBoxContainer
{
    private bool gamepad;
    public bool Match { get; init; }
    /// <summary>In the lobby world: adds the lobby panel's key.</summary>
    public bool Lobby { get; init; }
    public bool Compact { get; init; }
    public string? ResetAction { get; init; }

    public override void _Ready() => Rebuild();

    public override void _Process(double delta)
    {
        if (gamepad != MenuNavigation.Gamepad)
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        gamepad = MenuNavigation.Gamepad;
        foreach (var child in GetChildren())
        {
            RemoveChild(child);
            child.QueueFree();
        }
        foreach (var (keyboard, pad, action) in new[]
                 {
                     ("W A S D", "Left stick", "Move"), ("Shift", "Left stick click", "Sprint"),
                     ("Space", "A / Cross", "Jump"), ("Mouse", "Right stick", "Look"),
                     ("Left click", "Right trigger", "Fire"), ("Right click", "Left trigger", "Aim (tighter spread)"),
                     ("F / mouse thumb", "Right stick click", "Rifle-butt strike"),
                     ("R", "X / Square", ResetAction ?? (Match ? "Reload" : "New suburb")), ("Esc", "Start / Options", "Resume / pause"),
                 })
        {
            AddRow(gamepad ? pad : keyboard, action);
        }
        if (Lobby)
        {
            AddRow(gamepad ? "View / Create" : "Tab", "Lobby panel: names, colors, bots, settings");
        }
        if (!gamepad)
        {
            AddRow("F11  or  Alt+Enter", "Fullscreen");
            if (!Match || OS.IsDebugBuild())
            {
                AddRow("F1  or  `", Match ? "Tuning (debug: changes everyone's)" : "Tuning panel");
            }
        }
        else
        {
            AddRow("D-pad / left stick", "Navigate menus");
            AddRow("A / Cross · B / Circle", "Confirm · back");
        }
    }

    private void AddRow(string keys, string action)
    {
        var row = new HBoxContainer();
        var key = UiStyle.Text(keys, Compact ? 16 : 19);
        key.AddThemeFontOverride("font", UiStyle.BoldFont);
        key.CustomMinimumSize = new Vector2(Compact ? 205 : 230, 0);
        row.AddChild(key);
        row.AddChild(UiStyle.Text(action, Compact ? 16 : 19, UiStyle.SoftInk));
        AddChild(row);
    }
}
