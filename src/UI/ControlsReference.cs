using System.Linq;
using Godot;
using Propane.Core;

namespace Propane.UI;

/// <summary>The pause reference follows the last active device, including a device change while it is open.</summary>
public partial class ControlsReference : VBoxContainer
{
    private bool gamepad;
    private int bindingRevision;
    public bool Match { get; init; }
    /// <summary>In the lobby world: adds the lobby panel's key.</summary>
    public bool Lobby { get; init; }
    public bool Compact { get; init; }
    public string? ResetAction { get; init; }

    public override void _Ready() => Rebuild();

    public override void _Process(double delta)
    {
        if (gamepad != MenuNavigation.Gamepad || bindingRevision != InputSetup.Revision)
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        gamepad = MenuNavigation.Gamepad;
        bindingRevision = InputSetup.Revision;
        foreach (var child in GetChildren())
        {
            RemoveChild(child);
            child.QueueFree();
        }
        AddRow(gamepad ? "Left stick" : string.Join(" ", new[] { InputSetup.MoveForward, InputSetup.MoveLeft, InputSetup.MoveBack, InputSetup.MoveRight }.Select(a => InputSetup.Describe(a, false))), "Move");
        foreach (var (id, action) in new[]
                 {
                     (InputSetup.Sprint, "Sprint"), (InputSetup.Jump, "Jump"),
                     (InputSetup.Fire, "Fire"), (InputSetup.Aim, "Aim (tighter spread)"),
                     (InputSetup.Melee, "Rifle-butt strike"),
                     (InputSetup.Reload, ResetAction ?? (Match ? "Reload" : "New suburb")),
                 })
        {
            AddRow(InputSetup.Describe(id, gamepad), action);
        }

        AddRow(gamepad ? "Right stick" : "Mouse", "Look");
        AddRow(gamepad ? "Start / Options" : "Esc", "Resume / pause");
        if (Lobby) AddRow(InputSetup.Describe(InputSetup.LobbyPanel, gamepad), "Lobby panel: names, colors, bots, settings");
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
        key.CustomMinimumSize = new Vector2(Compact ? 245 : 300, 0);
        key.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        row.AddChild(key);
        row.AddChild(UiStyle.Text(action, Compact ? 16 : 19, UiStyle.SoftInk));
        AddChild(row);
    }
}
