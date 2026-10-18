using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using Propane.Dev.UnitTests.Fixtures;
using Propane.Net;
using Propane.Tank;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev.UnitTests;

[UnitSuite]
public static class AuthorityLeaseTests
{
    public static void Register(TestRunner runner)
    {
        runner.Run("client authority/gather notification order and epoch revalidation", () =>
        {
            using var f = new AuthorityFixture(runner);
            var match = f.Match;
            var tank = match.LiveTanks.First();
            var owner = AuthorityTransactions.Owner(match);
            var order = new List<string>();
            Func<PropaneTank, bool>? granted = null;
            match.AuthorityResolved += _ =>
            {
                Assert(AuthorityTransactions.Count(owner, "pendingAuthority") == 0 && AuthorityTransactions.Count(owner, "pendingBodies") == 0,
                    "notification preceded request completion or reservation release");
                Assert(AuthorityTransactions.Count(owner, "gatherRequests") == 1, "gather membership removed before notification");
                Assert(!match.Bodies.Get(tank.NetId)!.ClaimPending, "notification preceded physical reconciliation");
                order.Add("before");
            };
            AuthorityTransactions.Gather(match, tank, predicate => { granted = predicate; order.Add("gather"); });
            match.AuthorityResolved += _ => order.Add("after");
            f.Flush();
            Assert(order.SequenceEqual(new[] { "before", "gather", "after" }), "gather moved relative to public listeners");
            Assert(AuthorityTransactions.Count(owner, "gatherRequests") == 0 && granted != null && granted(tank), "gather did not resolve its canonical lease");
            match.Bodies.Get(tank.NetId)!.Epoch++;
            Assert(!granted!(tank), "lease predicate accepted a newer epoch");
        });
    }
}
