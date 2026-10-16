using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using Propane.Lab;
using Propane.Tank;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev.UnitTests;

// Keep new native fixtures after the existing synchronous suites and their warmed allocation checks.
[UnitSuite(1)]
public static class TankTrackingTests
{
    public static void Register(TestRunner runner)
    {
        foreach (var order in new[] { "DRS", "DSR", "RDS", "RSD", "SDR", "SRD" })
        {
            runner.Run($"tank tracking/{order}: detonate, retire and shutdown are idempotent", () =>
            {
                var released = 0;
                var punctures = 0;
                var detonations = 0;
                using var owner = new TankTracking(_ => released++);
                var tank = PropaneTank.Create(runner.World);
                try
                {
                    void Punctured(PropaneTank _) => punctures++;
                    void Detonated(PropaneTank changed) { detonations++; owner.Forget(changed); }
                    owner.Track(tank, Punctured, Detonated);
                    owner.Track(tank, Punctured, Detonated);
                    Raise(tank, "Punctured");
                    Assert(owner.Count == 1 && punctures == 1, "duplicate registration or puncture changed ownership");
                    foreach (var step in order)
                    {
                        if (step == 'D') Raise(tank, "Detonated");
                        if (step == 'R') owner.Forget(tank);
                        if (step == 'S') owner.Dispose();
                        Assert(owner.Count == 0 && released == 1, "lifecycle step kept or released a registration twice");
                    }
                    Raise(tank, "Punctured");
                    Assert(punctures == 1 && detonations == (order[0] == 'D' ? 1 : 0), "a detached handler was invoked");
                    Throws<ObjectDisposedException>(() => owner.Track(tank, Punctured, Detonated));
                }
                finally { tank.Free(); }
            });
        }

        runner.Run("tank tracking/reverse drain survives removal, reuse, native freeing and a cleanup failure", () =>
        {
            var first = PropaneTank.Create(runner.World);
            var second = PropaneTank.Create(runner.World);
            var third = PropaneTank.Create(runner.World);
            var log = new List<PropaneTank>();
            using var owner = new TankTracking(t => { log.Add(t); if (t == third) throw new InvalidOperationException("injected cleanup failure"); });
            try
            {
                owner.Track(first, _ => { }, _ => { });
                owner.Track(second, _ => { }, _ => { });
                owner.Track(third, _ => { }, _ => { });
                owner.Forget(second);
                owner.Track(second, _ => { }, _ => { });
                second.Free();
                Throws<AggregateException>(owner.Dispose);
                Assert(owner.Count == 0 && log.Count == 4 && log[0] == second && log[1] == second && log[2] == third && log[3] == first,
                    "drain changed reverse order, retained a freed wrapper or stopped on a cleanup error");
                owner.Dispose();
            }
            finally
            {
                first.Free();
                if (GodotObject.IsInstanceValid(second)) second.Free();
                third.Free();
            }
        });

        runner.Run("tank tracking/forget releases captured state while the owner and native tank remain alive", () =>
        {
            using var owner = new TankTracking(_ => { });
            var tank = PropaneTank.Create(runner.World);
            try
            {
                var state = Capture(owner, tank);
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                Assert(state.IsAlive, "live registration did not keep its captured callback state");
                owner.Forget(tank);
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                Assert(!state.IsAlive && owner.Count == 0 && GodotObject.IsInstanceValid(tank), "detachment retained captured state");
                GC.KeepAlive(owner);
            }
            finally { tank.Free(); }
        });

        runner.Run("tank tracking/forget detaches managed callbacks from a freed native body's wrapper", () =>
        {
            using var owner = new TankTracking(_ => { });
            var tank = PropaneTank.Create(runner.World);
            var state = Capture(owner, tank);
            tank.Free();
            owner.Forget(tank);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Assert(!state.IsAlive && owner.Count == 0, "a freed wrapper retained captured callback state after detachment");
            GC.KeepAlive(tank);
        });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Capture(TankTracking owner, PropaneTank tank)
    {
        var state = new object();
        owner.Track(tank, _ => GC.KeepAlive(state), _ => GC.KeepAlive(state));
        return new WeakReference(state);
    }

    private static void Raise(PropaneTank tank, string name) =>
        ((Action<PropaneTank>?)typeof(PropaneTank).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tank))?.Invoke(tank);
}
