using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Godot;
using Propane.Lab;
using Propane.Tank;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev;

public partial class LabTest
{
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
        var shots = (Dictionary<PropaneTank, int>)typeof(PrototypeRunner)
            .GetField("directShots", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runner)!;
        var drops = (HashSet<PropaneTank>)typeof(PrototypeRunner)
            .GetField("drops", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runner)!;
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
