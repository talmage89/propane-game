using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using Propane.Bots;
using Propane.Fx;
using Propane.Lab;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev.LabCases;

/// <summary>Opt-in paired brain/whole-runner cost probe using the real shared budgets and turn rotation.</summary>
internal sealed class BotCostScenario(ILabScenarioHost host) : LabScenario(host)
{
    protected override async Task RunCore()
    {
        foreach (var king in new[] { false, true })
        {
            using var tuning = template.Copy();
            tuning.FixedSeed = 23;
            tuning.LabBankBots = tuning.LabPrototypeBots = 7;
            tuning.LabDuelSeconds = tuning.KingRoundSeconds = 600;
            tuning.HitstopDuration = 0;
            var lab = new LabSession
            {
                Scenario = LabScenarios.Get(king ? "king" : "duel"),
                EnvironmentKind = LabEnvironment.Suburb,
                Template = tuning,
                TestRandomSeed = 23,
                TestSeedPhysicsRandom = true,
                DrivePlayer = true,
                PauseOnFocusLoss = false,
                Name = "Lab"
            };
            AddLab(lab);
            if (!king) lab.Bank!.AttachBot(lab.Player);
            try
            {
                await Frames(120).ConfigureAwait(true);
                capture.Screenshot(king ? "cost_king" : "cost_bank");
                object runner = king ? lab.Prototype! : lab.Bank!;
                var services = (BotServices)runner.GetType().GetField("services", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runner)!;
                Action tick = king ? () => lab.Prototype!._PhysicsProcess(1d / 120) : () => lab.Bank!._PhysicsProcess(1d / 120);
                const int warmup = 2000, ticks = 6000;
                EffectProfile.Enabled = true;
                for (var i = 0; i < warmup; i++) tick();
                EffectProfile.Drain(EffectProfile.System.BotBrain);
                var whole = new double[ticks];
                var brain = new double[ticks];
                var rays = services.RayLines;
                var queries = services.NavigationQueries;
                var thinks = lab.Prototype?.Thinks ?? 0;
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < ticks; i++)
                {
                    var beforeThink = lab.Prototype?.ThinkSeconds ?? 0;
                    var start = Stopwatch.GetTimestamp();
                    tick();
                    whole[i] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
                    brain[i] = king ? (lab.Prototype!.ThinkSeconds - beforeThink) * 1e6 : EffectProfile.Drain(EffectProfile.System.BotBrain) * 1000;
                }
                var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
                Assert(king ? lab.Prototype!.IsPlaying && lab.Prototype.Thinks - thinks == ticks * 8 : lab.Bank!.IsPlaying,
                    "cost probe ended early or changed the brain workload");
                var wholeMean = whole.Average();
                var brainMean = brain.Average();
                Array.Sort(whole);
                Array.Sort(brain);
                GD.Print(FormattableString.Invariant($"[bot-cost] mode={(king ? "king" : "bank")} bots=8 ticks={ticks} thinks={ticks * 8} rays={services.RayLines - rays} queries={services.NavigationQueries - queries} bytes_per_tick={bytes / (double)ticks:F2} brain_us={brainMean:F3}/{brain[(int)(ticks * .95) - 1]:F3}/{brain[(int)(ticks * .99) - 1]:F3} whole_us={wholeMean:F3}/{whole[(int)(ticks * .95) - 1]:F3}/{whole[(int)(ticks * .99) - 1]:F3}"));
            }
            finally
            {
                EffectProfile.Enabled = false;
                lab.Session.Dispose();
                lab.Free();
            }
        }
    }
}
