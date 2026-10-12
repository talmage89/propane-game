using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Core;

namespace Propane.UI;

/// <summary>
/// F1 panel: a live slider for every value in <see cref="Tuning"/>, built from its exported properties and grouped
/// like the inspector. Changes apply immediately; Save writes them to <see cref="Tuning.FilePath"/>.
/// </summary>
public partial class TuningPanel : CanvasLayer
{
    public required Tuning Target { get; init; }
    public bool IncludeLabDials { get; init; }
    public event System.Action? CloseRequested;
    private readonly List<(string Property, Range Slider, Label Value, bool IsInt)> rows = new();
    private readonly List<(string Property, CheckButton Toggle)> toggles = new();
    private Label status = null!;

    /// <summary>A value was changed from the panel (a match shares it with every player).</summary>
    public event System.Action<string, Variant>? ValueChanged;

    /// <summary>Shown in the header: in a match, changes apply to everyone.</summary>
    public string Note { get; set; } = "";

    /// <summary>Network matches enforce the declared range, including hints that allow larger local values.</summary>
    public bool UseDeclaredRanges { get; set; }

    public override void _Ready()
    {
        Layer = 20;
        ProcessMode = ProcessModeEnum.Always;
        Visible = false;

        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", UiStyle.Panel(0.93f));
        panel.SetAnchorsPreset(Control.LayoutPreset.RightWide);
        panel.OffsetLeft = -540;
        panel.OffsetRight = -16;
        panel.OffsetTop = 16;
        panel.OffsetBottom = -16;
        AddChild(panel);

        var layout = new VBoxContainer();
        layout.AddThemeConstantOverride("separation", 10);
        panel.AddChild(layout);

        var header = new HBoxContainer();
        header.AddChild(UiStyle.Text("Tuning", 26));
        header.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        header.AddChild(UiStyle.Text(Note.Length > 0 ? Note : "F1 or ` to close", 15, UiStyle.SoftInk));
        layout.AddChild(header);

        var scroll = new ScrollContainer { FollowFocus = true, SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        layout.AddChild(scroll);
        // Keep the values clear of the scroll bar.
        var gutter = new MarginContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        gutter.AddThemeConstantOverride("margin_right", 16);
        scroll.AddChild(gutter);
        var list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 4);
        gutter.AddChild(list);
        Populate(list);

        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 8);
        var save = UiStyle.Button("Save");
        save.Pressed += Save;
        var defaults = UiStyle.Button("Defaults");
        defaults.Pressed += ResetToDefaults;
        buttons.AddChild(save);
        buttons.AddChild(defaults);
        layout.AddChild(buttons);
        if (CloseRequested != null)
        {
            var close = UiStyle.Button("Close tuning");
            close.Pressed += () => CloseRequested?.Invoke();
            buttons.AddChild(close);
        }
        scroll.GetVScrollBar().FocusMode = Control.FocusModeEnum.None;
        if (IncludeLabDials)
            AddChild(new MenuNavigation { Screen = this, Root = layout, Back = () => CloseRequested?.Invoke() });
        status = UiStyle.Text(IncludeLabDials ? "Save writes gameplay dials to your saved template." : "", 14, UiStyle.SoftInk);
        layout.AddChild(status);
    }

    public void Toggle()
    {
        Visible = !Visible;
        if (Visible)
        {
            Refresh();
        }
    }

    private void Populate(VBoxContainer list)
    {
        var tuning = Target;
        // A group heading is only added once a slider follows it, which skips Resource's own built-in group.
        string? pendingGroup = null;
        foreach (var entry in tuning.GetPropertyList())
        {
            var name = entry["name"].AsString();
            var usage = (PropertyUsageFlags)entry["usage"].AsInt64();
            if (usage.HasFlag(PropertyUsageFlags.Group))
            {
                pendingGroup = name;
                continue;
            }
            if (!usage.HasFlag(PropertyUsageFlags.ScriptVariable) || !usage.HasFlag(PropertyUsageFlags.Editor))
            {
                continue;
            }
            var type = (Variant.Type)entry["type"].AsInt64();
            if (type != Variant.Type.Float && type != Variant.Type.Int && type != Variant.Type.Bool)
            {
                continue;
            }
            if (pendingGroup != null)
            {
                var heading = UiStyle.Text(pendingGroup.ToUpperInvariant(), 15, UiStyle.Accent);
                list.AddChild(new Control { CustomMinimumSize = new Vector2(0, 6) });
                list.AddChild(heading);
                pendingGroup = null;
            }
            if (type == Variant.Type.Bool)
            {
                list.AddChild(ToggleRow(name));
                continue;
            }
            AddNumber(list, name, type == Variant.Type.Int, entry["hint_string"].AsString());
        }
        if (IncludeLabDials)
        {
            list.AddChild(new Control { CustomMinimumSize = new Vector2(0, 6) });
            list.AddChild(UiStyle.Text("LAB · LAYOUT APPLIES ON RESET", 15, UiStyle.Accent));
            foreach (var property in typeof(Tuning).GetProperties())
            {
                if (System.Attribute.GetCustomAttribute(property, typeof(LabDialAttribute)) is not LabDialAttribute dial) continue;
                var hint = System.FormattableString.Invariant($"{dial.Min},{dial.Max},{dial.Step}");
                AddNumber(list, property.Name, property.PropertyType == typeof(int), hint);
            }
        }
        Refresh();
    }

    private void AddNumber(VBoxContainer list, string name, bool isInt, string hint)
    {
        var (min, max, step) = ParseRange(hint, isInt, UseDeclaredRanges);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        var label = UiStyle.Text(Pretty(name), 16);
        label.CustomMinimumSize = new Vector2(190, 0);
        label.ClipText = true;
        label.TooltipText = Pretty(name);
        var slider = new HSlider
        {
            MinValue = min,
            MaxValue = max,
            Step = step,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            // Only the Lab's panel has controller navigation. Elsewhere the game keeps running with it open, and a
            // focused slider or switch would take arrow and Space presses meant for the player.
            FocusMode = IncludeLabDials ? Control.FocusModeEnum.All : Control.FocusModeEnum.None,
            // The wheel scrolls this long list. A slider that also took it changed a dial a step per notch.
            Scrollable = false,
            CustomMinimumSize = new Vector2(0, 22),
        };
        var value = UiStyle.Text("", 16, UiStyle.SoftInk, HorizontalAlignment.Right);
        value.CustomMinimumSize = new Vector2(76, 0);
        var property = name;
        slider.ValueChanged += v =>
        {
            var set = isInt ? Variant.From((int)v) : Variant.From((float)v);
            Write(Target, property, set);
            value.Text = Format(v, isInt);
            ValueChanged?.Invoke(property, set);
        };
        row.AddChild(label);
        row.AddChild(slider);
        row.AddChild(value);
        list.AddChild(row);
        rows.Add((property, slider, value, isInt));
    }

    private Control ToggleRow(string property)
    {
        var row = new HBoxContainer();
        var label = UiStyle.Text(Pretty(property), 16);
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var toggle = new CheckButton { FocusMode = IncludeLabDials ? Control.FocusModeEnum.All : Control.FocusModeEnum.None };
        toggle.Toggled += on =>
        {
            Target.Set(property, on);
            ValueChanged?.Invoke(property, on);
        };
        row.AddChild(label);
        row.AddChild(toggle);
        toggles.Add((property, toggle));
        return row;
    }

    /// <summary>Re-reads every value, after a change from elsewhere.</summary>
    public void Refresh()
    {
        foreach (var (property, toggle) in toggles)
        {
            toggle.SetPressedNoSignal(Read(Target, property).AsBool());
        }
        foreach (var (property, slider, value, isInt) in rows)
        {
            var v = Read(Target, property).AsDouble();
            slider.SetValueNoSignal(v);
            value.Text = Format(v, isInt);
        }
    }

    private void Save()
    {
        var error = Tuning.SaveTemplate(Target);
        status.Text = error == Error.Ok ? $"Saved {Tuning.FilePath} at {Time.GetTimeStringFromSystem()}" : $"Save failed: {error}";
    }

    private void ResetToDefaults()
    {
        using var defaults = new Tuning();
        foreach (var property in rows.Select(r => r.Property).Concat(toggles.Select(t => t.Property)))
        {
            Write(Target, property, Read(defaults, property));
            ValueChanged?.Invoke(property, Read(defaults, property));
        }
        Refresh();
        status.Text = "Defaults restored (not saved yet)";
    }

    private static Variant Read(Tuning tuning, string name)
    {
        var property = typeof(Tuning).GetProperty(name);
        if (property != null && System.Attribute.IsDefined(property, typeof(LabDialAttribute)))
            return property.PropertyType == typeof(int) ? Variant.From((int)property.GetValue(tuning)!) : Variant.From((float)property.GetValue(tuning)!);
        return tuning.Get(name);
    }

    private static void Write(Tuning tuning, string name, Variant value)
    {
        var property = typeof(Tuning).GetProperty(name);
        if (property != null && System.Attribute.IsDefined(property, typeof(LabDialAttribute)))
        {
            if (property.PropertyType == typeof(int)) property.SetValue(tuning, value.AsInt32());
            else property.SetValue(tuning, value.AsSingle());
        }
        else
        {
            tuning.Set(name, value);
        }
    }

    private static (double, double, double) ParseRange(string hint, bool isInt, bool bounded)
    {
        var parts = hint.Split(',');
        if (parts.Length >= 2 && double.TryParse(parts[0], out var min) && double.TryParse(parts[1], out var max))
        {
            var step = parts.Length >= 3 && double.TryParse(parts[2], out var s) ? s : (isInt ? 1 : 0.01);
            if (!bounded && System.Array.Exists(parts, p => p.Trim() == "or_greater"))
            {
                max *= 2;
            }
            return (min, max, step);
        }
        return isInt ? (0, 100000, 1) : (0, 100, 0.01);
    }

    private static string Format(double v, bool isInt) => isInt ? ((int)v).ToString() : v < 0.1 && v > 0 ? v.ToString("0.0000") : v.ToString("0.00");

    /// <summary>"BlastSpeed" -&gt; "Blast speed".</summary>
    private static string Pretty(string name)
    {
        var chars = new System.Text.StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]))
            {
                chars.Append(' ');
                chars.Append(char.ToLowerInvariant(name[i]));
            }
            else
            {
                chars.Append(name[i]);
            }
        }
        return chars.ToString();
    }
}
