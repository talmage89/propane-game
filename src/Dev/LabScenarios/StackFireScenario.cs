using System.Linq;
using System.Threading.Tasks;
using Godot;
using Propane.Core;
using Propane.Lab;
using Propane.Tank;
using static Propane.Dev.LabCases.LabScenarioTools;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev.LabCases;

internal sealed class StackFireScenario(ILabScenarioHost host) : LabScenario(host)
{
    protected override Task RunCore() => StackFire();

    /// <summary>Real shots into the shipped maximum stack, through the whole chain, with frame times and frames.</summary>
    private async Task StackFire()
    {
        template.LabStackCount = Tuning.LabStackMaximum;
        var lab = Enter(LabScenarios.Get("stack"), LabEnvironment.Void);
        await Frames(90).ConfigureAwait(true);
        capture.Screenshot("stack_before");
        var start = lab.LiveTanks.Count();
        Assert(start == Tuning.LabStackMaximum, "stack must start at the shipped maximum");
        var target = lab.LiveTanks.OrderBy(t => t.GlobalPosition.DistanceSquaredTo(lab.Player.GlobalPosition)).First();
        await FireAt(lab.Player, () => target.GlobalTransform * PropaneTank.LocalCenter).ConfigureAwait(true);
        Assert(target.State == PropaneTank.TankState.Venting, "first shot must puncture the stack tank");
        capture.Screenshot("stack_punctured");
        await FireAt(lab.Player, () => target.GlobalTransform * PropaneTank.LocalCenter).ConfigureAwait(true);
        var frames = new System.Collections.Generic.List<double>();
        var last = Time.GetTicksUsec();
        async Task Run(int count, string shots)
        {
            for (var i = 0; i < count; i++)
            {
                await Frames(1).ConfigureAwait(true);
                var now = Time.GetTicksUsec(); frames.Add((now - last) / 1000d); last = now;
                if (shots.Length > 0 && i is 10 or 60 or 240) capture.Screenshot($"{shots}_{i:000}");
            }
        }
        await Run(180, "stack_blast").ConfigureAwait(true);
        Assert(lab.LiveTanks.Count() < start && lab.LiveTanks.Count() > 0, "the first blast should leave a venting stack");
        // Shoot venting tanks until the rest go up, as a player would.
        for (var shot = 0; shot < 12 && lab.LiveTanks.Count() > start / 4; shot++)
        {
            var next = lab.LiveTanks.OrderBy(t => t.GlobalPosition.DistanceSquaredTo(lab.Player.GlobalPosition)).First();
            await FireAt(lab.Player, () => next.GlobalTransform * PropaneTank.LocalCenter).ConfigureAwait(true);
            await Run(90, shot == 0 ? "stack_chain" : "").ConfigureAwait(true);
        }
        await Run(240, "stack_end").ConfigureAwait(true);
        frames.Sort();
        GD.Print($"[lab stackfire] tanks {start} -> {lab.LiveTanks.Count()} median_ms={frames[frames.Count / 2]:F1} p95_ms={frames[(int)(frames.Count * .95)]:F1} max_ms={frames[^1]:F1}");
        Assert(lab.LiveTanks.Count() <= start / 4, "shooting the stack should chain most of it");
        lab.Session.Dispose(); lab.Free();
    }
}
