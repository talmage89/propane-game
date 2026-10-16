using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Net;

namespace Propane.UI;

/// <summary>
/// The front end: the main menu, the multiplayer connect screen, the lobby browser and the lobby itself. One panel
/// whose contents are rebuilt for each screen, over the menu backdrop. In a lobby world the lobby screen is its panel:
/// docked at the side, over the world, and closed with Esc, B, View / Create or its Close button.
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

    private CenterContainer center = null!;
    private PanelContainer panel = null!;
    private VBoxContainer layout = null!;
    private Label version = null!;
    private string message = "";
    private bool connecting;
    private readonly MenuRefreshState refresh = new();
    private MenuCommands commands = null!;
    private ConnectScreen connectScreen = null!;
    private LobbyBrowserScreen browserScreen = null!;
    private LobbyScreen lobbyScreen = null!;
    private LobbyDialogs dialogs = null!;
    private MenuNavigation navigation = null!;

    /// <summary>The connection the multiplayer screens use. Set before adding to the tree.</summary>
    public NetClient Net { get; set; } = null!;

    /// <summary>The launch check for a newer release, offered on the main menu. Optional; set before adding to the tree.</summary>
    public UpdateCheck? Updates { get; set; }

    public Screen Current { get; private set; } = Screen.None;

    /// <summary>The lobby screen is showing as the lobby world's panel, beside the world rather than over the backdrop.</summary>
    public bool Docked { get; private set; }

    /// <summary>The docked lobby panel asked to close, back to the world.</summary>
    public event Action? PanelClosed;

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
        center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(center);
        panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", UiStyle.Panel(0.94f));
        center.AddChild(panel);
        layout = new VBoxContainer { CustomMinimumSize = new Vector2(520, 0) };
        layout.AddThemeConstantOverride("separation", 12);
        panel.AddChild(layout);
        var settings = new LobbySettingsPanel(Net);
        navigation = new MenuNavigation
        {
            Screen = this,
            Root = layout,
            Back = GoBack,
            Adjusters = new Dictionary<string, Action<int>> { ["match_length"] = settings.AdjustMatchLength, ["mint_pace"] = settings.AdjustMintPace, ["tank_supply"] = settings.AdjustTankSupply },
        };
        AddChild(navigation);
        commands = new MenuCommands(Net, text => { message = text; Rebuild(); }, () => message = "", port => HostRequested?.Invoke(port));
        connectScreen = new ConnectScreen(Net, commands, refresh, name => commands.Host(HostPort, name), () => Show(Screen.Main));
        browserScreen = new LobbyBrowserScreen(Net, commands, () => Show(Screen.Connect));
        dialogs = new LobbyDialogs(this, Net, navigation, commands, refresh);
        lobbyScreen = new LobbyScreen(Net, commands, refresh, dialogs, settings, () => PanelClosed?.Invoke());

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

    /// <summary>
    /// Shows a screen (or hides the menus with <see cref="Screen.None"/>). <paramref name="docked"/> shows the lobby
    /// screen as the lobby world's panel.
    /// </summary>
    public void Show(Screen screen, string? note = null, bool docked = false)
    {
        message = note ?? "";
        if (screen != Screen.Lobby || docked != Docked) { dialogs.Close(); }
        Current = screen;
        Visible = screen != Screen.None;
        Dock(docked && screen == Screen.Lobby);
        Rebuild();
    }

    /// <summary>The panel sits centered over the backdrop, or at the right over a lobby world, leaving the world in view.</summary>
    private void Dock(bool docked)
    {
        if (docked == Docked && center.AnchorLeft == (docked ? 1 : 0)) return;
        Docked = docked;
        center.SetAnchorsPreset(docked ? Control.LayoutPreset.RightWide : Control.LayoutPreset.FullRect);
        center.OffsetLeft = docked ? -820 : 0;
        center.OffsetRight = docked ? -24 : 0;
        panel.AddThemeStyleboxOverride("panel", UiStyle.Panel(docked ? 0.9f : 0.94f));
    }

    public override void _Input(InputEvent e)
    {
        // Esc, or View / Create again, closes the docked panel (B does through its navigation), unless a dialog is open.
        if (!Visible || !Docked || Current != Screen.Lobby || dialogs.IsOpen) return;
        if (e.IsActionPressed(InputSetup.Pause) || e is InputEventJoypadButton && e.IsActionPressed(InputSetup.LobbyPanel))
        {
            GetViewport().SetInputAsHandled();
            PanelClosed?.Invoke();
        }
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
            // A lobby world's start follows at once; the game enters it from this screen.
            if (Net.LobbyWorlds && Net.Lobby.WorldId != 0) Show(Screen.Loading, $"Entering {LobbyText.Creator(Net.Lobby)}'s lobby...");
            else Show(Screen.Lobby);
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
        refresh.Rebuild(Current, layout, navigation, BuildScreen, () => Visible && !dialogs.IsOpen);
    }

    private void BuildScreen()
    {
        version.Visible = Current == Screen.Main;
        switch (Current)
        {
            case Screen.Main:
                BuildMain();
                break;
            case Screen.Connect:
                connectScreen.Build(layout, connecting, message);
                break;
            case Screen.Browser:
                browserScreen.Build(layout, HostingNote, message);
                break;
            case Screen.Lobby:
                lobbyScreen.Build(layout, Docked, HostingNote, message);
                break;
            case Screen.Loading:
                layout.AddChild(UiStyle.Title("PROPANE", 46));
                layout.AddChild(UiStyle.Text(message.Length > 0 ? message : "Building the suburb...", 20, UiStyle.SoftInk, HorizontalAlignment.Center));
                break;
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
            MenuContent.AddDownload(layout, $"Download {latest}", latest);
        }
        MenuContent.AddMessage(layout, message);
    }

    /// <summary>Connects as if typed on the connect screen.</summary>
    public void ConnectTo(string address, string name) => commands.Connect(address, name.Length > 0 ? name : MenuProfile.DefaultName());

    internal static string DefaultBotName(LobbyInfo lobby) => LobbyDialogs.DefaultBotName(lobby);

    private void GoBack()
    {
        if (Docked && Current == Screen.Lobby)
        {
            PanelClosed?.Invoke();
            return;
        }
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
}
