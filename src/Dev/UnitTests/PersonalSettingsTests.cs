using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Dev.UnitTests.Fixtures;
using Propane.Net;
using Propane.Player;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev.UnitTests;

[UnitSuite]
public static class PersonalSettingsTests
{
    public static void Register(TestRunner runner)
    {
        runner.Run("settings/opt-in paired whole client, actor and camera callback cost", () => WithPath(path => Benchmark(runner, path)));
        runner.Run("settings/declarations round trip, clamp and reset every preference", () => WithPath(path =>
        {
            Assert(PreferenceCatalog.All.Select(p => p.Id).Distinct().Count() == PreferenceCatalog.All.Count, "duplicate ids");
            using var settings = new PlayerSettings(path);
            foreach (var preference in PreferenceCatalog.All)
            {
                Assert(settings.GetPreference(preference) == preference.Default, $"missing {preference.Id}");
                Assert(settings.SetPreference(preference, preference.Max), "save failed");
                using var reread = new PlayerSettings(path);
                Assert(reread.GetPreference(preference) == preference.Max, $"round trip {preference.Id}");
                settings.SetPreference(preference, double.NaN);
                Assert(settings.GetPreference(preference) == preference.Default, "NaN default");
                settings.SetPreference(preference, double.PositiveInfinity);
                Assert(settings.GetPreference(preference) == preference.Default, "infinity default");
                settings.SetPreference(preference, preference.Min - 100);
                Assert(settings.GetPreference(preference) == preference.Min, "lower clamp");
                settings.SetPreference(preference, preference.Max + 100);
                Assert(settings.GetPreference(preference) == preference.Max, "upper clamp");
                if (preference.Kind == PreferenceKind.Choice)
                {
                    for (var i = 0; i < preference.Options!.Count; i++)
                        Assert(preference.Describe(i) == preference.Options[i], "choice coverage");
                    Assert(preference.Options.Count == preference.Max + 1 && (preference.OptionValues == null || preference.OptionValues.Count == preference.Options.Count), "choice bounds");
                }
            }
            var notifications = 0;
            settings.PreferenceChanged += preference =>
            {
                using var reread = new PlayerSettings(path);
                Assert(reread.GetPreference(preference) == settings.GetPreference(preference), "apply ran before save");
                notifications++;
            };
            foreach (var category in PreferenceCatalog.All.Select(p => p.Category).Distinct()) Assert(settings.ResetPreferences(category), "reset save failed");
            Assert(notifications == PreferenceCatalog.All.Count, "reset skipped hooks");
            using var reset = new PlayerSettings(path);
            foreach (var preference in PreferenceCatalog.All) Assert(reset.GetPreference(preference) == preference.Default, "reset persistence");
            settings.Dispose();
            Throws<ObjectDisposedException>(() => settings.GetPreference(PreferenceCatalog.FieldOfView));
            Throws<ObjectDisposedException>(() => settings.SetPreference(PreferenceCatalog.FieldOfView, 80));
        }));
        runner.Run("settings/old files, wrong types, nonfinite values and unknown keys are safe", () => WithPath(path =>
        {
            using var file = new ConfigFile();
            file.SetValue("player", "name", "Veteran");
            file.SetValue("net", "server", "example.invalid");
            file.SetValue("hud", "leaderboard", false);
            file.SetValue("preferences", "future", new Vector3(1, 2, 3));
            file.Save(path);
            using (var old = new PlayerSettings(path))
            {
                Assert(old.Name == "Veteran" && old.Server == "example.invalid" && !old.ShowLeaderboard, "old identity/HUD lost");
                foreach (var preference in PreferenceCatalog.All.Where(p => p != PreferenceCatalog.ShowLeaderboard))
                    Assert(old.GetPreference(preference) == preference.Default, "old file missing default");
                old.SetPreference(PreferenceCatalog.FieldOfView, 80);
            }
            file.Load(path);
            Assert(file.HasSectionKey("preferences", "future"), "unknown data overwritten");
            foreach (var invalid in new[] { Variant.From("invalid"), Variant.From(new Vector3()), Variant.From(double.NaN), Variant.From(double.NegativeInfinity) })
            {
                foreach (var preference in PreferenceCatalog.All) file.SetValue(preference.Section, preference.Id, invalid);
                file.Save(path);
                using var settings = new PlayerSettings(path);
                foreach (var preference in PreferenceCatalog.All) Assert(settings.GetPreference(preference) == preference.Default, $"invalid {preference.Id}: {invalid.VariantType}");
            }
            foreach (var preference in PreferenceCatalog.All)
                file.SetValue(preference.Section, preference.Id, preference.Kind == PreferenceKind.Toggle ? Variant.From(true) : Variant.From(preference.Max + 100));
            file.Save(path);
            using var clamped = new PlayerSettings(path);
            foreach (var preference in PreferenceCatalog.All) Assert(clamped.GetPreference(preference) == preference.Max, "saved clamp");
        }));
        runner.Run("settings/reset isolates categories and reports write failures", () => WithPath(path =>
        {
            using var settings = new PlayerSettings(path);
            foreach (var preference in PreferenceCatalog.All) settings.SetPreference(preference, preference.Min);
            settings.ResetPreferences("Display");
            foreach (var preference in PreferenceCatalog.All)
                Assert(settings.GetPreference(preference) == (preference.Category == "Display" ? preference.Default : preference.Min), "reset crossed categories");
            // Existing directory is readable but cannot be replaced by a config file. A failed save still applies live.
            using var unwritable = new PlayerSettings(Path.GetDirectoryName(path)!);
            Assert(!unwritable.SetPreference(PreferenceCatalog.FieldOfView, 90) && unwritable.GetPreference(PreferenceCatalog.FieldOfView) == 90, "write failure hidden");
        }));
        runner.Run("settings/device preferences preserve scripts and shared tuners", () => WithPath(path =>
        {
            InputSetup.Register();
            using var settings = new PlayerSettings(path);
            using var tuning = Tuning.LoadBaked();
            var snapshot = tuning.Snapshot();
            Assert(snapshot.ContainsKey(nameof(Tuning.MouseSensitivity)) && snapshot.ContainsKey(nameof(Tuning.FieldOfView)), "shared tuner classification changed");
            settings.SetPreference(PreferenceCatalog.MouseSensitivity, 2);
            settings.SetPreference(PreferenceCatalog.InvertMouseY, 1);
            settings.SetPreference(PreferenceCatalog.GamepadSensitivity, 3);
            settings.SetPreference(PreferenceCatalog.InvertGamepadY, 1);
            settings.SetPreference(PreferenceCatalog.AimSensitivity, 0.5);
            var personal = new DeviceInput(tuning, () => true, preferences: settings);
            var plain = new DeviceInput(tuning, () => true);
            using var motion = new InputEventMouseMotion { ScreenRelative = new Vector2(10, 5) };
            personal.Accumulate(motion); plain.Accumulate(motion);
            Assert(personal.Read().Look == new Vector2(20, -10) && plain.Read().Look == new Vector2(10, 5), "device scaling escaped boundary");
            Input.ActionPress(InputSetup.Aim);
            Input.ActionPress(InputSetup.LookDown);
            try
            {
                personal.Accumulate(motion);
                var intent = personal.Read();
                Assert(intent.Look == new Vector2(10, -5) && intent.LookStick == Vector2.Zero &&
                    intent.LookAngularVelocity.IsEqualApprox(new Vector2(0, -tuning.StickLookDegreesPerSecond * 1.5f)), "ADS/stick gain or inversion");
                Assert(plain.Read().LookStick == Vector2.Down, "plain scripted stick units changed");
            }
            finally { Input.ActionRelease(InputSetup.Aim); Input.ActionRelease(InputSetup.LookDown); }
            foreach (var preference in PreferenceCatalog.All) settings.SetPreference(preference, preference.Max);
            Assert(tuning.Snapshot().RecursiveEqual(snapshot), "personal settings mutated shared tuning");
            // Mouse up + slower stick down at the upper limit must combine before the existing pitch clamp.
            using var aim = new LogicalAim(runner.World);
            aim.SetOrientation(0, 45);
            var sensitivity = Mathf.RadToDeg(runner.World.Tuning.MouseSensitivity);
            aim.Look(new Vector2(0, -2 / sensitivity), dt: 1, angularVelocity: Vector2.Down);
            Assert(aim.Pitch == 45, "simultaneous devices clamped pitch twice");
        }));
    }

    // Opt-in diagnostic window: manual 120 Hz simulation deltas; Stopwatch measures callback wall cost only.
    private static void Benchmark(TestRunner runner, string path)
    {
        if (DevArgs.Get("settings-bench") == null) return;
        using var settings = new PlayerSettings(path);
        using var server = new ServerFixture();
        for (var pair = 0; pair < 3; pair++)
        {
            foreach (var personal in pair % 2 == 0 ? new[] { false, true } : new[] { true, false })
            {
                using var client = new MatchClient(runner, server.Setup, server.Clock, 2, createLocalView: false);
                var match = client.Match;
                match.OnMessage(new NetMessage(Msg.MatchGo, server.Setup.Id, 0, new FloatMessage(6000)));
                var actor = match.Session.OwnedActors.Single().Character;
                actor.Runtime.IntentSource = new DeviceInput(match.World.Tuning, () => true, preferences: personal ? settings : null);
                var camera = match.Session.AddView(match, new CameraRig { World = match.World, Aim = actor.Aim, Preferences = personal ? settings : null });
                void Tick()
                {
                    server.Clock.Seconds += 1d / 120;
                    match._PhysicsProcess(1d / 120);
                    actor._PhysicsProcess(1d / 120);
                    camera._Process(1d / 120);
                }
                const int warmup = 20000;
                const int ticks = 60000;
                for (var i = 0; i < warmup; i++) Tick();
                var samples = new double[ticks];
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < ticks; i++)
                {
                    var start = Stopwatch.GetTimestamp();
                    Tick();
                    samples[i] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
                }
                var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                var mean = samples.Average();
                Array.Sort(samples);
                Assert(match.IsPlaying && match.Session.OwnedActors.Count() == 1, "benchmark workload retired");
                GD.Print(FormattableString.Invariant($"[settings-cost] pair={pair} personal={personal} actors={match.Session.Actors.Count} owned=1 bodies={match.Bodies.Entries.Count()} cameras=1 ticks={ticks} warmup={warmup} bytes={bytes} mean_us={mean:F3} p95_us={samples[(int)(ticks * .95) - 1]:F3} p99_us={samples[(int)(ticks * .99) - 1]:F3}"));
            }
        }
    }

    private static void WithPath(Action<string> test)
    {
        var directory = ProjectSettings.GlobalizePath("res://build/st-unit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { test(Path.Combine(directory, "settings.cfg")); }
        finally { Directory.Delete(directory, true); }
    }
}
