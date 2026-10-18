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
public static class ClientTransactionTests
{
    public static void Register(TestRunner runner)
    {
        runner.Run("client authority/empty flush has no steady allocation", () =>
        {
            using var transactions = new ClientAuthorityTransactions(2, new RetiringReplica());
            for (var i = 0; i < 100; i++) transactions.Flush();
            AssertNoAllocation(() =>
            {
                for (var i = 0; i < 1000; i++) transactions.Flush();
            }, "empty authority flush allocated");
        });
        runner.Run("client authority/session retirement clears pending gathers, queues and roots", () =>
        {
            using var f = new AuthorityFixture(runner);
            var match = f.Match;
            var owner = AuthorityTransactions.Owner(match);
            var tanks = match.LiveTanks.Take(2).ToArray();
            AuthorityTransactions.Gather(match, tanks[0], _ => throw new InvalidOperationException("retired gather callback ran"));
            tanks[0].TakeShot(tanks[0].ToGlobal(PropaneTank.LocalCenter + Vector3.Right * .15f), Vector3.Right, Vector3.Left, match.Player.ActorId);
            tanks[1].GlobalPosition = new Vector3(0, 60, 0);
            tanks[1].Detonate(match.Player.ActorId);
            Assert(AuthorityTransactions.Count(owner, "pendingAuthority") > 0 && AuthorityTransactions.Count(owner, "outgoingAuthority") > 0,
                "retirement fixture did not leave requests pending and queued");
            Assert(AuthorityTransactions.Listeners(match) == 1, "pending gather callback missing");
            match.Session.Dispose();
            match.Session.Dispose();
            AuthorityTransactions.Empty(owner);
            Assert(AuthorityTransactions.Listeners(match) == 0, "session retirement retained its gather callback");
        });
        runner.Run("client authority/repeated sessions and disposed owner release all collaborators", () =>
        {
            var references = new List<WeakReference>();
            for (var i = 0; i < 6; i++) references.Add(Retire(runner));
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert(references.All(reference => !reference.IsAlive), "repeated matches retained retired authority owners");
            using var transactions = DisposedOwner(out var collaborator);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert(!collaborator.IsAlive, "disposed owner retained its synchronous replica");
            AuthorityTransactions.Empty(transactions);
            GC.KeepAlive(transactions);
        });
        runner.Run("client authority/retirement during notification invalidates pending gather callbacks", () =>
        {
            using var f = new AuthorityFixture(runner);
            f.Match.AuthorityResolved += _ => f.Match.Session.Dispose();
            AuthorityTransactions.Gather(f.Match, f.Match.LiveTanks.First(),
                _ => throw new InvalidOperationException("retired gather callback ran from the event invocation snapshot"));
            f.Flush();
            AuthorityTransactions.Empty(AuthorityTransactions.Owner(f.Match));
            Assert(AuthorityTransactions.Listeners(f.Match) == 1, "retirement changed external listener ownership");
        });
        runner.Run("client authority/synchronous send retirement still notifies and drains", () =>
        {
            using var f = new AuthorityFixture(runner);
            var tank = f.Match.LiveTanks.First();
            f.Match.Claim(new[] { tank }, atomic: true);
            tank.TakeShot(tank.ToGlobal(PropaneTank.LocalCenter + Vector3.Right * .15f), Vector3.Right, Vector3.Left, f.Match.Player.ActorId);
            var notified = false;
            f.Match.AuthorityResolved += _ =>
            {
                Assert(!f.Match.Session.IsAlive, "notification did not follow the retiring queued send");
                notified = true;
            };
            f.Match.AuthoritySendOverride = _ => f.Match.Session.Dispose();
            f.Flush();
            Assert(notified, "synchronous send retirement lost the in-flight resolution notification");
            var transactions = (ClientAuthorityTransactions)AuthorityTransactions.Owner(f.Match);
            AuthorityTransactions.Empty(transactions);
            Throws<ObjectDisposedException>(() => transactions.NewRequest());
        });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Retire(TestRunner runner)
    {
        using var f = new AuthorityFixture(runner);
        var owner = AuthorityTransactions.Owner(f.Match);
        AuthorityTransactions.Empty(owner);
        AuthorityTransactions.Gather(f.Match, f.Match.LiveTanks.First(), _ => { });
        f.Match.Session.Dispose();
        AuthorityTransactions.Empty(owner);
        return new WeakReference(owner);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ClientAuthorityTransactions DisposedOwner(out WeakReference collaborator)
    {
        var replica = new RetiringReplica();
        var transactions = new ClientAuthorityTransactions(2, replica);
        collaborator = new WeakReference(replica);
        transactions.Dispose();
        return transactions;
    }

    private sealed class RetiringReplica : IClientAuthorityReplica
    {
        public BodySync.Entry? FindBody(uint id) => null;
        public BodyState? CurrentProposal(uint id) => null;
        public event Action<AuthorityMessage>? AuthorityResolved;
        public void SendAuthority(Msg type, object payload, int actorId, uint lifeId) => throw new InvalidOperationException("unexpected send");
        public void CancelRoot(uint request) { }
        public (BodyAuthority Authority, BodySync.Entry? Entry)? Adopt(BodyAuthority authority, bool gatherGrant) => (authority, null);
        public void Explode(BodyAuthority authority) { }
        public void Restore(BodyAuthority authority, BodySync.Entry? entry, ClaimRefusal reason, bool finishedPuncture) { }
        public void RemoveUnknown(uint id) { }
        public void Notify(AuthorityMessage result) => AuthorityResolved?.Invoke(result);
    }
}
