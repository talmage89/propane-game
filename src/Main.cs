using Godot;
using Propane.Core;
using Propane.Dev;
using Propane.Net;
using Propane.Tank;
using Propane.UI;
using Propane.World;

namespace Propane;

/// <summary>
/// The root of the game. Shows the menus over a backdrop, and runs single player (<see cref="Game"/>) or a
/// multiplayer match (<see cref="Match"/>) under them. Started with <c>-- --server</c> (and usually
/// <c>--headless</c>), it runs only the multiplayer server instead.
/// </summary>
public partial class Main : Node
{
    private NetClient net = null!;
    private Menus menus = null!;
    private Node3D? backdrop;
    private Game? game;
    private Match? match;
    private LobbyServer? hostedServer;
    private int matchGeneration;

    public override void _Ready()
    {
        if (DevArgs.Get("server") != null)
        {
            RunServer();
            return;
        }
        InputSetup.Register();
        net = new NetClient { Name = "Net" };
        AddChild(net);
        net.MatchStarting += OnMatchStarting;
        net.StatusChanged += OnStatusChanged;
        menus = new Menus { Name = "Menus", Net = net };
        menus.SinglePlayerRequested += StartSinglePlayer;
        menus.HostRequested += Host;
        AddChild(menus);
        ShowMenus(Menus.Screen.Main);
        if (DisplayServer.WindowGetMode() == DisplayServer.WindowMode.Windowed && DevArgs.Get("windowed") == null)
        {
            Callable.From(() => DisplayServer.WindowSetMode(DisplayServer.WindowMode.Maximized)).CallDeferred();
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (game == null && match == null && @event.IsActionPressed(InputSetup.ToggleFullscreen))
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
        var port = (int)DevArgs.GetFloat("port", Protocol.DefaultPort);
        var server = new LobbyServer
        {
            Name = "Server",
            Port = port,
            SimulatedLag = DevArgs.GetFloat("net-lag", 0) / 2000f,
            StartScore = (int)DevArgs.GetFloat("start-score", 0),
        };
        AddChild(server);
        if (server.Start() != Error.Ok)
        {
            GetTree().Quit(1);
        }
    }

    // ------------------------------------------------------------------ Menus and backdrop

    private void ShowMenus(Menus.Screen screen, string? note = null)
    {
        if (backdrop == null)
        {
            backdrop = new Backdrop { Name = "Backdrop" };
            AddChild(backdrop);
        }
        RenderingServer.GlobalShaderParameterSet("reveal_radius", 100000f);
        Input.MouseMode = Input.MouseModeEnum.Visible;
        menus.Show(screen, note);
    }

    private void HideBackdrop()
    {
        backdrop?.QueueFree();
        backdrop = null;
    }

    /// <summary>The hero shot behind the menus: one venting tank on the white floor, the camera circling it.</summary>
    private partial class Backdrop : Node3D
    {
        private Camera3D camera = null!;
        private float angle = 0.6f;

        public override void _Ready()
        {
            var environment = new VoidEnvironment { Name = "Void" };
            AddChild(environment);
            var tank = PropaneTank.Create();
            tank.Freeze = true;
            AddChild(tank);
            tank.RotationDegrees = new Vector3(0, 35, 0);
            tank.AddChild(new VentJet
            {
                Name = "VentJet",
                Transform = new Transform3D(Basis.LookingAt(Vector3.Back.Rotated(Vector3.Up, 0.5f), Vector3.Up), new Vector3(0.12f, 0.3f, 0.1f)),
            });
            camera = new Camera3D { Name = "Camera", Fov = 50, Current = true };
            AddChild(camera);
            environment.Follow = camera;
            Place();
        }

        public override void _Process(double delta)
        {
            angle += (float)delta * 0.09f;
            Place();
        }

        private void Place()
        {
            // The tank sits left of centre, clear of the menu panel.
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
        game = new Game { Name = "Game" };
        game.ExitRequested += ExitSinglePlayer;
        AddChild(game);
    }

    private void ExitSinglePlayer()
    {
        GetTree().Paused = false;
        Engine.TimeScale = 1.0;
        game?.QueueFree();
        game = null;
        ShowMenus(Menus.Screen.Main);
    }

    // ------------------------------------------------------------------ Multiplayer

    private void Host(int port)
    {
        if (hostedServer == null)
        {
            hostedServer = new LobbyServer { Name = "HostedServer", Port = port };
            AddChild(hostedServer);
            if (hostedServer.Start() != Error.Ok)
            {
                hostedServer.QueueFree();
                hostedServer = null;
                menus.Show(Menus.Screen.Connect, $"Could not open UDP port {port}: is a server already running here?");
                return;
            }
        }
        var color = PlayerSettings.Color;
        net.Connect("127.0.0.1", port, PlayerSettings.Name, color >= 0 ? color : 0);
    }

    private async void OnMatchStarting(MatchSetup setup)
    {
        EndMatch();
        var generation = ++matchGeneration;
        ShowMenus(Menus.Screen.Loading);
        // Let the loading screen draw before the suburb is built, which takes a moment.
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (generation != matchGeneration || net.State != NetClient.Status.Connected)
        {
            return;
        }
        HideBackdrop();
        menus.Show(Menus.Screen.None);
        match = new Match { Name = "Match", Net = net, Setup = setup };
        match.Finished += () => ReturnToLobby(null);
        match.LeaveRequested += () =>
        {
            net.LeaveLobby();
            ReturnToLobby(null);
        };
        AddChild(match);
    }

    private void EndMatch()
    {
        if (match != null)
        {
            match.QueueFree();
            match = null;
        }
    }

    private void ReturnToLobby(string? note)
    {
        matchGeneration++;
        EndMatch();
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
