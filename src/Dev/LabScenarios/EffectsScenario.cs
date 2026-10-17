using System.Linq;
using System.Threading.Tasks;
using Godot;
using Propane.Lab;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev.LabCases;

internal sealed class EffectsScenario(ILabScenarioHost host) : LabScenario(host)
{
    protected override Task RunCore() => EffectFrames();

    private async Task EffectFrames()
    {
        var count = (int)DevArgs.GetFloat("count", 128);
        Assert(count is >= 1 and <= 1024, "effect review count must be 1..1024");
        template.LabStackDistance = count <= 8 ? 10 : 24;
        var scenario = LabScenarios.Get("stack") with { Setup = lab => { lab.World.Tuning.LabStackCount = count; lab.BuildStack(); } };
        uint particleSeed = 0;
        void Seed(Node node)
        {
            if (node is GpuParticles3D particle) { particle.UseFixedSeed = true; particle.Seed = ++particleSeed; }
        }
        var tree = GetTree();
        var guarded = Guard<Node>(Seed);
        SceneTree.NodeAddedEventHandler observe = node => guarded(node);
        tree.NodeAdded += observe;
        using var subscription = DetachOnDisposal(() => tree.NodeAdded -= observe);
        var lab = Enter(scenario, LabEnvironment.Void);
        await Frames(180).ConfigureAwait(true);
        particleSeed = 0;
        var tank = lab.LiveTanks.First();
        tank.TakeShot(tank.GlobalPosition + Vector3.Up * .2f, Vector3.Back, Vector3.Forward);
        await Frames(60).ConfigureAwait(true);
        capture.Screenshot($"vent_{count}");
        await Frames(120).ConfigureAwait(true);
        capture.Screenshot($"smoke_{count}");
        lab.Session.Dispose(); lab.Free();
        lab = Enter(scenario, LabEnvironment.Void);
        await Frames(180).ConfigureAwait(true);
        particleSeed = 0;
        foreach (var primed in lab.LiveTanks.ToArray())
            primed.TakeShot(primed.GlobalPosition + Vector3.Up * .2f, Vector3.Back, Vector3.Forward);
        lab.LiveTanks.First().Detonate();
        var previous = 0;
        foreach (var frame in new[] { 1, 6, 15, 30, 60, 120, 240 })
        {
            await Frames(frame - previous).ConfigureAwait(true); previous = frame;
            capture.Screenshot($"chain_{count}_{frame:000}");
        }
        Assert(lab.LiveTanks.Count() < count, "effect review must detonate a tank");
        lab.Session.Dispose(); lab.Free();
        if (count >= 32)
        {
            lab = Enter(scenario, LabEnvironment.Void);
            await Frames(180).ConfigureAwait(true);
            particleSeed = 0;
            var target = lab.LiveTanks.First();
            target.TakeShot(target.GlobalPosition + Vector3.Up * .2f, Vector3.Back, Vector3.Forward);
            target.TakeShot(target.GlobalPosition + Vector3.Up * .2f, Vector3.Back, Vector3.Forward);
            previous = 0;
            foreach (var frame in new[] { 60, 180, 600 })
            {
                await Frames(frame - previous).ConfigureAwait(true); previous = frame;
                capture.Screenshot($"ordinary_{count}_{frame:000}");
            }
            lab.Session.Dispose(); lab.Free();
        }
        subscription.Dispose();
    }
}
