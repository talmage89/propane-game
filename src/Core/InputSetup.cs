using Godot;

namespace Propane.Core;

/// <summary>Registers the input map in code, so it lives next to the gameplay that reads it.</summary>
public static class InputSetup
{
    public const string MoveForward = "move_forward";
    public const string MoveBack = "move_back";
    public const string MoveLeft = "move_left";
    public const string MoveRight = "move_right";
    public const string Sprint = "sprint";
    public const string Jump = "jump";
    public const string Fire = "fire";
    public const string Aim = "aim";
    public const string NewSuburb = "new_suburb";
    /// <summary>Reload in a match, where R does not make a new suburb.</summary>
    public const string Reload = "reload";
    public const string Pause = "pause";
    public const string ToggleTuning = "toggle_tuning";
    public const string ToggleFullscreen = "toggle_fullscreen";

    public static void Register()
    {
        Bind(MoveForward, Key(Godot.Key.W));
        Bind(MoveBack, Key(Godot.Key.S));
        Bind(MoveLeft, Key(Godot.Key.A));
        Bind(MoveRight, Key(Godot.Key.D));
        Bind(Sprint, Key(Godot.Key.Shift));
        Bind(Jump, Key(Godot.Key.Space));
        Bind(NewSuburb, Key(Godot.Key.R));
        Bind(Reload, Key(Godot.Key.R));
        Bind(Pause, Key(Godot.Key.Escape));
        // Function keys need fn on a Mac (and F11 is Show Desktop there), so each has a plain alternative.
        Bind(ToggleTuning, Key(Godot.Key.F1), Key(Godot.Key.Quoteleft));
        Bind(ToggleFullscreen, Key(Godot.Key.F11), new InputEventKey { PhysicalKeycode = Godot.Key.Enter, AltPressed = true });
        Bind(Fire, new InputEventMouseButton { ButtonIndex = MouseButton.Left });
        Bind(Aim, new InputEventMouseButton { ButtonIndex = MouseButton.Right });
    }

    private static InputEventKey Key(Key key) => new() { PhysicalKeycode = key };

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
