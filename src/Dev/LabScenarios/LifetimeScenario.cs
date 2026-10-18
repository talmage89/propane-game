using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Godot;
using Propane.Core;
using Propane.Dev.UnitTests.Fixtures;
using Propane.Lab;
using Propane.Modes;
using Propane.Tank;
using static Godot.GodotObject;
using static Propane.Dev.LabCases.LabScenarioTools;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev.LabCases;

internal sealed class LifetimeScenario(ILabScenarioHost host, bool trackingOnly = false) : LabScenario(host)
{
    protected override Task RunCore() => trackingOnly ? TrackingLifetime() : Lifetime();

    private async Task Lifetime()
    {
        using var snapshot = template.Snapshot();
        Assert(!snapshot.ContainsKey(nameof(Tuning.LabStackCount)) && !snapshot.ContainsKey(nameof(Tuning.LabBotSkill)),
            "Lab dials entered a shared tuning snapshot");
        var shard = int.Parse(DevArgs.Get("lifetime-shard") ?? "1", CultureInfo.InvariantCulture);
        var shards = int.Parse(DevArgs.Get("lifetime-shards") ?? "1", CultureInfo.InvariantCulture);
        Assert(shards > 0 && shard >= 1 && shard <= shards, "invalid lifetime shard");
        var listing = DevArgs.Get("list-lifetime") != null;
        var candidates = 0;
        var selected = 0;
        foreach (var scenario in LabScenarios.All)
        {
            foreach (var environment in scenario.Environments)
            {
                if (candidates++ % shards != shard - 1) continue;
                selected++;
                if (listing)
                {
                    GD.Print($"[lab lifetime-case] {scenario.Id}/{environment}");
                    continue;
                }
                await Cycle(scenario, environment).ConfigureAwait(true);
                await Collect().ConfigureAwait(true);
                await Cycle(scenario, environment).ConfigureAwait(true);
                await Collect().ConfigureAwait(true);
                var baseline = Counts();
                for (var cycle = 1; cycle <= (int)DevArgs.GetFloat("cycles", 10); cycle++)
                {
                    await Cycle(scenario, environment).ConfigureAwait(true);
                    await Collect().ConfigureAwait(true);
                    Assert(Counts() == baseline, $"{scenario.Id}/{environment} cycle {cycle}: {baseline} -> {Counts()}");
                }
                GD.Print($"[lab lifetime] {scenario.Id}/{environment}: {(int)DevArgs.GetFloat("cycles", 10)} entry/reset/exit cycles stable {baseline}");
            }
        }
        Assert(selected > 0, "lifetime shard selected no pairs");
        if (listing) GD.Print($"[lab lifetime] listed {selected} of {candidates} pairs");
        else if (shard == 1) await TrackingLifetime().ConfigureAwait(true);
    }

    private async Task Cycle(Propane.Lab.LabScenario scenario, LabEnvironment environment)
    {
        var lab = Enter(scenario, environment);
        var tankCount = lab.LiveTanks.Count();
        Assert(tankCount > 0, "fixture has no tanks");
        Assert(!lab.World.Networked && lab.Session.Actors.Count == (scenario.Banking ? template.LabBankBots + 1 :
            scenario.Prototype && scenario.Definition != ModeRegistry.DissolveRun ? template.LabPrototypeBots + 1 : 1),
            "local actors/world");
        Assert((lab.Opponent == null ? null : ActorView.For(lab.Opponent)) == null, "bot acquired a camera");
        if (scenario.Id == "stack") Assert(tankCount == template.LabStackCount && lab.LiveTanks.All(t => t.Transform.Basis.Y.Dot(Vector3.Up) > .99f), "upright stack count");
        if (scenario.Id == "range") Assert(tankCount == 12, "range fixture count");
        lab.World.Tuning.RunSpeed = 7;
        lab.World.Tuning.LabRangeStep = 15;
        Assert(template.RunSpeed != 7 && template.LabRangeStep == 10, "Lab edited template");
        await Frames(8).ConfigureAwait(true);
        var retired = lab.Session;
        lab.RequestReset();
        await Frames(6).ConfigureAwait(true);
        Assert(lab.ResetCount == 1 && lab.Session != retired, "reset did not replace session");
        Assert(!retired.IsAlive && retired.Actors.Count == 0 && retired.World.Clock.PendingCount == 0 &&
            retired.World.BlastReceivers.Count == 0, "reset left old services live");
        Assert(lab.World.Tuning.RunSpeed == 7 && lab.World.Tuning.LabRangeStep == 15, "reset lost session tuning");
        Assert(lab.LiveTanks.Count() == tankCount, "reset lost fixtures");
        retired = lab.Session;
        lab.Session.Dispose(); lab.Free();
        Assert(!retired.IsAlive && retired.Actors.Count == 0 && retired.World.BlastReceivers.Count == 0, "exit retained services");
    }

    private async Task TrackingLifetime()
    {
        foreach (var detonateFirst in new[] { false, true })
        {
            var lab = Enter(LabScenarios.Get("dissolve"), LabEnvironment.Suburb);
            lab.World.SetPaused(true);
            var runner = lab.Prototype!;
            var freed = RetireTrackedSegment(lab, detonateFirst);
            await Collect().ConfigureAwait(true);
            Assert(freed.All(w => !w.TryGetTarget(out _)), "a retired tank wrapper stayed reachable in a live Lab");
            Assert(lab.Session.IsAlive && TrackingCount(runner) > 0, "collection proof lost the live session");
            var reset = TankWrappers(lab);
            var retiringRunner = runner;
            lab.Session.OnUnsubscribing(() => Assert(!lab.World.IsAlive && TrackingCount(retiringRunner) > 0,
                "world invalidation must precede the tank drain"));
            lab.Session.OnStopping(() => Assert(TrackingCount(retiringRunner) == 0,
                "tank subscriptions must drain before runner services stop"));
            lab.Reset();
            lab.World.SetPaused(true);
            Assert(TrackingCount(runner) == 0, "reset did not drain the old runner");
            await Collect().ConfigureAwait(true);
            Assert(reset.All(w => !w.TryGetTarget(out _)), "reset retained old tank wrappers");
            runner = lab.Prototype!;
            lab.Session.Dispose();
            Assert(TrackingCount(runner) == 0, "exit did not drain the fresh runner");
            lab.Session.Dispose();
            lab.Free();
            await Collect().ConfigureAwait(true);
            GD.Print($"[lab tracking lifetime] detonate_first={detonateFirst} retired={freed.Count} reset={reset.Count}: collectible before exit, reset and repeated exit drained");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<WeakReference<PropaneTank>> TankWrappers(LabSession lab) =>
        lab.Dissolve!.Source.Segments.SelectMany(s => s.Tanks).Select(t => new WeakReference<PropaneTank>(t)).ToList();

    // No segment/tank local may survive on the async caller's state machine across a collection checkpoint.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<WeakReference<PropaneTank>> RetireTrackedSegment(LabSession lab, bool detonateFirst)
    {
        var arena = lab.Dissolve!;
        var runner = lab.Prototype!;
        var source = arena.Source;
        var segment = source.Segments[0];
        var tanks = segment.Tanks;
        var count = TrackingCount(runner);
        var tank = tanks[0];
        var wrappers = tanks.Select(t => new WeakReference<PropaneTank>(t)).ToList();
        // Leave captured shooter/drop state on a tank that will retire without detonating.
        var shots = LabRunnerInspection.Shots(runner);
        var drops = LabRunnerInspection.Drops(runner);
        shots[tanks[1]] = lab.Player.ActorId;
        drops.Add(tanks[1]);
        if (detonateFirst)
        {
            tank.DetonateRemote(tank.GlobalTransform * PropaneTank.LocalCenter);
            tank.Detonate(); // A repeated detonation cannot score or detach twice.
            Assert(TrackingCount(runner) == count - 1, "detonation did not release exactly one registration");
        }
        var dissolve = typeof(DissolveArena).GetMethod("Dissolve", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var candidate in tanks.Where(t => t.State != PropaneTank.TankState.Exploded))
            dissolve.Invoke(arena, new object[] { candidate });
        Assert(TrackingCount(runner) == count - (detonateFirst ? 1 : 0) && shots.ContainsKey(tanks[1]),
            "visual dissolution retired subscriptions or captured state early");
        typeof(DissolveArena).GetMethod("Retire", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(arena, new object[] { segment });
        Assert(TrackingCount(runner) == count - tanks.Count && !shots.Keys.Any(tanks.Contains) && !drops.Any(tanks.Contains),
            "content retirement retained subscriptions, shooters or drops");
        foreach (var candidate in tanks) runner.ForgetTank(candidate); // Repeated retirement is harmless.
        var score = ((Propane.Modes.DissolveRunRules)runner.Rules).Score;
        if (!detonateFirst)
        {
            tank.DetonateRemote(tank.GlobalTransform * PropaneTank.LocalCenter);
            Assert(((Propane.Modes.DissolveRunRules)runner.Rules).Score == score && tank.BlastRequest == 0,
                "a retired tank still called its runner");
        }
        source.AdvanceRetire(double.PositiveInfinity);
        Assert(tanks.All(t => !IsInstanceValid(t)), "native retirement did not free all tank bodies");
        return wrappers;
    }
}
