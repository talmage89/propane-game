using Godot;

namespace Propane.Core;

/// <summary>Registers the input map in code, so it lives next to the gameplay that reads it.</summary>
public static class InputSetup
{
    public const string MoveForward = "move_forward";
    public const string MoveBack = "move_back";
    public const string MoveLeft = "move_left";
    public const string MoveRight = "move_right";
    public const string LookLeft = "look_left";
    public const string LookRight = "look_right";
    public const string LookUp = "look_up";
    public const string LookDown = "look_down";
    public const string Sprint = "sprint";
    public const string Jump = "jump";
    public const string Fire = "fire";
    public const string Aim = "aim";
    public const string Melee = "melee";
    public const string NewSuburb = "new_suburb";
    /// <summary>Reload in a match, where R does not make a new suburb.</summary>
    public const string Reload = "reload";
    public const string Pause = "pause";
    public const string ToggleTuning = "toggle_tuning";
    public const string ToggleFullscreen = "toggle_fullscreen";

    public static void Register()
    {
        Bind(MoveForward, Key(Godot.Key.W), Axis(JoyAxis.LeftY, -1));
        Bind(MoveBack, Key(Godot.Key.S), Axis(JoyAxis.LeftY, 1));
        Bind(MoveLeft, Key(Godot.Key.A), Axis(JoyAxis.LeftX, -1));
        Bind(MoveRight, Key(Godot.Key.D), Axis(JoyAxis.LeftX, 1));
        Bind(Sprint, Key(Godot.Key.Shift), Button(JoyButton.LeftStick));
        Bind(Jump, Key(Godot.Key.Space), Button(JoyButton.A));
        Bind(NewSuburb, Key(Godot.Key.R), Button(JoyButton.X));
        Bind(Reload, Key(Godot.Key.R), Button(JoyButton.X));
        Bind(Pause, Key(Godot.Key.Escape), LogicalKey(Godot.Key.Escape), Button(JoyButton.Start));
        // Function keys need fn on a Mac (and F11 is Show Desktop there), so each has a plain alternative.
        Bind(ToggleTuning, Key(Godot.Key.F1), LogicalKey(Godot.Key.F1), Key(Godot.Key.Quoteleft), LogicalKey(Godot.Key.Quoteleft));
        Bind(ToggleFullscreen, Key(Godot.Key.F11), LogicalKey(Godot.Key.F11),
            new InputEventKey { PhysicalKeycode = Godot.Key.Enter, AltPressed = true },
            new InputEventKey { Keycode = Godot.Key.Enter, AltPressed = true });
        Bind(Fire, new InputEventMouseButton { ButtonIndex = MouseButton.Left }, Axis(JoyAxis.TriggerRight, 1));
        Bind(Aim, new InputEventMouseButton { ButtonIndex = MouseButton.Right }, Axis(JoyAxis.TriggerLeft, 1));
        Bind(Melee, Key(Godot.Key.F), new InputEventMouseButton { ButtonIndex = MouseButton.Xbutton1 },
            new InputEventMouseButton { ButtonIndex = MouseButton.Xbutton2 }, Button(JoyButton.RightStick));
        Bind(LookLeft, Axis(JoyAxis.RightX, -1));
        Bind(LookRight, Axis(JoyAxis.RightX, 1));
        Bind(LookUp, Axis(JoyAxis.RightY, -1));
        Bind(LookDown, Axis(JoyAxis.RightY, 1));
    }

    private static InputEventJoypadMotion Axis(JoyAxis axis, float value) => new() { Device = -1, Axis = axis, AxisValue = value };
    private static InputEventJoypadButton Button(JoyButton button) => new() { Device = -1, ButtonIndex = button };
    private static InputEventKey Key(Key key) => new() { PhysicalKeycode = key };
    // System/menu shortcuts honor desktop remaps as well as the original physical key. Gameplay stays physical.
    private static InputEventKey LogicalKey(Key key) => new() { Keycode = key };

    private static void Bind(string action, params InputEvent[] events)
    {
        Ensure(action);
        foreach (var e in events)
        {
            InputMap.ActionAddEvent(action, e);
        }
    }

    private static void Ensure(string action)
    {
        if (InputMap.HasAction(action))
        {
            InputMap.ActionEraseEvents(action);
        }
        else
        {
            InputMap.AddAction(action);
        }
    }
}
