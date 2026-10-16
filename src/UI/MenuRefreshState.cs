using System;
using Godot;

namespace Propane.UI;

/// <summary>One rebuild transaction for screen identity, drafts, caret and focus.</summary>
internal sealed class MenuRefreshState
{
    private Menus.Screen builtScreen;
    public LineEdit? NameField { get; set; }
    public LineEdit? ServerField { get; set; }
    public bool Rebuilding { get; private set; }

    public void Rebuild(Menus.Screen screen, VBoxContainer layout, MenuNavigation navigation, Action build, Func<bool> canFocus)
    {
        var sameScreen = builtScreen == screen;
        var focus = sameScreen ? navigation.FocusKey() : screen == Menus.Screen.Lobby ? "Name" : null;
        var serverDraft = sameScreen && ServerField != null ? ServerField.Text : null;
        builtScreen = screen;
        string? typing = null;
        var caret = 0;
        if (sameScreen && NameField != null && GodotObject.IsInstanceValid(NameField))
        {
            typing = NameField.Text;
            caret = NameField.CaretColumn;
        }
        Rebuilding = true;
        foreach (var child in layout.GetChildren())
        {
            layout.RemoveChild(child);
            child.QueueFree();
        }
        Rebuilding = false;
        NameField = null;
        ServerField = null;
        build();
        if (typing != null && NameField != null)
        {
            NameField.Text = typing;
            NameField.CaretColumn = caret;
        }
        if (serverDraft != null && ServerField != null) ServerField.Text = serverDraft;
        // A surviving overlay owns focus until its close callback reconfigures navigation.
        if (canFocus()) navigation.Configure(focus);
    }
}
