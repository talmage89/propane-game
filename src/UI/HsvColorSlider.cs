using System;
using Godot;

namespace Propane.UI;

/// <summary>A focusable color strip. Values are normalized; mouse and controller share the same change path.</summary>
public partial class HsvColorSlider : Control
{
    public bool Hue { get; init; }
    public Func<float, Color> ColorAt { get; set; } = _ => Colors.White;
    public event Action? Changed;
    private float value;
    private bool dragging;
    private readonly StyleBoxFlat focus = UiStyle.Focus();
    public float Value
    {
        get => value;
        set
        {
            var next = Mathf.Clamp(value, 0, 1);
            if (this.value == next) { return; }
            this.value = next;
            QueueRedraw();
            Changed?.Invoke();
        }
    }

    public HsvColorSlider()
    {
        FocusMode = FocusModeEnum.All;
        CustomMinimumSize = new Vector2(300, 34);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
    }

    public override void _Ready()
    {
        FocusEntered += QueueRedraw;
        FocusExited += () => { dragging = false; QueueRedraw(); };
    }

    /// <summary>Fine taps, then 4x and 10x steps after sustained holds. This is local menu behavior.</summary>
    public void Adjust(int direction, double heldSeconds) =>
        Value += direction * (Hue ? 1f / 360 : 0.01f) * (heldSeconds >= 1.5 ? 10 : heldSeconds >= 0.7 ? 4 : 1);

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } button)
        {
            dragging = button.Pressed;
            if (dragging) { GrabFocus(); SetFromMouse(button.Position.X); }
            AcceptEvent();
        }
        else if (e is InputEventMouseMotion motion && dragging)
        {
            SetFromMouse(motion.Position.X);
            AcceptEvent();
        }
        else if (e is InputEventKey key && key.Pressed && (e.IsAction("ui_left") || e.IsAction("ui_right")))
        {
            Adjust(e.IsAction("ui_left") ? -1 : 1, 0);
            AcceptEvent();
        }
    }

    private void SetFromMouse(float x) => Value = (x - 6) / Mathf.Max(Size.X - 12, 1);

    public override void _Draw()
    {
        var strip = new Rect2(6, 5, Size.X - 12, Size.Y - 10);
        DrawRect(strip.Grow(1), UiStyle.Ink);
        const int segments = 180;
        for (var i = 0; i < segments; i++)
        {
            DrawRect(new Rect2(strip.Position + new Vector2(strip.Size.X * i / segments, 0),
                new Vector2(strip.Size.X / segments + 1, strip.Size.Y)), ColorAt((i + 0.5f) / segments));
        }
        var marker = new Rect2(strip.Position.X + Value * strip.Size.X - 3, 2, 6, Size.Y - 4);
        DrawRect(marker.Grow(1), UiStyle.Ink);
        DrawRect(marker, Colors.White, false, 2);
        if (HasFocus()) { DrawStyleBox(focus, new Rect2(Vector2.Zero, Size)); }
    }
}
