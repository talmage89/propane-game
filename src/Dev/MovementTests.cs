using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Propane.Core;
using Propane.Player;
using Propane.Tank;

namespace Propane.Dev;

/// <summary>Headless assertion suite for controller precedence and gameplay actions. Exits 1 on any failure.</summary>
public partial class MovementTests : Node
{
    private const float Dt = 1f / 120;
    private readonly StateTuning tuning = new(6, 9.5f, 0.98f, 0.12f, 0.12f, 1.3f, 0.3f);
    private int checks;
    private readonly DeviceInput device;
    private bool gameplayEnabled = true;
    private bool padIntentMode;
    private bool collectInput = true;

    public MovementTests() => device = new DeviceInput(() => gameplayEnabled);

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        Callable.From(RunTests).CallDeferred();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (collectInput) device.Accumulate(@event);
    }

    private async void RunTests()
    {
        try
        {
            // ParseInputEvent represents device input arriving between ticks, as in the game.
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Check(!Engine.IsInPhysicsFrame(), "input fixture runs between physics ticks");
            ResolverTests();
            InputSetup.Register();
            padIntentMode = true;
            ResolverTests();
            padIntentMode = false;
            InputTests();
            MeleeInputTests();
            SnapshotTests();
            PauseTests();
            await FacadeTests().ConfigureAwait(true);
            GD.Print($"[movement-tests] PASS {checks} assertions");
            ShutdownGuard.Quit(GetTree());
        }
        catch (Exception e)
        {
            GD.PushError($"[movement-tests] FAIL {e}");
            ShutdownGuard.Quit(GetTree(), 1);
        }
    }

    private void Check(bool condition, string message)
    {
        checks++;
        if (!condition) throw new InvalidOperationException(message);
    }

    private PlayerState Tick(PlayerStateResolver r, StateIntent intent, bool ground = true, bool reload = false,
        PlayerLifecycle lifecycle = PlayerLifecycle.Active, bool available = true, bool stagger = false)
    {
        if (padIntentMode)
        {
            Send(new InputEventJoypadMotion { Axis = JoyAxis.LeftX, AxisValue = intent.MoveX });
            Send(new InputEventJoypadMotion { Axis = JoyAxis.LeftY, AxisValue = -intent.MoveY });
            Send(new InputEventJoypadButton { ButtonIndex = JoyButton.LeftStick, Pressed = intent.Sprint });
            Send(new InputEventJoypadMotion { Axis = JoyAxis.TriggerLeft, AxisValue = intent.AimHeld ? 1 : 0 });
            Send(new InputEventJoypadButton { ButtonIndex = JoyButton.A, Pressed = false });
            if (intent.JumpPressed) Send(new InputEventJoypadButton { ButtonIndex = JoyButton.A, Pressed = true });
            Send(new InputEventJoypadMotion { Axis = JoyAxis.TriggerRight, AxisValue = 0 });
            if (intent.Trigger) Send(new InputEventJoypadMotion { Axis = JoyAxis.TriggerRight, AxisValue = 1 });
            var mapped = device.Read();
            var padIntent = new StateIntent(mapped.Move.X, mapped.Move.Y, mapped.Sprint, mapped.Aim,
                mapped.JumpPressed, mapped.FirePressed);
            Check(intent == padIntent, $"gamepad intent mismatch: wanted {intent}, got {padIntent}");
            intent = padIntent;
        }
        return r.Resolve(intent, new StatePhysics(lifecycle, ground, reload, stagger, available), tuning, Dt);
    }

    private void Expect(PlayerState state, PlayerGait gait, float speed, string label)
    {
        Check(state.Gait == gait, $"{label}: gait {state.Gait}, expected {gait}");
        Check(Math.Abs(state.TargetSpeed - speed) < 0.00001f, $"{label}: target {state.TargetSpeed}, expected {speed}");
    }

    private void ResolverTests()
    {
        var sprint = new StateIntent(MoveY: 1, Sprint: true);
        var aim = sprint with { AimHeld = true };
        var r = new PlayerStateResolver();
        Expect(Tick(r, sprint), PlayerGait.Sprint, 9.5f, "initial sprint");
        var s = Tick(r, sprint with { JumpPressed = true });
        Expect(s, PlayerGait.Sprint, 9.5f, "jump before aim");
        Check(s.Support == PlayerSupport.Jumping && s.Jump && !s.Grounded, "takeoff phase");
        s = Tick(r, aim, ground: false);
        Expect(s, PlayerGait.Sprint, 9.5f, "aim pressed in air");
        Check(s.AimHeld && !s.EffectiveAim, "air preserves held aim only");
        s = Tick(r, aim);
        Expect(s, PlayerGait.Creep, 0.98f, "aim held through landing");
        Check(s.Support == PlayerSupport.Landing && s.EffectiveAim, "landing restores aim on contact tick");
        s = Tick(r, aim with { JumpPressed = true });
        Expect(s, PlayerGait.Sprint, 9.5f, "aim before jump");
        Check(!s.EffectiveAim && s.Jump, "jump cancels effective aim immediately");
        Expect(Tick(r, sprint, ground: false), PlayerGait.Sprint, 9.5f, "release before landing");
        s = Tick(r, sprint);
        Expect(s, PlayerGait.Sprint, 9.5f, "land after release");
        Check(!s.EffectiveAim, "landing does not resurrect released aim");

        Check(Tick(r, aim with { Trigger = true }).Fire, "aimed shot accepted");
        s = Tick(r, sprint);
        Expect(s, PlayerGait.Sprint, 9.5f, "release after recent shot exact tick");
        Check(s.Weapon == WeaponAction.Ready, "recent fire remains presentation only");
        for (var i = 0; i < 24; i++)
        {
            Expect(Tick(r, i % 2 == 0 ? aim : sprint), i % 2 == 0 ? PlayerGait.Creep : PlayerGait.Sprint,
                i % 2 == 0 ? 0.98f : 9.5f, $"rapid toggle {i}");
        }
        Expect(Tick(r, aim with { Sprint = false }), PlayerGait.Creep, 0.98f, "aim without Shift");
        Expect(Tick(r, aim), PlayerGait.Creep, 0.98f, "Shift pressed during aim");
        s = Tick(r, aim with { Trigger = true }, reload: true);
        Expect(s, PlayerGait.Sprint, 9.5f, "reload during sprint/aim");
        Check(s.Weapon == WeaponAction.Reload && !s.Fire && !s.EffectiveAim, "reload owns weapon");
        Expect(Tick(r, aim), PlayerGait.Creep, 0.98f, "reload finishes with held aim");
        Check(!Tick(r, sprint with { Trigger = true }, available: false).Fire, "cooldown/empty blocks shot");
        Expect(Tick(r, sprint, stagger: true), PlayerGait.Default, 6, "stagger blocks sprint");
        foreach (var lifecycle in new[] { PlayerLifecycle.Ragdoll, PlayerLifecycle.GettingUp })
        {
            s = Tick(r, aim with { Trigger = true, JumpPressed = true }, lifecycle: lifecycle);
            Expect(s, PlayerGait.Default, 0, $"{lifecycle} blocks control");
            Check(!s.Fire && !s.Jump && !s.EffectiveAim, "inactive lifecycle owns control");
        }
        r.Reset();
        Tick(r, sprint);
        Check(Tick(r, aim with { JumpPressed = true }, ground: false).Jump, "coyote jump with aim");
        Check(!Tick(r, aim with { JumpPressed = true }, ground: false).Jump, "no double coyote jump");
        r.Reset();
        Check(!Tick(r, aim with { JumpPressed = true }, ground: false).Jump, "air press buffers");
        s = Tick(r, aim);
        Expect(s, PlayerGait.Sprint, 9.5f, "buffered landing jump suppresses aim");
        Check(s.Jump && s.Support == PlayerSupport.Jumping, "buffer accepted on landing");
        r.Reset();
        Tick(r, aim with { JumpPressed = true }, ground: false);
        for (var i = 0; i < 20; i++) Tick(r, aim, ground: false);
        s = Tick(r, aim);
        Check(!s.Jump && s.EffectiveAim, "expired buffer lands aimed");

    }

    private void SnapshotTests()
    {
        var creep = new PlayerNetState
        {
            Gait = PlayerGait.Creep,
            Support = PlayerSupport.Grounded,
            Weapon = WeaponAction.Aim,
            Grounded = true,
            Aim = 1
        };
        var air = new PlayerNetState
        {
            Gait = PlayerGait.Sprint,
            Support = PlayerSupport.Jumping,
            Weapon = WeaponAction.Carry,
            Grounded = false
        };
        var mixed = PlayerNetState.Lerp(creep, air, 0.75f);
        Check(mixed.Gait == PlayerGait.Sprint && mixed.Support == PlayerSupport.Jumping,
            "snapshot interpolation preserves discrete gait/support");
        Check(mixed.Aim == 0 && mixed.Weapon == WeaponAction.Carry, "snapshot air cancels blended aim");
        mixed = PlayerNetState.Lerp(air, creep, 0.75f);
        Check(mixed.Gait == PlayerGait.Creep && mixed.Weapon == WeaponAction.Aim && mixed.Aim > 0,
            "snapshot landing restores creep/aim");
        var reload = creep;
        reload.Weapon = WeaponAction.Reload;
        reload.Gait = PlayerGait.Sprint;
        mixed = PlayerNetState.Lerp(creep, reload, 0.75f);
        Check(mixed.Gait == PlayerGait.Sprint && mixed.Aim == 0, "snapshot reload cancels aim without canceling sprint");
    }

    private void Send(InputEvent e)
    {
        if (e is InputEventMouse mouse) mouse.Position = new Vector2(100, 100);
        Input.ParseInputEvent(e);
        Input.FlushBufferedEvents();
    }

    private void SendHandled(InputEvent e)
    {
        collectInput = false;
        Send(e);
        collectInput = true;
    }

    private void InputTests()
    {
        InputSetup.Register();
        Check(InputMap.EventIsAction(new InputEventJoypadButton { Device = 2, ButtonIndex = JoyButton.A, Pressed = true },
            InputSetup.Jump), "bindings accept any gamepad device");
        Check(InputMap.EventIsAction(new InputEventJoypadMotion { Device = 2, Axis = JoyAxis.RightX, AxisValue = 1 },
            InputSetup.LookRight), "look bindings accept any gamepad device");
        gameplayEnabled = true;
        Send(new InputEventJoypadMotion { Axis = JoyAxis.LeftY, AxisValue = -1 });
        Send(new InputEventJoypadButton { ButtonIndex = JoyButton.LeftStick, Pressed = true });
        Send(new InputEventJoypadMotion { Axis = JoyAxis.TriggerLeft, AxisValue = 1 });
        Send(new InputEventJoypadButton { ButtonIndex = JoyButton.A, Pressed = true });
        Send(new InputEventJoypadButton { ButtonIndex = JoyButton.X, Pressed = true });
        Send(new InputEventJoypadMotion { Axis = JoyAxis.TriggerRight, AxisValue = 0.7f });
        var intent = device.Read();
        Check(intent.Move.Y == 1 && intent.Sprint && intent.Aim, $"pad move/sprint/aim actions: {intent.Move} sprint={intent.Sprint} aim={intent.Aim} mode={Input.MouseMode}");
        Check(intent.JumpPressed && intent.ReloadPressed && intent.FirePressed && intent.FireHeld, $"pad action edges: jump={intent.JumpPressed} reload={intent.ReloadPressed} fire={intent.FirePressed} held={intent.FireHeld}");
        var resolved = Tick(new PlayerStateResolver(), new StateIntent(intent.Move.X, intent.Move.Y,
            intent.Sprint, intent.Aim, intent.JumpPressed));
        Expect(resolved, PlayerGait.Sprint, 9.5f, "pad jump while aiming exact tick");
        intent = device.Read();
        Check(!intent.JumpPressed && !intent.FirePressed && !intent.ReloadPressed, "edges consumed exactly once");
        Send(new InputEventJoypadMotion { Axis = JoyAxis.TriggerRight, AxisValue = 0.9f });
        Check(!device.Read().FirePressed, "trigger motion while held is not another press");
        Send(new InputEventJoypadMotion { Axis = JoyAxis.RightX, AxisValue = 0.1f });
        Check(device.Read().LookStick == Vector2.Zero, "stick deadzone");
        Send(new InputEventJoypadMotion { Axis = JoyAxis.RightX, AxisValue = 1 });
        Check(device.Read().LookStick == Vector2.Right, "full stick rate");
        var sixty = CameraRig.StickLookDelta(new Vector2(0.5f, 0), 1f / 60, 180, 2) * 60;
        var oneTwenty = CameraRig.StickLookDelta(new Vector2(0.5f, 0), Dt, 180, 2) * 120;
        Check(sixty.IsEqualApprox(oneTwenty) && Math.Abs(sixty.X - 45) < 0.001f, "look rate/curve independent of tick rate");
        Send(new InputEventJoypadButton { ButtonIndex = JoyButton.A, Pressed = false });
        Send(new InputEventJoypadButton { ButtonIndex = JoyButton.X, Pressed = false });
        Send(new InputEventJoypadMotion { Axis = JoyAxis.TriggerRight, AxisValue = 0 });
        Send(new InputEventJoypadMotion { Axis = JoyAxis.TriggerLeft, AxisValue = 0 });
        Send(new InputEventJoypadButton { ButtonIndex = JoyButton.LeftStick, Pressed = false });
        device.Read();
        Send(new InputEventKey { PhysicalKeycode = Key.Shift, Pressed = true });
        Send(new InputEventKey { PhysicalKeycode = Key.Space, Pressed = true, ShiftPressed = true });
        Send(new InputEventKey { PhysicalKeycode = Key.R, Pressed = true, ShiftPressed = true });
        Send(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = true });
        Send(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true });
        Send(new InputEventMouseMotion { ScreenRelative = new Vector2(12, -4) });
        intent = device.Read();
        Check(intent.Sprint && intent.JumpPressed && intent.ReloadPressed && intent.Aim && intent.FirePressed,
            $"keyboard/mouse actions with Shift modifier: sprint={intent.Sprint} jump={intent.JumpPressed} reload={intent.ReloadPressed} aim={intent.Aim} fire={intent.FirePressed}");
        Check(intent.Look == new Vector2(12, -4) && device.Read().Look == Vector2.Zero, "mouse displacement consumed once");
        Send(new InputEventKey { PhysicalKeycode = Key.Space, Pressed = true, Echo = true, ShiftPressed = true });
        Check(!device.Read().JumpPressed, "keyboard echo is not a new jump");
        Send(new InputEventJoypadMotion { Axis = JoyAxis.TriggerRight, AxisValue = 1 });
        Check(!device.Read().FirePressed, "second device does not re-press an already held action");
        Send(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false });
        Check(device.Read().FireHeld, "other device keeps fire action held");
        Send(new InputEventJoypadMotion { Axis = JoyAxis.TriggerRight, AxisValue = 0 });
        Check(!device.Read().FireHeld, "last device release clears fire action");
        Send(new InputEventJoypadMotion { Axis = JoyAxis.TriggerRight, AxisValue = 1 });
        Check(device.Read().FirePressed, "fresh trigger pull creates a new press");
        // A menu handled this release, so the gameplay event collector never receives it.
        SendHandled(new InputEventJoypadMotion { Axis = JoyAxis.TriggerRight, AxisValue = 0 });
        Check(!device.Read().FireHeld, "UI-consumed release clears the polled action");
        Send(new InputEventJoypadMotion { Axis = JoyAxis.TriggerRight, AxisValue = 1 });
        Check(device.Read().FirePressed, "press after UI-consumed release creates an edge");
        // A paused player does not Read at all. Resume and press before the next physics sample.
        SendHandled(new InputEventJoypadMotion { Axis = JoyAxis.TriggerRight, AxisValue = 0 });
        device.Reset();
        Send(new InputEventJoypadMotion { Axis = JoyAxis.TriggerRight, AxisValue = 1 });
        Check(device.Read().FirePressed, "press after paused release works without an intervening Read");
        gameplayEnabled = false;
        intent = device.Read();
        Check(intent.Move == Vector2.Zero && !intent.FireHeld && intent.LookStick == Vector2.Zero, "menus suppress gameplay");
    }

    private void MeleeInputTests()
    {
        gameplayEnabled = true;
        device.Reset();
        foreach (var e in new InputEvent[]
        {
            new InputEventKey { PhysicalKeycode = Key.F, Pressed = true, ShiftPressed = true },
            new InputEventMouseButton { ButtonIndex = MouseButton.Xbutton1, Pressed = true },
            new InputEventMouseButton { ButtonIndex = MouseButton.Xbutton2, Pressed = true },
            new InputEventJoypadButton { ButtonIndex = JoyButton.RightStick, Pressed = true },
        })
        {
            Send(e);
            Check(device.Read().MeleePressed && !device.Read().MeleePressed, $"melee press/consume {e.GetClass()}");
            device.Reset();
            Check(!device.Read().MeleePressed, "held melee repressed after reset");
            var released = (InputEvent)e.Duplicate();
            switch (released)
            {
                case InputEventKey key: key.Pressed = false; break;
                case InputEventMouseButton mouse: mouse.Pressed = false; break;
                case InputEventJoypadButton pad: pad.Pressed = false; break;
            }
            Send(released);
            device.Read();
        }
    }

    private sealed class TestInput : IPlayerInputSource
    {
        public PlayerIntent Intent;
        public PlayerIntent Read() => Intent;
    }

    private void PauseTests()
    {
        gameplayEnabled = true;
        foreach (var action in new[] { InputSetup.Jump, InputSetup.Fire, InputSetup.Reload }) Input.ActionRelease(action);
        device.Reset();
        var player = new PlayerCharacter { InputOverride = device, ProcessMode = ProcessModeEnum.Pausable };
        AddChild(player);
        void Buttons(bool pressed, bool handled = false)
        {
            InputEvent[] events =
            {
                new InputEventJoypadButton { ButtonIndex = JoyButton.A, Pressed = pressed },
                new InputEventJoypadButton { ButtonIndex = JoyButton.X, Pressed = pressed },
                new InputEventJoypadMotion { Axis = JoyAxis.TriggerRight, AxisValue = pressed ? 1 : 0 },
            };
            foreach (var e in events)
            {
                if (handled) SendHandled(e); else Send(e);
            }
        }
        Buttons(true);
        Send(new InputEventMouseMotion { ScreenRelative = new Vector2(12, -4) });
        GetTree().Paused = true;
        var pending = device.Read();
        Check(!pending.JumpPressed && !pending.FirePressed && !pending.ReloadPressed && pending.Look == Vector2.Zero,
            "pause notification discards queued presses and displacement");
        Buttons(false, handled: true);
        GetTree().Paused = false;
        Buttons(true);
        var intent = device.Read();
        Check(intent.JumpPressed && intent.FirePressed && intent.ReloadPressed,
            "resume notification restores all edges before the next physics tick");
        player.Notification((int)NotificationApplicationFocusOut);
        Buttons(false, handled: true);
        player.Notification((int)NotificationApplicationFocusIn);
        Buttons(true);
        intent = device.Read();
        Check(intent.JumpPressed && intent.FirePressed && intent.ReloadPressed,
            "focus return restores all edges without an intervening physics sample");
        Buttons(false);
        device.Read();
        player.Free();
    }

    private async Task FacadeTests()
    {
        GD.Seed(1);
        var floor = new StaticBody3D { CollisionLayer = Layers.Floor, CollisionMask = 0 };
        floor.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D() });
        AddChild(floor);
        var input = new TestInput { Intent = new PlayerIntent { Aim = true } };
        var player = new PlayerCharacter { InputOverride = input };
        AddChild(player);
        for (var i = 0; i < 60; i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        Check(player.ResolvedState.EffectiveAim && player.AimAmount > 0.99f, "facade starts grounded and aimed");
        var feet = player.FindChildren("*", "Skeleton3D", owned: false).OfType<Skeleton3D>()
            .Single(s => s.FindBone("foot_l") >= 0).GetNode<FootPlacement>("Feet");
        var creepSpeed = Tuning.Current.CreepSpeed;
        input.Intent.Move = Vector2.Down;
        foreach (var speed in new[] { 0.98f, 1.6f, 2.2f, 2.8f })
        {
            Tuning.Current.CreepSpeed = speed;
            for (var i = 0; i < 60; i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Check(player.ResolvedState.Gait == PlayerGait.Creep && Math.Abs(player.Velocity.Length() - speed) < 0.001f,
                $"creep facade reaches {speed} m/s");
            var phase = feet.Pose.Phase;
            for (var i = 0; i < 4; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Check(Math.Abs(feet.Pose.Phase - phase) > 0.00001f && feet.Pose.Walk > 0.99f,
                $"creep stride keeps advancing at {speed} m/s");
        }
        Tuning.Current.CreepSpeed = creepSpeed;
        input.Intent.Move = Vector2.Zero;
        player.ReceiveBlast(new BlastInfo(Vector3.Zero, Vector3.Forward, 0, 1));
        Check(player.ResolvedState.Lifecycle == PlayerLifecycle.Ragdoll && player.AimAmount == 0 &&
            player.CaptureNetState().Sprint == 0, "ragdoll immediately clears resolved control and weapon poses");
        input.Intent = default;
        for (var i = 0; i < 1200 && player.IsRagdolled; i++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            Check(player.AimAmount == 0, "released aim stays cleared through ragdoll/get-up");
        }
        Check(!player.IsRagdolled, "ragdoll/get-up fixture completes");
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        Check(!player.ResolvedState.EffectiveAim && player.AimAmount == 0, "recovery does not resurrect released aim");
        input.Intent = new PlayerIntent { Move = Vector2.Down, Sprint = true };
        for (var i = 0; i < 60; i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        player.Ammo = new AmmoState { Magazine = 10, Reserve = 40 };
        input.Intent.ReloadPressed = true;
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        input.Intent.ReloadPressed = false;
        Check(player.ResolvedState.Gait == PlayerGait.Sprint && player.CaptureNetState().Sprint > 0.99f &&
            player.ResolvedState.Weapon == WeaponAction.Reload && player.Ammo.Reloading,
            "facade reload leaves sprint selected and posed");
        player.ReceiveBlast(new BlastInfo(Vector3.Zero, Vector3.Forward, 0, 1));
        Check(player.CaptureNetState().Sprint == 0 && !player.Ammo.Reloading,
            "ragdoll immediately clears sprint pose and cancels reload");
        player.QueueFree();
        floor.QueueFree();
    }
}
