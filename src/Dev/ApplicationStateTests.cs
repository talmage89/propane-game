using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Propane.Core;
using Propane.Net;
using Propane.Tank;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev;

/// <summary>Native process-policy witness. Run alone: the unit runner itself owns a world.</summary>
public partial class ApplicationStateTests : Node
{
    public override async void _Ready()
    {
        try
        {
            foreach (var firstRetiresFirst in new[] { false, true }) PhysicsTranscript(firstRetiresFirst);
            if (DevArgs.Get("a1-bench") != null) Benchmark();
            await UpdateLifetime().ConfigureAwait(true);
            GD.Print("[application] PASS process policies and update lifetime");
            CoreTests.Complete(this);
        }
        catch (Exception error)
        {
            GD.Print($"[application] FAIL {error}");
            CoreTests.Complete(this, 1);
        }
    }

    private async Task UpdateLifetime()
    {
        using var launch = LaunchArgs.OverrideForTests(new LaunchArgs(new[]
            { "--update-check", "--update-url=https://example.invalid/release", "--fake-version=0.0.1" }));
        foreach (var outcome in new[] { "live", "not-newer", "malformed", "retired-success", "retired-failure", "detached", "unattached", "cancelled", "callback-retires" })
        {
            var response = new TaskCompletionSource<string>();
            var token = CancellationToken.None;
            var registration = default(CancellationTokenRegistration);
            var callbacks = 0;
            var updates = new UpdateCheck
            {
                Request = (url, cancellation) =>
                {
                    Assert(url == "https://example.invalid/release", "update URL precedence changed");
                    token = cancellation;
                    if (outcome == "cancelled") registration = cancellation.Register(() => response.TrySetCanceled(cancellation));
                    return response.Task;
                },
            };
            updates.Found += () =>
            {
                callbacks++;
                if (outcome == "callback-retires") updates.Free();
            };
            if (outcome == "unattached") updates._Ready();
            else AddChild(updates);
            var completion = updates.Completion;
            Assert(completion != null && !completion.IsCompleted, "request did not await its response");
            if (outcome.StartsWith("retired-", StringComparison.Ordinal) || outcome is "cancelled" or "detached")
            {
                RemoveChild(updates);
                Assert(token.IsCancellationRequested, "tree exit did not cancel request");
                if (outcome != "detached") updates.Free();
            }
            if (outcome == "unattached")
            {
                updates.Free();
                Assert(token.IsCancellationRequested, "predelete did not cancel an unattached request");
            }
            if (outcome == "retired-failure")
            {
                response.SetException(new InvalidOperationException("injected unavailable"));
            }
            else if (outcome == "cancelled")
            {
                Assert(response.Task.IsCanceled, "request did not observe cancellation during exit");
            }
            else
            {
                response.SetResult(outcome switch
                {
                    "not-newer" => "{\"tag_name\":\"v0.0.1\"}",
                    "malformed" => "{",
                    _ => "{\"tag_name\":\"v99.0.0\"}",
                });
            }

            await completion!.ConfigureAwait(true);
            registration.Dispose();
            var delivered = outcome is "live" or "callback-retires";
            Assert(callbacks == (delivered ? 1 : 0) && updates.Latest == (delivered ? "99.0.0" : null),
                $"update delivery crossed lifetime: {outcome}");
            if (GodotObject.IsInstanceValid(updates)) updates.Free();
            GD.Print($"[application] update {outcome} callbacks={callbacks}");
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
