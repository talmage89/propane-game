using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Propane.Core;
using Propane.Net;
using Propane.Player;
using Propane.UI;

namespace Propane.Dev;

/// <summary>Real root-viewport device events through the menus and both production world facades.</summary>
public partial class MenuDeviceInputTest : Node
{
    public required Main Main { get; init; }
    private CaptureDirector capture = null!;
    private int checks;
    private int failures;
    private Menus Menus => Main.GetNode<Menus>("Menus");

    public override async void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        capture = new CaptureDirector();
        AddChild(capture);
        try
        {
            Main.SavedTuning.FixedSeed = 23;
            await Frames().ConfigureAwait(true);
            await Activate("Single player").ConfigureAwait(true);
            var game = Main.GetNode<Game>("Game");
            game.PauseOnFocusLoss = false;
            EnableHeadlessInput(game.Player, game.GetNode<PauseMenu>("Pause"));
            await Wait(() => !game.InTransition && game.Player.IsOnFloor(), "single player ready").ConfigureAwait(true);
            await Gameplay(game.Player, "single").ConfigureAwait(true);
            await Pause(game.Player, game.GetNode<PauseMenu>("Pause"), "single").ConfigureAwait(true);
            TapKey(Key.R);
            await Frames().ConfigureAwait(true);
            Check(game.InTransition, "single R starts a new suburb");
            await Wait(() => !game.InTransition, "single-player replacement").ConfigureAwait(true);
            TapKey(Key.Escape);
            await Frames().ConfigureAwait(true);
            await Activate("Main menu").ConfigureAwait(true);
            await Wait(() => Menus.Current == Menus.Screen.Main, "main menu").ConfigureAwait(true);
            await Activate("Multiplayer").ConfigureAwait(true);
            await Activate("Host on this computer").ConfigureAwait(true);
            await Wait(() => Menus.Current == Menus.Screen.Browser, "host browser").ConfigureAwait(true);
            await Activate("Create lobby").ConfigureAwait(true);
            await Wait(() => Menus.Current == Menus.Screen.Lobby, "solo lobby").ConfigureAwait(true);
            var net = Main.GetNode<NetClient>("Net");
            var capacity = net.Lobby!.Capacity;
            await Activate("capacity_up", focusKey: true).ConfigureAwait(true);
            await Wait(() => net.Lobby!.Capacity == capacity + 1, "keyboard capacity increase").ConfigureAwait(true);
            Check(net.Lobby!.Capacity == capacity + 1, "Tab/Enter changes capacity through the real input path");
            await FocusButton("capacity_down").ConfigureAwait(true);
            Send(new InputEventJoypadButton { ButtonIndex = JoyButton.A, Pressed = true });
            Send(new InputEventJoypadButton { ButtonIndex = JoyButton.A, Pressed = false });
            await Wait(() => net.Lobby!.Capacity == capacity, "gamepad capacity decrease").ConfigureAwait(true);
            Check(net.Lobby!.Capacity == capacity, "gamepad A changes capacity through the real input path");
            await Activate("Start match").ConfigureAwait(true);
            await Wait(() => Main.HasNode("Match") && Main.GetNode<Match>("Match").IsPlaying, "match playing").ConfigureAwait(true);
            var match = Main.GetNode<Match>("Match");
            EnableHeadlessInput(match.Player, match.GetNode<MatchMenu>("Menu"));
            await Gameplay(match.Player, "match").ConfigureAwait(true);
            var ammo = match.Player.Ammo!;
            // Give reload its own precondition even when the fire regression fails on the baseline.
            if (ammo.Magazine == ammo.Tuning.MagazineSize) ammo.Magazine--;
            var magazine = ammo.Magazine;
            TapKey(Key.R);
            await Frames().ConfigureAwait(true);
            Check(ammo.Reloading, "match R starts reload");
            await Wait(() => !ammo.Reloading, "reload completes").ConfigureAwait(true);
            Check(ammo.Magazine > magazine, "match reload refills the fired round");
            ammo.Magazine--;
            Send(new InputEventJoypadButton { ButtonIndex = JoyButton.X, Pressed = true });
            Send(new InputEventJoypadButton { ButtonIndex = JoyButton.X, Pressed = false });
            await Frames().ConfigureAwait(true);
            Check(ammo.Reloading, "match gamepad X starts reload");
            await Wait(() => !ammo.Reloading, "gamepad reload completes").ConfigureAwait(true);
            Check(ammo.Magazine == ammo.Tuning.MagazineSize, "match gamepad reload refills magazine");
            await Pause(match.Player, match.GetNode<MatchMenu>("Menu"), "match").ConfigureAwait(true);
            TapKey(Key.Escape);
            await Frames().ConfigureAwait(true);
            await Activate("Leave match").ConfigureAwait(true);
            await Wait(() => !Main.HasNode("Match") && Menus.Current == Menus.Screen.Browser, "match retirement").ConfigureAwait(true);
            SystemBindings();
            GD.Print($"[device input] {checks} assertions, {failures} failures");
            ShutdownGuard.Quit(GetTree(), failures == 0 ? 0 : 1);
        }
        catch (Exception error)
        {
            GD.PushError($"[device input] FAIL {error}");
            ShutdownGuard.Quit(GetTree(), 1);
        }
    }

    private async Task Gameplay(PlayerCharacter player, string mode)
    {
        Check(player.InputOverride == null || player.InputOverride is DeviceInput, $"{mode} uses DeviceInput, no scripted intent");
        Check(player.GetViewport() is SubViewport && player.GetViewport().Name == "WorldSpace", $"{mode} actor is under WorldSpace");
        await Frames(12).ConfigureAwait(true);
        capture.Screenshot($"{mode}_01_before_turn");
        var yaw = player.CameraRig.Yaw;
        var pitch = player.CameraRig.Pitch;
        var motion = new Vector2(120, -40);
        Send(new InputEventMouseMotion { Relative = motion, ScreenRelative = motion });
        await Frames().ConfigureAwait(true);
        var sensitivity = Mathf.RadToDeg(player.World.Tuning.MouseSensitivity);
        Check(Mathf.IsEqualApprox(player.CameraRig.Yaw, yaw - motion.X * sensitivity) &&
            Mathf.IsEqualApprox(player.CameraRig.Pitch, pitch - motion.Y * sensitivity), $"{mode} mouse turns view exactly once");
        capture.Screenshot($"{mode}_02_after_turn");
        yaw = player.CameraRig.Yaw;
        await Frames().ConfigureAwait(true);
        Check(player.CameraRig.Yaw == yaw, $"{mode} mouse displacement is consumed");

        Send(new InputEventJoypadMotion { Axis = JoyAxis.RightX, AxisValue = 1 });
        await Frames(8).ConfigureAwait(true);
        Send(new InputEventJoypadMotion { Axis = JoyAxis.RightX, AxisValue = 0 });
        Check(player.Aim.Yaw < yaw, $"{mode} right stick turns the primary view");
        await Frames().ConfigureAwait(true);
        yaw = player.Aim.Yaw;
        await Frames().ConfigureAwait(true);
        Check(player.Aim.Yaw == yaw, $"{mode} neutral right stick stops turning");

        var height = player.GlobalPosition.Y;
        TapKey(Key.Space);
        await Frames(8).ConfigureAwait(true);
        Check(!player.IsOnFloor() && player.Velocity.Y > 0 && player.GlobalPosition.Y > height, $"{mode} Space jumps");
        capture.Screenshot($"{mode}_03_jump");
        await Wait(() => player.IsOnFloor(), $"{mode} jump lands").ConfigureAwait(true);
        Send(new InputEventJoypadButton { ButtonIndex = JoyButton.A, Pressed = true });
        Send(new InputEventJoypadButton { ButtonIndex = JoyButton.A, Pressed = false });
        await Frames(8).ConfigureAwait(true);
        Check(!player.IsOnFloor() && player.Velocity.Y > 0, $"{mode} gamepad A jumps");
        await Wait(() => player.IsOnFloor(), $"{mode} gamepad jump lands").ConfigureAwait(true);
        var swings = 0;
        void Swing(uint id, Vector3 direction) => swings++;
        player.MeleeStarted += Swing;
        TapKey(Key.F);
        await Frames().ConfigureAwait(true);
        Check(swings == 1 && player.ResolvedState.Weapon == WeaponAction.Melee, $"{mode} F starts one melee");
        await Frames((int)Math.Ceiling((player.World.Tuning.MeleeCooldown + player.World.Tuning.MeleeWindup +
            player.World.Tuning.MeleeActive + player.World.Tuning.MeleeRecovery) * 60) + 6).ConfigureAwait(true);
        Check(swings == 1, $"{mode} melee press is consumed");
        Send(new InputEventJoypadButton { ButtonIndex = JoyButton.RightStick, Pressed = true });
        Send(new InputEventJoypadButton { ButtonIndex = JoyButton.RightStick, Pressed = false });
        await Frames().ConfigureAwait(true);
        Check(swings == 2 && player.ResolvedState.Weapon == WeaponAction.Melee, $"{mode} R3 starts one melee");
        await Frames((int)Math.Ceiling((player.World.Tuning.MeleeCooldown + player.World.Tuning.MeleeWindup +
            player.World.Tuning.MeleeActive + player.World.Tuning.MeleeRecovery) * 60) + 6).ConfigureAwait(true);
        Check(swings == 2, $"{mode} R3 press is consumed");
        player.MeleeStarted -= Swing;

        var shots = 0;
        void Shot(ShotReport report) => shots++;
        player.Fired += Shot;
        // A complete click between physics samples cannot pass by polling held fire.
        Send(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true });
        Send(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false });
        Check(!Input.IsActionPressed(InputSetup.Fire), $"{mode} fire released before physics");
        await Frames().ConfigureAwait(true);
        Check(shots == 1, $"{mode} single fire click shoots");
        await Frames(20).ConfigureAwait(true);
        Check(shots == 1, $"{mode} fire click is consumed");
        Send(new InputEventJoypadMotion { Axis = JoyAxis.TriggerRight, AxisValue = 1 });
        Send(new InputEventJoypadMotion { Axis = JoyAxis.TriggerRight, AxisValue = 0 });
        Check(!Input.IsActionPressed(InputSetup.Fire), $"{mode} trigger released before physics");
        await Frames().ConfigureAwait(true);
        Check(shots == 2, $"{mode} complete trigger tap shoots");
        await Frames(20).ConfigureAwait(true);
        Check(shots == 2, $"{mode} trigger tap is consumed");
        // Deliver a held trigger and corrective mouse motion through the real application viewport.
        var lookUp = new Vector2(0, -(25 - player.Aim.Pitch) / sensitivity);
        Send(new InputEventMouseMotion { Relative = lookUp, ScreenRelative = lookUp });
        await Frames().ConfigureAwait(true);
        var burstStart = player.Aim.Pitch;
        var beforeBurst = shots;
        Send(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true });
        await Frames(60).ConfigureAwait(true);
        Send(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false });
        await Frames().ConfigureAwait(true);
        Check(shots - beforeBurst >= 10 && player.Aim.Pitch > burstStart + 2, $"{mode} held mouse fires and climbs");
        capture.Screenshot($"{mode}_burst_climb");
        var afterBurst = shots;
        await Frames(120).ConfigureAwait(true);
        Check(shots == afterBurst && player.Aim.Pitch > burstStart + 2, $"{mode} released trigger stops; climb persists");
        var pull = new Vector2(0, (player.Aim.Pitch - burstStart) / sensitivity);
        Send(new InputEventMouseMotion { Relative = pull, ScreenRelative = pull });
        await Frames().ConfigureAwait(true);
        Check(Math.Abs(player.Aim.Pitch - burstStart) < 0.1f, $"{mode} real mouse pull controls recoil");
        player.Fired -= Shot;
    }

    private async Task Pause(PlayerCharacter player, CanvasLayer menu, string mode)
    {
        // Desktop remap: logical Escape, physical Caps Lock.
        TapKey(Key.Capslock, Key.Escape);
        await Frames().ConfigureAwait(true);
        Check(menu.Visible && Input.MouseMode == Input.MouseModeEnum.Visible, $"{mode} logical Escape opens pause menu");
        if (!menu.Visible) { TapKey(Key.Escape); await Frames().ConfigureAwait(true); }
        Check(menu.Visible, $"{mode} pause menu is open");
        capture.Screenshot($"{mode}_04_remapped_pause");
        var yaw = player.CameraRig.Yaw;
        var position = player.GlobalPosition;
        var swings = 0;
        var shots = 0;
        void Swing(uint id, Vector3 direction) => swings++;
        void Shot(ShotReport report) => shots++;
        player.MeleeStarted += Swing;
        player.Fired += Shot;
        Send(new InputEventMouseMotion { Relative = new Vector2(80, 0), ScreenRelative = new Vector2(80, 0) });
        TapKey(Key.F);
        TapKey(Key.R);
        await Frames().ConfigureAwait(true);
        Check(player.CameraRig.Yaw == yaw && shots == 0 && swings == 0, $"{mode} menu blocks gameplay input");
        TapKey(Key.Escape, Key.Capslock);
        await Frames(8).ConfigureAwait(true);
        Check(!menu.Visible && (DisplayServer.GetName() == "headless" || Input.MouseMode == Input.MouseModeEnum.Captured),
            $"{mode} physical Escape resumes");
        Check(player.CameraRig.Yaw == yaw && player.GlobalPosition.DistanceTo(position) < 0.05f && shots == 0 && swings == 0 &&
            player.Ammo?.Reloading != true, $"{mode} resume has no queued gameplay input");
        TapKey(Key.Space);
        Send(new InputEventMouseMotion { Relative = new Vector2(80, 0), ScreenRelative = new Vector2(80, 0) });
        TapKey(Key.Escape, Key.Capslock);
        await Frames().ConfigureAwait(true);
        Check(menu.Visible, $"{mode} physical Escape opens pause menu");
        TapKey(Key.Escape, Key.Capslock);
        await Frames(8).ConfigureAwait(true);
        Check(player.CameraRig.Yaw == yaw && player.IsOnFloor(), $"{mode} pause discards pending jump and mouse motion");
        player.MeleeStarted -= Swing;
        player.Fired -= Shot;
    }

    private void SystemBindings()
    {
        foreach (var (action, key, alt) in new[]
        {
            (InputSetup.Pause, Key.Escape, false), (InputSetup.ToggleTuning, Key.F1, false),
            (InputSetup.ToggleTuning, Key.Quoteleft, false), (InputSetup.ToggleFullscreen, Key.F11, false),
            (InputSetup.ToggleFullscreen, Key.Enter, true)
        })
        {
            Check(InputMap.EventIsAction(new InputEventKey { Keycode = key, PhysicalKeycode = Key.Capslock, AltPressed = alt }, action),
                $"{action} matches logical {key}");
            Check(InputMap.EventIsAction(new InputEventKey { Keycode = Key.Capslock, PhysicalKeycode = key, AltPressed = alt }, action),
                $"{action} matches physical {key}");
        }
        Check(InputMap.EventIsAction(new InputEventKey { PhysicalKeycode = Key.W, Keycode = Key.Z }, InputSetup.MoveForward) &&
            !InputMap.EventIsAction(new InputEventKey { PhysicalKeycode = Key.Z, Keycode = Key.W }, InputSetup.MoveForward),
            "gameplay movement stays physical");
    }

    private static void EnableHeadlessInput(PlayerCharacter player, CanvasLayer menu)
    {
        // The headless display cannot capture a pointer. Keep the real DeviceInput and event route, substituting
        // only its existing capture predicate. Rendered runs exercise the production mouse-mode predicate.
        if (DisplayServer.GetName() == "headless")
        {
            player.View!.AssignDevice(new DeviceInput(player.World.Tuning, () => !menu.Visible));
        }
    }

    private async Task Activate(string text, bool focusKey = false)
    {
        await Frames().ConfigureAwait(true);
        var button = GetTree().Root.FindChildren("*", "Button", owned: false).OfType<Button>()
            .FirstOrDefault(b => (focusKey ? MenuNavigation.Key(b) == text : b.Text == text) && b.IsVisibleInTree() && !b.Disabled)
            ?? throw new InvalidOperationException($"missing enabled button {text}");
        for (var i = 0; GetViewport().GuiGetFocusOwner() != button; i++)
        {
            if (i >= 100) throw new InvalidOperationException($"cannot focus {text} with Tab");
            TapKey(Key.Tab);
            await Frames().ConfigureAwait(true);
        }
        Send(new InputEventKey { PhysicalKeycode = Key.Enter, Keycode = Key.Enter, Pressed = true });
        await Frames(1).ConfigureAwait(true);
        Send(new InputEventKey { PhysicalKeycode = Key.Enter, Keycode = Key.Enter, Pressed = false });
        await Frames().ConfigureAwait(true);
    }

    private async Task FocusButton(string key)
    {
        await Frames().ConfigureAwait(true);
        for (var i = 0; MenuNavigation.Key(GetViewport().GuiGetFocusOwner()) != key; i++)
        {
            if (i >= 100) throw new InvalidOperationException($"cannot focus {key} with Tab");
            TapKey(Key.Tab);
            await Frames().ConfigureAwait(true);
        }
    }

    private static void TapKey(Key physical, Key? logical = null)
    {
        Send(new InputEventKey { PhysicalKeycode = physical, Keycode = logical ?? physical, Pressed = true });
        Send(new InputEventKey { PhysicalKeycode = physical, Keycode = logical ?? physical, Pressed = false });
    }

    private static void Send(InputEvent e)
    {
        Input.ParseInputEvent(e);
        Input.FlushBufferedEvents();
    }

    private void Check(bool passed, string description)
    {
        checks++;
        if (!passed) failures++;
        GD.Print($"[device input] {(passed ? "PASS" : "FAIL")} {description}");
    }

    private async Task Wait(Func<bool> ready, string outcome)
    {
        var deadline = Time.GetTicksMsec() + 15000;
        while (Time.GetTicksMsec() < deadline)
        {
            if (ready()) return;
            await Frames(1).ConfigureAwait(true);
        }
        throw new TimeoutException(outcome);
    }

    private async Task Frames(int count = 3)
    {
        for (var i = 0; i < count; i++) { await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
    }
}
