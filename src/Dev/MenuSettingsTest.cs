using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Propane.Core;
using Propane.Lab;
using Propane.Net;
using Propane.Player;
using Propane.ReleaseSmoke;
using Propane.UI;

namespace Propane.Dev;

/// <summary>Real device delivery through the whole settings menu, physical facades and application store.</summary>
public partial class MenuSettingsTest : Node
{
    public required Main Main { get; init; }
    private IPlayerSettings Settings => PlayerPreferences.Application;
    private CaptureDirector capture = null!;
    private Menus Menus => Main.GetNode<Menus>("Menus");
    private SettingsMenu Editor => GetTree().Root.FindChildren("*", owned: false).OfType<SettingsMenu>().Single(m => m.Visible);
    private int checks;

    public override async void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        capture = new CaptureDirector();
        AddChild(capture);
        try
        {
            Main.SavedTuning.FixedSeed = 23;
            await Frames().ConfigureAwait(true);
            await Click("Settings").ConfigureAwait(true);
            await Categories("main", exercise: true).ConfigureAwait(true);
            await PadActivate("category_Camera & controls").ConfigureAwait(true);
            var mouseSlider = Slider(PreferenceCatalog.MouseSensitivity);
            await ClickAt(mouseSlider.GetGlobalRect().Position + mouseSlider.Size * new Vector2(0.75f, 0.5f)).ConfigureAwait(true);
            Check(Settings.GetPreference(PreferenceCatalog.MouseSensitivity) > 2, "real mouse click edits a slider");
            await PadActivate("Reset category").ConfigureAwait(true);
            Check(PreferenceCatalog.All.Where(p => p.Category == "Camera & controls").All(p => Settings.GetPreference(p) == p.Default), "camera reset restores every declaration");
            await Focus("category_Camera & controls", pad: true).ConfigureAwait(true);
            Send(new InputEventJoypadMotion { Axis = JoyAxis.LeftY, AxisValue = 1 });
            Send(new InputEventJoypadMotion { Axis = JoyAxis.LeftY, AxisValue = 0 });
            await Frames().ConfigureAwait(true);
            Check(MenuNavigation.Key(GetViewport().GuiGetFocusOwner()) == PreferenceCatalog.MouseSensitivity.Id, "left stick navigates to a preference");
            Send(new InputEventJoypadMotion { Axis = JoyAxis.LeftX, AxisValue = 1 });
            Send(new InputEventJoypadMotion { Axis = JoyAxis.LeftX, AxisValue = 0 });
            await Frames().ConfigureAwait(true);
            Check(Math.Abs(Settings.GetPreference(PreferenceCatalog.MouseSensitivity) - 1.05) < 0.001, "left stick adjusts a slider");
            await Edit(PreferenceCatalog.MouseSensitivity, 1).ConfigureAwait(true);
            await PadActivate("category_Display").ConfigureAwait(true);
            foreach (var preference in new[] { PreferenceCatalog.WindowMode, PreferenceCatalog.FrameCap })
            {
                for (var option = 0; option <= preference.Max; option++)
                {
                    await Edit(preference, option).ConfigureAwait(true);
                    CheckApplied(preference);
                }
                await Edit(preference, preference.Default).ConfigureAwait(true);
            }
            KeyTap(Key.F11);
            await Frames().ConfigureAwait(true);
            Check(Settings.GetPreference(PreferenceCatalog.WindowMode) == 1, "F11 in settings persists fullscreen");
            KeyTap(Key.F11);
            await Frames().ConfigureAwait(true);
            // Headless has no native window mode, so restore through the menu rather than the display query.
            await Edit(PreferenceCatalog.WindowMode, 0).ConfigureAwait(true);
            await PadActivate("Reset category").ConfigureAwait(true);
            Check(PreferenceCatalog.All.Where(p => p.Category == "Display").All(p => Settings.GetPreference(p) == p.Default), "controller resets category from its declarations");
            await PadActivate("category_Camera & controls").ConfigureAwait(true);
            await Edit(PreferenceCatalog.MouseSensitivity, 1.5).ConfigureAwait(true);
            await Edit(PreferenceCatalog.AimSensitivity, 0.5).ConfigureAwait(true);
            await Edit(PreferenceCatalog.InvertMouseY, 1).ConfigureAwait(true);
            await Edit(PreferenceCatalog.GamepadSensitivity, 1.5).ConfigureAwait(true);
            await Edit(PreferenceCatalog.InvertGamepadY, 1).ConfigureAwait(true);
            await Edit(PreferenceCatalog.FieldOfView, 90).ConfigureAwait(true);
            await Focus(PreferenceCatalog.CameraShake.Id, pad: true).ConfigureAwait(true);
            capture.Screenshot("main_camera_changed");
            Pad(JoyButton.B);
            await Frames().ConfigureAwait(true);
            Check(Menus.Current == Menus.Screen.Main && !VisibleEditors(), "B returns to main menu");
            await Click("Settings").ConfigureAwait(true);
            Check(Math.Abs(Slider(PreferenceCatalog.MouseSensitivity).Value - 1.5) < 0.001, "reopen preserves edited controls");
            using (var reread = new PlayerSettings(MenuTest.SettingsPath)) CheckSaved(reread);
            KeyTap(Key.Escape);
            await Frames().ConfigureAwait(true);
            Check(!VisibleEditors(), "Escape returns to main menu");

            await Click("Single player").ConfigureAwait(true);
            var game = Main.GetNode<Game>("Game");
            game.PauseOnFocusLoss = false;
            EnableHeadless(game.Player, game.GetNode<PauseMenu>("Pause"));
            await Wait(() => !game.InTransition && game.Player.IsOnFloor(), "single ready").ConfigureAwait(true);
            KeyTap(Key.Escape);
            await Frames().ConfigureAwait(true);
            await Click("Settings").ConfigureAwait(true);
            await Categories("single").ConfigureAwait(true);
            Check(game.World.Paused, "single-player settings preserve pause");
            await EditInPlay(game.Player, game.GetNode<PauseMenu>("Pause"), "single").ConfigureAwait(true);
            KeyTap(Key.Escape);
            await Frames().ConfigureAwait(true);
            await Click("Main menu").ConfigureAwait(true);
            await Wait(() => Menus.Current == Menus.Screen.Main, "main after single").ConfigureAwait(true);

            await Click("Lab").ConfigureAwait(true);
            await Frames().ConfigureAwait(true);
            // The range has a small bounded fixture and uses the same pause/editor path as every Lab mode.
            await Click("Range").ConfigureAwait(true);
            await Frames().ConfigureAwait(true);
            await Click("Enter Lab").ConfigureAwait(true);
            await Wait(() => Main.HasNode("Lab") && Main.GetNode<LabSession>("Lab").Initialized, "Lab ready").ConfigureAwait(true);
            var lab = Main.GetNode<LabSession>("Lab");
            lab.PauseOnFocusLoss = false;
            EnableHeadless(lab.Player, lab.Overlay.MenuLayer);
            await Wait(() => lab.Player.IsOnFloor(), "Lab grounded").ConfigureAwait(true);
            KeyTap(Key.Escape);
            await Frames().ConfigureAwait(true);
            await Click("Settings").ConfigureAwait(true);
            await Categories("lab").ConfigureAwait(true);
            Check(lab.World.Paused, "Lab settings preserve pause");
            await Blocked(lab.Player, "lab").ConfigureAwait(true);
            Pad(JoyButton.Start);
            await Frames().ConfigureAwait(true);
            Check(lab.Overlay.MenuVisible && !VisibleEditors() && lab.World.Paused, "Start returns to Lab pause without resetting or resuming");
            await Click("Lab menu").ConfigureAwait(true);
            await Wait(() => !Main.HasNode("Lab"), "leave Lab").ConfigureAwait(true);
            await Click("Main menu").ConfigureAwait(true);

            await Click("Multiplayer").ConfigureAwait(true);
            await Click("Host on this computer").ConfigureAwait(true);
            await Wait(() => Menus.Current == Menus.Screen.Browser, "host browser").ConfigureAwait(true);
            await Click("Create lobby").ConfigureAwait(true);
            await Wait(() => Menus.Current == Menus.Screen.Lobby, "lobby").ConfigureAwait(true);
            await Click("Start match").ConfigureAwait(true);
            await Wait(() => Main.HasNode("Match") && Main.GetNode<Match>("Match").IsPlaying, "match playing").ConfigureAwait(true);
            var match = Main.GetNode<Match>("Match");
            EnableHeadless(match.Player, match.GetNode<MatchMenu>("Menu"));
            await Wait(() => match.Player.IsOnFloor(), "match grounded").ConfigureAwait(true);
            KeyTap(Key.Escape);
            await Frames().ConfigureAwait(true);
            await Click("Settings").ConfigureAwait(true);
            await Categories("match").ConfigureAwait(true);
            await Edit(PreferenceCatalog.ShowLeaderboard, 0).ConfigureAwait(true);
            KeyTap(Key.Escape);
            await Frames().ConfigureAwait(true);
            Check(GetTree().Root.FindChildren("*", "Button", owned: false).OfType<Button>().Any(b => b.IsVisibleInTree() && b.Text == "Leaderboard: hidden"), "match shortcut reflects a settings edit");
            await Click("Settings").ConfigureAwait(true);
            await Edit(PreferenceCatalog.ShowLeaderboard, 1).ConfigureAwait(true);
            Check(!match.World.Paused && match.IsPlaying, "multiplayer keeps running in settings");
            await EditInPlay(match.Player, match.GetNode<MatchMenu>("Menu"), "match").ConfigureAwait(true);
            CheckSaved(Settings);
            GD.Print($"[settings menu] PASS {checks} checks");
            ShutdownGuard.Quit(GetTree());
        }
        catch (Exception error)
        {
            GD.Print($"[settings menu] FAIL after {checks} checks: {error}");
            ShutdownGuard.Quit(GetTree(), 1);
        }
    }

    public static void CheckSaved(IPlayerSettings settings)
    {
        foreach (var (preference, expected) in new[] {
            (PreferenceCatalog.MouseSensitivity, 1.5), (PreferenceCatalog.AimSensitivity, 0.5), (PreferenceCatalog.InvertMouseY, 1.0),
            (PreferenceCatalog.GamepadSensitivity, 1.5), (PreferenceCatalog.InvertGamepadY, 1.0), (PreferenceCatalog.FieldOfView, 90.0) })
        {
            if (Math.Abs(settings.GetPreference(preference) - expected) > 0.001) throw new InvalidOperationException($"saved {preference.Id} differs");
        }

        GD.Print("[settings menu] PASS saved mouse=1.5 ADS=0.5 invertMouse=true gamepad=1.5 invertGamepad=true FOV=90");
    }

    private async Task Categories(string prefix, bool exercise = false)
    {
        foreach (var category in PreferenceCatalog.All.Select(p => p.Category).Distinct())
        {
            await PadActivate("category_" + category).ConfigureAwait(true);
            foreach (var preference in PreferenceCatalog.All.Where(p => p.Category == category))
            {
                await Focus(preference.Id, pad: true).ConfigureAwait(true);
                if (!exercise) continue;
                var before = Settings.GetPreference(preference);
                var increase = before < preference.Max;
                Pad(increase ? JoyButton.DpadRight : JoyButton.DpadLeft);
                await Frames().ConfigureAwait(true);
                Check(Settings.GetPreference(preference) != before, $"controller adjusts {preference.Id}");
                CheckApplied(preference);
                Pad(increase ? JoyButton.DpadLeft : JoyButton.DpadRight);
                await Frames().ConfigureAwait(true);
                Check(Math.Abs(Settings.GetPreference(preference) - before) < 0.001, $"controller restores {preference.Id}");
                CheckApplied(preference);
            }
            // Return scroll to the beginning so every category's first rows appear in review shots.
            await Focus("category_" + category, pad: true).ConfigureAwait(true);
            Editor.FindChildren("*", "ScrollContainer", owned: false).OfType<ScrollContainer>().Single().ScrollVertical = 0;
            await Frames().ConfigureAwait(true);
            capture.Screenshot(prefix + "_" + category.Split(' ')[0].ToLowerInvariant());
        }
    }

    private void CheckApplied(PreferenceDefinition preference)
    {
        var value = Settings.GetPreference(preference);
        if (preference == PreferenceCatalog.FrameCap) Check(Engine.MaxFps == preference.OptionValues![(int)value], "frame cap applies to the engine");
        if (preference == PreferenceCatalog.RenderScale) Check(Math.Abs(GetViewport().Scaling3DScale - value) < 0.001, "3D scale applies to the presentation viewport");
        if (DisplayServer.GetName() == "headless") return;
        if (preference == PreferenceCatalog.Vsync)
        {
            var actual = DisplayServer.WindowGetVsyncMode();
            if (value == 0 && actual == DisplayServer.VSyncMode.Enabled)
            {
                Check(Editor.FindChildren("*", "Label", owned: false).OfType<Label>().Any(l => l.Text.Contains("VSync is forced on", StringComparison.Ordinal)), "native VSync fallback is explained in the menu");
                capture.Screenshot("main_display_vsync_fallback");
            }
            else
            {
                Check(actual == (value == 0 ? DisplayServer.VSyncMode.Disabled : DisplayServer.VSyncMode.Enabled), "VSync applies to the native display");
            }
        }
        if (preference == PreferenceCatalog.WindowMode)
        {
            Check(value == 0 ? DisplayServer.WindowGetMode() is DisplayServer.WindowMode.Windowed or DisplayServer.WindowMode.Maximized
                : DisplayServer.WindowGetMode() is DisplayServer.WindowMode.Fullscreen or DisplayServer.WindowMode.ExclusiveFullscreen, "window mode applies to the native display");
        }
    }

    private async Task EditInPlay(PlayerCharacter player, CanvasLayer pause, string name)
    {
        await PadActivate("category_Camera & controls").ConfigureAwait(true);
        // Change the same values live in each session, ending on the cross-process fixture's values.
        await Edit(PreferenceCatalog.FieldOfView, 80).ConfigureAwait(true);
        var view = LocalView.Find(player)!;
        Check(Math.Abs(view.CameraRig.Camera.Fov - player.World.Tuning.FieldOfView * 80 / 72) < 0.01, name + " FOV changes live while menu is open");
        await Edit(PreferenceCatalog.FieldOfView, 90).ConfigureAwait(true);
        await Edit(PreferenceCatalog.MouseSensitivity, 1).ConfigureAwait(true);
        await Edit(PreferenceCatalog.MouseSensitivity, 1.5).ConfigureAwait(true);
        await Edit(PreferenceCatalog.InvertMouseY, 0).ConfigureAwait(true);
        await Edit(PreferenceCatalog.InvertMouseY, 1).ConfigureAwait(true);
        await Edit(PreferenceCatalog.CameraShake, 0).ConfigureAwait(true);
        player.Aim.AddTrauma(1);
        player.Aim.Advance(1.0 / 60);
        var logicalEye = player.Aim.EyeOffset;
        Check(logicalEye != Transform3D.Identity, name + " shake fixture is nonzero");
        view.CameraRig._Process(0);
        Check(view.CameraRig.Camera.Position.IsZeroApprox() && view.CameraRig.Camera.Rotation.IsZeroApprox(), name + " zero shake affects only rendered eye");
        await Edit(PreferenceCatalog.CameraShake, 1).ConfigureAwait(true);
        Check(view.CameraRig.Camera.Transform != Transform3D.Identity, name + " restoring shake restores rendered eye");
        await Blocked(player, name).ConfigureAwait(true);
        KeyTap(Key.Escape);
        await Frames().ConfigureAwait(true);
        Check(pause.Visible && !VisibleEditors() && Input.MouseMode == Input.MouseModeEnum.Visible, name + " Escape returns to caller without resuming");
        KeyTap(Key.Escape);
        await Frames().ConfigureAwait(true);
        Check(!pause.Visible && !player.World.Paused, name + " second Escape resumes");
        var yaw = player.Aim.Yaw;
        var pitch = player.Aim.Pitch;
        Motion(new Vector2(10, 5));
        await Frames().ConfigureAwait(true);
        var speed = Mathf.RadToDeg(player.World.Tuning.MouseSensitivity);
        Check(Math.Abs(player.Aim.Yaw - (yaw - 15 * speed)) < 0.001 && Math.Abs(player.Aim.Pitch - (pitch + 7.5f * speed)) < 0.001,
            $"{name} real mouse applies sensitivity/inversion once: yaw={player.Aim.Yaw - yaw:F4} pitch={player.Aim.Pitch - pitch:F4}");
        yaw = player.Aim.Yaw; pitch = player.Aim.Pitch;
        Send(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = true });
        Motion(new Vector2(10, 5));
        await Frames().ConfigureAwait(true);
        Send(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = false });
        Check(Math.Abs(player.Aim.Yaw - (yaw - 7.5f * speed)) < 0.001 && Math.Abs(player.Aim.Pitch - (pitch + 3.75f * speed)) < 0.001,
            name + " real Aim applies relative speed live");
        pitch = player.Aim.Pitch;
        var clock = player.World.Clock.Now;
        Send(new InputEventJoypadMotion { Axis = JoyAxis.RightY, AxisValue = 1 });
        await Frames(6).ConfigureAwait(true);
        Send(new InputEventJoypadMotion { Axis = JoyAxis.RightY, AxisValue = 0 });
        var elapsed = player.World.Clock.Now - clock;
        var expected = player.World.Tuning.StickLookDegreesPerSecond * 1.5 * elapsed;
        Check(elapsed > 0 && Math.Abs(player.Aim.Pitch - pitch - expected) < 0.01, $"{name} real gamepad gain/inversion: {player.Aim.Pitch - pitch:F3}° over {elapsed:F3}s");
        await Frames().ConfigureAwait(true);
        player.Aim.SetOrientation(player.Aim.Yaw, 45);
        var mouseUp = player.World.Tuning.StickLookDegreesPerSecond * 1.5f / Engine.PhysicsTicksPerSecond * 0.5f;
        clock = player.World.Clock.Now;
        Send(new InputEventJoypadMotion { Axis = JoyAxis.RightY, AxisValue = -1 });
        Motion(new Vector2(0, mouseUp / (speed * 1.5f)));
        await Frames().ConfigureAwait(true);
        Send(new InputEventJoypadMotion { Axis = JoyAxis.RightY, AxisValue = 0 });
        expected = 45 + mouseUp - player.World.Tuning.StickLookDegreesPerSecond * 1.5 * (player.World.Clock.Now - clock);
        Check(Math.Abs(player.Aim.Pitch - expected) < 0.01, name + " real simultaneous mouse/stick input clamps pitch once");
        capture.Screenshot(name + "_live_camera");
    }

    private async Task Blocked(PlayerCharacter player, string name)
    {
        var yaw = player.Aim.Yaw; var pitch = player.Aim.Pitch;
        var position = player.GlobalPosition;
        var shots = 0; var swings = 0;
        void Shot(ShotReport report) => shots++;
        void Swing(uint id, Vector3 direction) => swings++;
        player.Fired += Shot; player.MeleeStarted += Swing;
        Motion(new Vector2(100, 50));
        await ClickAt(new Vector2(8, 8)).ConfigureAwait(true);
        Send(new InputEventKey { Keycode = Key.W, PhysicalKeycode = Key.W, Pressed = true });
        KeyTap(Key.Space); KeyTap(Key.R); KeyTap(Key.F); KeyTap(Key.Backspace); KeyTap(Key.F1);
        Send(new InputEventJoypadMotion { Axis = JoyAxis.RightX, AxisValue = 1 });
        Send(new InputEventJoypadMotion { Axis = JoyAxis.TriggerRight, AxisValue = 1 });
        await Frames(8).ConfigureAwait(true);
        Send(new InputEventKey { Keycode = Key.W, PhysicalKeycode = Key.W, Pressed = false });
        Send(new InputEventJoypadMotion { Axis = JoyAxis.RightX, AxisValue = 0 });
        Send(new InputEventJoypadMotion { Axis = JoyAxis.TriggerRight, AxisValue = 0 });
        await Frames().ConfigureAwait(true);
        Check(VisibleEditors() && player.Aim.Yaw == yaw && player.Aim.Pitch == pitch && shots == 0 && swings == 0 && player.Ammo?.Reloading != true && player.IsOnFloor() && player.GlobalPosition.DistanceTo(position) < 0.05f,
            name + " settings block real look, click, trigger, jump, reload, melee, reset and tuning");
        player.Fired -= Shot; player.MeleeStarted -= Swing;
    }

    private void EnableHeadless(PlayerCharacter player, CanvasLayer pause)
    {
        if (DisplayServer.GetName() == "headless")
            LocalView.Find(player)!.AssignDevice(new DeviceInput(player.World.Tuning, () => !pause.Visible, preferences: Settings));
    }

    private bool VisibleEditors() => GetTree().Root.FindChildren("*", owned: false).OfType<SettingsMenu>().Any(m => m.Visible);
    private HSlider Slider(PreferenceDefinition preference) => Editor.FindChildren("*", "HSlider", owned: false).OfType<HSlider>().Single(s => MenuNavigation.Key(s) == preference.Id);

    private async Task Edit(PreferenceDefinition preference, double value)
    {
        await Focus(preference.Id).ConfigureAwait(true);
        for (var i = 0; Math.Abs(Settings.GetPreference(preference) - value) > 0.001; i++)
        {
            if (i > 150) throw new InvalidOperationException("edit " + preference.Id);
            KeyTap(Settings.GetPreference(preference) < value ? Key.Right : Key.Left);
            await Frames(1).ConfigureAwait(true);
        }
        Check(Math.Abs(Settings.GetPreference(preference) - value) < 0.001, "keyboard edits " + preference.Id);
    }

    private async Task Focus(string key, bool pad = false)
    {
        for (var i = 0; GetViewport().GuiGetFocusOwner() is not { } owner || MenuNavigation.Key(owner) != key; i++)
        {
            if (pad && key.StartsWith("category_", StringComparison.Ordinal) && GetViewport().GuiGetFocusOwner() is { } focused &&
                MenuNavigation.Key(focused).StartsWith("category_", StringComparison.Ordinal))
            {
                Pad(JoyButton.DpadRight);
                await Frames().ConfigureAwait(true);
                continue;
            }
            if (i > 90) throw new InvalidOperationException("focus " + key);
            if (pad) Pad(JoyButton.DpadDown); else KeyTap(Key.Tab);
            await Frames().ConfigureAwait(true);
        }
    }

    private async Task PadActivate(string key)
    {
        await Focus(key, pad: true).ConfigureAwait(true);
        Pad(JoyButton.A);
        await Frames().ConfigureAwait(true);
    }

    private async Task Click(string text)
    {
        await Frames().ConfigureAwait(true);
        var button = GetTree().Root.FindChildren("*", "Button", owned: false).OfType<Button>()
            .FirstOrDefault(b => b.Text == text && b.IsVisibleInTree() && !b.Disabled) ?? throw new InvalidOperationException("missing " + text);
        await ClickAt(button.GetGlobalRect().GetCenter()).ConfigureAwait(true);
    }

    private async Task ClickAt(Vector2 point)
    {
        var at = GetTree().Root.GetFinalTransform() * point;
        Send(new InputEventMouseMotion { Position = at, GlobalPosition = at });
        await Frames(1).ConfigureAwait(true);
        Send(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = at, GlobalPosition = at, ButtonMask = MouseButtonMask.Left });
        await Frames(1).ConfigureAwait(true);
        Send(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = at, GlobalPosition = at });
        await Frames().ConfigureAwait(true);
    }

    private static void Motion(Vector2 delta) => Send(new InputEventMouseMotion { Relative = delta, ScreenRelative = delta });
    private static void KeyTap(Key key) { Send(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = true }); Send(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = false }); }
    private static void Pad(JoyButton button) { Send(new InputEventJoypadButton { ButtonIndex = button, Pressed = true }); Send(new InputEventJoypadButton { ButtonIndex = button, Pressed = false }); }
    private static void Send(InputEvent e) { Input.ParseInputEvent(e); Input.FlushBufferedEvents(); e.Dispose(); }
    private void Check(bool passed, string message) { checks++; if (!passed) throw new InvalidOperationException(message); GD.Print("[settings menu] PASS " + message); }
    private async Task Frames(int count = 3) { for (var i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
    private async Task Wait(Func<bool> condition, string message)
    {
        var deadline = Time.GetTicksMsec() + 20000;
        while (!condition()) { if (Time.GetTicksMsec() > deadline) throw new TimeoutException(message); await Frames(1).ConfigureAwait(true); }
    }
}
