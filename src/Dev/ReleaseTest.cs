using System.Linq;
using Godot;
using Propane.Core;
using Propane.Net;
using Propane.UI;

namespace Propane.Dev;

/// <summary>
/// Exercises the real release menus and single player, or joins a server and plays a match against another client.
/// --scenario=network --connect=host:port creates a lobby and waits for --players (default 2).
/// Exported archives exclude dev scenes and disallow scene overrides: use --release-test on the normal main scene.
/// Every stage has a timeout and a failing exit code. Screenshots use --capture-dir.
/// </summary>
public partial class ReleaseTest : Node
{
    private Main main = null!;
    private CaptureDirector director = null!;
    private int stage;
    private double elapsed;
    private double deadline = 15;
    private double nextStage;
    private bool network;
    private bool sawPlaying;
    private bool roamingLobby;
    private bool finished;
    private double playingShot;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
        Engine.MaxFps = 60;
        Core.PlayerSettings.UseFile("user://release_test_settings.cfg");
        network = DevArgs.Get("scenario") == "network";
        main = new Main { Name = "Main" };
        AddChild(main);
        director = new CaptureDirector();
        AddChild(director);
    }

    public override void _Process(double delta)
    {
        elapsed += delta;
        if (elapsed > deadline)
        {
            Fail($"stage {stage} timed out");
            return;
        }
        if (elapsed < nextStage)
        {
            return;
        }
        if (stage == 0)
        {
            if (network)
            {
                var net = main.GetNode<NetClient>("Net");
                if (net.State != NetClient.Status.Connected || !Press("Create lobby"))
                {
                    return;
                }
                Advance(60);
            }
            else if (FindButton("Single player") != null && elapsed >= 2)
            {
                director.Screenshot("01_main_menu");
                if (!Press("Single player"))
                {
                    Fail("single player button missing");
                    return;
                }
                main.GetNode<Game>("Game").PauseOnFocusLoss = false;
                main.GetNode<Game>("Game").World.SetPaused(false);
                Advance(20, 6);
            }
        }
        else if (stage == 1)
        {
            if (!network)
            {
                var game = main.GetNodeOrNull<Game>("Game");
                if (game?.Current == null || !game.Current.Plan.Tanks.Any())
                {
                    Fail("single player did not build a suburb with tanks");
                    return;
                }
                director.Screenshot("02_single_player");
                GD.Print($"[releasetest] PASS main menu and single player ({game.Current.Plan.Tanks.Count} tanks)");
                ShutdownGuard.Quit(GetTree());
                stage = 99;
                return;
            }
            var net = main.GetNode<NetClient>("Net");
            if (net.Lobby is { } lobby && lobby.Members.Count >= DevArgs.GetFloat("players", 2))
            {
                roamingLobby = net.LobbyWorlds && lobby.WorldId != 0;
                if (roamingLobby)
                {
                    if (main.GetNodeOrNull<Match>("LobbyWorld") is not { IsPlaying: true } world) { return; }
                    if (!world.PanelOpen)
                    {
                        // Use device delivery, as the player does, and let the panel receive a frame before pressing it.
                        Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Key.Tab, Keycode = Key.Tab, Pressed = true });
                        Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Key.Tab, Keycode = Key.Tab, Pressed = false });
                        nextStage = elapsed + 0.1;
                        return;
                    }
                    if (main.GetNode<Menus>("Menus") is not { Current: Menus.Screen.Lobby, Docked: true })
                    {
                        Fail("lobby panel did not open");
                        return;
                    }
                }
                director.Screenshot("02_network_lobby");
                net.SetMatchLength(DevArgs.GetFloat("length", 30));
                if (!Press("Start match"))
                {
                    Fail("start match button missing");
                    return;
                }
                Advance(30);
            }
        }
        else if (stage == 2)
        {
            var match = main.GetNodeOrNull<Match>("Match");
            if (match == null || !match.IsPlaying)
            {
                return;
            }
            if (!match.RemoteBodies.Any() || !match.LiveTanks.Any())
            {
                Fail("match is missing remote players or tanks");
                return;
            }
            match.Player.InputOverride = new Bot();
            match.Finished += () =>
            {
                finished = true;
                GD.Print("[releasetest] final scores " + string.Join(" ", match.Scores.Select(s => $"{s.Key}={s.Value}")));
            };
            sawPlaying = true;
            playingShot = elapsed + 12;
            director.Screenshot("03_cross_platform_match");
            GD.Print($"[releasetest] playing with {match.RemoteBodies.Count()} remote players, {match.LiveTanks.Count()} tanks");
            Advance(DevArgs.GetFloat("length", 30) + 30);
        }
        else if (stage == 3)
        {
            var match = main.GetNodeOrNull<Match>("Match");
            if (match != null)
            {
                if (elapsed >= playingShot)
                {
                    director.Screenshot("04_playing");
                    playingShot = double.PositiveInfinity;
                }
                return;
            }
            var returned = roamingLobby
                ? main.GetNodeOrNull<Match>("LobbyWorld") is { IsLobby: true, IsPlaying: true } &&
                  main.GetNode<Menus>("Menus").Current == Menus.Screen.None
                : main.GetNode<Menus>("Menus").Current == Menus.Screen.Lobby;
            if (sawPlaying && finished && returned && main.GetNode<NetClient>("Net").Lobby is { Phase: LobbyServer.LobbyPhase.Waiting })
            {
                // Let the returned scene draw before requesting its proof screenshot.
                Advance(5, 0.25);
            }
        }
        else if (stage == 4)
        {
            director.Screenshot("05_returned_to_lobby");
            GD.Print("[releasetest] PASS cross-platform match finished and returned to lobby");
            ShutdownGuard.Quit(GetTree());
            stage = 99;
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        if (stage == 3 && main.GetNodeOrNull<Match>("Match") is { } match)
        {
            ((Bot)match.Player.InputOverride!).Think(match, (float)delta);
        }
    }

    private void Advance(double timeout, double delay = 0)
    {
        stage++;
        deadline = elapsed + timeout;
        nextStage = elapsed + delay;
    }

    private Button? FindButton(string text) => GetTree().Root.FindChildren("*", "Button", owned: false).OfType<Button>()
        .FirstOrDefault(b => b.Text == text && b.IsVisibleInTree() && !b.Disabled);

    private bool Press(string text)
    {
        var button = FindButton(text);
        button?.EmitSignal(BaseButton.SignalName.Pressed);
        return button != null;
    }

    private void Fail(string reason)
    {
        GD.PushError($"[releasetest] FAIL {reason}");
        ShutdownGuard.Quit(GetTree(), 1);
        stage = 99;
    }
}
