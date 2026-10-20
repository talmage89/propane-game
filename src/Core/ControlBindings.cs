using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Propane.Core;

public enum BindingKind { None, Key, Mouse, Button, Trigger }

/// <summary>Device-independent persisted value; keys are physical, triggers positive, gamepads device-agnostic.</summary>
public readonly record struct ControlBinding(BindingKind Kind, int Code = 0)
{
    public bool Gamepad => Kind is BindingKind.Button or BindingKind.Trigger;
    public bool Valid => Kind switch
    {
        BindingKind.None => Code == 0,
        BindingKind.Key => Enum.IsDefined((Key)Code) && (Key)Code is not (Key.None or Key.Special or Key.Unknown or Key.Escape or Key.F1 or Key.Quoteleft or Key.F11),
        BindingKind.Mouse => (MouseButton)Code is MouseButton.Left or MouseButton.Right or MouseButton.Middle or MouseButton.Xbutton1 or MouseButton.Xbutton2,
        BindingKind.Button => Enum.IsDefined((JoyButton)Code) && (JoyButton)Code is >= JoyButton.A and < JoyButton.Max && (JoyButton)Code is not (JoyButton.B or JoyButton.Start),
        BindingKind.Trigger => (JoyAxis)Code is JoyAxis.TriggerLeft or JoyAxis.TriggerRight,
        _ => false
    };
    public override string ToString() => $"{Kind}:{Code}";
    public static bool TryParse(string text, out ControlBinding binding)
    {
        binding = default;
        var parts = text.Split(':');
        if (parts.Length != 2 || !Enum.TryParse<BindingKind>(parts[0], out var kind) || !int.TryParse(parts[1], out var code)) return false;
        binding = new(kind, code);
        return binding.Valid;
    }
}

public sealed record ControlAction(string Id, string Label, IReadOnlyList<ControlBinding> Keyboard, ControlBinding Pad, string? FixedPad = null);

/// <summary>Immutable personal binding defaults. Reload/new-suburb deliberately share one contextual slot.</summary>
public static class ControlBindings
{
    private static ControlBinding K(Key key) => new(BindingKind.Key, (int)key);
    private static ControlBinding M(MouseButton button) => new(BindingKind.Mouse, (int)button);
    private static ControlBinding B(JoyButton button) => new(BindingKind.Button, (int)button);
    private static IReadOnlyList<ControlBinding> Keys(params ControlBinding[] values) => Array.AsReadOnly(values);
    public static IReadOnlyList<ControlAction> All { get; } = Array.AsReadOnly(new[]
    {
        new ControlAction("move_forward", "Move forward", Keys(K(Key.W)), default, "Left stick ↑"),
        new ControlAction("move_back", "Move backward", Keys(K(Key.S)), default, "Left stick ↓"),
        new ControlAction("move_left", "Move left", Keys(K(Key.A)), default, "Left stick ←"),
        new ControlAction("move_right", "Move right", Keys(K(Key.D)), default, "Left stick →"),
        new ControlAction("sprint", "Sprint (hold)", Keys(K(Key.Shift)), B(JoyButton.LeftStick)),
        new ControlAction("jump", "Jump", Keys(K(Key.Space)), B(JoyButton.A)),
        new ControlAction("fire", "Fire", Keys(M(MouseButton.Left)), new(BindingKind.Trigger, (int)JoyAxis.TriggerRight)),
        new ControlAction("aim", "Aim (hold)", Keys(M(MouseButton.Right)), new(BindingKind.Trigger, (int)JoyAxis.TriggerLeft)),
        new ControlAction("melee", "Rifle-butt strike", Keys(K(Key.F), M(MouseButton.Xbutton1), M(MouseButton.Xbutton2)), B(JoyButton.RightStick)),
        new ControlAction("reload", "Reload / new suburb", Keys(K(Key.R)), B(JoyButton.X)),
        new ControlAction("lobby_panel", "Lobby panel", Keys(K(Key.Tab)), B(JoyButton.Back))
    });
    public static ControlAction Action(string id) => All.Single(a => a.Id == id);
    public static string Slot(string id, bool gamepad, int index = 0) => id + (gamepad ? ".pad" : ".key" + index);
    public static IEnumerable<(string Slot, ControlBinding Default)> Slots => All.SelectMany(a =>
        a.Keyboard.Select((b, i) => (Slot(a.Id, false, i), b)).Concat(a.FixedPad == null ? new[] { (Slot(a.Id, true), a.Pad) } : Array.Empty<(string, ControlBinding)>()));
}
