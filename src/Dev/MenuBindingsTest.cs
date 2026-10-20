using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Propane.Core;
using Propane.Net;
using Propane.Player;
using Propane.ReleaseSmoke;
using Propane.UI;

namespace Propane.Dev;

/// <summary>Real event capture, persistent map, safe menu exits and gameplay delivery in a network match.</summary>
public partial class MenuBindingsTest : Node
{
    public required Main Main { get; init; }
    private IPlayerSettings Settings => PlayerPreferences.Application;
    private Menus Menus => Main.GetNode<Menus>("Menus");
    private SettingsMenu Editor => GetTree().Root.FindChildren("*", owned: false).OfType<SettingsMenu>().Single(m => m.Visible);
    private CaptureDirector capture = null!;
    private int checks;
    private bool Editing => GetTree().Root.FindChildren("*", owned: false).OfType<SettingsMenu>().Any(m => m.Visible);
    private bool Waiting => Editing && Editor.FindChildren("*", "Button", owned: false).OfType<Button>().Any(b => b.Text == "Cancel binding" && b.Visible);

    public override async void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        capture = new CaptureDirector(); AddChild(capture);
        try
        {
            Main.SavedTuning.FixedSeed = 23;
            await Frames().ConfigureAwait(true);
            if (DevArgs.Get("scenario") == "bindings") await EditBindings().ConfigureAwait(true);
            Check(Settings.GetBinding("jump.key0") == new ControlBinding(BindingKind.Key, (int)Key.J), "persistent physical J");
            Check(Settings.GetBinding("fire.key0") == new ControlBinding(BindingKind.Mouse, (int)MouseButton.Middle), "persistent middle mouse");
            Check(Settings.GetBinding("jump.pad") == new ControlBinding(BindingKind.Button, (int)JoyButton.Y), "persistent gamepad Y");
            Check(InputSetup.Describe(InputSetup.Jump, false) == "J", "startup/live prompt matches binding");
            await MatchInput().ConfigureAwait(true);
            GD.Print($"[bindings menu] PASS {checks} checks ({DevArgs.Get("scenario")})");
            ShutdownGuard.Quit(GetTree());
        }
        catch (Exception error)
        {
            GD.Print($"[bindings menu] FAIL after {checks} checks: {error}");
            ShutdownGuard.Quit(GetTree(), 1);
        }
    }

    private async Task EditBindings()
    {
        await Activate("Settings").ConfigureAwait(true);
        await Activate("category_Controls").ConfigureAwait(true);
        await Frames().ConfigureAwait(true);
        capture.Screenshot("controls_normal");
        var scroll = Editor.FindChildren("*", "ScrollContainer", owned: false).OfType<ScrollContainer>().Single();
        scroll.ScrollVertical = int.MaxValue;
        await Frames().ConfigureAwait(true);
        capture.Screenshot("controls_bottom");
        scroll.ScrollVertical = 0;
        await Frames().ConfigureAwait(true);
        await Activate("jump.key0").ConfigureAwait(true);
        Check(Waiting, "select opens capture");
        capture.Screenshot("controls_waiting");
        // A repeated key and idle axes must not bind. A logical key differs from its physical position.
        Send(new InputEventKey { Keycode = Key.Q, PhysicalKeycode = Key.Q, Pressed = true, Echo = true });
        Send(new InputEventJoypadMotion { Axis = JoyAxis.LeftY, AxisValue = 1 });
        Send(new InputEventJoypadMotion { Axis = JoyAxis.LeftY, AxisValue = 0 });
        Check(Waiting, "repeat and sticks ignored");
        Tap(Key.F);
        await Frames().ConfigureAwait(true);
        Check(!Waiting && Settings.GetBinding("melee.key0").Kind == BindingKind.None, "conflict unbinds victim");
        Check(Labels().Any(t => t.Contains("left unbound", StringComparison.Ordinal)), "conflict warning visible");
        capture.Screenshot("controls_conflict");
        await Activate("reset_melee").ConfigureAwait(true);
        Check(Settings.GetBinding("jump.key0").Kind == BindingKind.None, "per-action reset uses conflict policy");
        await Activate("Reset category").ConfigureAwait(true);
        Check(ControlBindings.Slots.All(s => Settings.GetBinding(s.Slot) == s.Default), "reset all restores defaults");
        foreach (var cancel in new Action[] { () => Tap(Key.Escape), () => Pad(JoyButton.B), () => Pad(JoyButton.Start) })
        {
            await Activate("jump.key0").ConfigureAwait(true);
            cancel(); await Frames().ConfigureAwait(true);
            Check(Editing && !Waiting && Settings.GetBinding("jump.key0") == ControlBindings.Action("jump").Keyboard[0], "fixed cancel leaves menu and binding intact");
        }
        await Activate("jump.key0").ConfigureAwait(true);
        await Click("Cancel binding").ConfigureAwait(true);
        Check(Editing && !Waiting, "mouse cancel works without assigning left click");
        await Activate("jump.key0").ConfigureAwait(true);
        await Frames(610).ConfigureAwait(true);
        Check(!Waiting && Editing && Labels().Any(t => t.Contains("timed out", StringComparison.Ordinal)), "UI timeout leaves binding intact");
        await Activate("jump.key0").ConfigureAwait(true);
        Tap(Key.F1); await Frames().ConfigureAwait(true);
        Check(Waiting && Labels().Any(t => t.Contains("reserved", StringComparison.Ordinal)), "reserved key explains rejection");
        Send(new InputEventKey { PhysicalKeycode = Key.J, Keycode = Key.Z, Pressed = true });
        Send(new InputEventKey { PhysicalKeycode = Key.J, Keycode = Key.Z, Pressed = false });
        await Frames().ConfigureAwait(true);
        Check(Settings.GetBinding("jump.key0") == new ControlBinding(BindingKind.Key, (int)Key.J), "capture saves physical rather than logical key");
        await Activate("fire.key0").ConfigureAwait(true);
        Mouse(MouseButton.Middle); await Frames().ConfigureAwait(true);
        Check(!Waiting && Settings.GetBinding("fire.key0").Code == (int)MouseButton.Middle, "real mouse binding captured");
        // Exercise both thumb buttons and a trigger, including a trigger/button conflict, then restore melee/aim.
        await Activate("melee.key0").ConfigureAwait(true);
        Mouse(MouseButton.Xbutton1); await Frames().ConfigureAwait(true);
        Check(Settings.GetBinding("melee.key1").Kind == BindingKind.None, "thumb 1 capture conflicts within an action");
        await Activate("melee.key0").ConfigureAwait(true);
        Mouse(MouseButton.Xbutton2); await Frames().ConfigureAwait(true);
        Check(Settings.GetBinding("melee.key2").Kind == BindingKind.None, "thumb 2 capture supported");
        await Activate("reset_melee").ConfigureAwait(true);
        await Activate("aim.pad").ConfigureAwait(true);
        Axis(JoyAxis.TriggerRight, 1); Axis(JoyAxis.TriggerRight, 0); await Frames().ConfigureAwait(true);
        Check(Settings.GetBinding("aim.pad").Kind == BindingKind.Trigger && Settings.GetBinding("fire.pad").Kind == BindingKind.None, "trigger captured with conflict");
        await Activate("reset_aim").ConfigureAwait(true);
        await Activate("reset_fire").ConfigureAwait(true);
        // Fire reset restores both devices, so put the intended mouse fixture back through the editor.
        await Activate("fire.key0").ConfigureAwait(true);
        Mouse(MouseButton.Middle); await Frames().ConfigureAwait(true);
        await Activate("jump.pad").ConfigureAwait(true);
        // Capture must preempt MenuNavigation: Y is raw device input, never a direct action press.
        Pad(JoyButton.Y); await Frames().ConfigureAwait(true);
        Check(!Waiting && Settings.GetBinding("jump.pad").Code == (int)JoyButton.Y, "real gamepad button captured");
        Pad(JoyButton.B); await Frames().ConfigureAwait(true);
        Check(!Editing && Menus.Current == Menus.Screen.Main, "B leaves settings after rebinding");
    }

    private async Task MatchInput()
    {
        await Activate("Multiplayer").ConfigureAwait(true);
        await Activate("Host on this computer").ConfigureAwait(true);
        await Wait(() => Menus.Current == Menus.Screen.Browser, "host browser").ConfigureAwait(true);
        await Activate("Create lobby").ConfigureAwait(true);
        await Wait(() => Menus.Current == Menus.Screen.Lobby, "lobby").ConfigureAwait(true);
        await Activate("Start match").ConfigureAwait(true);
        await Wait(() => Main.HasNode("Match") && Main.GetNode<Match>("Match").IsPlaying, "match playing").ConfigureAwait(true);
        var match = Main.GetNode<Match>("Match");
        var player = match.Player;
        var pause = match.GetNode<MatchMenu>("Menu");
        if (DisplayServer.GetName() == "headless")
            LocalView.Find(player)!.AssignDevice(new DeviceInput(player.World.Tuning, () => !pause.Visible, preferences: Settings));
        await Wait(() => player.IsOnFloor(), "grounded").ConfigureAwait(true);
        Check(player.Runtime.IntentSource is DeviceInput, "real device endpoint under application viewport");
        Tap(Key.Space); await Frames(8).ConfigureAwait(true);
        Check(player.IsOnFloor(), "old keyboard input does not jump in match");
        Tap(Key.J); await Frames(8).ConfigureAwait(true);
        Check(!player.IsOnFloor() && player.Velocity.Y > 0, "new keyboard input jumps in match");
        await Wait(() => player.IsOnFloor(), "keyboard jump landed").ConfigureAwait(true);
        Pad(JoyButton.A); await Frames(8).ConfigureAwait(true);
        Check(player.IsOnFloor(), "old gamepad input does not jump in match");
        Pad(JoyButton.Y); await Frames(8).ConfigureAwait(true);
        Check(!player.IsOnFloor() && player.Velocity.Y > 0, "new gamepad input jumps in match");
        await Wait(() => player.IsOnFloor(), "pad jump landed").ConfigureAwait(true);
        var shots = 0;
        void Shot(ShotReport report) => shots++;
        player.Fired += Shot;
        Mouse(MouseButton.Left); await Frames(8).ConfigureAwait(true);
        Check(shots == 0, "old mouse input does not fire in match");
        Mouse(MouseButton.Middle); await Frames(8).ConfigureAwait(true);
        Check(shots == 1, "new mouse input fires once in match");
        player.Fired -= Shot;
        Tap(Key.Escape); await Frames().ConfigureAwait(true);
        Check(pause.Visible, "fixed Escape opens match menu");
        await Activate("Settings").ConfigureAwait(true);
        await Activate("category_Controls").ConfigureAwait(true);
        await Activate("jump.pad").ConfigureAwait(true);
        Pad(JoyButton.A); await Frames().ConfigureAwait(true);
        Check(!Waiting && Settings.GetBinding("jump.pad").Code == (int)JoyButton.A, "A capture preempts menu confirmation");
        await Activate("jump.pad").ConfigureAwait(true);
        Pad(JoyButton.Y); await Frames().ConfigureAwait(true);
        // A live edit in the match must affect gameplay, in addition to the startup map above.
        await Activate("jump.key0").ConfigureAwait(true);
        Tap(Key.K); await Frames().ConfigureAwait(true);
        Pad(JoyButton.Start); await Frames().ConfigureAwait(true);
        Check(!Editing && pause.Visible && !match.World.Paused, "Start leaves settings without leaving running match");
        Tap(Key.Tab); await Frames().ConfigureAwait(true);
        Check(Labels().Contains("K") && !Labels().Contains("J"), "pause controls reference follows live edit");
        capture.Screenshot("match_controls_reference");
        Pad(JoyButton.Start); await Frames().ConfigureAwait(true);
        Check(!pause.Visible, "Start resumes after rebind");
        Tap(Key.J); await Frames(8).ConfigureAwait(true);
        Check(player.IsOnFloor(), "former binding stops after live edit");
        Tap(Key.K); await Frames(8).ConfigureAwait(true);
        Check(!player.IsOnFloor() && player.Velocity.Y > 0, "live binding fires in current match");
        await Wait(() => player.IsOnFloor(), "live jump landed").ConfigureAwait(true);
        Tap(Key.Escape); await Frames().ConfigureAwait(true);
        await Activate("Settings").ConfigureAwait(true);
        await Activate("jump.key0").ConfigureAwait(true);
        Tap(Key.J); await Frames().ConfigureAwait(true);
        Tap(Key.Escape); await Frames().ConfigureAwait(true);
        Check(!Editing && pause.Visible, "Escape always leaves settings");
        Tap(Key.Escape); await Frames().ConfigureAwait(true);
        Check(!pause.Visible, "Escape always resumes");
    }

    private string[] Labels() => GetTree().Root.FindChildren("*", "Label", owned: false).OfType<Label>().Where(l => l.IsVisibleInTree()).Select(l => l.Text).ToArray();
    private async Task Activate(string key)
    {
        await Frames().ConfigureAwait(true);
        var button = GetTree().Root.FindChildren("*", "Button", owned: false).OfType<Button>()
            .FirstOrDefault(b => b.IsVisibleInTree() && !b.Disabled && MenuNavigation.Key(b) == key) ?? throw new InvalidOperationException("missing " + key);
        for (var i = 0; GetViewport().GuiGetFocusOwner() != button; i++)
        {
            if (i > 100) throw new InvalidOperationException("cannot focus " + key);
            Tap(Key.Tab); await Frames().ConfigureAwait(true);
        }
        Send(new InputEventKey { PhysicalKeycode = Key.Enter, Keycode = Key.Enter, Pressed = true });
        await Frames(1).ConfigureAwait(true);
        Send(new InputEventKey { PhysicalKeycode = Key.Enter, Keycode = Key.Enter, Pressed = false });
        await Frames().ConfigureAwait(true);
    }
    private async Task Click(string text)
    {
        var button = Editor.FindChildren("*", "Button", owned: false).OfType<Button>().Single(b => b.Text == text && b.IsVisibleInTree());
        var point = GetTree().Root.GetFinalTransform() * button.GetGlobalRect().GetCenter();
        Send(new InputEventMouseMotion { Position = point, GlobalPosition = point }); await Frames(1).ConfigureAwait(true);
        Send(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Position = point, GlobalPosition = point, Pressed = true });
        await Frames(1).ConfigureAwait(true);
        Send(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Position = point, GlobalPosition = point, Pressed = false });
        await Frames().ConfigureAwait(true);
    }
    private static void Tap(Key key) { Send(new InputEventKey { PhysicalKeycode = key, Keycode = key, Pressed = true }); Send(new InputEventKey { PhysicalKeycode = key, Keycode = key, Pressed = false }); }
    private static void Pad(JoyButton button) { Send(new InputEventJoypadButton { ButtonIndex = button, Pressed = true }); Send(new InputEventJoypadButton { ButtonIndex = button, Pressed = false }); }
    private static void Mouse(MouseButton button) { Send(new InputEventMouseButton { ButtonIndex = button, Pressed = true }); Send(new InputEventMouseButton { ButtonIndex = button, Pressed = false }); }
    private static void Axis(JoyAxis axis, float value) => Send(new InputEventJoypadMotion { Axis = axis, AxisValue = value });
    private static void Send(InputEvent e) { Input.ParseInputEvent(e); Input.FlushBufferedEvents(); e.Dispose(); }
    private void Check(bool passed, string message) { checks++; if (!passed) throw new InvalidOperationException(message); GD.Print("[bindings menu] PASS " + message); }
    private async Task Frames(int count = 3) { for (var i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
    private async Task Wait(Func<bool> condition, string message)
    {
        // Test readiness uses monotonic wall time; timeout is not a simulation rule.
        var deadline = Time.GetTicksMsec() + 20000;
        while (!condition()) { if (Time.GetTicksMsec() > deadline) throw new TimeoutException(message); await Frames(1).ConfigureAwait(true); }
    }
}
