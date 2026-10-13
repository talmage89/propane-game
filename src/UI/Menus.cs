using System;
using System.Linq;
using Godot;
using Propane.Bots;
using Propane.Core;
using Propane.Net;

namespace Propane.UI;

/// <summary>
/// The front end: the main menu, the multiplayer connect screen, the lobby browser and the lobby itself. One panel
/// whose contents are rebuilt for each screen, over the menu backdrop.
/// </summary>
public partial class Menus : CanvasLayer
{
    public enum Screen
    {
        None,
        Main,
        Connect,
        Browser,
        Lobby,
        Loading,
    }

    private PanelContainer panel = null!;
    private VBoxContainer layout = null!;
    private Label version = null!;
    private Label? status;
    private string message = "";
    private bool connecting;
    private LineEdit? nameField;
    private bool rebuilding;
    private MenuNavigation navigation = null!;
    private Screen builtScreen;
    private LineEdit? serverField;
    private AppearancePicker? picker;
    private BotEditor? botEditor;

    /// <summary>The connection the multiplayer screens use. Set before adding to the tree.</summary>
    public NetClient Net { get; set; } = null!;

    /// <summary>The launch check for a newer release, offered on the main menu. Optional; set before adding to the tree.</summary>
    public UpdateCheck? Updates { get; set; }

    public Screen Current { get; private set; } = Screen.None;

    /// <summary>When this game is hosting, where others can reach it (shown in the lobby screens).</summary>
    public string HostingNote { get; set; } = "";

    /// <summary>Local hosting port; harnesses override it so parallel checkouts keep separate servers.</summary>
    public int HostPort { get; set; } = Protocol.DefaultPort;

    public event Action? SinglePlayerRequested;
    public event Action? LabRequested;

    /// <summary>Run a server in this game and join it, on the given port.</summary>
    public event Action<int>? HostRequested;

    public override void _Ready()
    {
        Layer = 40;
        var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(center);
        panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", UiStyle.Panel(0.94f));
        center.AddChild(panel);
        layout = new VBoxContainer { CustomMinimumSize = new Vector2(520, 0) };
        layout.AddThemeConstantOverride("separation", 12);
        panel.AddChild(layout);
        navigation = new MenuNavigation
        {
            Screen = this,
            Root = layout,
            Back = GoBack,
            AdjustLength = AdjustMatchLength,
        };
        AddChild(navigation);

        version = UiStyle.Text($"Propane {BuildInfo.GameVersion}" + (BuildInfo.Build != "dev" ? $" ({BuildInfo.Build})" : ""), 14, UiStyle.SoftInk);
        version.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
        version.Position = new Vector2(16, -34);
        AddChild(version);

        if (Updates != null)
        {
            Updates.Found += () => Refresh(Screen.Main);
        }
        Net.StatusChanged += OnStatus;
        Net.LobbiesChanged += () => Refresh(Screen.Browser);
        Net.LobbyChanged += OnLobbyChanged;
        Net.NoticeReceived += text =>
        {
            message = text;
            Refresh(Current);
        };
    }

    /// <summary>Shows a screen (or hides the menus with <see cref="Screen.None"/>).</summary>
    public void Show(Screen screen, string? note = null)
    {
        message = note ?? "";
        if (screen != Screen.Lobby) { picker?.Close(); botEditor?.Close(); }
        Current = screen;
        Visible = screen != Screen.None;
        Rebuild();
    }

    private void Refresh(Screen screen)
    {
        if (Current == screen)
        {
            Rebuild();
        }
    }

    private void OnStatus(NetClient.Status state, string? reason)
    {
        connecting = state == NetClient.Status.Connecting;
        if (state == NetClient.Status.Connected && Current == Screen.Connect)
        {
            Show(Screen.Browser);
        }
        else if (state == NetClient.Status.Offline && Current is Screen.Connect or Screen.Browser or Screen.Lobby)
        {
            Show(Screen.Connect, reason);
        }
        else
        {
            Refresh(Current);
        }
    }

    private void OnLobbyChanged()
    {
        if (Current == Screen.Browser && Net.Lobby != null)
        {
            Show(Screen.Lobby);
        }
        else if (Current == Screen.Lobby && Net.Lobby == null)
        {
            Show(Screen.Browser);
        }
        else
        {
            Refresh(Screen.Lobby);
        }
    }

    private void Rebuild()
    {
        // Network refreshes preserve the control identity, drafts and caret; screen changes start at the top.
        var sameScreen = builtScreen == Current;
        // A new lobby screen starts on the player's name, even when bot controls come first in the layout.
        var focus = sameScreen ? navigation.FocusKey() : Current == Screen.Lobby ? "Name" : null;
        var serverDraft = sameScreen && serverField != null ? serverField.Text : null;
        builtScreen = Current;
        string? typing = null;
        var caret = 0;
        if (sameScreen && nameField != null && IsInstanceValid(nameField))
        {
            typing = nameField.Text;
            caret = nameField.CaretColumn;
        }
        rebuilding = true;
        foreach (var child in layout.GetChildren())
        {
            layout.RemoveChild(child);
            child.QueueFree();
        }
        rebuilding = false;
        status = null;
        nameField = null;
        serverField = null;
        version.Visible = Current == Screen.Main;
        switch (Current)
        {
            case Screen.Main:
                BuildMain();
                break;
            case Screen.Connect:
                BuildConnect();
                break;
            case Screen.Browser:
                BuildBrowser();
                break;
            case Screen.Lobby:
                BuildLobby();
                break;
            case Screen.Loading:
                layout.AddChild(UiStyle.Title("PROPANE", 46));
                layout.AddChild(UiStyle.Text(message.Length > 0 ? message : "Building the suburb...", 20, UiStyle.SoftInk, HorizontalAlignment.Center));
                break;
        }
        if (typing != null && nameField != null)
        {
            nameField.Text = typing;
            nameField.CaretColumn = caret;
        }
        if (serverDraft != null && serverField != null)
        {
            serverField.Text = serverDraft;
        }
        // An open overlay keeps focus; it reconfigures this screen when it closes.
        if (Visible && picker == null && botEditor == null)
        {
            navigation.Configure(focus);
        }
    }

    // ------------------------------------------------------------------ Main menu

    private void BuildMain()
    {
        layout.AddChild(UiStyle.Title("PROPANE", 64));
        layout.AddChild(UiStyle.Text("Find the tanks. Shoot them twice.", 18, UiStyle.SoftInk, HorizontalAlignment.Center));
        layout.AddChild(new HSeparator());
        var single = UiStyle.Button("Single player");
        single.Pressed += () => SinglePlayerRequested?.Invoke();
        layout.AddChild(single);
        var multi = UiStyle.Button("Multiplayer");
        multi.Pressed += () => Show(Net.State == NetClient.Status.Connected ? Net.Lobby != null ? Screen.Lobby : Screen.Browser : Screen.Connect);
        layout.AddChild(multi);
        var lab = UiStyle.Button("Lab");
        lab.Pressed += () => LabRequested?.Invoke();
        layout.AddChild(lab);
        var quit = UiStyle.Button("Quit");
        quit.Pressed += () => ShutdownGuard.Quit(GetTree());
        layout.AddChild(quit);
        if (Updates?.Latest is { } latest)
        {
            layout.AddChild(new HSeparator());
            layout.AddChild(UiStyle.Text($"Propane {latest} is out.", 16, UiStyle.SoftInk, HorizontalAlignment.Center));
            AddDownload($"Download {latest}", latest);
        }
        AddMessage();
    }

    // ------------------------------------------------------------------ Connect

    private void BuildConnect()
    {
        layout.AddChild(UiStyle.Title("MULTIPLAYER", 40));
        layout.AddChild(Wrap(UiStyle.Text("Tank bank: blow up tanks to bank them. Get shot and they spill out for anyone to take.", 16,
            UiStyle.SoftInk, HorizontalAlignment.Center)));
        layout.AddChild(new HSeparator());

        layout.AddChild(UiStyle.Text("Your name", 16, UiStyle.SoftInk));
        var name = nameField = UiStyle.Field(DefaultName(), "Name");
        name.MaxLength = Protocol.MaxNameLength;
        layout.AddChild(name);

        layout.AddChild(UiStyle.Text("Server", 16, UiStyle.SoftInk));
        var server = serverField = UiStyle.Field(PlayerSettings.Server, $"address or address:port (port {Protocol.DefaultPort} if left out)");
        layout.AddChild(server);
        server.TextSubmitted += _ => Connect(server.Text, name.Text);

        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        buttons.AddThemeConstantOverride("separation", 10);
        var connect = UiStyle.Button(connecting ? "Connecting..." : "Connect");
        connect.Disabled = connecting;
        connect.Pressed += () => Connect(server.Text, name.Text);
        var host = UiStyle.Button("Host on this computer");
        host.Disabled = connecting;
        host.Pressed += () =>
        {
            SaveName(name.Text);
            HostRequested?.Invoke(HostPort);
        };
        var back = UiStyle.Button("Back");
        back.Pressed += () =>
        {
            SaveName(name.Text);
            Net.Disconnect();
            Show(Screen.Main);
        };
        buttons.AddChild(connect);
        buttons.AddChild(host);
        buttons.AddChild(back);
        layout.AddChild(buttons);
        AddMessage();
        // Turned away by a newer server: offer the download that plays there.
        if (message.Length > 0 && Net.State == NetClient.Status.Offline && Net.RejectedBy is { } needed && BuildInfo.IsNewer(needed))
        {
            AddDownload($"Download Propane {BuildInfo.Series(needed)}", needed);
        }
    }

    /// <summary>Connects as if typed on the connect screen.</summary>
    public void ConnectTo(string address, string name) => Connect(address, name.Length > 0 ? name : DefaultName());

    private void Connect(string address, string name)
    {
        var text = address.Trim();
        if (text.Length == 0)
        {
            message = "Type the server's address.";
            Rebuild();
            return;
        }
        var host = text;
        var port = Protocol.DefaultPort;
        var colon = text.LastIndexOf(':');
        if (colon > 0 && text.IndexOf(':') == colon && int.TryParse(text[(colon + 1)..], out var parsed))
        {
            host = text[..colon];
            port = parsed;
        }
        PlayerSettings.Server = text;
        SaveName(name);
        message = "";
        Net.Connect(host, port, Protocol.CleanName(name), DefaultAppearance());
    }

    // ------------------------------------------------------------------ Lobby browser

    private void BuildBrowser()
    {
        layout.AddChild(UiStyle.Title("LOBBIES", 40));
        layout.AddChild(UiStyle.Text($"Playing as {Net.PlayerName}" + (Net.PingMs > 0 ? $"   ·   {Net.PingMs:0} ms to the server" : ""), 16, UiStyle.SoftInk,
            HorizontalAlignment.Center));
        AddHostingNote();
        layout.AddChild(new HSeparator());
        if (Net.Lobbies.Count == 0)
        {
            layout.AddChild(UiStyle.Text("No lobbies yet. Create one, then the others can join it.", 18, UiStyle.SoftInk, HorizontalAlignment.Center));
        }
        VBoxContainer? list = null;
        if (Net.Lobbies.Count > 0)
        {
            // Keep long server lists inside the panel; focusing a Join button scrolls its row into view.
            var scroll = new ScrollContainer
            {
                FollowFocus = true,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
                CustomMinimumSize = new Vector2(0, Mathf.Min(Net.Lobbies.Count, 4) * 52),
            };
            layout.AddChild(scroll);
            list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            list.AddThemeConstantOverride("separation", 2);
            scroll.AddChild(list);
            scroll.GetVScrollBar().FocusMode = Control.FocusModeEnum.None;
        }
        foreach (var lobby in Net.Lobbies)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 10);
            var name = UiStyle.Text($"{lobby.CreatorName}'s lobby", 20);
            name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            row.AddChild(name);
            var inMatch = lobby.Phase != LobbyServer.LobbyPhase.Waiting;
            row.AddChild(UiStyle.Text(inMatch ? "in a match" : $"{lobby.Players} / {lobby.Capacity}", 18, UiStyle.SoftInk));
            var join = UiStyle.Button("Join");
            join.Disabled = inMatch || lobby.Players >= lobby.Capacity;
            var id = lobby.Id;
            join.SetMeta("focus_key", $"lobby_{id}");
            join.Pressed += () => Net.JoinLobby(id);
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
        create.Pressed += Net.CreateLobby;
        var back = UiStyle.Button("Disconnect");
        back.Pressed += () =>
        {
            Net.Disconnect();
            Show(Screen.Connect);
        };
        buttons.AddChild(create);
        buttons.AddChild(back);
        layout.AddChild(buttons);
        AddMessage();
    }

    // ------------------------------------------------------------------ Lobby

    private void BuildLobby()
    {
        var lobby = Net.Lobby;
        if (lobby == null)
        {
            return;
        }
        var tuning = Net.Template;
        var creator = lobby.Members.FirstOrDefault(m => m.OwnerPeerId == lobby.CreatorId && m.Kind == ActorKind.Human);
        layout.AddChild(UiStyle.Title($"{(creator?.Name ?? "?").ToUpperInvariant()}'S LOBBY", 34));
        layout.AddChild(UiStyle.Text(lobby.Phase == LobbyServer.LobbyPhase.Waiting ? $"{lobby.Members.Count} of {lobby.Capacity} players" : "A match is on",
            16, UiStyle.SoftInk, HorizontalAlignment.Center));
        AddHostingNote();
        layout.AddChild(new HSeparator());
        var compact = lobby.Members.Count > 4;
        layout.CustomMinimumSize = new Vector2(compact ? 720 : 520, 0);
        layout.AddThemeConstantOverride("separation", compact ? 6 : 12);
        var roster = new GridContainer { Columns = compact ? 2 : 1 };
        roster.AddThemeConstantOverride("h_separation", 24);
        roster.AddThemeConstantOverride("v_separation", 2);
        layout.AddChild(roster);
        foreach (var member in lobby.Members)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 12);
            row.AddChild(UiStyle.ColorSample(member.Appearance.Color, 20, member.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            var bot = member.Kind == ActorKind.Bot;
            var difficulty = (BotDifficulty)Math.Min(member.ControllerConfig.Difficulty, (byte)BotDifficulty.Hard);
            var label = UiStyle.Text(member.Name + (bot ? $"  ({difficulty} bot)" : member.OwnerPeerId == Net.MyId ? "  (you)" : ""), compact ? 18 : 20);
            label.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            label.CustomMinimumSize = new Vector2(compact ? 200 : 260, 0);
            label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            row.AddChild(label);
            if (member.OwnerPeerId == lobby.CreatorId && member.Kind == ActorKind.Human)
            {
                row.AddChild(UiStyle.Text(compact ? "creator" : "starts the match", 15, UiStyle.SoftInk));
            }
            if (bot && member.OwnerPeerId == Net.MyId)
            {
                // Only a bot's owner may change it; the server enforces the same rule.
                var edit = UiStyle.Button("Edit");
                // As short as a name row, so a full lobby of bots still fits on the screen.
                edit.CustomMinimumSize = new Vector2(0, compact ? 26 : 30);
                edit.AddThemeFontSizeOverride("font_size", 15);
                edit.SetMeta("focus_key", $"bot_{member.Id}");
                edit.Disabled = lobby.Phase != LobbyServer.LobbyPhase.Waiting;
                var editing = member;
                edit.Pressed += () => OpenBotEditor(editing);
                row.AddChild(edit);
            }
            roster.AddChild(row);
        }
        var bots = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        bots.AddThemeConstantOverride("separation", 12);
        var addBot = UiStyle.Button("Add bot");
        addBot.SetMeta("focus_key", "add_bot");
        addBot.Disabled = lobby.Phase != LobbyServer.LobbyPhase.Waiting || lobby.Members.Count >= lobby.Capacity;
        addBot.TooltipText = "Bots play on this computer and leave with you";
        addBot.Pressed += () => OpenBotEditor(null);
        bots.AddChild(addBot);
        layout.AddChild(bots);
        layout.AddChild(new HSeparator());

        // Name and color.
        var profile = new HBoxContainer();
        profile.AddThemeConstantOverride("separation", 10);
        var name = nameField = UiStyle.Field(Net.PlayerName, "Name");
        name.MaxLength = Protocol.MaxNameLength;
        name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        name.TextSubmitted += text => SetProfile(text, Net.Appearance);
        name.FocusExited += () =>
        {
            if (!rebuilding && IsInstanceValid(name) && Protocol.CleanName(name.Text) != Net.PlayerName)
            {
                SetProfile(name.Text, Net.Appearance);
            }
        };
        profile.AddChild(name);
        layout.AddChild(profile);
        var colors = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        colors.AddThemeConstantOverride("separation", 8);
        for (var i = 0; i < Protocol.PlayerColors.Length; i++)
        {
            var index = i;
            var mine = Net.Appearance.Color == AppearanceRecord.FromPalette(i).Color;
            var swatch = UiStyle.Swatch(Protocol.PlayerColor(i), mine, $"color_{i}", Protocol.ColorNames[i]);
            swatch.Pressed += () => SetProfile(nameField?.Text ?? Net.PlayerName, Net.Appearance.WithColor(Protocol.PlayerColor(index)));
            colors.AddChild(swatch);
        }
        layout.AddChild(colors);
        var custom = UiStyle.Button($"Custom color · #{Net.Appearance.Color.ToHtml(false).ToUpperInvariant()}");
        custom.SetMeta("focus_key", "custom_color");
        custom.Disabled = lobby.Phase != LobbyServer.LobbyPhase.Waiting;
        custom.Pressed += () =>
        {
            if (picker != null) { return; }
            picker = new AppearancePicker { Initial = Net.Appearance };
            picker.Applied += appearance => SetProfile(nameField?.Text ?? Net.PlayerName, appearance);
            picker.Closed += () =>
            {
                picker = null;
                if (Visible) { navigation.Configure("custom_color"); }
            };
            AddChild(picker);
        };
        layout.AddChild(custom);
        layout.AddChild(new HSeparator());

        // Match length: the creator sets it.
        var length = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        length.AddThemeConstantOverride("separation", 12);
        length.AddChild(UiStyle.Text("Match length", 20));
        var step = Mathf.Max(tuning.MatchLengthStep, 1f);
        if (Net.IsCreator)
        {
            var shorter = UiStyle.Button("−");
            shorter.CustomMinimumSize = new Vector2(44, 40);
            shorter.Disabled = lobby.MatchLength <= tuning.MatchLengthMin;
            shorter.Pressed += () => Net.SetMatchLength(Mathf.Max(tuning.MatchLengthMin, lobby.MatchLength - step));
            length.AddChild(shorter);
        }
        if (Net.IsCreator)
        {
            var minutes = UiStyle.Button(FormatTime(lobby.MatchLength));
            minutes.SetMeta("focus_key", "match_length");
            minutes.TooltipText = "Left / right to change match length";
            minutes.CustomMinimumSize = new Vector2(80, 40);
            // Keep the existing mouse −/+ buttons; the time itself is a controller adjustment target.
            minutes.GuiInput += e =>
            {
                if (e is InputEventKey && (e.IsActionPressed("ui_left") || e.IsActionPressed("ui_right")))
                {
                    AdjustMatchLength(e.IsActionPressed("ui_left") ? -1 : 1);
                    minutes.AcceptEvent();
                }
            };
            length.AddChild(minutes);
        }
        else
        {
            var minutes = UiStyle.Title(FormatTime(lobby.MatchLength), 26);
            minutes.CustomMinimumSize = new Vector2(80, 0);
            length.AddChild(minutes);
        }
        if (Net.IsCreator)
        {
            var longer = UiStyle.Button("+");
            longer.CustomMinimumSize = new Vector2(44, 40);
            longer.Disabled = lobby.MatchLength >= tuning.MatchLengthMax;
            longer.Pressed += () => Net.SetMatchLength(Mathf.Min(tuning.MatchLengthMax, lobby.MatchLength + step));
            length.AddChild(longer);
        }
        var settings = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        settings.AddThemeConstantOverride("separation", 24);
        settings.AddChild(length);
        var capacity = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        capacity.AddThemeConstantOverride("separation", 8);
        capacity.AddChild(UiStyle.Text("Capacity", 20));
        if (Net.IsCreator)
        {
            var fewer = UiStyle.Button("−");
            fewer.SetMeta("focus_key", "capacity_down");
            fewer.Disabled = lobby.Phase != LobbyServer.LobbyPhase.Waiting || lobby.Capacity <= Mathf.Max(2, lobby.Members.Count);
            fewer.Pressed += () => Net.SetCapacity(lobby.Capacity - 1);
            capacity.AddChild(fewer);
        }
        capacity.AddChild(UiStyle.Text($"{lobby.Capacity} / {Net.LobbyCapacity}", 20, UiStyle.Ink));
        if (Net.IsCreator)
        {
            var more = UiStyle.Button("+");
            more.SetMeta("focus_key", "capacity_up");
            more.Disabled = lobby.Phase != LobbyServer.LobbyPhase.Waiting || lobby.Capacity >= Net.LobbyCapacity;
            more.Pressed += () => Net.SetCapacity(lobby.Capacity + 1);
            capacity.AddChild(more);
        }
        settings.AddChild(capacity);
        layout.AddChild(settings);

        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        buttons.AddThemeConstantOverride("separation", 10);
        if (Net.IsCreator)
        {
            // Two players make a match; a debug build may start alone, for testing.
            var needed = OS.IsDebugBuild() ? 1 : 2;
            var start = UiStyle.Button(lobby.Members.Count < needed ? "Waiting for players" : "Start match");
            start.Disabled = lobby.Members.Count < needed || lobby.Phase != LobbyServer.LobbyPhase.Waiting;
            start.Pressed += Net.StartMatch;
            buttons.AddChild(start);
        }
        else
        {
            layout.AddChild(UiStyle.Text($"Waiting for {creator?.Name ?? "the creator"} to start", 16, UiStyle.SoftInk, HorizontalAlignment.Center));
        }
        var leave = UiStyle.Button("Leave lobby");
        leave.Pressed += Net.LeaveLobby;
        buttons.AddChild(leave);
        layout.AddChild(buttons);
        AddMessage();
    }

    private void OpenBotEditor(PlayerProfile? editing)
    {
        if (botEditor != null || picker != null || Net.Lobby is not { Phase: LobbyServer.LobbyPhase.Waiting } lobby) { return; }
        botEditor = new BotEditor
        {
            Net = Net,
            Editing = editing,
            InitialName = editing?.Name ?? DefaultBotName(lobby),
            InitialAppearance = editing?.Appearance ?? DefaultBotAppearance(lobby),
            InitialDifficulty = editing != null ? (BotDifficulty)Math.Min(editing.ControllerConfig.Difficulty, (byte)BotDifficulty.Hard) : BotDifficulty.Normal,
        };
        botEditor.Closed += () =>
        {
            botEditor = null;
            if (Visible) { navigation.Configure(editing != null ? $"bot_{editing.Id}" : "add_bot"); }
        };
        AddChild(botEditor);
    }

    /// <summary>"Bot 1", "Bot 2"...: the first number nobody in the lobby is named with.</summary>
    internal static string DefaultBotName(LobbyInfo lobby)
    {
        for (var n = 1; ; n++)
        {
            var name = $"Bot {n}";
            if (!lobby.Members.Any(m => Protocol.SameName(m.Name, name))) { return name; }
        }
    }

    /// <summary>The first preset nobody in the lobby wears; any color stays allowed.</summary>
    private static AppearanceRecord DefaultBotAppearance(LobbyInfo lobby)
    {
        for (var i = 0; i < Protocol.PlayerColors.Length; i++)
        {
            var preset = AppearanceRecord.FromPalette(i);
            if (!lobby.Members.Any(m => m.Appearance.Color == preset.Color)) { return preset; }
        }
        return AppearanceRecord.FromPalette(lobby.Members.Count);
    }

    private void AdjustMatchLength(int direction)
    {
        if (Net.Lobby is not { } lobby || !Net.IsCreator)
        {
            return;
        }
        var tuning = Net.Template;
        var seconds = Mathf.Clamp(lobby.MatchLength + direction * Mathf.Max(tuning.MatchLengthStep, 1f),
            tuning.MatchLengthMin, tuning.MatchLengthMax);
        if (seconds != lobby.MatchLength)
        {
            Net.SetMatchLength(seconds);
        }
    }

    private void GoBack()
    {
        var text = Current switch
        {
            Screen.Connect => "Back",
            Screen.Browser => "Disconnect",
            Screen.Lobby => "Leave lobby",
            _ => null,
        };
        var button = layout.FindChildren("*", "Button", owned: false).OfType<Button>().FirstOrDefault(b => b.Text == text);
        button?.EmitSignal(BaseButton.SignalName.Pressed);
    }

    private void SetProfile(string name, AppearanceRecord appearance)
    {
        SaveName(name);
        PlayerSettings.Appearance = appearance;
        Net.SetProfile(name, appearance);
    }

    // ------------------------------------------------------------------ Helpers

    private void AddHostingNote()
    {
        if (HostingNote.Length > 0)
        {
            layout.AddChild(Wrap(UiStyle.Text(HostingNote, 15, UiStyle.SoftInk, HorizontalAlignment.Center)));
        }
    }

    /// <summary>A button that downloads this platform's archive through the browser.</summary>
    private void AddDownload(string text, string release)
    {
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        var download = UiStyle.Button(text);
        download.Pressed += () => OS.ShellOpen(BuildInfo.DownloadUrl(release, OS.GetName()));
        row.AddChild(download);
        layout.AddChild(row);
    }

    private void AddMessage()
    {
        status = UiStyle.Text(message, 16, new Color(0.75f, 0.2f, 0.12f), HorizontalAlignment.Center);
        Wrap(status);
        status.Visible = message.Length > 0;
        layout.AddChild(status);
    }

    private static Label Wrap(Label label)
    {
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.CustomMinimumSize = new Vector2(480, 0);
        return label;
    }

    private static string FormatTime(float seconds)
    {
        var whole = Mathf.RoundToInt(seconds);
        return $"{whole / 60}:{whole % 60:00}";
    }

    private static string DefaultName()
    {
        var saved = PlayerSettings.Name;
        if (saved.Length > 0)
        {
            return saved;
        }
        var user = System.Environment.UserName;
        return Protocol.CleanName(user.Length > 0 ? char.ToUpperInvariant(user[0]) + user[1..] : "Player");
    }

    private static AppearanceRecord DefaultAppearance()
    {
        return PlayerSettings.Appearance ?? AppearanceRecord.FromPalette((int)(GD.Randi() % Protocol.PlayerColors.Length));
    }

    private static void SaveName(string name) => PlayerSettings.Name = Protocol.CleanName(name);
}
