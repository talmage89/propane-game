using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Core;

namespace Propane.UI;

/// <summary>Parent-menu-lived editor borrowing the application store. Native children and handlers retire with it.</summary>
public partial class SettingsMenu : CanvasLayer
{
    public required CanvasLayer OwnerMenu { get; init; }
    public required IPlayerSettings Settings { get; init; }
    private readonly List<CanvasItem> hidden = new();
    private VBoxContainer rows = null!;
    private VBoxContainer layout = null!;
    private Label status = null!;
    private MarginContainer inset = null!;
    private MenuNavigation navigation = null!;
    private string category = PreferenceCatalog.All[0].Category;
    private Button launcher = null!;
    private bool saveSucceeded = true;
    private string displayNotice = "";
    private string bindingNotice = "";
    private string? captureSlot;
    // UI time (unscaled render delta, Always processing); independent of simulation/network clocks.
    private double captureRemaining;
    private Button cancelCapture = null!;
    private readonly Dictionary<string, Action<int>> adjusters = new();

    public static void AddLauncher(CanvasLayer owner, BoxContainer layout, IPlayerSettings? settings = null)
    {
        // Standalone harnesses without application preferences keep working; ordinary Main always binds a store.
        settings ??= PlayerPreferences.Current;
        if (settings == null) return;
        var menu = owner.GetNodeOrNull<SettingsMenu>("PersonalSettings");
        if (menu == null)
        {
            menu = new SettingsMenu { OwnerMenu = owner, Settings = settings, Name = "PersonalSettings" };
            owner.AddChild(menu);
        }
        menu.launcher = UiStyle.Button("Settings");
        menu.launcher.Pressed += menu.Open;
        layout.AddChild(menu.launcher);
    }

    public override void _Ready()
    {
        Layer = 60;
        ProcessMode = ProcessModeEnum.Always;
        Visible = false;
        var shade = new ColorRect { Color = new Color(0.95f, 0.95f, 0.96f, 0.45f) };
        shade.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(shade);
        inset = new MarginContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        inset.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        foreach (var edge in new[] { "left", "right", "top", "bottom" }) inset.AddThemeConstantOverride("margin_" + edge, 24);
        AddChild(inset);
        GetViewport().SizeChanged += Resize;
        Resize();
        var panel = new PanelContainer { CustomMinimumSize = new Vector2(0, 0) };
        panel.AddThemeStyleboxOverride("panel", UiStyle.Panel(0.98f));
        inset.AddChild(panel);
        layout = new VBoxContainer();
        layout.AddThemeConstantOverride("separation", 12);
        panel.AddChild(layout);
        layout.AddChild(UiStyle.Title("SETTINGS", 32));
        layout.AddChild(UiStyle.Text("Personal preferences · applied and saved as you change them", 16, UiStyle.SoftInk, HorizontalAlignment.Center));
        var tabs = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        tabs.AddThemeConstantOverride("separation", 12);
        foreach (var name in PreferenceCatalog.All.Select(p => p.Category).Distinct().Append("Controls"))
        {
            var tab = UiStyle.Button(name);
            tab.SetMeta("focus_key", "category_" + name);
            tab.Pressed += () => { category = name; BuildRows(); navigation.Configure("category_" + name); };
            tabs.AddChild(tab);
        }
        layout.AddChild(tabs);
        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        layout.AddChild(scroll);
        var gutter = new MarginContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        foreach (var edge in new[] { "left", "right", "top", "bottom" }) gutter.AddThemeConstantOverride("margin_" + edge, 8);
        scroll.AddChild(gutter);
        rows = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        rows.AddThemeConstantOverride("separation", 14);
        gutter.AddChild(rows);
        cancelCapture = UiStyle.Button("Cancel binding");
        cancelCapture.Visible = false;
        cancelCapture.Pressed += CancelCapture;
        layout.AddChild(cancelCapture);
        status = UiStyle.Text("Changes saved", 16, UiStyle.SoftInk, HorizontalAlignment.Center);
        status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        layout.AddChild(status);
        var footer = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        footer.AddThemeConstantOverride("separation", 18);
        var reset = UiStyle.Button("Reset category");
        reset.Pressed += () =>
        {
            bindingNotice = "";
            Saved(category == "Controls" ? Settings.ResetBindings() : Settings.ResetPreferences(category));
            BuildRows(); navigation.Configure("Reset category");
        };
        footer.AddChild(reset);
        var back = UiStyle.Button("Back");
        back.Pressed += Close;
        footer.AddChild(back);
        layout.AddChild(footer);
        layout.AddChild(UiStyle.Text("Tab / ↑ ↓ to navigate · ← → to adjust · Esc / B to go back", 14, UiStyle.SoftInk, HorizontalAlignment.Center));
        navigation = new MenuNavigation { Screen = this, Root = layout, Back = Close, Adjusters = adjusters };
        AddChild(navigation);
    }

    private void Open()
    {
        hidden.Clear();
        foreach (var item in OwnerMenu.GetChildren().OfType<CanvasItem>().Where(c => c.Visible))
        {
            hidden.Add(item);
            item.Visible = false;
        }
        Input.MouseMode = Input.MouseModeEnum.Visible;
        Visible = true;
        BuildRows();
        navigation.Configure("category_" + category);
    }

    private void Close()
    {
        CancelCapture();
        Visible = false;
        foreach (var item in hidden) if (IsInstanceValid(item)) item.Visible = true;
        hidden.Clear();
        if (IsInstanceValid(launcher) && launcher.IsVisibleInTree()) launcher.GrabFocus();
    }

    public override void _ExitTree()
    {
        GetViewport().SizeChanged -= Resize;
        Close();
    }

    private void Resize()
    {
        var size = GetViewport().GetVisibleRect().Size;
        foreach (var edge in new[] { "left", "right" }) inset.AddThemeConstantOverride("margin_" + edge, (int)Mathf.Max(24, (size.X - 1200) / 2));
        foreach (var edge in new[] { "top", "bottom" }) inset.AddThemeConstantOverride("margin_" + edge, (int)Mathf.Max(24, (size.Y - 950) / 2));
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        if (captureSlot != null)
        {
            captureRemaining -= delta / Math.Max(Engine.TimeScale, 0.0001);
            if (captureRemaining <= 0) { CancelCapture(); bindingNotice = "Binding timed out · unchanged"; }
            UpdateStatus();
        }
        if (!OwnerMenu.Visible) Close();
        // Swapchain changes can finish after the synchronous apply hook returns. Observe the effective mode.
        else if (category == PreferenceCatalog.WindowMode.Category && DisplayPreferences.FallbackNotice(Settings) != displayNotice) UpdateStatus();
    }

    public override void _Input(InputEvent e)
    {
        if (!Visible) return;
        if (captureSlot != null)
        {
            GetViewport().SetInputAsHandled();
            if (e is InputEventKey { Pressed: true } key && (key.Keycode == Key.Escape || key.PhysicalKeycode == Key.Escape) ||
                e is InputEventJoypadButton { Pressed: true, ButtonIndex: JoyButton.B or JoyButton.Start } ||
                e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mouse && cancelCapture.GetGlobalRect().HasPoint(mouse.Position))
            {
                CancelCapture();
                return;
            }
            var binding = InputSetup.Capture(e);
            if (binding.Kind == BindingKind.None) return;
            if (!binding.Valid) { bindingNotice = "That input is reserved or unsupported. Choose another."; UpdateStatus(); return; }
            if (binding.Gamepad != captureSlot.EndsWith(".pad", StringComparison.Ordinal)) return;
            var slot = captureSlot;
            var conflicts = ControlBindings.Slots.Where(s => s.Slot != slot && Settings.GetBinding(s.Slot) == binding).Select(s => s.Slot).ToArray();
            CancelCapture();
            bindingNotice = conflicts.Length == 0 ? "Binding saved" : "Warning · " + string.Join(", ", conflicts.Select(DescribeSlot)) + " left unbound";
            Saved(Settings.SetBinding(slot, binding));
            BuildRows(); navigation.Configure(slot);
            return;
        }
        if (e.IsActionPressed(InputSetup.ToggleFullscreen))
        {
            Saved(DisplayPreferences.ToggleFullscreen());
            if (category == PreferenceCatalog.WindowMode.Category)
            {
                var focus = navigation.FocusKey();
                BuildRows();
                navigation.Configure(focus);
            }
            GetViewport().SetInputAsHandled();
        }
        else if (e.IsActionPressed(InputSetup.Pause))
        {
            Close();
            GetViewport().SetInputAsHandled();
        }
    }

    // Navigation and GUI see events first. Nothing left over may toggle tuning, reset Lab, resume or recapture.
    public override void _UnhandledInput(InputEvent e)
    {
        if (Visible) GetViewport().SetInputAsHandled();
    }

    private void Saved(bool saved)
    {
        saveSucceeded = saved;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var notice = category == PreferenceCatalog.WindowMode.Category ? DisplayPreferences.FallbackNotice(Settings) : "";
        displayNotice = notice;
        status.Text = captureSlot != null ? $"Press a {(captureSlot.EndsWith(".pad", StringComparison.Ordinal) ? "gamepad button or trigger" : "physical key or mouse button")} for {DescribeSlot(captureSlot)} · {Math.Ceiling(captureRemaining)}s · Esc / B / Start cancels. {bindingNotice}"
            : !saveSucceeded ? "Could not save preferences. Check that your user data folder is writable."
            : bindingNotice.Length > 0 && category == "Controls" ? bindingNotice : notice.Length > 0 ? "Saved · " + notice : "Changes saved";
    }

    private void BuildRows()
    {
        UpdateStatus();
        foreach (var child in rows.GetChildren()) { rows.RemoveChild(child); child.QueueFree(); }
        adjusters.Clear();
        foreach (var tab in layout.FindChildren("*", "Button", owned: false).OfType<Button>().Where(b => MenuNavigation.Key(b).StartsWith("category_", StringComparison.Ordinal)))
            UiStyle.Choose(tab, MenuNavigation.Key(tab) == "category_" + category);
        if (category == "Controls") { BuildControls(); return; }
        foreach (var preference in PreferenceCatalog.All.Where(p => p.Category == category))
        {
            var row = new VBoxContainer();
            row.AddThemeConstantOverride("separation", 4);
            rows.AddChild(row);
            var line = new HBoxContainer();
            line.AddThemeConstantOverride("separation", 24);
            row.AddChild(line);
            var label = UiStyle.Text(preference.Label, 19);
            label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            line.AddChild(label);
            var value = Settings.GetPreference(preference);
            if (preference.Kind == PreferenceKind.Number)
            {
                var slider = new HSlider
                {
                    MinValue = preference.Min,
                    MaxValue = preference.Max,
                    Step = preference.Step,
                    Value = value,
                    CustomMinimumSize = new Vector2(240, 38),
                    FocusMode = Control.FocusModeEnum.All,
                    MouseForcePassScrollEvents = true,
                    Scrollable = false
                };
                var knob = new GradientTexture2D
                {
                    Width = 20,
                    Height = 20,
                    Fill = GradientTexture2D.FillEnum.Radial,
                    FillFrom = new Vector2(0.5f, 0.5f),
                    FillTo = new Vector2(1, 0.5f),
                    Gradient = new Gradient { Offsets = new[] { 0f, 0.8f, 1f }, Colors = new[] { UiStyle.Ink, UiStyle.Ink, new Color(UiStyle.Ink, 0) } },
                };
                slider.AddThemeIconOverride("grabber", knob);
                slider.AddThemeIconOverride("grabber_highlight", knob);
                slider.SetMeta("focus_key", preference.Id);
                slider.AddThemeStyleboxOverride("focus", UiStyle.Focus());
                line.AddChild(slider);
                var number = UiStyle.Text(preference.Describe(value), 18, align: HorizontalAlignment.Right);
                number.CustomMinimumSize = new Vector2(85, 0);
                line.AddChild(number);
                slider.ValueChanged += v => { Saved(Settings.SetPreference(preference, v)); number.Text = preference.Describe(Settings.GetPreference(preference)); };
            }
            else
            {
                var button = UiStyle.Button(preference.Describe(value));
                button.CustomMinimumSize = new Vector2(325, 42);
                button.SetMeta("focus_key", preference.Id);
                line.AddChild(button);
                void Adjust(int direction)
                {
                    var next = Settings.GetPreference(preference) + direction;
                    if (next > preference.Max) next = preference.Min;
                    if (next < preference.Min) next = preference.Max;
                    Saved(Settings.SetPreference(preference, next));
                    button.Text = preference.Describe(Settings.GetPreference(preference));
                }
                button.Pressed += () => Adjust(1);
                adjusters[preference.Id] = Adjust;
                button.GuiInput += e =>
                {
                    if (e is InputEventKey { Pressed: true, Keycode: Key.Left or Key.Right } key)
                    {
                        Adjust(key.Keycode == Key.Left ? -1 : 1);
                        button.AcceptEvent();
                    }
                };
            }
            var help = UiStyle.Text(preference.Help, 15, UiStyle.SoftInk);
            help.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            row.AddChild(help);
        }
    }
    private static string DescribeSlot(string slot)
    {
        var action = ControlBindings.All.Single(a => slot.StartsWith(a.Id + ".", StringComparison.Ordinal));
        return action.Label + (slot.EndsWith(".pad", StringComparison.Ordinal) ? " (gamepad)" :
            action.Keyboard.Count == 1 ? " (keyboard/mouse)" : $" (keyboard/mouse slot {slot[^1] - '0' + 1})");
    }

    private void CancelCapture()
    {
        captureSlot = null;
        bindingNotice = "";
        if (navigation != null) navigation.Suspended = false;
        if (cancelCapture != null) cancelCapture.Visible = false;
        if (status != null) UpdateStatus();
    }

    private void BeginCapture(string slot)
    {
        bindingNotice = "";
        captureSlot = slot;
        captureRemaining = 10;
        cancelCapture.Visible = true;
        navigation.Suspended = true;
        UpdateStatus();
    }

    private void BuildControls()
    {
        var help = UiStyle.Text("Select a binding, then press its replacement. Conflicts leave the other slot unbound.\nMelee keeps three keyboard/mouse slots; other actions have one. Sticks and menu shortcuts stay fixed.", 15, UiStyle.SoftInk);
        help.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        rows.AddChild(help);
        // Column headings sized like the rows below, so each sits over its column.
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 10);
        rows.AddChild(header);
        var actionHeading = UiStyle.Text("Action", 15, UiStyle.SoftInk);
        actionHeading.CustomMinimumSize = new Vector2(200, 0);
        actionHeading.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        header.AddChild(actionHeading);
        foreach (var (heading, width) in new[] { ("Keyboard / mouse", 420f), ("Gamepad", 230f) })
        {
            var column = UiStyle.Text(heading, 15, UiStyle.SoftInk);
            column.CustomMinimumSize = new Vector2(width, 0);
            column.HorizontalAlignment = HorizontalAlignment.Center;
            header.AddChild(column);
        }
        // An invisible copy of the row's Reset button keeps the last column's width.
        var resetSpacer = UiStyle.Button("Reset");
        resetSpacer.AddThemeFontSizeOverride("font_size", 16);
        resetSpacer.Modulate = Colors.Transparent;
        resetSpacer.FocusMode = Control.FocusModeEnum.None;
        resetSpacer.MouseFilter = Control.MouseFilterEnum.Ignore;
        header.AddChild(resetSpacer);
        foreach (var action in ControlBindings.All)
        {
            var line = new HBoxContainer();
            line.AddThemeConstantOverride("separation", 10);
            rows.AddChild(line);
            var label = UiStyle.Text(action.Label, 17);
            label.CustomMinimumSize = new Vector2(200, 0);
            label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            line.AddChild(label);
            void AddBinding(bool pad, int index = 0)
            {
                var slot = ControlBindings.Slot(action.Id, pad, index);
                var button = UiStyle.Button(InputSetup.Describe(Settings.GetBinding(slot), compact: true));
                button.AddThemeFontSizeOverride("font_size", 16);
                button.SizeFlagsHorizontal = Control.SizeFlags.Fill;
                button.ClipText = true;
                button.CustomMinimumSize = new Vector2(pad ? 230 : (420f - 10 * (action.Keyboard.Count - 1)) / action.Keyboard.Count, 40);
                button.SetMeta("focus_key", slot);
                button.TooltipText = DescribeSlot(slot) + " · " + InputSetup.Describe(Settings.GetBinding(slot)) + " · select to rebind";
                button.Pressed += () => BeginCapture(slot);
                line.AddChild(button);
            }
            for (var i = 0; i < action.Keyboard.Count; i++) AddBinding(false, i);
            if (action.FixedPad == null)
            {
                AddBinding(true);
            }
            else
            {
                var fixedPad = UiStyle.Text(action.FixedPad + " (fixed)", 16, UiStyle.SoftInk);
                fixedPad.CustomMinimumSize = new Vector2(230, 0);
                line.AddChild(fixedPad);
            }
            var reset = UiStyle.Button("Reset");
            reset.AddThemeFontSizeOverride("font_size", 16);
            reset.SetMeta("focus_key", "reset_" + action.Id);
            reset.Pressed += () =>
            {
                var before = ControlBindings.Slots.ToDictionary(s => s.Slot, s => Settings.GetBinding(s.Slot));
                var saved = Settings.ResetBindings(action.Id);
                var conflicts = before.Where(p => p.Value.Kind != BindingKind.None && Settings.GetBinding(p.Key).Kind == BindingKind.None).Select(p => DescribeSlot(p.Key));
                bindingNotice = string.Join(", ", conflicts);
                if (bindingNotice.Length > 0) bindingNotice = "Warning · " + bindingNotice + " left unbound";
                Saved(saved); BuildRows(); navigation.Configure("reset_" + action.Id);
            };
            line.AddChild(reset);
        }
        rows.AddChild(UiStyle.Text("Look: Mouse / right stick (fixed) · Pause: Esc / Start · Menus: arrows, Tab / D-pad, A to confirm, B to back\nFullscreen: F11 / Alt+Enter (fixed)", 15, UiStyle.SoftInk));
    }

}
