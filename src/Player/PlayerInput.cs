using System;
using System.Collections.Generic;
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

/// <summary>Primary preserves the shipped single-view device switching; explicit assignments isolate couch views.</summary>
public readonly record struct InputDeviceAssignment(int GamepadId, bool KeyboardMouse)
{
    public static InputDeviceAssignment Primary => new(-1, true);
    public static InputDeviceAssignment KeyboardAndMouse => new(-2, true);
    public static InputDeviceAssignment Gamepad(int id) => id >= 0 ? new(id, false) : throw new ArgumentOutOfRangeException(nameof(id));
    public bool IsPrimary => GamepadId == -1 && KeyboardMouse;
    public bool Accepts(InputEvent e) => e switch
    {
        InputEventJoypadButton or InputEventJoypadMotion => IsPrimary || e.Device == GamepadId,
        InputEventKey or InputEventMouse => KeyboardMouse,
        InputEventAction => IsPrimary || e.Device == GamepadId || KeyboardMouse && e.Device == -2,
        _ => false,
    };
}

/// <summary>Gameplay actions from any device. Mouse motion accumulates between reads.</summary>
public sealed class DeviceInput : IPlayerInputSource
{
    private readonly Func<bool> gameplayEnabled;
    private readonly Tuning tuning;

    /// <summary>The facade supplies UI focus; tests can enable actions without a rendered mouse capture.</summary>
    public DeviceInput(Tuning tuning, Func<bool>? gameplayEnabled = null, InputDeviceAssignment? assignment = null)
    {
        this.tuning = tuning;
        Assignment = assignment ?? InputDeviceAssignment.Primary;
        this.gameplayEnabled = gameplayEnabled ?? (() => Input.MouseMode == Input.MouseModeEnum.Captured);
    }

    public InputDeviceAssignment Assignment { get; }
    private readonly Dictionary<(string Action, long Binding), float> strengths = new();
    private static readonly string[] actions = { InputSetup.MoveLeft, InputSetup.MoveRight, InputSetup.MoveBack,
        InputSetup.MoveForward, InputSetup.LookLeft, InputSetup.LookRight, InputSetup.LookUp, InputSetup.LookDown,
        InputSetup.Sprint, InputSetup.Aim, InputSetup.Jump, InputSetup.Fire, InputSetup.Reload, InputSetup.Melee };

    private float Strength(string action)
    {
        if (Assignment.IsPrimary) return Input.GetActionRawStrength(action);
        var strength = 0f;
        foreach (var entry in strengths) if (entry.Key.Action == action) strength = Mathf.Max(strength, entry.Value);
        return strength;
    }
    private bool Held(string action) => Assignment.IsPrimary ? Input.IsActionPressed(action) : Strength(action) > 0 && Strength(action) >= InputMap.ActionGetDeadzone(action);
    private Vector2 Vector(string left, string right, string up, string down, float deadzone)
    {
        if (Assignment.IsPrimary) return Input.GetVector(left, right, up, down, deadzone);
        var vector = new Vector2(Strength(right) - Strength(left), Strength(down) - Strength(up));
        var length = vector.Length();
        return length <= deadzone ? Vector2.Zero : vector.Normalized() * Mathf.Min(1, (length - deadzone) / (1 - deadzone));
    }

    private static long Binding(InputEvent e) => e switch
    {
        InputEventKey key => (1L << 32) | (long)(key.PhysicalKeycode != 0 ? key.PhysicalKeycode : key.Keycode),
        InputEventMouseButton button => (2L << 32) | (long)button.ButtonIndex,
        InputEventJoypadButton button => (3L << 32) | (long)button.ButtonIndex,
        InputEventJoypadMotion motion => (4L << 32) | (long)motion.Axis,
        _ => 0,
    };

    public void Disconnect(long device)
    {
        if (Assignment.IsPrimary || Assignment.GamepadId == device) { strengths.Clear(); Reset(); }
    }

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
        jumpDown = Held(InputSetup.Jump);
        fireDown = Held(InputSetup.Fire);
        reloadDown = Held(InputSetup.Reload);
        meleeDown = Held(InputSetup.Melee);
    }

    public void Accumulate(InputEvent @event)
    {
        if (!Assignment.Accepts(@event)) return;
        if (!Assignment.IsPrimary)
        {
            foreach (var action in actions)
            {
                if (@event is InputEventJoypadMotion padMotion)
                {
                    foreach (var binding in InputMap.ActionGetEvents(action))
                    {
                        if (binding is InputEventJoypadMotion axis && axis.Axis == padMotion.Axis)
                            strengths[(action, Binding(@event))] = Mathf.Max(0, padMotion.AxisValue * Mathf.Sign(axis.AxisValue));
                    }
                }
                else if (@event.IsAction(action))
                {
                    strengths[(action, Binding(@event))] = @event.GetActionStrength(action);
                }
            }
        }

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

    private void AccumulatePress(InputEvent e, string action, bool enabled, ref bool down, ref bool pressed)
    {
        if (!e.IsAction(action)) return;
        // Input holds the aggregate action state, including other devices. Axis events above the threshold can
        // arrive repeatedly; only the rising edge is a press. Track releases even while gameplay is disabled.
        var held = Held(action);
        pressed |= enabled && held && !down && e.IsActionPressed(action);
        down = held;
    }

    public PlayerIntent Read()
    {
        var captured = gameplayEnabled();
        var intent = new PlayerIntent
        {
            Move = captured ? Vector(InputSetup.MoveLeft, InputSetup.MoveRight, InputSetup.MoveBack, InputSetup.MoveForward, tuning.StickDeadzone) : Vector2.Zero,
            Sprint = captured && Held(InputSetup.Sprint),
            Aim = captured && Held(InputSetup.Aim),
            JumpPressed = captured && jump,
            FirePressed = captured && fire,
            FireHeld = captured && Held(InputSetup.Fire),
            ReloadPressed = captured && reload,
            MeleePressed = captured && melee,
            Look = captured ? look : Vector2.Zero,
            LookStick = captured ? Vector(InputSetup.LookLeft, InputSetup.LookRight,
                InputSetup.LookUp, InputSetup.LookDown, tuning.StickDeadzone) : Vector2.Zero,
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
