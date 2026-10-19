using System;
using System.Diagnostics;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Dev.UnitTests.Fixtures;
using Propane.Net;
using Propane.Tank;

namespace Propane.Dev.UnitTests;

/// <summary>Identical parent/candidate workload, measuring complete Match callbacks including remote vent correction.</summary>
[UnitSuite]
public static class BodyCorrectionBenchTests
{
    public static void Register(TestRunner runner)
    {
        runner.Run("correction/whole client callback allocation and cost", () =>
        {
            if (DevArgs.Get("correction-bench") == null) { return; }
            using var server = new ServerFixture();
            using var client = new MatchClient(runner, server.Setup, server.Clock, 3, createLocalView: false);
            var match = client.Match;
            match.OnMessage(new NetMessage(Msg.MatchGo, server.Setup.Id, 0, new FloatMessage(6000)));
            var tanks = match.LiveTanks.OrderBy(t => t.NetId).Take(8).ToArray();
            TestRunner.Assert(tanks.Length == 8, "correction benchmark requires eight remote vents");
            foreach (var tank in tanks)
            {
                tank.PunctureRemote(PropaneTank.LocalCenter + Vector3.Right * .15f, Vector3.Right, 1);
                tank.CollisionLayer = 0;
                tank.CollisionMask = Layers.Floor;
            }
            var sequence = 0u;
            var displacedSamples = 0;
            var aligned = DevArgs.Get("correction-bench-aligned") != null;
            void Tick(int tick)
            {
                server.Clock.Seconds += 1d / 120;
                // Four free-flight copies and four upright floor copies, with publications at 15 Hz.
                // Native physics does not advance inside this controlled callback window.
                var phase = (tick % 120) / 120f;
                for (var i = 0; i < tanks.Length; i++)
                {
                    var tank = tanks[i];
                    Transform3D Pose(float time) => new(Basis.Identity, new Vector3(
                        80 + i * 2 + (i < 4 ? 0 : 5 * time * time), i < 4 ? 10 + 2 * time - 5 * time * time : 0, 0));
                    var pose = Pose(phase);
                    tank.GlobalTransform = pose;
                    if (tick % 8 == 0)
                    {
                        var sampledPhase = aligned ? phase : phase - .15f;
                        var sampled = Pose(sampledPhase);
                        if (sampled.Origin.DistanceTo(pose.Origin) > .01f) { displacedSamples++; }
                        var velocity = i < 4 ? Vector3.Up * (2 - 10 * sampledPhase) : Vector3.Right * (10 * sampledPhase);
                        match.Bodies.OnState(2, new BodyState(tank.NetId, sampled, velocity, Vector3.Zero, 1, ++sequence),
                            false, server.Clock.Seconds, .15);
                    }
                }
                match._PhysicsProcess(1d / 120);
            }
            Tick(0);
            using (var query = new PhysicsTestMotionParameters3D { RecoveryAsCollision = true })
            {
                for (var i = 0; i < tanks.Length; i++)
                {
                    query.From = tanks[i].GlobalTransform;
                    TestRunner.Assert(PhysicsServer3D.BodyTestMotion(tanks[i].GetRid(), query) == (i >= 4),
                        $"correction benchmark contact classification changed for body {i}");
                }
            }
            const int warmup = 20000;
            const int ticks = 60000;
            for (var i = 0; i < warmup; i++) { Tick(i); }
            var samples = new double[ticks];
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < ticks; i++)
            {
                var start = Stopwatch.GetTimestamp();
                Tick(warmup + i);
                samples[i] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
            }
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            var mean = samples.Average();
            Array.Sort(samples);
            TestRunner.Assert(match.IsPlaying && tanks.All(t => match.Bodies.Get(t.NetId) is { HasTarget: true, Owner: 2, ClaimPending: false }),
                "correction workload stopped observing its owner");
            TestRunner.Assert(displacedSamples == (aligned ? 0 : (int)sequence), "aged publication geometry coverage changed");
            match.Bodies.Clear();
            TestRunner.Assert(tanks.All(t => t.MaxContactsReported == 0 && !t.ContactMonitor), "benchmark changed native contact policy");
            GD.Print(FormattableString.Invariant($"[correction-cost] sample_pose={(aligned ? "aligned" : "aged")} vents={tanks.Length} free=4 floor=4 warmup={warmup} ticks={ticks} publications={sequence} displaced_samples={displacedSamples} bytes={bytes} mean_us={mean:F3} p95_us={samples[(int)(ticks * .95) - 1]:F3} p99_us={samples[(int)(ticks * .99) - 1]:F3}"));
        });
    }
}
