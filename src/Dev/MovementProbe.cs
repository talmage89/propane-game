using System;
using Godot;
using Propane.Core;
using Propane.Player;

namespace Propane.Dev;

/// <summary>Checks the node facade after every physics tick, including exact aim release and landing ticks.</summary>
public partial class MovementProbe : Node
{
    public PlayerCharacter Player { get; init; } = null!;
    public ScriptedInput Input { get; init; } = null!;
    private PlayerState previous;
    private int ticks;
    private int releases;
    private int aimedLandings;
    private int sprintReleases;

    public override void _Ready() => ProcessPhysicsPriority = 10;

    public override void _PhysicsProcess(double delta)
    {
        var state = Player.ResolvedState;
        var intent = Input.LastRead;
        var tuning = Tuning.Current;
        var expected = state.EffectiveAim ? PlayerGait.Creep
            : intent.Sprint && intent.Move.Y > tuning.SprintForwardThreshold ? PlayerGait.Sprint : PlayerGait.Default;
        var speed = expected == PlayerGait.Creep ? tuning.CreepSpeed
            : expected == PlayerGait.Sprint ? tuning.SprintSpeed : tuning.RunSpeed;
        if (state.Gait != expected || Math.Abs(state.TargetSpeed - speed) > 0.00001f ||
            (state.EffectiveAim && (!state.Grounded || !intent.Aim)) ||
            (intent.Aim && state.Grounded && state.Weapon != WeaponAction.Reload && !state.EffectiveAim))
        {
            GD.PushError($"[movement-probe] FAIL t={Input.Now:0.0000} {state}");
            ShutdownGuard.Quit(GetTree(), 1);
            return;
        }
        ticks++;
        if (previous.AimHeld && !state.AimHeld)
        {
            releases++;
            if (state.Gait == PlayerGait.Sprint) sprintReleases++;
        }
        if (state.Support == PlayerSupport.Landing && state.EffectiveAim) aimedLandings++;
        if (state.Gait != previous.Gait || state.Support != previous.Support || state.Weapon != previous.Weapon ||
            state.AimHeld != previous.AimHeld)
        {
            GD.Print($"[movement] t={Input.Now:0.0000} heldAim={state.AimHeld} effectiveAim={state.EffectiveAim} " +
                $"support={state.Support} gait={state.Gait} target={state.TargetSpeed:0.00} weapon={state.Weapon}");
        }
        previous = state;
    }

    public void Finish(bool requireAimedLanding)
    {
        if (releases < 1 || sprintReleases != releases || ticks < 100 || (requireAimedLanding && aimedLandings < 1))
        {
            GD.PushError($"[movement-probe] FAIL incomplete sequence: ticks={ticks} releases={releases} aimedLandings={aimedLandings}");
            ShutdownGuard.Quit(GetTree(), 1);
            return;
        }
        GD.Print($"[movement-probe] PASS ticks={ticks} releases={releases} aimedLandings={aimedLandings}");
    }
}
