using System;
using System.Linq;
using Godot;
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

    /// <summary>The connection the multiplayer screens use. Set before adding to the tree.</summary>
    public NetClient Net { get; set; } = null!;

    public Screen Current { get; private set; } = Screen.None;

    /// <summary>When this game is hosting, where others can reach it (shown in the lobby screens).</summary>
    public string HostingNote { get; set; } = "";

    public event Action? SinglePlayerRequested;

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

        version = UiStyle.Text($"Propane {BuildInfo.GameVersion}" + (BuildInfo.Build != "dev" ? $" ({BuildInfo.Build})" : ""), 14, UiStyle.SoftInk);
        version.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
        version.Position = new Vector2(16, -34);
        AddChild(version);

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
        // Someone joining or a server update rebuilds the screen: keep a name being typed, and its focus.
        string? typing = null;
        var caret = 0;
        if (nameField != null && IsInstanceValid(nameField) && nameField.HasFocus())
        {
            typing = nameField.Text;
            caret = nameField.CaretColumn;
        }
        foreach (var child in layout.GetChildren())
        {
            layout.RemoveChild(child);
            child.QueueFree();
        }
        status = null;
        nameField = null;
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
            nameField.CallDeferred(Control.MethodName.GrabFocus);
            nameField.CaretColumn = caret;
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
        var quit = UiStyle.Button("Quit");
        quit.Pressed += () => GetTree().Quit();
        layout.AddChild(quit);
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
        var server = UiStyle.Field(PlayerSettings.Server, $"address or address:port (port {Protocol.DefaultPort} if left out)");
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
            HostRequested?.Invoke(Protocol.DefaultPort);
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
    }

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
        Net.Connect(host, port, Protocol.CleanName(name), DefaultColor());
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
        foreach (var lobby in Net.Lobbies)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 10);
            var name = UiStyle.Text($"{lobby.CreatorName}'s lobby", 20);
            name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            row.AddChild(name);
            var inMatch = lobby.Phase != LobbyServer.LobbyPhase.Waiting;
            row.AddChild(UiStyle.Text(inMatch ? "in a match" : $"{lobby.Players} / {Net.LobbyCapacity}", 18, UiStyle.SoftInk));
            var join = UiStyle.Button("Join");
            join.Disabled = inMatch || lobby.Players >= Net.LobbyCapacity;
            var id = lobby.Id;
            join.Pressed += () => Net.JoinLobby(id);
            row.AddChild(join);
            layout.AddChild(row);
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
        var tuning = Tuning.Current;
        var creator = lobby.Members.FirstOrDefault(m => m.Id == lobby.CreatorId);
        layout.AddChild(UiStyle.Title($"{(creator?.Name ?? "?").ToUpperInvariant()}'S LOBBY", 34));
        layout.AddChild(UiStyle.Text(lobby.Phase == LobbyServer.LobbyPhase.Waiting ? $"{lobby.Members.Count} of {Net.LobbyCapacity} players" : "A match is on",
            16, UiStyle.SoftInk, HorizontalAlignment.Center));
        AddHostingNote();
        layout.AddChild(new HSeparator());
        foreach (var member in lobby.Members)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 12);
            row.AddChild(new ColorRect { Color = Protocol.PlayerColor(member.Color), CustomMinimumSize = new Vector2(20, 20), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter });
            var label = UiStyle.Text(member.Name + (member.Id == Net.MyId ? "  (you)" : ""), 20);
            label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            row.AddChild(label);
            if (member.Id == lobby.CreatorId)
            {
                row.AddChild(UiStyle.Text("starts the match", 15, UiStyle.SoftInk));
            }
            layout.AddChild(row);
        }
        layout.AddChild(new HSeparator());

        // Name and colour.
        var profile = new HBoxContainer();
        profile.AddThemeConstantOverride("separation", 10);
        var name = nameField = UiStyle.Field(Net.PlayerName, "Name");
        name.MaxLength = Protocol.MaxNameLength;
        name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        name.TextSubmitted += text => SetProfile(text, Net.Color);
        name.FocusExited += () =>
        {
            if (IsInstanceValid(name) && Protocol.CleanName(name.Text) != Net.PlayerName)
            {
                SetProfile(name.Text, Net.Color);
            }
        };
        profile.AddChild(name);
        layout.AddChild(profile);
        var colors = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        colors.AddThemeConstantOverride("separation", 8);
        var taken = lobby.Members.Where(m => m.Id != Net.MyId).Select(m => m.Color).ToHashSet();
        for (var i = 0; i < Protocol.PlayerColors.Length; i++)
        {
            var index = i;
            var swatch = new Button
            {
                CustomMinimumSize = new Vector2(40, 40),
                FocusMode = Control.FocusModeEnum.None,
                TooltipText = Protocol.ColorNames[i] + (taken.Contains(i) ? " (taken)" : ""),
                Disabled = taken.Contains(i),
            };
            var mine = Net.Color == i;
            foreach (var state in new[] { "normal", "hover", "pressed", "disabled" })
            {
                var color = Protocol.PlayerColor(i);
                swatch.AddThemeStyleboxOverride(state, new StyleBoxFlat
                {
                    BgColor = state == "disabled" ? new Color(color, 0.25f) : state == "hover" ? color.Lightened(0.15f) : color,
                    BorderColor = UiStyle.Ink,
                    BorderWidthBottom = mine ? 3 : 0,
                    BorderWidthTop = mine ? 3 : 0,
                    BorderWidthLeft = mine ? 3 : 0,
                    BorderWidthRight = mine ? 3 : 0,
                    CornerRadiusTopLeft = 20,
                    CornerRadiusTopRight = 20,
                    CornerRadiusBottomLeft = 20,
                    CornerRadiusBottomRight = 20,
                });
            }
            swatch.Pressed += () => SetProfile(nameField?.Text ?? Net.PlayerName, index);
            colors.AddChild(swatch);
        }
        layout.AddChild(colors);
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
        var minutes = UiStyle.Title(FormatTime(lobby.MatchLength), 26);
        minutes.CustomMinimumSize = new Vector2(80, 0);
        length.AddChild(minutes);
        if (Net.IsCreator)
        {
            var longer = UiStyle.Button("+");
            longer.CustomMinimumSize = new Vector2(44, 40);
            longer.Disabled = lobby.MatchLength >= tuning.MatchLengthMax;
            longer.Pressed += () => Net.SetMatchLength(Mathf.Min(tuning.MatchLengthMax, lobby.MatchLength + step));
            length.AddChild(longer);
        }
        layout.AddChild(length);

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

    private void SetProfile(string name, int color)
    {
        SaveName(name);
        PlayerSettings.Color = color;
        Net.SetProfile(name, color);
    }

    // ------------------------------------------------------------------ Helpers

    private void AddHostingNote()
    {
        if (HostingNote.Length > 0)
        {
            layout.AddChild(Wrap(UiStyle.Text(HostingNote, 15, UiStyle.SoftInk, HorizontalAlignment.Center)));
        }
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
        var user = OS.GetEnvironment("USER");
        return Protocol.CleanName(user.Length > 0 ? char.ToUpperInvariant(user[0]) + user[1..] : "Player");
    }

    private static int DefaultColor()
    {
        var saved = PlayerSettings.Color;
        return saved >= 0 ? saved : (int)(GD.Randi() % Protocol.PlayerColors.Length);
    }

    private static void SaveName(string name) => PlayerSettings.Name = Protocol.CleanName(name);
}
