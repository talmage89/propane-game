using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using Propane.Bots;
using Propane.Modes;
using Propane.Net;
using Propane.Player;

namespace Propane.Dev.UnitTests.Fixtures;

/// <summary>A surviving native client world; all actor assembly/removal uses Match's actual attachment paths.</summary>
internal sealed class ActorLifetimeFixture : IDisposable
{
    private readonly LobbyWorldFixture server = new(3);
    private readonly MatchClient client;
    private LobbyInfo lobby;
    public Match Match => client.Match;
    public ActorLifetimeFixture(Node runner, bool roaming, bool king = false)
    {
        lobby = server.Lobby();
        var survivor = lobby.Members.First() with { Id = 40, Kind = ActorKind.Bot, AllocationSlot = 40, ControllerConfig = ControllerConfig.ForBot(0) };
        lobby = lobby with { Members = lobby.Members.Append(survivor).ToArray() };
        if (!roaming) { server.StartMatch(); server.Advance(2); }
        var setup = (roaming ? server.World() : server.Match()) with { Players = lobby.Members.Select((p, i) => (p, i)).ToArray() };
        if (king) setup = setup with { ModeId = ModeRegistry.KingId };
        client = new MatchClient(runner, setup, server.Clock, 2);
        SetRoster(lobby);
    }

    public static object Field(object owner, string name) => owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(owner)!;
    public static int Count(object owner, string name) => ((ICollection)Field(owner, name)).Count;
    public static object? Call(object owner, string name, params object[] args) => owner.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, args);
    public static void Set(object owner, string name, object value) => owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.SetValue(owner, value);
    private void SetRoster(LobbyInfo value) => client.Client.Handle(MessageCodec.Encode(new NetMessage(Msg.LobbyState, 0, 0, value), true));

    public PlayerCharacter Add(int id, ActorKind kind, int owner = 2)
    {
        var profile = lobby.Members.First() with { Id = id, Kind = kind, OwnerPeerId = owner, AllocationSlot = id, ControllerConfig = kind == ActorKind.Bot ? ControllerConfig.ForBot(0) : ControllerConfig.Human };
        if (Match.IsLobby)
        {
            SetRoster(lobby with { Members = lobby.Members.Append(profile).ToArray() });
        }
        else
        {
            var character = (PlayerCharacter)Call(Match, "AddActor", profile, -1, new Vector3(id, 0, 0), 0f)!;
            if (!character.IsRemote) Call(Match, "SubscribeOwned", character);
            if (kind == ActorKind.Bot && !character.IsRemote) Call(Match, "AttachLobbyBot", character, profile);
            Call(Match, "RefreshBotPeers");
        }
        return Match.Session.Actor(id)!.Character;
    }

    public void Remove(PlayerCharacter actor)
    {
        if (Match.IsLobby) SetRoster(lobby);
        else Match.Session.RemoveActor(actor.Runtime);
    }

    public string Counts() => $"actors={Match.Session.Actors.Count} registry={Count(Match.Session, "registry")} unsubscribe={Count(Match.Session, "unsubscribe")} colliders={Count(Field(Match, "botServices"), "actorColliders")} bots={Count(Match, "bots")} peers={((IEnumerable)Field(Match, "bots")).Cast<object>().Sum(bot => Count(bot, "Others"))} actions={Count(Match, "receivedActions")} melee={Count(Match, "remoteMelee")} views={Count(Match.World, "views")} receivers={Match.World.BlastReceivers.Count}" +
        (Match.IsLobby ? $" profiles={Count(Match, "profiles")} scores={Count(Match, "scores")}" : "");

    [MethodImpl(MethodImplOptions.NoInlining)]
    public WeakReference[] Cycle(int id, ActorKind kind, int owner = 2)
    {
        var actor = Add(id, kind, owner);
        var runtime = actor.Runtime;
        if (kind == ActorKind.Human) Match.Session.AddLocalView(runtime, Match);
        // Populate relay caches too: late actions from a removed actor must still be ignored by registry lookup.
        Match.OnMessage(new NetMessage(Msg.MeleeStart, Match.Setup.Id, owner, new MeleeStartMessage(1, Vector3.Forward), id, actor.LifeId, 1));
        var brain = Match.BotBrain(40)!;
        if (brain is BankingBot)
        {
            Set(Field(brain, "targets"), "target", actor);
            var combat = Field(brain, "combat");
            foreach (var field in new[] { "fightTarget", "sighted", "coverObserver", "shooterSeen", "watcher" }) Set(combat, field, actor);
        }
        else
        {
            Set(Field(brain, "prizes"), "pointsTarget", actor);
            var zone = Field(brain, "zonePolicy");
            Set(zone, "rival", actor);
            Set(zone, "watched", actor);
        }
        var refs = new List<WeakReference> { new(actor), new(runtime) };
        // Attachments are inspected without requiring a candidate-only API on the parent.
        if (Match.Session.GetType().GetField("registrations", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(Match.Session) is IDictionary registrations)
            refs.Add(new WeakReference(registrations[runtime]!));
        string? retirementFailure = null;
        actor.TreeExiting += () =>
        {
            if (Match.Session.Actor(id) != null) retirementFailure = "native free preceded roster removal";
            foreach (var name in new[] { "Fired", "Ragdolled", "GotUp", "MeleeStarted", "MeleeHit", "PushedBody" })
            {
                var expected = DevArgs.Get("actor-lifetime-parent") != null && !actor.IsRemote;
                if ((Field(actor, name) is Delegate) != expected) retirementFailure = $"{name} subscription at native free differs from expectation";
            }
        };
        Remove(actor);
        // Native signal callbacks do not propagate managed exceptions to Free's caller. Assert here for exit 1.
        TestRunner.Assert(retirementFailure == null, retirementFailure ?? "actor retirement failed");
        Match.OnMessage(new NetMessage(Msg.MeleeStart, Match.Setup.Id, owner, new MeleeStartMessage(2, Vector3.Forward), id, actor.LifeId, 2));
        TestRunner.Assert(!GodotObject.IsInstanceValid(actor), "removal left the native character alive");
        return refs.ToArray();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public WeakReference[] RemovePrimary()
    {
        var actor = Match.Player;
        var runtime = actor.Runtime;
        var view = actor.View!;
        var references = new[] { new WeakReference(actor), new WeakReference(runtime), new WeakReference(view) };
        Match.Session.RemoveActor(runtime);
        return references;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public WeakReference[] RetireRemaining()
    {
        var references = Match.Session.Actors.SelectMany(actor => new[] { new WeakReference(actor), new WeakReference(actor.Character) }).ToArray();
        Match.Session.Dispose();
        return references;
    }
    public void Dispose() { client.Dispose(); server.Dispose(); }
}
