using System;
using System.Diagnostics;
using System.Linq;
using Godot;
using Propane.Dev.UnitTests.Fixtures;

namespace Propane.Dev.UnitTests;

/// <summary>Opt-in complete Match plus owned-character physics callback, on identical stationary native bodies.</summary>
[UnitSuite]
public static class ActorTickBenchTests
{
    public static void Register(TestRunner runner)
    {
        runner.Run("actor lifetime/opt-in whole physics callback cost", () =>
        {
            if (DevArgs.Get("actor-tick-bench") == null) return;
            using var server = new ServerFixture();
            using var client = new MatchClient(runner, server.Setup, server.Clock, 2);
            var match = client.Match;
            var owned = match.Session.OwnedActors.ToArray();
            void Tick()
            {
                server.Clock.Seconds += 1d / 120;
                match._PhysicsProcess(1d / 120);
                foreach (var actor in owned) actor.Character._PhysicsProcess(1d / 120);
            }
            const int ticks = 60000;
            for (var i = 0; i < 20000; i++) Tick();
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
            TestRunner.Assert(match.Session.IsAlive && owned.Length == 1, "physics benchmark workload changed");
            GD.Print(FormattableString.Invariant($"[actor-tick-cost] actors={match.Session.Actors.Count} owned={owned.Length} bodies={match.Bodies.Entries.Count()} ticks={ticks} bytes={bytes} mean_us={mean:F3} p95_us={samples[(int)(ticks * .95) - 1]:F3} p99_us={samples[(int)(ticks * .99) - 1]:F3}"));
        });
    }
}
