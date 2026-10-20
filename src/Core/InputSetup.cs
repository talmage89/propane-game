using System.Collections.Generic;
using System.Linq;
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
    /// <summary>The lobby world's panel (the lobby menu, over the world).</summary>
    public const string LobbyPanel = "lobby_panel";

    public static void Register(IPlayerSettings? settings = null)
    {
        ApplyBindings(settings);
        Bind(Pause, Key(Godot.Key.Escape), LogicalKey(Godot.Key.Escape), Button(JoyButton.Start));
        // Function keys need fn on a Mac (and F11 is Show Desktop there), so each has a plain alternative.
        Bind(ToggleTuning, Key(Godot.Key.F1), LogicalKey(Godot.Key.F1), Key(Godot.Key.Quoteleft), LogicalKey(Godot.Key.Quoteleft));
        Bind(ToggleFullscreen, Key(Godot.Key.F11), LogicalKey(Godot.Key.F11),
            new InputEventKey { PhysicalKeycode = Godot.Key.Enter, AltPressed = true },
            new InputEventKey { Keycode = Godot.Key.Enter, AltPressed = true });
        Bind(LookLeft, Axis(JoyAxis.RightX, -1));
        Bind(LookRight, Axis(JoyAxis.RightX, 1));
        Bind(LookUp, Axis(JoyAxis.RightY, -1));
        Bind(LookDown, Axis(JoyAxis.RightY, 1));
    }

    /// <summary>Called before scene attachment and synchronously on settings edits. No input polling changes.</summary>
    public static void ApplyBindings(IPlayerSettings? settings = null)
    {
        foreach (var action in ControlBindings.All)
        {
            Ensure(action.Id);
            foreach (var (slot, fallback) in ControlBindings.Slots.Where(s => s.Slot.StartsWith(action.Id + ".", System.StringComparison.Ordinal)))
            {
                var binding = settings?.GetBinding(slot) ?? fallback;
                var e = Event(binding);
                if (e != null) InputMap.ActionAddEvent(action.Id, e);
            }
            // Keep the shipped system remap behavior until the player overrides the lobby shortcut.
            if (action.Id == LobbyPanel && (settings?.GetBinding(ControlBindings.Slot(LobbyPanel, false)) ?? action.Keyboard[0]) == action.Keyboard[0])
                InputMap.ActionAddEvent(action.Id, LogicalKey(Godot.Key.Tab));
            if (action.FixedPad != null)
            {
                var axis = action.Id is MoveForward or MoveBack ? JoyAxis.LeftY : JoyAxis.LeftX;
                InputMap.ActionAddEvent(action.Id, Axis(axis, action.Id is MoveForward or MoveLeft ? -1 : 1));
            }
        }
        Ensure(NewSuburb);
        foreach (var e in InputMap.ActionGetEvents(Reload)) InputMap.ActionAddEvent(NewSuburb, e);
        descriptions.Clear();
        foreach (var action in ControlBindings.All)
            foreach (var pad in new[] { false, true }) descriptions[(action.Id, pad)] = DescribeCurrent(action, pad, settings);
        Revision++;
    }

    public static int Revision { get; private set; }

    public static InputEvent? Event(ControlBinding binding) => binding.Kind switch
    {
        BindingKind.Key => Key((Key)binding.Code),
        BindingKind.Mouse => new InputEventMouseButton { ButtonIndex = (MouseButton)binding.Code },
        BindingKind.Button => Button((JoyButton)binding.Code),
        BindingKind.Trigger => Axis((JoyAxis)binding.Code, 1),
        _ => null
    };

    public static ControlBinding Capture(InputEvent e) => e switch
    {
        InputEventKey { Pressed: true, Echo: false } k => new(BindingKind.Key, (int)k.PhysicalKeycode),
        InputEventMouseButton { Pressed: true } m => new(BindingKind.Mouse, (int)m.ButtonIndex),
        InputEventJoypadButton { Pressed: true } b => new(BindingKind.Button, (int)b.ButtonIndex),
        InputEventJoypadMotion { AxisValue: > 0.5f } a when a.Axis is JoyAxis.TriggerLeft or JoyAxis.TriggerRight => new(BindingKind.Trigger, (int)a.Axis),
        _ => default
    };

    // Derived display cache, application-lived, replaced synchronously with the native map. No per-tick queries
    // of the store or event/resource construction in prompts. It owns only managed strings.
    private static readonly Dictionary<(string Action, bool Pad), string> descriptions = new();
    public static string Describe(string action, bool gamepad)
    {
        if (action == NewSuburb) action = Reload;
        return descriptions.TryGetValue((action, gamepad), out var text) ? text : DescribeCurrent(ControlBindings.Action(action), gamepad, null);
    }

    private static string DescribeCurrent(ControlAction action, bool gamepad, IPlayerSettings? settings)
    {
        if (gamepad && action.FixedPad != null) return action.FixedPad;
        var count = gamepad ? 1 : action.Keyboard.Count;
        return string.Join(" / ", Enumerable.Range(0, count).Select(i => Describe(settings?.GetBinding(ControlBindings.Slot(action.Id, gamepad, i)) ??
            (gamepad ? action.Pad : action.Keyboard[i]))).Where(s => s != "Unbound").DefaultIfEmpty("Unbound"));
    }

    public static string Describe(ControlBinding binding, bool compact = false) => binding.Kind switch
    {
        BindingKind.Key => OS.GetKeycodeString((Key)binding.Code),
        BindingKind.Mouse => (MouseButton)binding.Code switch
        {
            MouseButton.Left => "Left click",
            MouseButton.Right => "Right click",
            MouseButton.Middle => "Middle click",
            MouseButton.Xbutton1 => compact ? "Thumb 1" : "Mouse thumb 1",
            MouseButton.Xbutton2 => compact ? "Thumb 2" : "Mouse thumb 2",
            _ => "Unbound"
        },
        BindingKind.Button => (JoyButton)binding.Code switch
        {
            JoyButton.A => "A / Cross",
            JoyButton.X => "X / Square",
            JoyButton.Y => "Y / Triangle",
            JoyButton.Back => "View / Create",
            JoyButton.LeftStick => "Left stick click",
            JoyButton.RightStick => "Right stick click",
            JoyButton.LeftShoulder => "LB / L1",
            JoyButton.RightShoulder => "RB / R1",
            _ => ((JoyButton)binding.Code).ToString()
        },
        BindingKind.Trigger => (JoyAxis)binding.Code == JoyAxis.TriggerLeft ? "Left trigger" : "Right trigger",
        _ => "Unbound"
    };

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
