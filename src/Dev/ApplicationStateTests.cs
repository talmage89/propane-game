using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Tank;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev;

/// <summary>Native process-policy witness. Run alone: the unit runner itself owns a world.</summary>
public partial class ApplicationStateTests : Node
{
    public override void _Ready()
    {
        try
        {
            foreach (var firstRetiresFirst in new[] { false, true }) PhysicsTranscript(firstRetiresFirst);
            if (DevArgs.Get("a1-bench") != null) Benchmark();
            GD.Print("[application] PASS process policies");
            CoreTests.Complete(this);
        }
        catch (Exception error)
        {
            GD.Print($"[application] FAIL {error}");
            CoreTests.Complete(this, 1);
        }
    }

    private void PhysicsTranscript(bool firstRetiresFirst)
    {
        var original = Engine.MaxPhysicsStepsPerFrame;
        var trace = new List<string>();
        void Observe(string name, double scale, int steps)
        {
            trace.Add(FormattableString.Invariant($"{name}:{Engine.TimeScale:0.00}/{Engine.MaxPhysicsStepsPerFrame}"));
            if (DevArgs.Get("a1-negative") == name) steps++;
            Assert(Math.Abs(Engine.TimeScale - scale) < .000001 && Engine.MaxPhysicsStepsPerFrame == steps, name);
        }
        try
        {
            Engine.MaxPhysicsStepsPerFrame = 8;
            using var tuning = Tuning.LoadBaked();
            tuning.DenseChainFuseThreshold = 1;
            tuning.DenseChainPhysicsSteps = 4;
            using var first = new WorldContext(this, tuning);
            Assert(first.TryHitstop(100, .1f), "sole world hitstop denied");
            Observe("sole", .1, 8);
            var a = Fuse(first);
            Observe("dense", .1, 4);
            using var second = new WorldContext(this, tuning);
            Observe("overlap", 1, 4);
            Assert(!first.TryHitstop(100, .1f) && !second.TryHitstop(100, .1f), "overlap acquired hitstop");
            second.Tuning.DenseChainPhysicsSteps = 2;
            var b = Fuse(second);
            Observe("minimum", 1, 2);
            second.SetPaused(true);
            b._Process(0);
            Observe("paused", 1, 4);
            second.SetPaused(false);
            b._Process(0);
            Observe("resumed", 1, 2);
            using (var network = new WorldContext(this, tuning, networked: true))
            {
                Observe("network-before-tank", 1, 2);
                var tank = PropaneTank.Create(network);
                network.Spawn.InWorld(tank);
                Observe("network-registered", 1, 8);
                network.SetPaused(true);
                a._Process(0);
                Observe("paused-network-veto", 1, 8);
                Assert(!network.TryHitstop(100, .1f), "network hitstop allowed");
            }
            Observe("network-retired", 1, 2);
            var retiring = firstRetiresFirst ? first : second;
            var survivor = firstRetiresFirst ? second : first;
            var simulation = firstRetiresFirst ? b : a;
            retiring.Dispose();
            Observe("one-retired", 1, firstRetiresFirst ? 2 : 4);
            Assert(survivor.TryHitstop(100, .1f), "survivor hitstop denied");
            survivor.SetPaused(true);
            simulation._Process(0);
            Observe("survivor-paused", 1, 8);
            Assert(!survivor.TryHitstop(100, .1f), "paused hitstop allowed");
            survivor.SetPaused(false);
            Assert(survivor.TryHitstop(-1, .1f), "expiry fixture denied");
            survivor.PollTimeScale();
            Observe("hitstop-expired", 1, 8);
            Assert(survivor.TryHitstop(100, .1f), "retirement fixture denied");
            simulation._Process(0);
            survivor.Dispose();
            Observe("all-retired", 1, 8);
            survivor.Dispose(); retiring.Dispose();
            Observe("repeat-dispose", 1, 8);
            GD.Print($"[application] transcript first-retires-first={firstRetiresFirst}: {string.Join(" ", trace)}");
        }
        finally { Engine.MaxPhysicsStepsPerFrame = original; }
    }

    private void Benchmark()
    {
        using var tuning = Tuning.LoadBaked();
        tuning.DenseChainFuseThreshold = 1;
        using var first = new WorldContext(this, tuning);
        using var second = new WorldContext(this, tuning);
        var a = Fuse(first);
        var b = Fuse(second);
        // Stable real-time samples: each iteration is both worlds' full managed tank callbacks and hitstop polls.
        // No frame stepping or fuse expiry is involved; native tank access remains in the measured callbacks.
        void Tick()
        {
            a._Process(0); b._Process(0);
            a._PhysicsProcess(1d / 120); b._PhysicsProcess(1d / 120);
            first.PollTimeScale(); second.PollTimeScale();
        }
        for (var i = 0; i < 2000; i++) Tick();
        var samples = new double[10000];
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < samples.Length; i++)
        {
            var start = Stopwatch.GetTimestamp();
            Tick();
            samples[i] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Array.Sort(samples);
        GD.Print(FormattableString.Invariant($"[application] cost worlds=2 tanks=2 ticks={samples.Length} bytes={allocated} mean-us={samples.Average():0.000} p95-us={samples[9499]:0.000} p99-us={samples[9899]:0.000}"));
        Assert(allocated == 0, "steady whole tank callbacks allocated");
    }

    private static TankSimulation Fuse(WorldContext world)
    {
        var tank = PropaneTank.Create(world);
        world.Spawn.InWorld(tank);
        tank.FuseScheduleOverride = (_, _) => { };
        tank.PunctureRemote(Vector3.Up * .2f, Vector3.Right, 1);
        tank.ChainHit(Vector3.Zero, 0);
        return world.EffectsRoot.GetChildren().OfType<TankSimulation>().Single();
    }
}
