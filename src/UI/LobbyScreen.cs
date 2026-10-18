using System;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Net;

namespace Propane.UI;

/// <summary>Shared classic and docked lobby composition, including the local profile controls.</summary>
internal sealed class LobbyScreen
{
    private readonly NetClient net;
    private readonly MenuCommands commands;
    private readonly MenuRefreshState refresh;
    private readonly LobbyDialogs dialogs;
    private readonly LobbySettingsPanel settings;
    private readonly Action close;

    public LobbyScreen(NetClient net, MenuCommands commands, MenuRefreshState refresh, LobbyDialogs dialogs, LobbySettingsPanel settings, Action close)
    {
        this.net = net;
        this.commands = commands;
        this.refresh = refresh;
        this.dialogs = dialogs;
        this.settings = settings;
        this.close = close;
    }

    public void Build(VBoxContainer layout, bool docked, string hostingNote, string message)
    {
        var lobby = net.Lobby;
        if (lobby == null)
        {
            return;
        }
        var creator = lobby.Members.FirstOrDefault(m => m.OwnerPeerId == lobby.CreatorId && m.Kind == ActorKind.Human);
        layout.AddChild(UiStyle.Title($"{(creator?.Name ?? "?").ToUpperInvariant()}'S LOBBY", 34));
        layout.AddChild(UiStyle.Text(lobby.Phase == LobbyPhase.Waiting ? $"{lobby.Members.Count} of {lobby.Capacity} players" : "A match is on",
            16, UiStyle.SoftInk, HorizontalAlignment.Center));
        MenuContent.AddHostingNote(layout, hostingNote);
        layout.AddChild(new HSeparator());
        var compact = lobby.Members.Count > 4;
        layout.CustomMinimumSize = new Vector2(compact ? 720 : 520, 0);
        layout.AddThemeConstantOverride("separation", compact ? 6 : 12);
        LobbyRoster.Build(layout, net, lobby, compact, dialogs.OpenBotEditor);
        var bots = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        bots.AddThemeConstantOverride("separation", 12);
        var addBot = UiStyle.Button("Add bot");
        addBot.SetMeta("focus_key", "add_bot");
        addBot.Disabled = lobby.Phase != LobbyPhase.Waiting || lobby.Members.Count >= lobby.Capacity;
        addBot.TooltipText = "Bots play on this computer and leave with you";
        addBot.Pressed += () => dialogs.OpenBotEditor(null);
        bots.AddChild(addBot);
        layout.AddChild(bots);
        layout.AddChild(new HSeparator());

        // Name and color.
        var profile = new HBoxContainer();
        profile.AddThemeConstantOverride("separation", 10);
        var name = refresh.NameField = UiStyle.Field(net.PlayerName, "Name");
        name.MaxLength = Protocol.MaxNameLength;
        name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        name.TextSubmitted += text => commands.SetProfile(text, net.Appearance);
        name.FocusExited += () =>
        {
            if (!refresh.Rebuilding && GodotObject.IsInstanceValid(name) && Protocol.CleanName(name.Text) != net.PlayerName)
            {
                commands.SetProfile(name.Text, net.Appearance);
            }
        };
        profile.AddChild(name);
        layout.AddChild(profile);
        var colors = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        colors.AddThemeConstantOverride("separation", 8);
        for (var i = 0; i < AppearancePalette.PlayerColors.Length; i++)
        {
            var index = i;
            var mine = net.Appearance.Color == AppearanceRecord.FromPalette(i).Color;
            var swatch = UiStyle.Swatch(AppearancePalette.PlayerColor(i), mine, $"color_{i}", AppearancePalette.ColorNames[i]);
            swatch.Pressed += () => commands.SetProfile(refresh.NameField?.Text ?? net.PlayerName, net.Appearance.WithColor(AppearancePalette.PlayerColor(index)));
            colors.AddChild(swatch);
        }
        layout.AddChild(colors);
        var custom = UiStyle.Button($"Custom color · #{net.Appearance.Color.ToHtml(false).ToUpperInvariant()}");
        custom.SetMeta("focus_key", "custom_color");
        custom.Disabled = lobby.Phase != LobbyPhase.Waiting;
        custom.Pressed += dialogs.OpenAppearance;
        layout.AddChild(custom);
        layout.AddChild(new HSeparator());

        settings.Build(layout, lobby);

        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        buttons.AddThemeConstantOverride("separation", 10);
        if (net.IsCreator)
        {
            // Two players make a match; a debug build may start alone, for testing.
            var needed = OS.IsDebugBuild() ? 1 : 2;
            var start = UiStyle.Button(lobby.Members.Count < needed ? "Waiting for players" : "Start match");
            start.Disabled = lobby.Members.Count < needed || lobby.Phase != LobbyPhase.Waiting;
            start.Pressed += commands.StartMatch;
            buttons.AddChild(start);
        }
        else
        {
            layout.AddChild(UiStyle.Text($"Waiting for {creator?.Name ?? "the creator"} to start", 16, UiStyle.SoftInk, HorizontalAlignment.Center));
        }
        var leave = UiStyle.Button("Leave lobby");
        leave.Pressed += commands.LeaveLobby;
        buttons.AddChild(leave);
        if (docked)
        {
            var close = UiStyle.Button("Close");
            close.SetMeta("focus_key", "close_panel");
            close.TooltipText = "Back to the lobby world (Esc)";
            close.Pressed += () => this.close();
            buttons.AddChild(close);
        }
        layout.AddChild(buttons);
        MenuContent.AddMessage(layout, message);
    }
}
