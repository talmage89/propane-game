using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using Propane.Core;
using Propane.Dev.UnitTests;
using Propane.Dev.UnitTests.Fixtures;
using Propane.Modes;
using Propane.Net;
using Propane.Player;
using Propane.ReleaseSmoke;
using Propane.UI;

namespace Propane.Dev;

/// <summary>Real Main, hosted ENet server and an independent lobby. Injects an owner failure through server dispatch,
/// then uses the recovered front end to start another match. All subscriptions die with this scene.</summary>
public partial class MenuRetirementTest : Node
{
    public required Main Main { get; init; }
    private NetClient? other;
    private Menus Menus => Main.GetNode<Menus>("Menus");
    private NetClient Net => Main.GetNode<NetClient>("Net");
    private CaptureDirector capture = null!;
    private readonly IMonotonicClock clock = MonotonicClock.Instance;
    private static void Check(bool value, string description)
    {
        TestRunner.Assert(value, description);
        GD.Print("[retirement] PASS " + description);
    }

    public override async void _Ready()
    {
        capture = new CaptureDirector();
        AddChild(capture);
        try
        {
            Main.SavedTuning.CountdownTime = 0;
            Main.SavedTuning.LobbyStageTime = DevArgs.Get("retirement-case") == "loading" ? 1 : 0;
            await Frames(3).ConfigureAwait(true);
            await Activate("Multiplayer").ConfigureAwait(true);
            await Activate("Host on this computer").ConfigureAwait(true);
            await Wait(() => Menus.Current == Menus.Screen.Browser, "host browser").ConfigureAwait(true);
            var server = Main.GetNode<LobbyServer>("HostedServer");
            var runs = new System.Collections.Generic.List<ServerRunFixture.Release>();
            server.RunCreated = run => runs.Add(new ServerRunFixture.Release(server, run));
            await Activate("Create lobby").ConfigureAwait(true);
            await Wait(() => Net.Lobby != null && (Net.LobbyWorlds ? Main.HasNode("LobbyWorld") : Menus.Current == Menus.Screen.Lobby), "initial lobby").ConfigureAwait(true);
            var sendCase = DevArgs.Get("retirement-case");
            if (sendCase == "loading")
            {
                await LoadingFailure(server).ConfigureAwait(true);
                return;
            }
            if (sendCase == "send-menu")
            {
                await SendFailure(gameplay: false).ConfigureAwait(true);
                return;
            }
            var worldFailure = DevArgs.Get("retirement-case") == "world";
            if (!worldFailure)
            {
                if (Net.LobbyWorlds) Tap(Key.Tab);
                await Frames(3).ConfigureAwait(true);
                await Activate("Start match").ConfigureAwait(true);
                await Wait(() => Main.HasNode("Match") && Main.GetNode<Match>("Match").IsPlaying, "first match playing").ConfigureAwait(true);
            }
            if (sendCase == "send-game")
            {
                await SendFailure(gameplay: true).ConfigureAwait(true);
                return;
            }
            var scene = Main.GetNode<Match>(worldFailure ? "LobbyWorld" : "Match");
            var world = scene.World;
            var failed = runs.Single(r => r.Run.Id == scene.Setup.Id);
            other = new NetClient(PacketTransports.CreateClient(), ModeRegistry.Codec) { LobbyWorlds = false };
            AddChild(other);
            other.Connect("127.0.0.1", server.Port, "Independent", 1);
            await Wait(() => other.State == NetClient.Status.Connected, "independent connection").ConfigureAwait(true);
            other.CreateLobby();
            await Wait(() => other.Lobby != null, "independent lobby").ConfigureAwait(true);
            other.StartMatch();
            await Wait(() => other.ActiveMatchId != 0, "independent match").ConfigureAwait(true);
            other.Send(new NetWriter(Msg.Loaded, other.ActiveMatchId));
            var unrelated = runs.Single(r => r.Run.Id == other.ActiveMatchId);
            var clocks = 0;
            other.AttachMatch(other.ActiveMatchId, message => { if (message.Type == Msg.Clock) clocks++; });
            await Wait(() => clocks > 0, "independent clocks before failure").ConfigureAwait(true);
            using var log = new NetLogCapture();
            // Forward healthy traffic directly through the same native adapter. Throw only on the affected peer's
            // Pong: LobbyServer's real dispatch guard must select and retire that peer's lobby owner.
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var transport = (IPacketTransport)typeof(LobbyServer).GetField("transport", flags)!.GetValue(server)!;
            var peers = (ProtocolPeerMap)typeof(LobbyServer).GetField("peerIds", flags)!.GetValue(server)!;
            server.SendOverride = (peer, bytes, traffic) =>
            {
                if (peer == Net.MyId && (Msg)bytes[0] == Msg.Pong)
                {
                    server.SendOverride = null;
                    throw new InvalidOperationException("injected native owner retirement");
                }
                TestRunner.Assert(transport.Send(peers.Connection(peer)!, traffic, bytes) == SendResult.Success, "healthy native forwarding");
            };
            Net.Send(new NetWriter(Msg.Ping).Double(0));
            await Wait(() => failed.Run.IsRetired && Net.Lobby is { Phase: LobbyPhase.Waiting, WorldId: 0 }, "server failure delivered").ConfigureAwait(true);
            failed.AssertOnce();
            var before = clocks;
            await Wait(() => clocks > before, "independent clocks after failure").ConfigureAwait(true);
            unrelated.AssertLive();
            Check(log.Lines.Any(line => line.Contains("injected native owner retirement", StringComparison.Ordinal)), "real server failure cause logged");
            capture.Screenshot(worldFailure ? "world_failure_received" : "match_failure_received");
            await Wait(() => !world.IsAlive && Menus.Current == Menus.Screen.Lobby, "client recovered usable lobby").ConfigureAwait(true);
            Check(Net.State == NetClient.Status.Connected && Net.ActiveMatchId == 0, "connection survives and retired inbox clears");
            await Frames(4).ConfigureAwait(true);
            capture.Screenshot(worldFailure ? "world_recovered_lobby" : "match_recovered_lobby");
            await Activate("Start match").ConfigureAwait(true);
            await Wait(() => Main.HasNode("Match") && Main.GetNode<Match>("Match").IsPlaying, "replacement match playing").ConfigureAwait(true);
            Check(Main.GetNode<Match>("Match").Setup.Id > failed.Run.Id, "recovered menu starts a fresh match");
            unrelated.AssertLive();
            Check(other.State == NetClient.Status.Connected, "unrelated lobby stays connected and running");
            GD.Print("[retirement] PASS native recovery " + (worldFailure ? "world" : "match"));
            ShutdownGuard.Quit(GetTree());
        }
        catch (Exception error)
        {
            GD.Print("[retirement] FAIL " + error);
            ShutdownGuard.Quit(GetTree(), 1);
        }
    }
    private async Task LoadingFailure(LobbyServer server)
    {
        using var log = new NetLogCapture();
        var stagedLoading = false;
        void Starting(MatchSetup setup)
        {
            if (setup.ModeId != ModeIds.Lobby) stagedLoading |= Main.GetNode<StageScreen>("Stage").Visible;
        }
        Net.MatchStarting += Starting;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var transport = (IPacketTransport)typeof(LobbyServer).GetField("transport", flags)!.GetValue(server)!;
        var peers = (ProtocolPeerMap)typeof(LobbyServer).GetField("peerIds", flags)!.GetValue(server)!;
        // The real start has sent its setup, but its loading lobby state fails before the client can assemble it.
        server.SendOverride = (peer, bytes, traffic) =>
        {
            if (peer == Net.MyId && (Msg)bytes[0] == Msg.LobbyState &&
                Net.Codec.Decode(bytes, true).Payload is LobbyInfo { Phase: LobbyPhase.Loading })
            {
                server.SendOverride = null;
                throw new InvalidOperationException("injected staged loading owner failure");
            }
            TestRunner.Assert(transport.Send(peers.Connection(peer)!, traffic, bytes) == SendResult.Success, "loading native forwarding");
        };
        Tap(Key.Tab);
        await Frames(3).ConfigureAwait(true);
        await Activate("Start match").ConfigureAwait(true);
        await Wait(() => Net.Lobby is { Phase: LobbyPhase.Waiting, WorldId: 0 } &&
            log.Lines.Any(line => line.Contains("injected staged loading owner failure", StringComparison.Ordinal)), "loading failure delivered").ConfigureAwait(true);
        Check(stagedLoading, "failure supersedes a staged loading screen");
        capture.Screenshot("loading_failure_received");
        await Wait(() => Menus.Current == Menus.Screen.Lobby && Net.ActiveMatchId == 0, "loading failure returned to lobby").ConfigureAwait(true);
        await Frames(5).ConfigureAwait(true);
        Check(!Main.HasNode("Match") && !Main.HasNode("LobbyWorld"), "abandoned construction cannot resurrect its scene");
        capture.Screenshot("loading_recovered_lobby");
        Net.MatchStarting -= Starting;
        await Activate("Start match").ConfigureAwait(true);
        await Wait(() => Main.HasNode("Match") && Main.GetNode<Match>("Match").IsPlaying, "match after loading failure").ConfigureAwait(true);
        Check(true, "loading recovery starts a fresh match");
        ShutdownGuard.Quit(GetTree());
    }

    private async Task SendFailure(bool gameplay)
    {
        using var log = new NetLogCapture();
        var field = typeof(NetClient).GetField("transport", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var injected = new InjectedPacketTransport((IPacketTransport)field.GetValue(Net)!)
        {
            FailureType = gameplay ? Msg.Shot : Msg.StartMatch,
            AfterAcceptance = DevArgs.Get("send-after") != null,
        };
        field.SetValue(Net, injected);
        var failureFrame = ulong.MaxValue;
        var retiredFrame = ulong.MaxValue;
        injected.OnFailure = () => failureFrame = Engine.GetProcessFrames();
        void Retired(NetClient.Status status, string? _)
        {
            if (status == NetClient.Status.Offline) retiredFrame = Engine.GetProcessFrames();
        }
        Net.StatusChanged += Retired;
        if (DevArgs.Get("send-throw") != null) injected.Failure = new InvalidOperationException("injected native client send");
        else injected.Result = SendResult.Backpressure;
        var world = gameplay ? Main.GetNode<Match>("Match").World : null;
        // Prove the deferred same-frame boundary independently of either network poll callback.
        Net.SetProcess(false);
        Net.SetPhysicsProcess(false);
        if (gameplay)
        {
            var match = Main.GetNode<Match>("Match");
            if (DisplayServer.GetName() == "headless")
                ActorView.For(match.Player)!.AssignDevice(new DeviceInput(match.World.Tuning, () => true));
            Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true });
            Input.FlushBufferedEvents();
        }
        else
        {
            await Activate("Start match").ConfigureAwait(true);
        }

        await Wait(() => Net.State == NetClient.Status.Offline && Menus.Current == Menus.Screen.Connect, "send failure returned to connection menu").ConfigureAwait(true);
        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false });
        Input.FlushBufferedEvents();
        Net.StatusChanged -= Retired;
        Check(failureFrame == retiredFrame, "deferred send retirement occurs in the same process frame");
        Check(injected.Faults == 1 && (world == null || !world.IsAlive), "native send retires once after its origin returns");
        Check(log.Lines.Any(line => line.Contains(DevArgs.Get("send-throw") != null ? "injected native client send" : "Backpressure", StringComparison.Ordinal)),
            "real native send failure cause logged");
        capture.Screenshot(gameplay ? "send_game_recovered_menu" : "send_ui_recovered_menu");
        Net.SetProcess(true);
        Net.SetPhysicsProcess(true);
        Menus.ConnectTo("127.0.0.1:" + Menus.HostPort, "Recovered");
        await Wait(() => Menus.Current == Menus.Screen.Browser, "reconnected browser").ConfigureAwait(true);
        await Activate("Create lobby").ConfigureAwait(true);
        await Wait(() => Menus.Current == Menus.Screen.Lobby, "reconnected lobby").ConfigureAwait(true);
        await Activate("Start match").ConfigureAwait(true);
        await Wait(() => Main.HasNode("Match") && Main.GetNode<Match>("Match").IsPlaying, "reconnected match").ConfigureAwait(true);
        Check(true, "native send recovery starts a fresh match");
        ShutdownGuard.Quit(GetTree());
    }

    private async Task Activate(string text)
    {
        var button = Main.FindChildren("*", "Button", owned: false).OfType<Button>()
            .FirstOrDefault(b => b.Text == text && b.IsVisibleInTree() && !b.Disabled);
        TestRunner.Assert(button != null, "missing enabled button " + text);
        button!.GrabFocus();
        Tap(Key.Enter);
        await Frames(3).ConfigureAwait(true);
    }
    private static void Tap(Key key)
    {
        Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = key, Keycode = key, Pressed = true });
        Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = key, Keycode = key, Pressed = false });
    }
    private async Task Wait(Func<bool> ready, string outcome)
    {
        var deadline = clock.Seconds + 15;
        while (clock.Seconds < deadline)
        {
            if (ready()) return;
            await Frames(1).ConfigureAwait(true);
        }
        throw new TimeoutException(outcome);
    }
    private async Task Frames(int count)
    {
        for (var i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }
    public override void _ExitTree() => other?.Disconnect();
}
