using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Godot;
using Propane.Bots;
using Propane.Core;
using Propane.Net;

namespace Propane.UI;

/// <summary>Lobby overlay lifetime and stable focus return, independent of rebuilt controls.</summary>
[SuppressMessage("Design", "CA1001", Justification = "The Menus scene tree owns and frees the dialog nodes; this object only tracks open overlays.")]
internal sealed class LobbyDialogs
{
    private readonly CanvasLayer owner;
    private readonly NetClient net;
    private readonly MenuNavigation navigation;
    private readonly MenuCommands commands;
    private readonly MenuRefreshState refresh;
    private AppearancePicker? picker;
    private BotEditor? botEditor;

    public bool IsOpen => picker != null || botEditor != null;

    public LobbyDialogs(CanvasLayer owner, NetClient net, MenuNavigation navigation, MenuCommands commands, MenuRefreshState refresh)
    {
        this.owner = owner;
        this.net = net;
        this.navigation = navigation;
        this.commands = commands;
        this.refresh = refresh;
    }

    public void Close() { picker?.Close(); botEditor?.Close(); }

    public void OpenAppearance()
    {
        if (picker != null) { return; }
        picker = new AppearancePicker { Initial = net.Appearance };
        picker.Applied += appearance => commands.SetProfile(refresh.NameField?.Text ?? net.PlayerName, appearance);
        picker.Closed += () =>
        {
            picker = null;
            if (owner.Visible) { navigation.Configure("custom_color"); }
        };
        owner.AddChild(picker);
    }

    public void OpenBotEditor(PlayerProfile? editing)
    {
        if (botEditor != null || picker != null || net.Lobby is not { Phase: LobbyPhase.Waiting } lobby) { return; }
        botEditor = new BotEditor
        {
            Net = net,
            Editing = editing,
            InitialName = editing?.Name ?? DefaultBotName(lobby),
            InitialAppearance = editing?.Appearance ?? DefaultBotAppearance(lobby),
            InitialDifficulty = editing != null ? (BotDifficulty)Math.Min(editing.ControllerConfig.Difficulty, (byte)BotDifficulty.Hard) : BotDifficulty.Normal,
        };
        botEditor.Closed += () =>
        {
            botEditor = null;
            if (owner.Visible) { navigation.Configure(editing != null ? $"bot_{editing.Id}" : "add_bot"); }
        };
        owner.AddChild(botEditor);
    }

    internal static string DefaultBotName(LobbyInfo lobby)
    {
        for (var n = 1; ; n++)
        {
            var name = $"Bot {n}";
            if (!lobby.Members.Any(m => Protocol.SameName(m.Name, name))) { return name; }
        }
    }

    private static AppearanceRecord DefaultBotAppearance(LobbyInfo lobby)
    {
        for (var i = 0; i < AppearancePalette.PlayerColors.Length; i++)
        {
            var preset = AppearanceRecord.FromPalette(i);
            if (!lobby.Members.Any(m => m.Appearance.Color == preset.Color)) { return preset; }
        }
        return AppearanceRecord.FromPalette(lobby.Members.Count);
    }
}
