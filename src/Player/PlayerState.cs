using System;

namespace Propane.Player;

public enum PlayerLifecycle : byte { Active, Ragdoll, GettingUp }
public enum PlayerSupport : byte { Grounded, Jumping, Airborne, Landing }
public enum PlayerGait : byte { Default, Sprint, Creep }
// Melee reserves ownership of the weapon for a later package; no melee input is resolved yet.
public enum WeaponAction : byte { Carry, Ready, Aim, Reload, Melee }

/// <summary>Device-independent intent and physics facts sampled before movement, once per physics tick.</summary>
public readonly record struct StateIntent(float MoveX = 0, float MoveY = 0, bool Sprint = false,
    bool AimHeld = false, bool JumpPressed = false, bool Trigger = false);
public readonly record struct StatePhysics(PlayerLifecycle Lifecycle, bool Grounded, bool Reloading = false,
    bool Staggered = false, bool ShotAvailable = false);
public readonly record struct StateTuning(float DefaultSpeed, float SprintSpeed, float CreepSpeed,
    float CoyoteTime, float JumpBufferTime, float ReadyHoldTime, float SprintForwardThreshold);

/// <summary>The sole resolved movement/combat decision. TargetSpeed is a gait target, not physical velocity.</summary>
public readonly record struct PlayerState(PlayerLifecycle Lifecycle, PlayerSupport Support, PlayerGait Gait,
    WeaponAction Weapon, float TargetSpeed, float MoveX, float MoveY, bool AimHeld, bool Jump, bool Fire)
{
    public bool Active => Lifecycle == PlayerLifecycle.Active;
    public bool Grounded => Support is PlayerSupport.Grounded or PlayerSupport.Landing;
    public bool EffectiveAim => Weapon == WeaponAction.Aim;
    public bool Ready => Weapon is WeaponAction.Ready or WeaponAction.Aim or WeaponAction.Reload;
}

/// <summary>
/// Pure controller model: no node, engine input or animation dependencies. Reload owns the weapon but leaves
/// movement free. Only effective grounded aim selects creep; a recent shot owns presentation, never speed.
/// Floor contact is the pre-move physics sample. Landing is the first tick with contact after air, and an accepted
/// (including buffered/coyote) jump overrides that contact on its exact takeoff tick.
/// </summary>
public sealed class PlayerStateResolver
{
    private float coyote;
    private float jumpBuffer;
    private float readyLeft;
    private bool wasGrounded;

    public void Reset()
    {
        coyote = jumpBuffer = readyLeft = 0;
        wasGrounded = false;
    }

    public PlayerState Resolve(StateIntent intent, StatePhysics physics, StateTuning tuning, float dt)
    {
        if (physics.Lifecycle != PlayerLifecycle.Active)
        {
            Reset();
            return new PlayerState(physics.Lifecycle, PlayerSupport.Airborne, PlayerGait.Default,
                WeaponAction.Carry, 0, 0, 0, intent.AimHeld, false, false);
        }

        coyote = physics.Grounded ? tuning.CoyoteTime : Math.Max(0, coyote - dt);
        jumpBuffer = intent.JumpPressed ? tuning.JumpBufferTime : Math.Max(0, jumpBuffer - dt);
        var jump = (intent.JumpPressed || jumpBuffer > 0) && (physics.Grounded || coyote > 0);
        var support = jump ? PlayerSupport.Jumping : physics.Grounded
            ? (wasGrounded ? PlayerSupport.Grounded : PlayerSupport.Landing) : PlayerSupport.Airborne;
        if (jump)
        {
            coyote = jumpBuffer = 0;
        }
        wasGrounded = physics.Grounded && !jump;

        readyLeft = Math.Max(0, readyLeft - dt);
        var fire = intent.Trigger && physics.ShotAvailable && !physics.Reloading;
        if (fire)
        {
            readyLeft = tuning.ReadyHoldTime;
        }
        var aim = intent.AimHeld && physics.Grounded && !jump && !physics.Reloading;
        var weapon = physics.Reloading ? WeaponAction.Reload : aim ? WeaponAction.Aim
            : fire || readyLeft > 0 ? WeaponAction.Ready : WeaponAction.Carry;
        var gait = aim ? PlayerGait.Creep
            : intent.Sprint && intent.MoveY > tuning.SprintForwardThreshold && !physics.Staggered
                ? PlayerGait.Sprint : PlayerGait.Default;
        var speed = gait switch
        {
            PlayerGait.Creep => tuning.CreepSpeed,
            PlayerGait.Sprint => tuning.SprintSpeed,
            _ => tuning.DefaultSpeed,
        };
        return new PlayerState(physics.Lifecycle, support, gait, weapon, speed,
            intent.MoveX, intent.MoveY, intent.AimHeld, jump, fire);
    }
}
