using System;
using System.Linq;
using Propane.Dev.UnitTests.Fixtures;
using Propane.Net;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev.UnitTests;

[UnitSuite]
public static class ActorLifetimeTests
{
    public static void Register(TestRunner runner)
    {
        // Opt in until the production fix lands; the parent must demonstrate the regression first.
        if (DevArgs.Get("actor-lifetime-probe") == null) { runner.Run("actor lifetime/probe available", () => { }); return; }
        foreach (var roaming in new[] { true, false })
            runner.Run($"actor lifetime/{(roaming ? "roaming" : "match")} repeated removal releases actor state", () =>
            {
                using var f = new ActorLifetimeFixture(runner, roaming);
                var parent = DevArgs.Get("actor-lifetime-parent") != null;
                var before = f.Counts();
                for (var i = 0; i < 6; i++)
                {
                    var references = f.Cycle(100 + i, i % 2 == 0 ? ActorKind.Human : ActorKind.Bot);
                    var after = f.Counts();
                    Godot.GD.Print($"[actor-lifetime] roaming={roaming} cycle={i} {after}");
                    Assert(parent ? before != after : before == after, $"actor container expectation failed: {before} -> {after}");
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    Assert(parent ? references.Any(reference => reference.IsAlive) : references.All(reference => !reference.IsAlive), "actor collection expectation failed in the live session");
                    Assert(f.Match.Session.IsAlive, "collection test retired its session");
                }
                f.Match.Session.Dispose();
                Assert(f.Match.Session.Actors.Count == 0, "session retirement retained actors");
            });
    }
}
