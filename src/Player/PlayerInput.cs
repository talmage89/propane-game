using System;
using Godot;
using Propane.Core;

namespace Propane.Player;

/// <summary>One frame of player intent, decoupled from devices so test scenes can script the player.</summary>
public struct PlayerIntent
{
    /// <summary>Move input: X = right, Y = forward, each -1..1.</summary>
    public Vector2 Move;
    public bool Sprint;
    public bool Aim;
    public bool JumpPressed;
    public bool FirePressed;

    /// <summary>The fire button is down (for automatic fire).</summary>
    public bool FireHeld;
    public bool ReloadPressed;
    public bool MeleePressed;
    /// <summary>Mouse displacement in screen pixels, consumed once per tick.</summary>
    public Vector2 Look;
    /// <summary>Stick direction after deadzone, before response curve; a rate, not displacement.</summary>
    public Vector2 LookStick;
}

public interface IPlayerInputSource
{
    PlayerIntent Read();
}

/// <summary>Gameplay actions from any device. Mouse motion accumulates between reads.</summary>
public sealed class DeviceInput : IPlayerInputSource
{
    private readonly Func<bool> gameplayEnabled;

    /// <summary>The facade supplies UI focus; tests can enable actions without a rendered mouse capture.</summary>
    public DeviceInput(Func<bool>? gameplayEnabled = null) =>
        this.gameplayEnabled = gameplayEnabled ?? (() => Input.MouseMode == Input.MouseModeEnum.Captured);

    private Vector2 look;
    private bool jump;
    private bool fire;
    private bool reload;
    private bool melee;
    private bool meleeDown;
    private bool jumpDown;
    private bool fireDown;
    private bool reloadDown;

    /// <summary>Discard queued input and sample held actions when physics pauses/resumes or focus changes.</summary>
    public void Reset()
    {
        look = Vector2.Zero;
        jump = fire = reload = melee = false;
        SynchronizeHeldActions();
    }

    private void SynchronizeHeldActions()
    {
        jumpDown = Input.IsActionPressed(InputSetup.Jump);
        fireDown = Input.IsActionPressed(InputSetup.Fire);
        reloadDown = Input.IsActionPressed(InputSetup.Reload);
        meleeDown = Input.IsActionPressed(InputSetup.Melee);
    }

    public void Accumulate(InputEvent @event)
    {
        var enabled = gameplayEnabled();
        // Motion is displacement and has no InputMap event binding; buttons/axes use actions only.
        if (enabled && @event is InputEventMouseMotion motion)
        {
            look += motion.ScreenRelative;
        }
        AccumulatePress(@event, InputSetup.Jump, enabled, ref jumpDown, ref jump);
        AccumulatePress(@event, InputSetup.Fire, enabled, ref fireDown, ref fire);
        AccumulatePress(@event, InputSetup.Reload, enabled, ref reloadDown, ref reload);
        AccumulatePress(@event, InputSetup.Melee, enabled, ref meleeDown, ref melee);
    }

    private static void AccumulatePress(InputEvent e, string action, bool enabled, ref bool down, ref bool pressed)
    {
        if (!e.IsAction(action)) return;
        // Input holds the aggregate action state, including other devices. Axis events above the threshold can
        // arrive repeatedly; only the rising edge is a press. Track releases even while gameplay is disabled.
        var held = Input.IsActionPressed(action);
        pressed |= enabled && held && !down && e.IsActionPressed(action);
        down = held;
    }

    public PlayerIntent Read()
    {
        var captured = gameplayEnabled();
        var intent = new PlayerIntent
        {
            Move = captured ? Input.GetVector(InputSetup.MoveLeft, InputSetup.MoveRight, InputSetup.MoveBack, InputSetup.MoveForward, Tuning.Current.StickDeadzone) : Vector2.Zero,
            Sprint = captured && Input.IsActionPressed(InputSetup.Sprint),
            Aim = captured && Input.IsActionPressed(InputSetup.Aim),
            JumpPressed = captured && jump,
            FirePressed = captured && fire,
            FireHeld = captured && Input.IsActionPressed(InputSetup.Fire),
            ReloadPressed = captured && reload,
            MeleePressed = captured && melee,
            Look = captured ? look : Vector2.Zero,
            LookStick = captured ? Input.GetVector(InputSetup.LookLeft, InputSetup.LookRight,
                InputSetup.LookUp, InputSetup.LookDown, Tuning.Current.StickDeadzone) : Vector2.Zero,
        };
        look = Vector2.Zero;
        jump = false;
        fire = false;
        reload = false;
        melee = false;
        // UI can consume a release before _UnhandledInput sees it. Resynchronize at the physics sample so
        // returning to gameplay does not lose the next press; pending edges above are still consumed once.
        SynchronizeHeldActions();
        return intent;
    }
}
