using System;

namespace Propane.Player;

public enum PlayerLifecycle : byte { Active, Ragdoll, GettingUp }
public enum PlayerSupport : byte { Grounded, Jumping, Airborne, Landing }
public enum PlayerGait : byte { Default, Sprint, Creep }
public enum MeleePhase : byte { None, Windup, Active, Recovery }
public enum WeaponAction : byte { Carry, Ready, Aim, Reload, Melee }

/// <summary>Device-independent intent and physics facts sampled before movement, once per physics tick.</summary>
public readonly record struct StateIntent(float MoveX = 0, float MoveY = 0, bool Sprint = false,
    bool AimHeld = false, bool JumpPressed = false, bool Trigger = false, bool MeleePressed = false);
public readonly record struct StatePhysics(PlayerLifecycle Lifecycle, bool Grounded, bool Reloading = false,
    bool Staggered = false, bool ShotAvailable = false);
public readonly record struct StateTuning(float DefaultSpeed, float SprintSpeed, float CreepSpeed,
    float CoyoteTime, float JumpBufferTime, float ReadyHoldTime, float SprintForwardThreshold,
    float MeleeWindup = 0, float MeleeActive = 0, float MeleeRecovery = 0, float MeleeCooldown = 0,
    float MeleeLungeSpeed = 0);

/// <summary>The sole resolved movement/combat decision. TargetSpeed is a gait target, not physical velocity.</summary>
public readonly record struct PlayerState(PlayerLifecycle Lifecycle, PlayerSupport Support, PlayerGait Gait,
    WeaponAction Weapon, float TargetSpeed, float MoveX, float MoveY, bool AimHeld, bool Jump, bool Fire,
    MeleePhase MeleePhase = MeleePhase.None, float MeleeProgress = 0, bool MeleeStarted = false)
{
    public bool Active => Lifecycle == PlayerLifecycle.Active;
    public bool Grounded => Support is PlayerSupport.Grounded or PlayerSupport.Landing;
    public bool EffectiveAim => Weapon == WeaponAction.Aim;
    public bool Ready => Weapon is WeaponAction.Ready or WeaponAction.Aim or WeaponAction.Reload or WeaponAction.Melee;
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
    private float meleeElapsed = -1;
    private float meleeCooldown;

    public bool CanMelee => meleeElapsed < 0 && meleeCooldown <= 0;

    /// <summary>Throwing cancels contact without letting a lifecycle transition bypass the cooldown.</summary>
    public void Interrupt()
    {
        coyote = jumpBuffer = readyLeft = 0;
        wasGrounded = false;
        meleeElapsed = -1;
    }

    public void Reset()
    {
        Interrupt();
        meleeCooldown = 0;
    }

    public PlayerState Resolve(StateIntent intent, StatePhysics physics, StateTuning tuning, float dt)
    {
        meleeCooldown = Math.Max(0, meleeCooldown - dt);
        if (physics.Lifecycle != PlayerLifecycle.Active)
        {
            Interrupt();
            return new PlayerState(physics.Lifecycle, PlayerSupport.Airborne, PlayerGait.Default,
                WeaponAction.Carry, 0, 0, 0, intent.AimHeld, false, false);
        }

        var duration = tuning.MeleeWindup + tuning.MeleeActive + tuning.MeleeRecovery;
        var started = intent.MeleePressed && physics.Grounded && CanMelee && duration > 0;
        if (started)
        {
            meleeElapsed = 0;
            meleeCooldown = duration + tuning.MeleeCooldown;
            readyLeft = jumpBuffer = coyote = 0;
        }
        else if (meleeElapsed >= 0)
        {
            meleeElapsed += dt;
            if (meleeElapsed >= duration || !physics.Grounded) meleeElapsed = -1;
        }
        if (meleeElapsed >= 0)
        {
            var phase = meleeElapsed < tuning.MeleeWindup ? MeleePhase.Windup
                : meleeElapsed < tuning.MeleeWindup + tuning.MeleeActive ? MeleePhase.Active : MeleePhase.Recovery;
            wasGrounded = true;
            return new PlayerState(physics.Lifecycle, PlayerSupport.Grounded, PlayerGait.Default,
                WeaponAction.Melee, phase == MeleePhase.Recovery ? 0 : tuning.MeleeLungeSpeed,
                0, phase == MeleePhase.Recovery ? 0 : 1, intent.AimHeld, false, false,
                phase, meleeElapsed / duration, started);
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
