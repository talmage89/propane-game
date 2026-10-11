using System;
using Godot;
using Propane.Core;
using Propane.Player;

namespace Propane.Dev;

/// <summary>Headless assertion suite for controller precedence and gameplay actions. Exits 1 on any failure.</summary>
public partial class MovementTests : Node
{
    private const float Dt = 1f / 120;
    private readonly StateTuning tuning = new(6, 9.5f, 0.98f, 0.12f, 0.12f, 1.3f, 0.3f);
    private int checks;

    public override void _Ready() => Callable.From(RunTests).CallDeferred();

    private void RunTests()
    {
        try
        {
            ResolverTests();
            GD.Print($"[movement-tests] PASS {checks} assertions");
            GetTree().Quit();
        }
        catch (Exception e)
        {
            GD.PushError($"[movement-tests] FAIL {e}");
            GetTree().Quit(1);
        }
    }

    private void Check(bool condition, string message)
    {
        checks++;
        if (!condition) throw new InvalidOperationException(message);
    }

    private PlayerState Tick(PlayerStateResolver r, StateIntent intent, bool ground = true, bool reload = false,
        PlayerLifecycle lifecycle = PlayerLifecycle.Active, bool available = true, bool stagger = false) =>
        r.Resolve(intent, new StatePhysics(lifecycle, ground, reload, stagger, available), tuning, Dt);

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

        // Device-equivalent data has identical precedence; actual mappings/edges are exercised below.
        var pad = new PlayerStateResolver();
        r.Reset();
        foreach (var input in new[] { sprint, aim, aim with { JumpPressed = true }, aim, sprint })
        {
            var grounded = !input.JumpPressed;
            Check(Tick(r, input, grounded) == Tick(pad, input, grounded), "gamepad-equivalent intent");
        }
    }

}
