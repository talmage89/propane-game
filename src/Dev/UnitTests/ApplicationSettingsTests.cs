using System;
using System.IO;
using Godot;
using Propane.Core;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev.UnitTests;

[UnitSuite]
public static class ApplicationSettingsTests
{
    public static void Register(TestRunner runner)
    {
        runner.Run("application/settings stores round trip immediately and remain isolated", () =>
        {
            var directory = ProjectSettings.GlobalizePath("res://build/a1-settings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var firstPath = Path.Combine(directory, "first.cfg");
                var secondPath = Path.Combine(directory, "second.cfg");
                using var first = new PlayerSettings(firstPath);
                using var second = new PlayerSettings(secondPath);
                Assert(first.Name == "" && first.Server == "" && first.Appearance == null && first.ShowLeaderboard, "defaults changed");
                Assert(!File.Exists(firstPath) && !File.Exists(secondPath), "reading defaults saved settings");
                first.Name = "A \"quoted\" name";
                first.Server = "example.invalid:24685";
                first.Appearance = new AppearanceRecord(12, 34, 56, Hat: "hat");
                first.ShowLeaderboard = false;
                using var reread = new PlayerSettings(firstPath);
                Assert(reread.Name == first.Name && reread.Server == first.Server && reread.Appearance == first.Appearance && !reread.ShowLeaderboard,
                    "setter did not save before returning");
                Assert(second.Name == "" && second.Server == "" && second.Appearance == null && second.ShowLeaderboard && !File.Exists(secondPath),
                    "one store changed another");
                second.Name = "Second";
                second.ShowLeaderboard = false;
                Assert(first.Name != second.Name, "store caches overlap");
                using var config = new ConfigFile();
                Assert(config.Load(firstPath) == Error.Ok && config.GetValue("player", "name").AsString() == first.Name &&
                    config.GetValue("net", "server").AsString() == first.Server && !config.GetValue("hud", "leaderboard").AsBool() &&
                    AppearanceCodec.Decode(config.GetValue("player", "appearance").AsByteArray()) == first.Appearance, "settings format changed");
                config.SetValue("hud", "leaderboard", true); config.Save(firstPath);
                Assert(!first.ShowLeaderboard && !reread.ShowLeaderboard, "leaderboard preference stopped caching");
                using var fresh = new PlayerSettings(firstPath);
                Assert(fresh.ShowLeaderboard, "a new store reused another cache");
                first.Dispose(); first.Dispose();
                Throws<ObjectDisposedException>(() => _ = first.Name);
                Throws<ObjectDisposedException>(() => _ = first.ShowLeaderboard);
                Throws<ObjectDisposedException>(() => first.Name = "Retired");
            }
            finally { Directory.Delete(directory, true); }
        });
        runner.Run("application/settings scopes restore exact stores after exceptions", () =>
        {
            var original = PlayerPreferences.Application;
            using var first = new PlayerSettings("res://build/a1-unused-first.cfg");
            using var second = new PlayerSettings("res://build/a1-unused-second.cfg");
            using (var outer = PlayerSettings.Bind(first))
            {
                Assert(ReferenceEquals(PlayerPreferences.Application, first), "outer store not selected");
                try
                {
                    using var inner = PlayerSettings.Bind(second);
                    Throws<InvalidOperationException>(() => outer.Dispose());
                    Assert(ReferenceEquals(PlayerPreferences.Application, second), "out-of-order disposal changed store");
                    throw new InvalidDataException("scope witness");
                }
                catch (InvalidDataException) { }
                Assert(ReferenceEquals(PlayerPreferences.Application, first), "exception lost outer store");
            }
            Assert(ReferenceEquals(PlayerPreferences.Application, original), "scope lost application store");
            first.Dispose();
            Throws<ObjectDisposedException>(() => PlayerSettings.Bind(first));
        });
        runner.Run("application/launch scopes restore snapshots through nesting and exceptions", () =>
        {
            var original = LaunchArgs.Current;
            var outerArgs = new LaunchArgs(new[] { "--name=first", "--name=last", "--flag" });
            using (var outer = LaunchArgs.OverrideForTests(outerArgs))
            {
                Assert(ReferenceEquals(LaunchArgs.Current, outerArgs) && LaunchArgs.Current.Get("name") == "last", "launch precedence changed");
                try
                {
                    using var inner = LaunchArgs.OverrideForTests(new LaunchArgs(new[] { "--name=inner" }));
                    Throws<InvalidOperationException>(() => outer.Dispose());
                    Assert(LaunchArgs.Current.Get("name") == "inner", "out-of-order launch disposal changed snapshot");
                    throw new InvalidDataException("scope witness");
                }
                catch (InvalidDataException) { }
                Assert(ReferenceEquals(LaunchArgs.Current, outerArgs) && LaunchArgs.Current.Has("flag"), "inner override lost original snapshot");
            }
            Assert(ReferenceEquals(LaunchArgs.Current, original), "launch override leaked");
        });
        runner.Run("application/world tuning is owned while cache assets stay borrowed", () =>
        {
            using var template = Tuning.LoadBaked();
            using var first = new WorldContext(runner, template);
            using var second = new WorldContext(runner, template);
            var value = template.BlastRadius;
            first.Tuning.BlastRadius = value + 1;
            Assert(second.Tuning.BlastRadius == value && template.BlastRadius == value && first.Tuning != second.Tuning,
                "world tuning edits escaped their owner");
            var mesh = GameAssets.TankMesh;
            var shape = GameAssets.TankShape;
            var material = GameAssets.TankOuterMaterial;
            first.Dispose();
            Assert(GodotObject.IsInstanceValid(second.Tuning) && GameAssets.TankMesh == mesh && GameAssets.TankShape == shape && GameAssets.TankOuterMaterial == material &&
                GodotObject.IsInstanceValid(mesh) && GodotObject.IsInstanceValid(shape) && GodotObject.IsInstanceValid(material), "world retirement disposed borrowed assets");
        });
    }
}
