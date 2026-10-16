using System;
using Godot;
using Propane.Net;

namespace Propane.UI;

/// <summary>Server lobby list, join/create controls and disconnect presentation.</summary>
internal sealed class LobbyBrowserScreen
{
    private readonly NetClient net;
    private readonly Action back;
    private readonly MenuCommands commands;

    public LobbyBrowserScreen(NetClient net, MenuCommands commands, Action back)
    {
        this.net = net;
        this.back = back;
        this.commands = commands;
    }

    public void Build(VBoxContainer layout, string hostingNote, string message)
    {
        layout.AddChild(UiStyle.Title("LOBBIES", 40));
        layout.AddChild(UiStyle.Text($"Playing as {net.PlayerName}" + (net.PingMs > 0 ? $"   ·   {net.PingMs:0} ms to the server" : ""), 16, UiStyle.SoftInk,
            HorizontalAlignment.Center));
        MenuContent.AddHostingNote(layout, hostingNote);
        layout.AddChild(new HSeparator());
        if (net.Lobbies.Count == 0)
        {
            layout.AddChild(UiStyle.Text("No lobbies yet. Create one, then the others can join it.", 18, UiStyle.SoftInk, HorizontalAlignment.Center));
        }
        VBoxContainer? list = null;
        if (net.Lobbies.Count > 0)
        {
            // Keep long server lists inside the panel; focusing a Join button scrolls its row into view.
            var scroll = new ScrollContainer
            {
                FollowFocus = true,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
                CustomMinimumSize = new Vector2(0, Mathf.Min(net.Lobbies.Count, 4) * 52),
            };
            layout.AddChild(scroll);
            list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            list.AddThemeConstantOverride("separation", 2);
            scroll.AddChild(list);
            scroll.GetVScrollBar().FocusMode = Control.FocusModeEnum.None;
        }
        foreach (var lobby in net.Lobbies)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 10);
            var description = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            description.AddThemeConstantOverride("separation", 0);
            description.AddChild(UiStyle.Text($"{lobby.CreatorName}'s lobby", 20));
            description.AddChild(UiStyle.Text(Modes.ModeRegistry.ForLobby(lobby.ModeId)!.Name, 15, UiStyle.SoftInk));
            row.AddChild(description);
            var inMatch = lobby.Phase != LobbyServer.LobbyPhase.Waiting;
            row.AddChild(UiStyle.Text(inMatch ? "in a match" : $"{lobby.Players} / {lobby.Capacity}", 18, UiStyle.SoftInk));
            var join = UiStyle.Button("Join");
            join.Disabled = inMatch || lobby.Players >= lobby.Capacity;
            var id = lobby.Id;
            join.SetMeta("focus_key", $"lobby_{id}");
            join.Pressed += () => commands.JoinLobby(id);
            row.AddChild(join);
            var inset = new MarginContainer();
            foreach (var side in new[] { "left", "right", "top", "bottom" })
            {
                inset.AddThemeConstantOverride($"margin_{side}", 4);
            }
            inset.AddChild(row);
            list!.AddChild(inset);
        }
        layout.AddChild(new HSeparator());
        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        buttons.AddThemeConstantOverride("separation", 10);
        var create = UiStyle.Button("Create lobby");
        create.Pressed += commands.CreateLobby;
        var back = UiStyle.Button("Disconnect");
        back.Pressed += () =>
        {
            commands.Disconnect();
            this.back();
        };
        buttons.AddChild(create);
        buttons.AddChild(back);
        layout.AddChild(buttons);
        MenuContent.AddMessage(layout, message);
    }
}
