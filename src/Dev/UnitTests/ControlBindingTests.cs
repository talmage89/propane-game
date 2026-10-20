using System;
using System.Linq;
using Godot;
using Propane.Core;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev.UnitTests;

[UnitSuite]
public static class ControlBindingTests
{
    private const string Path = "res://build/control-binding-unit.cfg";
    public static void Register(TestRunner runner)
    {
        runner.Run("bindings/round trip every supported kind and existing preferences", () => Fresh(() =>
        {
            using var settings = new PlayerSettings(Path);
            settings.Name = "Binding test";
            settings.SetPreference(PreferenceCatalog.FieldOfView, 85);
            var assignments = new[] {
                ("jump.key0", new ControlBinding(BindingKind.Key, (int)Key.J)),
                ("fire.key0", new ControlBinding(BindingKind.Mouse, (int)MouseButton.Middle)),
                ("aim.pad", new ControlBinding(BindingKind.Button, (int)JoyButton.RightShoulder)),
                ("reload.pad", new ControlBinding(BindingKind.Trigger, (int)JoyAxis.TriggerRight)),
                ("melee.key1", new ControlBinding()),
            };
            foreach (var (slot, binding) in assignments) Assert(settings.SetBinding(slot, binding), "save failed");
            using var reread = new PlayerSettings(Path);
            foreach (var (slot, binding) in assignments) Assert(reread.GetBinding(slot) == binding, "round trip " + slot);
            Assert(reread.Name == "Binding test" && reread.GetPreference(PreferenceCatalog.FieldOfView) == 85, "existing settings lost");
            Assert(reread.GetBinding("fire.pad").Kind == BindingKind.None, "trigger conflict did not persist unbound victim");
        }));
        runner.Run("bindings/differences only and reset all", () => Fresh(() =>
        {
            using var settings = new PlayerSettings(Path);
            settings.SetBinding("jump.key0", new(BindingKind.Key, (int)Key.J));
            using var file = new ConfigFile();
            Assert(file.Load(Path) == Error.Ok && file.GetSectionKeys("bindings").SequenceEqual(new[] { "jump.key0" }), "saved defaults");
            settings.ResetBindings();
            file.Clear(); file.Load(Path);
            Assert(!file.HasSection("bindings"), "reset left overrides");
            foreach (var (slot, fallback) in ControlBindings.Slots) Assert(settings.GetBinding(slot) == fallback, "reset all " + slot);
        }));
        runner.Run("bindings/missing legacy invalid and unknown entries default", () => Fresh(() =>
        {
            using (var missing = new PlayerSettings(Path))
                foreach (var (slot, fallback) in ControlBindings.Slots) Assert(missing.GetBinding(slot) == fallback, "missing file");
            using var file = new ConfigFile();
            file.SetValue("player", "name", "Old file"); file.SetValue("hud", "leaderboard", false); file.Save(Path);
            using (var old = new PlayerSettings(Path))
            {
                Assert(old.Name == "Old file" && !old.ShowLeaderboard, "old file incompatible");
                foreach (var (slot, fallback) in ControlBindings.Slots) Assert(old.GetBinding(slot) == fallback, "old file defaults");
            }
            foreach (var invalid in new[] { "garbage", "Key:0", "Key:999999", "Key:" + (int)Key.Special, "Key:" + (int)Key.Unknown, "Trigger:0", "Button:1", "None:1", "Mouse:4", "900:1" })
            {
                file.SetValue("bindings", "jump.key0", invalid); file.Save(Path);
                using var bad = new PlayerSettings(Path);
                Assert(bad.GetBinding("jump.key0") == ControlBindings.Action("jump").Keyboard[0], "invalid " + invalid);
            }
            file.SetValue("bindings", "jump.key0", 7);
            file.SetValue("bindings", "jump.pad", "Key:74");
            file.SetValue("bindings", "unknown.key0", "Key:74");
            file.Save(Path);
            using var unknown = new PlayerSettings(Path);
            Assert(unknown.GetBinding("jump.key0") == ControlBindings.Action("jump").Keyboard[0] && unknown.GetBinding("jump.pad") == ControlBindings.Action("jump").Pad, "wrong types/devices accepted");
        }));
        runner.Run("bindings/conflicts reset action and synchronous save notification", () => Fresh(() =>
        {
            using var settings = new PlayerSettings(Path);
            var notifications = 0;
            settings.BindingsChanged += () =>
            {
                notifications++;
                using var reread = new PlayerSettings(Path);
                foreach (var (slot, _) in ControlBindings.Slots) Assert(reread.GetBinding(slot) == settings.GetBinding(slot), "notification before save " + slot);
            };
            settings.SetBinding("jump.key0", new(BindingKind.Key, (int)Key.F));
            Assert(notifications == 1 && settings.GetBinding("melee.key0").Kind == BindingKind.None, "conflict should unbind only matching slot");
            Assert(settings.GetBinding("melee.key1") == ControlBindings.Action("melee").Keyboard[1], "secondary lost");
            settings.ResetBindings("melee");
            Assert(notifications == 2 && settings.GetBinding("jump.key0").Kind == BindingKind.None, "reset conflict policy differs");
            settings.ResetBindings("jump");
            Assert(settings.GetBinding("jump.key0") == ControlBindings.Action("jump").Keyboard[0], "action reset");
            settings.SetBinding("jump.pad", ControlBindings.Action("fire").Pad);
            Assert(settings.GetBinding("fire.pad").Kind == BindingKind.None, "trigger conflict");
            settings.ResetBindings();
            foreach (var (slot, fallback) in ControlBindings.Slots) Assert(settings.GetBinding(slot) == fallback, "all reset conflict");
        }));
        runner.Run("bindings/corrupt duplicates fall back and reserved inputs reject", () => Fresh(() =>
        {
            using var file = new ConfigFile();
            file.SetValue("bindings", "jump.key0", "Key:70"); // F is still claimed by default melee.
            file.SetValue("bindings", "fire.key0", "Key:" + (int)Key.Space); // Restoring jump exposes a second collision.
            file.SetValue("bindings", "fire.pad", "Button:0"); // A is still claimed by default jump.
            file.Save(Path);
            using var settings = new PlayerSettings(Path);
            foreach (var (slot, fallback) in ControlBindings.Slots) Assert(settings.GetBinding(slot) == fallback, "duplicate fallback " + slot);
            foreach (var binding in new[] { new ControlBinding(BindingKind.Key, (int)Key.Escape), new(BindingKind.Key, (int)Key.F1),
                new(BindingKind.Button, (int)JoyButton.B), new(BindingKind.Button, (int)JoyButton.Start), new(BindingKind.Trigger, (int)JoyAxis.LeftX) })
            {
                Assert(!binding.Valid, "reserved binding accepted");
                try { settings.SetBinding(binding.Gamepad ? "jump.pad" : "jump.key0", binding); throw new InvalidOperationException("setter accepted reserved binding"); }
                catch (ArgumentException) { }
            }
        }));
        runner.Run("bindings/map preserves physical keys fixed sticks and contextual alias", () => Fresh(() =>
        {
            using var settings = new PlayerSettings(Path);
            InputSetup.Register();
            using var held = new InputEventKey { PhysicalKeycode = Key.Space, Keycode = Key.Space, Pressed = true };
            Input.ParseInputEvent(held); Input.FlushBufferedEvents();
            Assert(Input.IsActionPressed(InputSetup.Jump), "held-input fixture failed");
            settings.SetBinding("jump.key0", new(BindingKind.Key, (int)Key.J));
            InputSetup.ApplyBindings(settings);
            Assert(!Input.IsActionPressed(InputSetup.Jump), "rebind retained a removed held input");
            using var released = new InputEventKey { PhysicalKeycode = Key.Space, Keycode = Key.Space, Pressed = false };
            Input.ParseInputEvent(released); Input.FlushBufferedEvents();
            Assert(InputMap.ActionGetEvents(InputSetup.Jump).OfType<InputEventKey>().Single().PhysicalKeycode == Key.J, "not physical");
            Assert(InputMap.ActionGetEvents(InputSetup.MoveForward).OfType<InputEventJoypadMotion>().Single().Axis == JoyAxis.LeftY, "movement axis lost");
            Assert(InputMap.ActionGetEvents(InputSetup.LookLeft).OfType<InputEventJoypadMotion>().Single().Axis == JoyAxis.RightX, "look axis lost");
            Assert(InputMap.ActionGetEvents(InputSetup.Pause).OfType<InputEventJoypadButton>().Single().ButtonIndex == JoyButton.Start, "escape route lost");
            settings.SetBinding("reload.key0", new(BindingKind.Key, (int)Key.K)); InputSetup.ApplyBindings(settings);
            using var k = new InputEventKey { PhysicalKeycode = Key.K, Pressed = true };
            Assert(k.IsActionPressed(InputSetup.Reload) && k.IsActionPressed(InputSetup.NewSuburb), "contextual alias split");
            InputSetup.Register();
        }));
    }
    private static void Fresh(Action action)
    {
        DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(Path));
        try { action(); }
        finally { DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(Path)); InputSetup.Register(); }
    }
}
