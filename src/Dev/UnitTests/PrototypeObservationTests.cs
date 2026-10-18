using System;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Propane.Bots;
using Propane.Dev.UnitTests.Fixtures;
using Propane.Modes;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev.UnitTests;

[UnitSuite(1)]
public static class PrototypeObservationTests
{
    public static void Register(TestRunner host)
    {
        Assert(typeof(IBotWorld).GetProperty("Rules") == null && typeof(ModeHudInput).GetProperty("Rules") == null,
            "observation contracts expose live rules");
        foreach (var id in new[] { "ringout", "king", "shortfuse", "dissolve" })
        {
            host.Run($"prototype observations/{id}: bot facts match rules without allocations", () =>
            {
                using var fixture = new PrototypeObservationFixture(host, id);
                var runner = fixture.Runner;
                var world = fixture.Observations;
                var clock = runner.Clock;
                void Read()
                {
                    if (runner.Rules is RingOutRules ring)
                    {
                        var value = world.RingOut;
                        Assert(value is { } view && ReferenceEquals(view.Plan, ring.Plan) && view.Elapsed == ring.Elapsed(world.Clock), "slab/clock changed");
                        for (var actor = 1; actor <= 4; actor++) Assert(world.RoundAlive(actor) == ring.IsAlive(actor), "Ring Out life changed");
                    }
                    else if (runner.Rules is ShortFuseRules fuse)
                    {
                        Assert(world.ShortFuseMaxHealth == fuse.MaxHealth, "health scale changed");
                        for (var actor = 1; actor <= 4; actor++)
                        {
                            Assert(world.RoundAlive(actor) == fuse.IsAlive(actor), "Short Fuse life changed");
                            Assert(world.ShortFuseHealth(actor, clock) == fuse.Health.Health(actor, clock.Now), "health read clock changed");
                        }
                    }
                    else if (runner.Rules is DissolveRunRules run)
                    {
                        Assert(world.DissolveRun == new DissolveRunBotView(run.Caught, run.Front), "integrated front changed");
                    }
                    else if (runner.Rules is KingRules king)
                    {
                        Assert(world.King == KingBotView.Read(king, world.Clock), "zone changed");
                    }
                }
                foreach (var now in new[] { 0d, 1d, 6d, 20d })
                {
                    clock = clock with { Now = now };
                    if (runner.Rules is ShortFuseRules fuse && now == 1)
                        fuse.Handle(new BulletHitReported(1, 1, 1, 2, 1, now), clock);
                    Read();
                    AssertNoAllocation(() => { for (var i = 0; i < 1000; i++) Read(); }, "hot observations allocate");
                }
                // Keep this snapshot across accepted facts: all exposed collections must remain copied values.
                var snapshot = runner.View!;
                var before = JsonSerializer.Serialize(snapshot, snapshot.GetType());
                runner.Rules.Handle(runner.Rules is DissolveRunRules
                    ? new ActorsSampled(new[] { new ActorSample(1, -100, 0, 0) }) : new ActorLeft(2), clock);
                Read();
                AssertNoAllocation(() => { for (var i = 0; i < 1000; i++) Read(); }, "changed hot observations allocate");
                Assert(JsonSerializer.Serialize(snapshot, snapshot.GetType()) == before, "a retained snapshot changed with its rules");
            });
            host.Run($"prototype observations/{id}: snapshots do not retain runners or rules", () =>
            {
                var (runner, rules, snapshot) = Retire(host, id);
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                Assert(!runner.IsAlive && !rules.IsAlive, "snapshot retained its retired producer");
                GC.KeepAlive(snapshot);
            });
            host.Run($"prototype observations/{id}: unretained snapshots collect", () =>
            {
                var snapshot = Release(host, id);
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                Assert(!snapshot.IsAlive, "observation snapshot stayed reachable after retirement");
            });
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Runner, WeakReference Rules, IModeView Snapshot) Retire(TestRunner host, string id)
    {
        using var fixture = new PrototypeObservationFixture(host, id);
        return (new WeakReference(fixture.Runner), new WeakReference(fixture.Runner.Rules), fixture.Runner.View!);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Release(TestRunner host, string id)
    {
        using var fixture = new PrototypeObservationFixture(host, id);
        return new WeakReference(fixture.Runner.View!);
    }
}
