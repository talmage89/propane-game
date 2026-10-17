using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Godot;
using Propane.Core;
using Propane.Lab;
using Propane.Modes;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev.UnitTests;

[UnitSuite]
public static class ModeTunerSamplerTests
{
    public static void Register(TestRunner runner)
    {
        foreach (var mode in ModeRegistry.Prototypes.Prepend(ModeRegistry.TankBank).Append(ModeRegistry.Sandbox))
        {
            runner.Run($"sampler/{mode.Name} scalar edits, snapshots, defaults and resource replacement", () => Scalars(mode));
            runner.Run($"sampler/{mode.Name} unchanged sampling allocates nothing", () =>
            {
                using var tuning = new Tuning();
                var sampler = new ModeTunerSampler(mode);
                var initial = sampler.Sample(tuning);
                for (var i = 0; i < 1000; i++) sampler.Sample(tuning);
                AssertNoAllocation(() =>
                {
                    for (var i = 0; i < 1000; i++)
                        Assert(ReferenceEquals(initial, sampler.Sample(tuning)), "unchanged snapshot replaced");
                }, "unchanged sampler allocated");
            });
            runner.Run($"sampler/{mode.Name} ordered rule commands match reflection sampling", () => Commands(mode));
        }
        runner.Run("sampler/float, integer and toggle conversions retain exact bits", () =>
        {
            using var tuning = new Tuning();
            var mode = ModeRegistry.Sandbox with { Tuners = new[] { nameof(Tuning.RunSpeed), nameof(Tuning.FixedSeed), nameof(Tuning.FullAuto) } };
            Scalars(mode);
            var sampler = new ModeTunerSampler(mode);
            foreach (var value in new[] { 0d, -0d, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            {
                tuning.RunSpeed = (float)value;
                Compare(mode.CopyTuners(tuning), sampler.Sample(tuning));
            }
        });
        runner.Run("sampler/unknown tuner fails during resolution", () =>
            Throws<InvalidOperationException>(() => new ModeTunerSampler(ModeRegistry.Sandbox with { Tuners = new[] { "MissingTuner" } })));
    }

    private static void Scalars(ModeDefinition mode)
    {
        using var tuning = new Tuning();
        using var defaults = new Tuning();
        var sampler = new ModeTunerSampler(mode);
        var original = sampler.Sample(tuning);
        Compare(mode.CopyTuners(tuning), original);
        foreach (var name in mode.Tuners)
        {
            var property = typeof(Tuning).GetProperty(name)!;
            var before = sampler.Sample(tuning);
            var old = before.Float(name);
            var next = old + 1;
            if (property.GetCustomAttribute<ExportAttribute>() is { Hint: PropertyHint.Range } export)
            {
                var range = export.HintString.Split(',');
                var min = double.Parse(range[0], CultureInfo.InvariantCulture);
                var max = double.Parse(range[1], CultureInfo.InvariantCulture);
                // Stay inside float bounds: e.g. (float)0.01 is slightly below the schema's double minimum.
                next = (min + max) / 2;
                if (Convert.ToDouble(Convert.ChangeType(next, property.PropertyType, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) == old)
                {
                    next = old == min ? max : min;
                }
                if (property.PropertyType == typeof(float) && (next == min || next == max)) next = min + (max - min) / 4;
            }
            var value = property.PropertyType == typeof(bool) ? (object)(old == 0) : Convert.ChangeType(next, property.PropertyType, CultureInfo.InvariantCulture);
            property.SetValue(tuning, value); // The public property path bypasses every UI notification.
            var after = sampler.Sample(tuning);
            Assert(!ReferenceEquals(before, after), $"{name}: edit did not replace snapshot");
            Assert(before.Float(name) == old, $"{name}: prior snapshot mutated");
            Compare(mode.CopyTuners(tuning), after);
            Assert(ReferenceEquals(after, sampler.Sample(tuning)), $"{name}: unchanged snapshot replaced");
            // Defaults also restores local Lab dials, which deliberately do not travel in shared snapshots.
            using var editedSnapshot = tuning.Snapshot();
            using var snapshot = defaults.Snapshot();
            tuning.Apply(snapshot);
            tuning.CopyLocalTunersFrom(defaults);
            Compare(mode.CopyTuners(defaults), sampler.Sample(tuning));
            tuning.Apply(editedSnapshot);
            Compare(mode.CopyTuners(tuning), sampler.Sample(tuning));
            tuning.Apply(snapshot);
            tuning.CopyLocalTunersFrom(defaults);
            Compare(mode.CopyTuners(defaults), sampler.Sample(tuning));
        }
        using var defaultSnapshot = defaults.Snapshot();
        using var restored = Tuning.FromSnapshot(defaultSnapshot);
        Compare(mode.CopyTuners(restored), sampler.Sample(restored));
        var replacement = new ModeTunerSampler(mode); // A fresh round owns a fresh sampler, even with carried rules.
        Compare(mode.CopyTuners(restored), replacement.Sample(restored));
        Compare(mode.CopyTuners(defaults), original);
    }

    private static void Compare(ModeTuners expected, ModeTuners actual)
    {
        Assert(expected.Names.SequenceEqual(actual.Names), "sampled name/order mismatch");
        foreach (var name in expected.Names)
        {
            Assert(BitConverter.DoubleToInt64Bits(expected.Float(name)) == BitConverter.DoubleToInt64Bits(actual.Float(name)),
                $"{name}: sampled scalar bits differ");
        }
    }

    private static void Commands(ModeDefinition mode)
    {
        using var tuning = new Tuning();
        var sampler = new ModeTunerSampler(mode);
        var expected = mode.CreateRules();
        var actual = mode.CreateRules();
        var roster = new[] { new ModeActor(1, "One"), new ModeActor(2, "Two") };
        var world = new ModeWorld(ModeWorldSource.Void, MapTanks: 30, Spots: new[] { new ModeSpot(0, 0, 0) });
        expected.Initialize(new ModeSetup(roster, world, mode.CopyTuners(tuning), 23));
        actual.Initialize(new ModeSetup(roster, world, sampler.Sample(tuning), 23));
        for (uint round = 1; round <= 2; round++)
        {
            var clock = new ModeClock(ModePhase.Round, round, round * 100, round * 100 + 60);
            Send(new PhaseEntered(ModePhase.Round, new RoundInfo(round, round, mode.Timer, clock.Now, 60)), clock);
            for (var tick = 0; tick < 120; tick++)
            {
                clock = clock with { Now = clock.Now + 1d / 120 };
                if (tick == 40)
                {
                    foreach (var name in mode.Tuners)
                    {
                        var p = typeof(Tuning).GetProperty(name)!;
                        p.SetValue(tuning, Convert.ChangeType(Convert.ToDouble(p.GetValue(tuning), CultureInfo.InvariantCulture) + 1, p.PropertyType, CultureInfo.InvariantCulture));
                    }
                }

                if (tick == 80) { using var defaults = new Tuning(); using var snapshot = defaults.Snapshot(); tuning.Apply(snapshot); }
                Equal(expected.Handle(new TunersChanged(mode.CopyTuners(tuning)), clock), actual.Handle(new TunersChanged(sampler.Sample(tuning)), clock));
                Send(new ActorsSampled(new[] { new ActorSample(1, 0, 0, 0, false), new ActorSample(2, 20, 0, 20, false) }), clock);
                if (tick % 30 == 0) Send(new TankDetonated(1, TankOrigin.Map), clock);
                Assert(JsonSerializer.Serialize(expected.Snapshot()) == JsonSerializer.Serialize(actual.Snapshot()), "rule snapshots diverged");
            }
            sampler = new ModeTunerSampler(mode);
        }

        void Send(AcceptedEvent input, ModeClock clock) => Equal(expected.Handle(input, clock), actual.Handle(input, clock));
        static void Equal(System.Collections.Generic.IReadOnlyList<ModeCommand> a, System.Collections.Generic.IReadOnlyList<ModeCommand> b) =>
            Assert(a.Select(Encode).SequenceEqual(b.Select(Encode)), "ordered rule commands diverged");
        static string Encode(ModeCommand command) => command.GetType().Name + JsonSerializer.Serialize(command, command.GetType());
    }
}
