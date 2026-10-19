using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Propane.Core;
using Propane.Modes;
using Propane.Net;
using Propane.Session;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev.UnitTests.Fixtures;

/// <summary>Native mode owner inspection without production state hooks; Match remains reachable during collection.</summary>
internal static class ModeClientLifetimeFixture
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    public static object? Field(object owner, string name) => owner.GetType().GetField(name, Fields)?.GetValue(owner);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static WeakReference[] Retire(Match match)
    {
        var adapter = (IModeClient)Field(match, "clientMode")!;
        var owned = new List<object> { adapter, adapter.Scores };
        if (Field(adapter, "bank") is { } bank) owned.Add(bank);
        if (Field(adapter, "piles") is { } piles) owned.Add(piles);
        Assert(adapter.Scores.Count == match.Session.Actors.Count, "adapter was not initialized with this session's roster");
        match.OnMessage(new NetMessage(Msg.MatchGo, match.Setup.Id, 0, new FloatMessage(10)));
        if (match.Setup.ModeId == ModeRegistry.KingId)
        {
            var state = new KingState(1, new ModeSpot(0, 0, 0), new ModeSpot(20, 0, 0), 6, 1, new[] { 1 }, 2, 2, 45, 3,
                new[] { new KingMinted(1, 2) }, 0);
            match.OnMessage(new NetMessage(Msg.ModeSnapshot, match.Setup.Id, 0, new ModeEnvelope(1, 1, match.Setup.ModeId, 1, 0, 0, 1,
                ModeSchemas.KingKinds.Zone, state)));
            owned.Add(state);
        }
        var references = owned.Select(item => new WeakReference(item)).ToArray();
        Action<AuthorityMessage>? lateStock = adapter is LobbyClientAdapter stockAdapter ? stockAdapter.MarkStock : null;
        match.Session.Dispose();
        Assert(Field(match, "clientMode") == null, "retired Match retained its adapter");
        Assert(adapter.Scores.Count == 0 && adapter.KingSnapshot == null && adapter.ViewAt(10) == null, "retired adapter retained scores/view");
        foreach (var owner in owned)
        {
            if (Field(owner, "lobbyDeparted") is { } departed)
                Assert((int)departed.GetType().GetProperty("Count")!.GetValue(departed)! == 0, "retired lobby retained departure history");
            Assert(Field(owner, "replica") == null && Field(owner, "lobbyReplica") == null, "adapter retained a borrowed collaborator");
        }
        if (adapter is LobbyClientAdapter lobby)
            Assert(!lobby.PanelOpen && !lobby.Staged && !lobby.SuppressHud, "retired lobby retained presentation flags");
        adapter.Dispose();
        lateStock?.Invoke(new AuthorityMessage(2, 0, ClaimRefusal.None, Array.Empty<BodyAuthority>()));
        return references;
    }

    // Keep fixture-only strong references off the frame that forces collection (Debug extends local lifetimes).
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void PopulateLobby(MatchClient client, LobbyInfo original, int id)
    {
        var profile = original.Members.First() with
        {
            Id = id,
            AllocationSlot = id,
            Kind = ActorKind.Bot,
            ControllerConfig = ControllerConfig.Bot
        };
        void Roster(LobbyInfo value) => client.Client.Handle(MessageCodec.Encode(new NetMessage(Msg.LobbyState, 0, 0, value), true));
        Roster(original with { Members = original.Members.Append(profile).ToArray() });
        Roster(original);
        var adapter = (LobbyClientAdapter)Field(client.Match, "clientMode")!;
        client.Match.PanelOpen = true;
        Roster(original with { Phase = LobbyPhase.Staging, StartsIn = 2 });
        Assert(adapter.Staged && adapter.PanelOpen && ((HashSet<int>)Field(adapter, "lobbyDeparted")!).Contains(profile.Id),
            "lifetime fixture failed to populate lobby-owned state");
    }

    private sealed class InjectedFailure : Exception { }

    private sealed class FailAfterConfigure(Match match, ISessionMode inner) : ISessionMode, ISessionWorldSource
    {
        public WeakReference[] References { get; private set; } = Array.Empty<WeakReference>();
        public void Configure(SessionContext session)
        {
            inner.Configure(session);
            var adapter = Field(match, "clientMode")!;
            var owners = new List<object> { adapter };
            if (Field(adapter, "bank") is { } bank) owners.Add(bank);
            if (Field(adapter, "piles") is { } piles) owners.Add(piles);
            References = owners.Select(owner => new WeakReference(owner)).ToArray();
            throw new InjectedFailure();
        }
        public void Build(SessionContext session) => throw new InvalidOperationException("source ran after failed configuration");
        public void CreateActors(SessionContext session) => throw new InvalidOperationException("actors ran after failed configuration");
        public void CreateViews(SessionContext session) => throw new InvalidOperationException("views ran after failed configuration");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static WeakReference[] FailedAssembly(TestRunner runner, MatchSetup setup, IMonotonicClock clock)
    {
        var net = new NetClient(PacketTransports.CreateClient(), ModeRegistry.Codec, clock);
        Handshake.Welcome(net, 2);
        var match = new Match { Net = net, Setup = setup, Clock = clock, CreateLocalView = false };
        try
        {
            var modeType = typeof(Match).GetNestedType("MatchMode", BindingFlags.NonPublic)!;
            var realMode = (ISessionMode)Activator.CreateInstance(modeType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new object[] { match }, null)!;
            var failure = new FailAfterConfigure(match, realMode);
            SessionContext built = null!;
            Throws<InjectedFailure>(() => SessionFactory.Build(runner, runner.World.Tuning, failure, failure,
                session => { built = session; typeof(Match).GetProperty(nameof(Match.Session))!.SetValue(match, session); }, networked: true, present: false));
            Assert(!built.IsAlive && !built.World.IsAlive && Field(match, "clientMode") == null, "failed construction retained its mode adapter/session");
            built.Dispose();
            return failure.References;
        }
        finally { match.Free(); net.Free(); }
    }
}
