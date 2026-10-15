using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Propane.Net;
using Propane.UI;

namespace Propane.Dev;

/// <summary>
/// <c>menu_test --scenario=lobby</c>: the lobby world through the real front end. Hosting and creating a lobby enters
/// its world; Tab or View opens the panel docked beside it, and Esc, B or its Close button closes it, with its settings
/// set by keyboard, controller and mouse; a second client's player arrives and leaves; a bot added in the panel roams;
/// the creator's start stages a countdown everyone sees, which Esc or B takes back; the match loads and, after its
/// results, everyone is back in a new world. Run rendered with <c>--capture-dir</c> for the review captures; it also
/// runs headlessly.
/// </summary>
public partial class MenuGamepadTest
{
    private Match? LobbyWorld => Main.GetNodeOrNull<Match>("LobbyWorld");
    private StageScreen Stage => Main.GetNode<StageScreen>("Stage");

    private async Task LobbyWorldScenario()
    {
        await Frames().ConfigureAwait(true);
        CheckFocus("Single player");
        await Press(JoyButton.DpadDown).ConfigureAwait(true);
        await Press(JoyButton.A).ConfigureAwait(true);
        await Navigate("Host on this computer").ConfigureAwait(true);
        await Press(JoyButton.A).ConfigureAwait(true);
        await Wait(() => Menus.Current == Menus.Screen.Browser, "host browser opened").ConfigureAwait(true);
        await Press(JoyButton.A).ConfigureAwait(true);
        await Wait(() => LobbyWorld is { IsPlaying: true } && Menus.Current == Menus.Screen.None, "creating a lobby enters its world").ConfigureAwait(true);
        var world = LobbyWorld!;
        Check(world.IsLobby && world.LiveTanks.Count() == Core.Tuning.SavedTemplate.LobbyPiles * Core.Tuning.SavedTemplate.LobbyPileCount,
            "the world stands every pile");
        CheckMouse(Input.MouseModeEnum.Captured);
        Check(Labels().Contains(LobbyText.Title(Net.Lobby!)), "the lobby card names the lobby");
        await Frames(60).ConfigureAwait(true);
        await Shot("01_arrival").ConfigureAwait(true);
        await Frames(150).ConfigureAwait(true);
        await Shot("02_roaming").ConfigureAwait(true);

        // The panel: Tab opens it beside the world; it is the lobby menu, with its focus and controls.
        await Tap(Key.Tab).ConfigureAwait(true);
        await Wait(() => Menus.Current == Menus.Screen.Lobby && Menus.Docked && world.PanelOpen, "Tab opens the docked panel").ConfigureAwait(true);
        CheckFocus("Name");
        CheckMouse(Input.MouseModeEnum.Visible);
        Check(Reachable().ContainsKey("Start match") && Reachable().ContainsKey("tank_supply") && Reachable().ContainsKey("add_bot"),
            "the creator's panel holds the lobby's settings");
        await Shot("03_panel").ConfigureAwait(true);
        await KeyFocus("tank_supply").ConfigureAwait(true);
        await Tap(Key.Right).ConfigureAwait(true);
        await Wait(() => Net.Lobby!.Mode.TankSupply == 2, "the creator changes tank supply from the panel").ConfigureAwait(true);
        await Tap(Key.Escape).ConfigureAwait(true);
        await Wait(() => Menus.Current == Menus.Screen.None && !world.PanelOpen, "Esc closes the panel").ConfigureAwait(true);
        CheckMouse(Input.MouseModeEnum.Captured);

        // The same by controller (View opens it, the D-pad sets, B closes) and by mouse (its buttons and Close).
        await Press(JoyButton.Back).ConfigureAwait(true);
        await Wait(() => Menus.Current == Menus.Screen.Lobby && Menus.Docked && world.PanelOpen, "View opens the docked panel").ConfigureAwait(true);
        await Navigate("tank_supply").ConfigureAwait(true);
        await Press(JoyButton.DpadRight).ConfigureAwait(true);
        await Wait(() => Net.Lobby!.Mode.TankSupply == 3, "the D-pad changes tank supply").ConfigureAwait(true);
        await Press(JoyButton.B).ConfigureAwait(true);
        await Wait(() => Menus.Current == Menus.Screen.None && !world.PanelOpen, "B closes the panel").ConfigureAwait(true);
        await Tap(Key.Tab).ConfigureAwait(true);
        await Wait(() => Menus.Current == Menus.Screen.Lobby && Menus.Docked, "Tab reopens the panel").ConfigureAwait(true);
        await KingClick("tank_less").ConfigureAwait(true);
        await Wait(() => Net.Lobby!.Mode.TankSupply == 2, "a click changes tank supply").ConfigureAwait(true);
        await KingClick("close_panel").ConfigureAwait(true);
        await Wait(() => Menus.Current == Menus.Screen.None && !world.PanelOpen, "Close closes the panel").ConfigureAwait(true);
        CheckMouse(Input.MouseModeEnum.Captured);

        // A second player joins the world (a guest that builds nothing: its character stands where it arrives).
        var guest = new NetClient { Name = "LobbyGuest", LobbyWorlds = false };
        AddChild(guest);
        guest.Connect("127.0.0.1", Menus.HostPort, "Guest", AppearanceRecord.FromPalette(5));
        await Wait(() => guest.State == NetClient.Status.Connected && guest.Lobbies.Count == 1, "guest sees the lobby").ConfigureAwait(true);
        guest.JoinLobby(guest.Lobbies[0].Id);
        await Wait(() => world.RemoteBodies.Count() == 1, "the guest arrives in the world").ConfigureAwait(true);
        Check(guest.Lobby!.Mode.TankSupply == 2 && !guest.IsCreator, "the guest sees the creator's settings");
        guest.SetMode(guest.Lobby.Mode with { TankSupply = 5 });
        await Frames(20).ConfigureAwait(true);
        Check(Net.Lobby!.Mode.TankSupply == 2, "only the creator changes settings");
        await Shot("04_player_joined").ConfigureAwait(true);

        // A bot added in the panel arrives in the world with its brain.
        await Tap(Key.Tab).ConfigureAwait(true);
        await Wait(() => Menus.Current == Menus.Screen.Lobby && Menus.Docked, "panel reopens").ConfigureAwait(true);
        await KeyFocus("add_bot").ConfigureAwait(true);
        await Tap(Key.Enter).ConfigureAwait(true);
        await Wait(() => Editor != null, "Enter opens the bot dialog").ConfigureAwait(true);
        await KeyFocus("bot_save").ConfigureAwait(true);
        await Tap(Key.Enter).ConfigureAwait(true);
        await Wait(() => Editor == null && world.Session.OwnedActors.Count() == 2 && world.BotBrain(MyBot("Bot 1")!.Id) != null,
            "the panel's bot roams with a brain").ConfigureAwait(true);
        await Tap(Key.Escape).ConfigureAwait(true);
        await Wait(() => Menus.Current == Menus.Screen.None, "panel closes after adding a bot").ConfigureAwait(true);
        await Frames(120).ConfigureAwait(true);
        await Shot("05_bot_roaming").ConfigureAwait(true);

        // The start stages a countdown over the world; the creator takes it back once, then lets it run.
        guest.LeaveLobby();
        await Wait(() => world.RemoteBodies.Count() == 0, "the guest leaves the world").ConfigureAwait(true);
        await Tap(Key.Tab).ConfigureAwait(true);
        await Wait(() => Menus.Current == Menus.Screen.Lobby && Menus.Docked, "panel opens to start").ConfigureAwait(true);
        await KeyFocus("Start match").ConfigureAwait(true);
        await Tap(Key.Enter).ConfigureAwait(true);
        await Wait(() => Net.Lobby!.Phase == LobbyServer.LobbyPhase.Staging && Stage.Visible && Menus.Current == Menus.Screen.None, "start stages a countdown").ConfigureAwait(true);
        Check(world.Session.OwnedActors.All(a => a.Character.InputLocked), "the stage holds everyone still");
        await Shot("06_stage").ConfigureAwait(true);
        await Tap(Key.Escape).ConfigureAwait(true);
        await Wait(() => Net.Lobby!.Phase == LobbyServer.LobbyPhase.Waiting && !Stage.Visible, "Esc takes the start back").ConfigureAwait(true);
        await Wait(() => world.Session.OwnedActors.All(a => !a.Character.InputLocked), "roaming resumes").ConfigureAwait(true);
        await Press(JoyButton.Back).ConfigureAwait(true);
        await Wait(() => Menus.Current == Menus.Screen.Lobby && Menus.Docked, "View opens the panel to start").ConfigureAwait(true);
        await Navigate("Start match").ConfigureAwait(true);
        await Press(JoyButton.A).ConfigureAwait(true);
        await Wait(() => Net.Lobby!.Phase == LobbyServer.LobbyPhase.Staging && Stage.Visible, "A starts from the panel").ConfigureAwait(true);
        await Press(JoyButton.B).ConfigureAwait(true);
        await Wait(() => Net.Lobby!.Phase == LobbyServer.LobbyPhase.Waiting && !Stage.Visible, "B takes the start back").ConfigureAwait(true);
        await Tap(Key.Tab).ConfigureAwait(true);
        await Wait(() => Menus.Current == Menus.Screen.Lobby && Menus.Docked, "panel opens to start again").ConfigureAwait(true);
        await KeyFocus("Start match").ConfigureAwait(true);
        await Tap(Key.Enter).ConfigureAwait(true);
        await Wait(() => Stage.Visible, "the stage counts down again").ConfigureAwait(true);
        await Frames(70).ConfigureAwait(true);
        await Shot("07_stage_countdown").ConfigureAwait(true);
        await Wait(() => Main.HasNode("Match") && LobbyWorld == null, "the world gives way to the match", seconds: 20).ConfigureAwait(true);
        await Wait(() => Main.GetNode<Match>("Match").IsPlaying && !Stage.Visible, "the match plays").ConfigureAwait(true);
        Check(Main.GetNode<Match>("Match").World.Tuning.TankCountMultiplier == 2, "the match plays the panel's settings");
        await Frames(30).ConfigureAwait(true);
        await Shot("08_match").ConfigureAwait(true);

        // After the results, everyone is back in a new world, with the bot.
        Main.GetNode<LobbyServer>("HostedServer").AdvanceMatches(MonotonicClock.Now + 2000);
        await Wait(() => Main.GetNode<Match>("Match").IsOver, "server ended the match").ConfigureAwait(true);
        await Frames(30).ConfigureAwait(true);
        await Shot("09_results").ConfigureAwait(true);
        Main.GetNode<LobbyServer>("HostedServer").AdvanceMatches(MonotonicClock.Now + 4000);
        await Wait(() => LobbyWorld is { IsPlaying: true } back && back != world && !Main.HasNode("Match"), "back in a new lobby world after results", seconds: 30).ConfigureAwait(true);
        Check(LobbyWorld!.Session.OwnedActors.Count() == 2 && Net.Lobby!.Members.Count == 2, "membership is kept");
        await Frames(60).ConfigureAwait(true);
        await Shot("10_return").ConfigureAwait(true);

        // Leaving from the pause menu tears the world down for the lobby list.
        await Tap(Key.Escape).ConfigureAwait(true);
        await Wait(() => LobbyWorld!.GetNodeOrNull<MatchMenu>("Menu") is { Visible: true }, "Esc opens the lobby world's menu").ConfigureAwait(true);
        Check(Labels().Contains("You are in the lobby"), "the menu says where you are");
        await KeyFocus("Leave lobby").ConfigureAwait(true);
        await Tap(Key.Enter).ConfigureAwait(true);
        await Wait(() => LobbyWorld == null && Menus.Current == Menus.Screen.Browser, "leaving the lobby returns to the browser").ConfigureAwait(true);
        guest.Disconnect();
        guest.QueueFree();
    }
}
