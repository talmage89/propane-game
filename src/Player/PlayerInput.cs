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
    public Vector2 Look;
}

public interface IPlayerInputSource
{
    PlayerIntent Read();
}

/// <summary>Keyboard and mouse. Mouse motion accumulates between reads.</summary>
public sealed class DeviceInput : IPlayerInputSource
{
    private Vector2 look;
    private bool jump;
    private bool fire;

    public void Accumulate(InputEvent @event)
    {
        if (Input.MouseMode != Input.MouseModeEnum.Captured)
        {
            return;
        }
        switch (@event)
        {
            case InputEventMouseMotion motion:
                look += motion.ScreenRelative;
                break;
            case InputEventMouseButton { Pressed: true } button when button.IsAction(InputSetup.Fire):
                fire = true;
                break;
            case InputEventKey { Pressed: true, Echo: false } key when key.IsAction(InputSetup.Jump):
                jump = true;
                break;
        }
    }

    public PlayerIntent Read()
    {
        var captured = Input.MouseMode == Input.MouseModeEnum.Captured;
        var intent = new PlayerIntent
        {
            Move = captured ? Input.GetVector(InputSetup.MoveLeft, InputSetup.MoveRight, InputSetup.MoveBack, InputSetup.MoveForward) : Vector2.Zero,
            Sprint = captured && Input.IsActionPressed(InputSetup.Sprint),
            Aim = captured && Input.IsActionPressed(InputSetup.Aim),
            JumpPressed = jump,
            FirePressed = fire,
            FireHeld = captured && Input.IsActionPressed(InputSetup.Fire),
            Look = look,
        };
        look = Vector2.Zero;
        jump = false;
        fire = false;
        return intent;
    }
}
