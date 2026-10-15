using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Propane.Core;
using Propane.Modes;
using Propane.Net;
using Propane.Player;
using Propane.UI;

namespace Propane.Dev;

/// <summary>
/// Render the production lobby/results presenters and the match HUD's leaderboard at 2, 8 or 16 entries without
/// starting a match. <c>--mode=king</c> shows King's lobby and HUD.
/// </summary>
public partial class CapacityUiTests : Node
{
    public override async void _Ready()
    {
        var net = new NetClient();
        var menus = new Menus { Net = net, HostingNote = "Host on this computer · UDP lobby" };
        var results = new ResultsScreen();
        var capture = new CaptureDirector();
        AddChild(net);
        AddChild(menus);
        AddChild(results);
        AddChild(capture);
        var background = new ColorRect { Color = new Color(.95f, .95f, .96f), MouseFilter = Control.MouseFilterEnum.Ignore };
        background.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        var canvas = new CanvasLayer { Layer = -1 };
        AddChild(canvas); canvas.AddChild(background);
        try
        {
            InputSetup.Register();
            var count = (int)DevArgs.GetFloat("players", 16);
            var profiles = Enumerable.Range(0, count).Select(i => new PlayerProfile(1001 + i,
                i == 0 ? "Creator" : $"Participant {i + 1:00}", AppearanceRecord.FromPalette(i))
            { OwnerPeerId = 2 + i, AllocationSlot = i }).ToArray();
            net.Handle(MessageCodec.Encode(new NetMessage(Msg.Welcome, 0, 0, new WelcomeMessage(2, null)), true));
            net.Handle(MessageCodec.Encode(new NetMessage(Msg.LobbyList, 0, 0, new LobbyListMessage(16, Array.Empty<LobbySummary>())), true));
            net.Handle(MessageCodec.Encode(new NetMessage(Msg.LobbyState, 0, 0, new LobbyInfo(1, 2, 180,
                LobbyServer.LobbyPhase.Waiting, profiles, count)
            {
                Mode = DevArgs.Get("mode") == "king"
                    ? new ModeSettings(Modes.ModeRegistry.KingId, 2) : ModeSettings.Default
            }), true));
            menus.Show(Menus.Screen.Lobby);
            await Frames(12).ConfigureAwait(true);
            AssertPanels(menus);
            TestRunnerCount(menus, count);
            await TestLobbyInput(menus).ConfigureAwait(true);
            capture.Screenshot($"lobby_{count}");
            await Frames(12).ConfigureAwait(true);
            menus.Show(Menus.Screen.None);
            var standings = profiles.Select((p, i) => new Standing(p.Id, p.Name, p.Appearance, count - i,
                i != count - 1)).ToArray();
            results.Show(standings, profiles[0].Id, 30, new[] { Modes.TankBankHud.Column });
            await Frames(12).ConfigureAwait(true);
            AssertPanels(results);
            foreach (var name in results.FindChildren("*", "Label", owned: false).OfType<Label>().Where(l => l.Text.Contains("Participant") || l.Text.Contains("Creator")))
                UnitTests.TestRunner.Assert(name.Size.X >= 200, "standing name has no readable width");
            TestRunnerCount(results, count);
            capture.Screenshot($"results_{count}");
            await Frames(12).ConfigureAwait(true);
            results.Visible = false;
            await TestLeaderboard(profiles, capture).ConfigureAwait(true);
            GD.Print($"[capacity UI] PASS {count} lobby/results entries at window {GetWindow().Size}, logical viewport {GetViewport().GetVisibleRect().Size}");
            ShutdownGuard.Quit(GetTree());
        }
        catch (Exception error)
        {
            GD.PushError($"[capacity UI] FAIL {error}");
            ShutdownGuard.Quit(GetTree(), 1);
        }
    }
    private async Task TestLobbyInput(Menus menus)
    {
        var start = menus.FindChildren("*", "Button", owned: false).OfType<Button>().Single(b => b.Text == "Start match");
        var presses = 0;
        start.Pressed += () => presses++;
        for (var step = 0; GetViewport().GuiGetFocusOwner() != start; step++)
        {
            UnitTests.TestRunner.Assert(step < 40, "Tab cannot reach Start match");
            Send(new InputEventKey { Keycode = Key.Tab, PhysicalKeycode = Key.Tab, Pressed = true });
            Send(new InputEventKey { Keycode = Key.Tab, PhysicalKeycode = Key.Tab, Pressed = false });
            await Frames(2).ConfigureAwait(true);
            UnitTests.TestRunner.Assert(GetViewport().GuiGetFocusOwner() is not BaseButton { Disabled: true }, "Tab focused a disabled capacity control");
        }
        Send(new InputEventKey { Keycode = Key.Enter, PhysicalKeycode = Key.Enter, Pressed = true });
        await Frames(1).ConfigureAwait(true);
        Send(new InputEventKey { Keycode = Key.Enter, PhysicalKeycode = Key.Enter, Pressed = false });
        await Frames(2).ConfigureAwait(true);
        UnitTests.TestRunner.Assert(presses == 1, "keyboard confirmation did not reach the lobby button exactly once");
        Send(new InputEventJoypadButton { ButtonIndex = JoyButton.DpadRight, Pressed = true });
        Send(new InputEventJoypadButton { ButtonIndex = JoyButton.DpadRight, Pressed = false });
        UnitTests.TestRunner.Assert(GetViewport().GuiGetFocusOwner() is Button { Text: "Leave lobby" }, "gamepad could not navigate the lobby button row");
        Send(new InputEventJoypadButton { ButtonIndex = JoyButton.DpadLeft, Pressed = true });
        Send(new InputEventJoypadButton { ButtonIndex = JoyButton.DpadLeft, Pressed = false });
        Send(new InputEventJoypadButton { ButtonIndex = JoyButton.A, Pressed = true });
        Send(new InputEventJoypadButton { ButtonIndex = JoyButton.A, Pressed = false });
        await Frames(2).ConfigureAwait(true);
        UnitTests.TestRunner.Assert(presses == 2, "gamepad confirmation did not reach the lobby button exactly once");
        var position = GetViewport().GetFinalTransform() * start.GetGlobalRect().GetCenter();
        Send(new InputEventMouseMotion { Position = position, GlobalPosition = position });
        Send(new InputEventMouseButton { Position = position, GlobalPosition = position, ButtonIndex = MouseButton.Left, Pressed = true });
        await Frames(1).ConfigureAwait(true);
        Send(new InputEventMouseButton { Position = position, GlobalPosition = position, ButtonIndex = MouseButton.Left, Pressed = false });
        await Frames(2).ConfigureAwait(true);
        UnitTests.TestRunner.Assert(presses == 3, "mouse confirmation did not reach the scaled lobby button exactly once");
        GD.Print("[capacity UI] PASS real keyboard, mouse and gamepad delivery");
    }
    /// <summary>The match HUD's leaderboard over the real HUD: local inside and outside the top rows, and a climb mid-slide.</summary>
    private async Task TestLeaderboard(PlayerProfile[] profiles, CaptureDirector capture)
    {
        PlayerSettings.UseFile("user://capacity_ui_settings.cfg");
        PlayerSettings.ShowLeaderboard = true;
        using var world = new WorldContext(this, Tuning.SavedTemplate);
        var hud = new MatchHud { World = world };
        AddChild(hud);
        var count = profiles.Length;
        var king = DevArgs.Get("mode") == "king";
        IModeHudPresenter presenter = king ? new KingHud() : new TankBankHud();
        var roster = profiles.Select(p => new LeaderboardEntry(p.Id, p.Name, p.Appearance.Color)).ToList();
        // Descending banks with third and fourth tied.
        var scores = profiles.Select((p, i) => (p.Id, Score: 2 * (count - i) + (i == 3 ? 2 : 0))).ToDictionary(p => p.Id, p => p.Score);
        var panel = hud.FindChildren("Leaderboard", "", owned: false).OfType<LeaderboardPanel>().Single();
        var rows = Math.Min(count, world.Tuning.LeaderboardRows);
        var label = king ? $"king_{count}" : $"{count}";
        hud.SetAmmo(new AmmoState { Tuning = world.Tuning, Magazine = 12, Reserve = 30 });

        async Task Show(int local, int frames)
        {
            for (var frame = 0; frame < frames; frame++)
            {
                hud.Show(presenter.Present(new ModeHudInput(local, ModePhase.Round, 95, scores, Array.Empty<Vector3>(), false)));
                hud.SetStandings(roster, scores, local, live: true);
                await Frames(1).ConfigureAwait(true);
            }
        }
        void Check(int local, bool detached)
        {
            var board = panel.Board;
            UnitTests.TestRunner.Assert(panel.Visible && board.TopCount == rows && board.HasDetached == detached &&
                board.Rows.Count(r => r.Local) == 1 && board.Rows.Single(r => r.Local).Entry.Id == local, $"leaderboard rows {board.Rows.Count} local {local}");
            var bounds = panel.Bounds;
            var size = GetViewport().GetVisibleRect().Size;
            // Inside the screen and clear of the top-center clock and bank (320 wide) and the crosshair.
            UnitTests.TestRunner.Assert(bounds.Position.X >= 0 && bounds.Position.Y >= 0 && bounds.End.Y < size.Y / 2 - 60 &&
                bounds.End.X + 60 < size.X / 2 - 160, $"leaderboard overlaps the HUD: {bounds}, viewport {size}");
        }

        var inside = profiles[Math.Min(2, count - 1)].Id;
        await Show(inside, 30).ConfigureAwait(true);
        Check(inside, detached: false);
        capture.Screenshot($"leaderboard_{label}_inside");
        if (count > rows)
        {
            var outside = profiles[count - 2].Id;
            await Show(outside, 30).ConfigureAwait(true);
            Check(outside, detached: true);
            capture.Screenshot($"leaderboard_{label}_outside");
            // The fifth climbs to first: mid-slide, with its flash and "+N".
            scores[profiles[rows - 1].Id] += 2 * count;
            await Show(outside, 6).ConfigureAwait(true);
            capture.Screenshot($"leaderboard_{label}_climb");
            await Show(outside, 30).ConfigureAwait(true);
            UnitTests.TestRunner.Assert(panel.Board.Rows[0].Entry.Id == profiles[rows - 1].Id && panel.Board.Motion(profiles[rows - 1].Id)!.Slot < 0.01f,
                "climber did not settle first");
        }
        PlayerSettings.ShowLeaderboard = false;
        await Show(inside, 30).ConfigureAwait(true);
        UnitTests.TestRunner.Assert(!panel.Visible, "the player's setting did not hide the leaderboard");
        hud.Free();
        GD.Print($"[capacity UI] PASS leaderboard at {count} players");
    }

    private static void Send(InputEvent input)
    {
        Input.ParseInputEvent(input);
        Input.FlushBufferedEvents();
    }
    private static void TestRunnerCount(Node root, int count) => UnitTests.TestRunner.Assert(
        root.FindChildren("*", "ColorRect", owned: false).OfType<ColorRect>().Count(c => c.HasMeta("appearance_player")) == count,
        "presenter lost roster entries");
    private void AssertPanels(Node root)
    {
        var size = GetViewport().GetVisibleRect().Size;
        foreach (var panel in root.FindChildren("*", "PanelContainer", owned: false).OfType<PanelContainer>())
        {
            var rect = panel.GetGlobalRect();
            UnitTests.TestRunner.Assert(rect.Position.X >= 0 && rect.Position.Y >= 0 && rect.End.X <= size.X && rect.End.Y <= size.Y,
                $"panel overflow: {rect}, viewport {size}");
        }
    }
    private async Task Frames(int count)
    {
        for (var i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }
}
