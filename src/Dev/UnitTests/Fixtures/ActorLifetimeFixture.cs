using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
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
    public ActorLifetimeFixture(Node runner, bool roaming)
    {
        lobby = server.Lobby();
        var survivor = lobby.Members.First() with { Id = 40, Kind = ActorKind.Bot, AllocationSlot = 40, ControllerConfig = ControllerConfig.ForBot(0) };
        lobby = lobby with { Members = lobby.Members.Append(survivor).ToArray() };
        if (!roaming) { server.StartMatch(); server.Advance(2); }
        var setup = (roaming ? server.World() : server.Match()) with { Players = lobby.Members.Select((p, i) => (p, i)).ToArray() };
        client = new MatchClient(runner, setup, server.Clock, 2);
        SetRoster(lobby);
    }

    public static object Field(object owner, string name) => owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(owner)!;
    public static int Count(object owner, string name) => ((ICollection)Field(owner, name)).Count;
    public static object? Call(object owner, string name, params object[] args) => owner.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, args);
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

    public string Counts() => $"actors={Match.Session.Actors.Count} registry={Count(Match.Session, "registry")} unsubscribe={Count(Match.Session, "unsubscribe")} colliders={Count(Field(Match, "botServices"), "actorColliders")} bots={Count(Match, "bots")} actions={Count(Match, "receivedActions")} melee={Count(Match, "remoteMelee")} views={Count(Match.World, "views")}";

    [MethodImpl(MethodImplOptions.NoInlining)]
    public WeakReference[] Cycle(int id, ActorKind kind, int owner = 2)
    {
        var actor = Add(id, kind, owner);
        var runtime = actor.Runtime;
        var refs = new[] { new WeakReference(actor), new WeakReference(runtime), new WeakReference(Field(Match, "bots")) };
        // Attachments are inspected without requiring a candidate-only API on the parent.
        if (Match.Session.GetType().GetField("registrations", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(Match.Session) is IDictionary registrations)
            refs[2] = new WeakReference(registrations[runtime]!);
        else refs = refs.Take(2).ToArray();
        actor.TreeExiting += () => TestRunner.Assert(Match.Session.Actor(id) == null, "native free preceded roster removal");
        Remove(actor);
        TestRunner.Assert(!GodotObject.IsInstanceValid(actor), "removal left the native character alive");
        return refs;
    }
    public void Dispose() { client.Dispose(); server.Dispose(); }
}
