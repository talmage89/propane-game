using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Core;

namespace Propane.UI;

/// <summary>
/// Controller navigation for code-built menus. Rows follow the layout's control order; left/right crosses a row,
/// up/down crosses rows. Stable focus keys survive network rebuilds. Keyboard navigation uses the same neighbors.
/// </summary>
public partial class MenuNavigation : Node
{
    private const float StickThreshold = 0.5f;
    private const double RepeatDelay = 0.4;
    private const double RepeatInterval = 0.12;
    private Vector2 stick;
    private Vector2I pad;
    private Vector2I held;
    private double repeat;
    private double heldSeconds;
    private bool wasActive;
    private int scrollDelay;

    public required CanvasLayer Screen { get; init; }
    public required Control Root { get; init; }
    public Action? Back { get; init; }
    public Action<int>? AdjustLength { get; init; }

    /// <summary>Shared across front end and play, including while the tree is paused. Ignore idle stick drift.</summary>
    public static bool Gamepad { get; private set; }

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        AddToGroup("menu_navigation");
        Input.JoyConnectionChanged += OnJoyConnectionChanged;
    }

    public override void _ExitTree() => Input.JoyConnectionChanged -= OnJoyConnectionChanged;

    private void OnJoyConnectionChanged(long device, bool connected)
    {
        if (!connected)
        {
            ClearHeld();
        }
    }

    private bool Active => Screen.Visible &&
        !GetTree().GetNodesInGroup("menu_navigation").OfType<MenuNavigation>()
            .Any(n => n != this && n.Screen.Visible && n.Screen.Layer > Screen.Layer);

    public override void _Input(InputEvent e)
    {
        switch (e)
        {
            case InputEventJoypadButton { Pressed: true }:
            case InputEventJoypadMotion motion when Mathf.Abs(motion.AxisValue) > Tuning.SavedTemplate.StickDeadzone:
                Gamepad = true;
                break;
            case InputEventKey { Pressed: true }:
            case InputEventMouseButton { Pressed: true }:
            case InputEventMouseMotion mouse when mouse.Relative != Vector2.Zero:
                Gamepad = false;
                break;
        }
        if (!Active || e is not (InputEventJoypadButton or InputEventJoypadMotion))
        {
            return;
        }
        EnsureFocus();
        if (e is InputEventJoypadButton button)
        {
            if (button.ButtonIndex is JoyButton.A or JoyButton.B || button.ButtonIndex == JoyButton.Start && Back != null)
            {
                GetViewport().SetInputAsHandled();
                if (button.Pressed)
                {
                    if (button.ButtonIndex == JoyButton.A)
                    {
                        Confirm();
                    }
                    else
                    {
                        Back?.Invoke();
                    }
                }
                return;
            }
            var direction = button.ButtonIndex switch
            {
                JoyButton.DpadUp => Vector2I.Up,
                JoyButton.DpadDown => Vector2I.Down,
                JoyButton.DpadLeft => Vector2I.Left,
                JoyButton.DpadRight => Vector2I.Right,
                _ => Vector2I.Zero,
            };
            if (direction == Vector2I.Zero)
            {
                return;
            }
            if (direction.X != 0)
            {
                pad.X = button.Pressed ? direction.X : pad.X == direction.X ? 0 : pad.X;
            }
            else
            {
                pad.Y = button.Pressed ? direction.Y : pad.Y == direction.Y ? 0 : pad.Y;
            }
        }
        else if (e is InputEventJoypadMotion motion)
        {
            if (motion.Axis == JoyAxis.LeftX)
            {
                stick.X = motion.AxisValue;
            }
            else if (motion.Axis == JoyAxis.LeftY)
            {
                stick.Y = motion.AxisValue;
            }
            else
            {
                return;
            }
        }
        GetViewport().SetInputAsHandled();
        var next = pad.Y != 0 ? new Vector2I(0, pad.Y) : pad.X != 0 ? new Vector2I(pad.X, 0) :
            Mathf.Max(Mathf.Abs(stick.X), Mathf.Abs(stick.Y)) < StickThreshold ? Vector2I.Zero :
            Mathf.Abs(stick.X) > Mathf.Abs(stick.Y) ? new Vector2I(Math.Sign(stick.X), 0) : new Vector2I(0, Math.Sign(stick.Y));
        if (next != held)
        {
            held = next;
            heldSeconds = 0;
            repeat = RepeatDelay;
            if (held != Vector2I.Zero)
            {
                Move(held);
            }
        }
    }

    public override void _Process(double delta)
    {
        if (!Active)
        {
            wasActive = false;
            ClearHeld();
            return;
        }
        if (!wasActive)
        {
            Configure(FocusKey());
            wasActive = true;
        }
        EnsureFocus();
        if (scrollDelay > 0 && --scrollDelay == 0)
        {
            RevealFocus();
        }
        if (held == Vector2I.Zero)
        {
            return;
        }
        heldSeconds += delta;
        repeat -= delta;
        if (repeat <= 0)
        {
            repeat = RepeatInterval;
            Move(held);
        }
    }

    private void ClearHeld()
    {
        heldSeconds = 0;
        stick = Vector2.Zero;
        pad = held = Vector2I.Zero;
    }

    private List<Control> Controls() => Root.FindChildren("*", "Control", owned: false).OfType<Control>()
        .Prepend(Root).Where(c => c.IsVisibleInTree() && c.FocusMode == Control.FocusModeEnum.All && c is not BaseButton { Disabled: true }).ToList();

    public static string Key(Control control) => control.HasMeta("focus_key") ? control.GetMeta("focus_key").AsString() :
        control is Button button ? button.Text : control is LineEdit field ? field.PlaceholderText : control.Name.ToString();

    public string? FocusKey()
    {
        var owner = GetViewport().GuiGetFocusOwner();
        return owner != null && (owner == Root || Root.IsAncestorOf(owner)) ? Key(owner) : null;
    }

    /// <summary>Call after rebuilding, only after removing the old controls. A missing/disabled target falls back.</summary>
    public void Configure(string? preferred = null)
    {
        var controls = Controls();
        var rows = controls.GroupBy(c => c.GetParent() is HBoxContainer ? c.GetParent() : c).Select(g => g.ToList()).ToList();
        for (var r = 0; r < rows.Count; r++)
        {
            for (var col = 0; col < rows[r].Count; col++)
            {
                var c = rows[r][col];
                Control Vertical(int offset)
                {
                    var row = rows[(r + offset + rows.Count) % rows.Count];
                    return row[Math.Min(col, row.Count - 1)];
                }
                c.FocusNeighborTop = c.GetPathTo(Vertical(-1));
                c.FocusNeighborBottom = c.GetPathTo(Vertical(1));
                c.FocusNeighborLeft = c.GetPathTo(rows[r][(col + rows[r].Count - 1) % rows[r].Count]);
                c.FocusNeighborRight = c.GetPathTo(rows[r][(col + 1) % rows[r].Count]);
                var i = controls.IndexOf(c);
                c.FocusNext = c.GetPathTo(controls[(i + 1) % controls.Count]);
                c.FocusPrevious = c.GetPathTo(controls[(i + controls.Count - 1) % controls.Count]);
            }
        }
        var target = controls.FirstOrDefault(c => Key(c) == preferred) ?? controls.FirstOrDefault();
        target?.GrabFocus();
        scrollDelay = 2;
    }

    private void EnsureFocus()
    {
        var owner = GetViewport().GuiGetFocusOwner();
        if (owner == null || !owner.IsVisibleInTree() || owner.FocusMode != Control.FocusModeEnum.All ||
            owner is BaseButton { Disabled: true } || owner != Root && !Root.IsAncestorOf(owner))
        {
            Configure();
        }
    }

    private void Move(Vector2I direction)
    {
        var owner = GetViewport().GuiGetFocusOwner();
        if (owner == null)
        {
            return;
        }
        if (direction.X != 0 && owner is HsvColorSlider colorSlider)
        {
            colorSlider.Adjust(direction.X, heldSeconds);
            return;
        }
        if (direction.X != 0 && owner is HSlider slider)
        {
            slider.Value += direction.X * slider.Step;
            return;
        }
        if (direction.X != 0 && Key(owner) == "match_length")
        {
            AdjustLength?.Invoke(direction.X);
            return;
        }
        var path = direction.Y < 0 ? owner.FocusNeighborTop : direction.Y > 0 ? owner.FocusNeighborBottom :
            direction.X < 0 ? owner.FocusNeighborLeft : owner.FocusNeighborRight;
        owner.GetNodeOrNull<Control>(path)?.GrabFocus();
        RevealFocus();
    }

    private void RevealFocus()
    {
        var owner = GetViewport().GuiGetFocusOwner();
        Control? target = owner;
        // Rebuilt lists need a laid-out frame. Include row padding so the focus outline stays inside the clip.
        for (var parent = owner?.GetParent(); parent != null && parent != Screen; parent = parent.GetParent())
        {
            // A margin around the whole scrolled list (the tuning panel's gutter) is not a row's padding. Revealing
            // it scrolls to the bottom of the list, away from the control that has focus.
            if (parent is MarginContainer margin && margin.GetParent() is not ScrollContainer)
            {
                target = margin;
            }
            if (parent is ScrollContainer scroll && target != null)
            {
                scroll.EnsureControlVisible(target);
            }
        }
    }

    private void Confirm()
    {
        var owner = GetViewport().GuiGetFocusOwner();
        if (owner is BaseButton { Disabled: false } button)
        {
            if (button.ToggleMode) button.ButtonPressed = !button.ButtonPressed;
            button.EmitSignal(BaseButton.SignalName.Pressed);
        }
        else if (owner is LineEdit field)
        {
            // Steam owns on-screen text entry. Accept defaults and continue through the form without typing.
            var next = field.GetNodeOrNull<Control>(field.FocusNext);
            field.EmitSignal(LineEdit.SignalName.TextSubmitted, field.Text);
            if (IsInstanceValid(next) && next.IsInsideTree())
            {
                next.GrabFocus();
            }
        }
    }
}
