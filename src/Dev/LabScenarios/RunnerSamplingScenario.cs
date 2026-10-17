using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Godot;
using Propane.Lab;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev.LabCases;

/// <summary>Opt-in whole-callback probe; identical source can run on the parent without production instrumentation.</summary>
internal sealed class RunnerSamplingScenario(ILabScenarioHost host) : LabScenario(host)
{
    protected override Task RunCore()
    {
        foreach (var id in new[] { "ringout", "king", "shortfuse", "dissolve" })
        {
            using var tuning = template.Copy();
            tuning.FixedSeed = 23;
            tuning.LabPrototypeBots = 2;
            // Hold the fixture's round open for this synchronous callback window. Jolt and the scene tree do not
            // advance, but runner clocks, brains, retunes, actor samples and command application all run normally.
            tuning.RingOutBandSeconds = 60;
            tuning.RingOutRoundLimit = 600;
            tuning.KingRoundSeconds = 600;
            tuning.ShortFuseRoundSeconds = 300;
            tuning.DissolveFrontStartGap = 60;
            tuning.DissolveFrontMaxGap = 100;
            tuning.DissolveFrontSpeed = 0;
            tuning.DissolveFrontMaxSpeed = 0.5f;
            var lab = new LabSession
            {
                Scenario = LabScenarios.Get(id),
                EnvironmentKind = id is "shortfuse" or "dissolve" ? LabEnvironment.Suburb : LabEnvironment.Void,
                Template = tuning,
                CreateLocalView = false,
                PauseOnFocusLoss = false,
                TestRandomSeed = 23,
                TestSeedPhysicsRandom = true,
                Name = "Lab"
            };
            AddLab(lab);
            try
            {
                var runner = lab.Prototype!;
                const int warmup = 2000, ticks = 10000;
                const double delta = 1d / 120;
                for (var i = 0; i < warmup; i++) runner._PhysicsProcess(delta);
                var times = new double[ticks];
                var thinks = runner.Thinks;
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                var start = Stopwatch.GetTimestamp();
                for (var i = 0; i < ticks; i++)
                {
                    var tick = Stopwatch.GetTimestamp();
                    runner._PhysicsProcess(delta);
                    times[i] = Stopwatch.GetElapsedTime(tick).TotalMicroseconds;
                }
                var elapsed = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
                var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
                Assert(runner.IsPlaying && Math.Abs(runner.Elapsed - (warmup + ticks) * delta) < 0.001, "runner benchmark ended early");
                Array.Sort(times);
                GD.Print(FormattableString.Invariant($"[runner sampling] mode={id} ticks={ticks} thinks={runner.Thinks - thinks} bytes_per_tick={bytes / (double)ticks:F2} mean_us={elapsed / ticks:F3} p95_us={times[(int)(ticks * .95) - 1]:F3} p99_us={times[(int)(ticks * .99) - 1]:F3}"));
            }
            finally { lab.Session.Dispose(); lab.Free(); }
        }
        return Task.CompletedTask;
    }
}
