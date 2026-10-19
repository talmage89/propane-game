using System.Linq;
using Godot;
using Propane.Core;
using Propane.Lab;
using Propane.Modes;
using Propane.Net;
using Propane.ReleaseSmoke;
using Propane.Tank;
using Propane.UI;
using Propane.World;

namespace Propane;

/// <summary>
/// The root of the game. Shows the menus over a backdrop, and runs single player (<see cref="Game"/>), a multiplayer
/// lobby world or match (both a <see cref="Match"/>) or the Lab under them. Joining a lobby enters its world; the
/// lobby menu is its panel, and the stage screen carries a start from the world into the match. Started with
/// <c>-- --server</c> (and usually <c>--headless</c>), it runs only the multiplayer server instead.
/// </summary>
public partial class Main : Node
{
    private IPlayerSettings settings = null!;
    private PlayerSettings? ownedSettings;
    private System.IDisposable? settingsBinding;
    public Tuning SavedTuning { get; init; } = Tuning.SavedTemplate;
    private NetClient net = null!;
    private Menus menus = null!;
    private StageScreen stage = null!;
    private Node3D? backdrop;
    private Game? game;
    private Match? match;
    private LabMenu? labMenu;
    private LabSession? lab;
    private LobbyServer? hostedServer;
    private int matchGeneration;
    /// <summary>The last scene built was a match, so the next lobby world is a return.</summary>
    private bool afterMatch;

    public override void _Ready()
    {
        // Test roots bind their own isolated stores. The ordinary application owns the default store until exit.
        settings = PlayerPreferences.Current ?? (ownedSettings = new PlayerSettings("user://settings.cfg"));
        if (ownedSettings != null) settingsBinding = PlayerSettings.Bind(ownedSettings);
        // Official release templates disallow command-line scene overrides. Keep the same smoke test available in
        // an untouched archive; its nested Main goes through the normal front end below.
        if (LaunchArgs.Current.Has("release-test") && GetParent() == GetTree().Root)
        {
            AddChild(new ReleaseTest());
            return;
        }
        if (LaunchArgs.Current.Has("server"))
        {
            RunServer();
            return;
        }
        InputSetup.Register();
        net = new NetClient(PacketTransports.CreateClient(), ModeRegistry.Codec) { Name = "Net", Template = SavedTuning };
        AddChild(net);
        net.MatchStarting += OnMatchStarting;
        net.StatusChanged += OnStatusChanged;
        net.LobbyChanged += OnLobbyChanged;
        var updates = new UpdateCheck { Name = "UpdateCheck" };
        AddChild(updates);
        menus = new Menus { Name = "Menus", Net = net, Updates = updates, Settings = settings };
        menus.SinglePlayerRequested += StartSinglePlayer;
        menus.LabRequested += ShowLab;
        menus.HostRequested += Host;
        menus.PanelClosed += ClosePanel;
        AddChild(menus);
        stage = new StageScreen { Name = "Stage", Net = net };
        stage.CancelRequested += net.CancelStart;
        AddChild(stage);
        ShowMenus(Menus.Screen.Main);
        // --connect=host[:port] goes straight to that server's lobbies.
        if (LaunchArgs.Current.Get("connect") is { } server)
        {
            menus.Show(Menus.Screen.Connect);
            menus.ConnectTo(server, LaunchArgs.Current.Get("name") ?? settings.Name);
        }
        if (DisplayServer.WindowGetMode() == DisplayServer.WindowMode.Windowed && !LaunchArgs.Current.Has("windowed"))
        {
            Callable.From(() => DisplayServer.WindowSetMode(DisplayServer.WindowMode.Maximized)).CallDeferred();
        }
    }

    public override void _ExitTree()
    {
        settingsBinding?.Dispose();
        ownedSettings?.Dispose();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (game == null && match == null && lab == null && @event.IsActionPressed(InputSetup.ToggleFullscreen))
        {
            var fullscreen = DisplayServer.WindowGetMode() == DisplayServer.WindowMode.Fullscreen;
            DisplayServer.WindowSetMode(fullscreen ? DisplayServer.WindowMode.Windowed : DisplayServer.WindowMode.Fullscreen);
        }
    }

    // ------------------------------------------------------------------ Server mode

    private void RunServer()
    {
        // A headless server has nothing to draw: tick often enough for snappy relays without spinning a core.
        Engine.MaxFps = 120;
        // Dev admission probes need their explicit tuners before the first client connects.
        if (OS.IsDebugBuild()) { LaunchArgs.Current.ApplyTuningOverrides(); }
        var port = (int)LaunchArgs.Current.GetFloat("port", Protocol.DefaultPort);
        var server = Modes.ModeRegistry.CreateServer();
        server.Name = "Server";
        server.Port = port;
        server.StartScore = (int)LaunchArgs.Current.GetFloat("start-score", 0);
        AddChild(server);
        if (!server.Start().Success)
        {
            ShutdownGuard.Quit(GetTree(), 1);
        }
    }

    // ------------------------------------------------------------------ Menus and backdrop

    private void ShowMenus(Menus.Screen screen, string? note = null)
    {
        if (backdrop == null)
        {
            backdrop = new Backdrop { Name = "Backdrop", Template = SavedTuning };
            AddChild(backdrop);
        }
        Input.MouseMode = Input.MouseModeEnum.Visible;
        menus.Show(screen, note);
    }

    private void HideBackdrop()
    {
        backdrop?.Free();
        backdrop = null;
    }

    /// <summary>The hero shot behind the menus: one venting tank on the white floor, the camera circling it.</summary>
    private partial class Backdrop : Node3D
    {
        public required Tuning Template { get; init; }
        private WorldContext world = null!;
        private Camera3D camera = null!;
        private float angle = 0.6f;

        public override void _Ready()
        {
            world = new WorldContext(this, Template, present: true);
            world.Construct(Build);
        }

        private void Build()
        {
            var environment = new VoidEnvironment { Name = "Void" };
            world.Root.AddChild(environment);
            var tank = PropaneTank.Create(world);
            tank.Freeze = true;
            world.Root.AddChild(tank);
            tank.RotationDegrees = new Vector3(0, 35, 0);
            tank.AddChild(new VentJet
            {
                World = world,
                ContentSpawn = world.Spawn,
                Name = "VentJet",
                Transform = new Transform3D(Basis.LookingAt(Vector3.Back.Rotated(Vector3.Up, 0.5f), Vector3.Up), new Vector3(0.12f, 0.3f, 0.1f)),
            });
            camera = new Camera3D { Name = "Camera", Fov = 50, Current = true };
            world.AddView(this, camera, followsPause: true);
            environment.Follow = camera;
            Place();
        }

        public override void _ExitTree() => world.Dispose();

        public override void _Process(double delta)
        {
            if (!world.IsAlive || world.Paused) { return; }
            angle += (float)delta * 0.09f;
            Place();
        }

        private void Place()
        {
            // The tank sits left of center, clear of the menu panel.
            var focus = new Vector3(0, 0.32f, 0);
            var position = focus + new Vector3(Mathf.Sin(angle) * 2.3f, 0.75f, Mathf.Cos(angle) * 2.3f);
            var right = (focus - position).Normalized().Cross(Vector3.Up).Normalized();
            camera.LookAtFromPosition(position, focus + right * 1.05f + Vector3.Up * 0.12f);
        }
    }

    // ------------------------------------------------------------------ Single player

    private void StartSinglePlayer()
    {
        menus.Show(Menus.Screen.None);
        HideBackdrop();
        game = new Game { Name = "Game", Template = SavedTuning };
        game.ExitRequested += ExitSinglePlayer;
        AddChild(game);
    }

    private void ExitSinglePlayer()
    {
        game?.Session.Dispose();
        game?.Free();
        game = null;
        ShowMenus(Menus.Screen.Main);
    }

    // ------------------------------------------------------------------ Lab

    private void ShowLab()
    {
        menus.Show(Menus.Screen.None);
        if (labMenu == null)
        {
            labMenu = new LabMenu { Name = "LabMenu", Template = SavedTuning };
            labMenu.BackRequested += () => { labMenu.Visible = false; ShowMenus(Menus.Screen.Main); };
            labMenu.StartRequested += StartLab;
            AddChild(labMenu);
        }
        else
        {
            labMenu.ShowScenarios();
        }
    }

    private void StartLab(LabScenario scenario, LabEnvironment environment, Tuning tuning)
    {
        labMenu!.Visible = false;
        HideBackdrop();
        lab = new LabSession { Name = "Lab", Scenario = scenario, EnvironmentKind = environment, Template = tuning };
        lab.ExitRequested += () =>
        {
            lab.Session.Dispose();
            lab.Free();
            lab = null;
            ShowMenus(Menus.Screen.None);
            labMenu.ShowScenarios();
        };
        AddChild(lab);
    }

    // ------------------------------------------------------------------ Multiplayer

    private void Host(int port)
    {
        if (hostedServer == null)
        {
            hostedServer = Modes.ModeRegistry.CreateServer();
            hostedServer.Name = "HostedServer";
            hostedServer.Port = port;
            hostedServer.Worlds = net.LobbyWorlds;
            AddChild(hostedServer);
            if (!hostedServer.Start().Success)
            {
                hostedServer.QueueFree();
                hostedServer = null;
                menus.Show(Menus.Screen.Connect, $"Could not open UDP port {port}: is a server already running here?");
                return;
            }
        }
        var addresses = IP.GetLocalAddresses().Where(a => a.Contains('.') && !a.StartsWith("127.") && !a.StartsWith("169.254.")).ToList();
        menus.HostingNote = $"Hosting here. Others connect to {(addresses.Count > 0 ? string.Join(" or ", addresses.Select(a => $"{a}:{port}")) : $"this computer's address, port {port}")}" +
                            " (UDP; from outside your network, forward the port).";
        net.Connect("127.0.0.1", port, settings.Name, settings.Appearance ?? AppearanceRecord.FromPalette(0));
    }

    private async void OnMatchStarting(MatchSetup setup)
    {
        if (setup.ModeId == Modes.ModeRegistry.LobbyWorldId)
        {
            EnterLobbyWorld(setup);
            return;
        }
        // A staged start keeps its lineup on screen while the match builds; otherwise the plain loading screen.
        var staged = stage.Visible;
        ClosePanel();
        EndMatch();
        var generation = ++matchGeneration;
        if (staged) { stage.ShowLoading(); }
        else { ShowMenus(Menus.Screen.Loading); }
        // Let the loading screen draw before the suburb is built, which takes a moment.
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (generation != matchGeneration || net.State != NetClient.Status.Connected || net.ActiveMatchId != setup.Id)
        {
            return;
        }
        HideBackdrop();
        menus.Show(Menus.Screen.None);
        var built = match = new Match { Name = "Match", Net = net, Setup = setup };
        afterMatch = true;
        match.Finished += () =>
        {
            // A newer scene (the lobby world that follows) may already have replaced this one.
            if (match == built) ReturnToLobby(null);
        };
        match.LeaveRequested += () =>
        {
            net.LeaveLobby();
            ReturnToLobby(null);
        };
        AddChild(match);
        stage.Close();
    }

    /// <summary>The lobby's world: entered on joining, and again after every match. The lobby menu is its panel.</summary>
    private async void EnterLobbyWorld(MatchSetup setup)
    {
        var returning = afterMatch;
        ClosePanel();
        EndMatch();
        stage.Close();
        var generation = ++matchGeneration;
        ShowMenus(Menus.Screen.Loading, returning ? "Back to the lobby..." : $"Entering {(net.Lobby is { } lobby ? LobbyText.Creator(lobby) : "the")}'s lobby...");
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (generation != matchGeneration || net.State != NetClient.Status.Connected || net.ActiveMatchId != setup.Id || net.Lobby == null)
        {
            return;
        }
        HideBackdrop();
        menus.Show(Menus.Screen.None);
        afterMatch = false;
        match = new Match { Name = "LobbyWorld", Net = net, Setup = setup, Returning = returning };
        match.LeaveRequested += net.LeaveLobby;
        match.PanelRequested += OpenPanel;
        AddChild(match);
        if (net.Lobby?.Phase == LobbyPhase.Staging) { stage.ShowCountdown(); }
    }

    /// <summary>The lobby menu, docked beside the world: names, colors, bots and (for the creator) the match settings.</summary>
    private void OpenPanel()
    {
        if (match is not { IsLobby: true } world || net.Lobby is not { Phase: LobbyPhase.Waiting }) return;
        menus.Show(Menus.Screen.Lobby, docked: true);
        world.PanelOpen = true;
    }

    private void ClosePanel()
    {
        if (menus.Docked && menus.Current == Menus.Screen.Lobby) { menus.Show(Menus.Screen.None); }
        if (match is { IsLobby: true } world) { world.PanelOpen = false; }
    }

    /// <summary>
    /// The lobby as the server describes it: a start staging (the stage counts down over the world) or taken back,
    /// or this player out of it (the world goes, back to the lobby list). A retired run also cancels pending scenes.
    /// </summary>
    private void OnLobbyChanged()
    {
        if (net.Lobby is not { } lobby)
        {
            stage.Close();
            if (match is { IsLobby: true })
            {
                matchGeneration++;
                EndMatch();
                ShowMenus(net.State == NetClient.Status.Connected ? Menus.Screen.Browser : Menus.Screen.Connect);
            }
            return;
        }
        if (lobby is { Phase: LobbyPhase.Waiting, WorldId: 0 } &&
            (match is { IsOver: false } || menus.Current == Menus.Screen.Loading ||
             match == null && net.ActiveMatchId != 0))
        {
            net.RetireMatchInbox(net.ActiveMatchId);
            // As with Finished, navigate after the current notification returns: its delegate snapshot still
            // contains the roaming world's lobby card. A newer setup must keep its own navigation.
            var generation = matchGeneration;
            Callable.From(() =>
            {
                if (generation == matchGeneration && net.Lobby is { Phase: LobbyPhase.Waiting, WorldId: 0 }) ReturnToLobby(null);
            }).CallDeferred();
            return;
        }
        if (lobby.Phase == LobbyPhase.Staging && match is { IsLobby: true })
        {
            ClosePanel();
            stage.ShowCountdown();
        }
        else if (lobby.Phase == LobbyPhase.Waiting && stage.Visible)
        {
            // The creator took the start back (or left): back to roaming.
            stage.Close();
        }
    }

    private void EndMatch()
    {
        if (match != null)
        {
            // Retire replication, views and content before another scene enters.
            match.Session.Dispose();
            match.Free();
            match = null;
        }
    }

    private void ReturnToLobby(string? note)
    {
        matchGeneration++;
        ClosePanel();
        EndMatch();
        stage.Close();
        if (net.State == NetClient.Status.Connected && net.LobbyWorlds && net.Lobby is { WorldId: not 0 } or { Phase: not LobbyPhase.Waiting })
        {
            // The lobby's next world is on its way (or already building: it would have replaced this scene).
            ShowMenus(Menus.Screen.Loading, note ?? "Back to the lobby...");
            return;
        }
        var screen = net.State != NetClient.Status.Connected ? Menus.Screen.Connect : net.Lobby != null ? Menus.Screen.Lobby : Menus.Screen.Browser;
        ShowMenus(screen, note);
    }

    private void OnStatusChanged(NetClient.Status status, string? reason)
    {
        if (status == NetClient.Status.Offline && (match != null || menus.Current == Menus.Screen.Loading))
        {
            ReturnToLobby(reason);
        }
    }
}
