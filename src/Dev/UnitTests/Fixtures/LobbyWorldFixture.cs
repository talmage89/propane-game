using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Modes;
using Propane.Net;
using Propane.Player;

namespace Propane.Dev.UnitTests.Fixtures;

/// <summary>
/// A real LobbyServer with lobby worlds and recorded packets, a lobby created by peer 2 and joined by any guests. Unlike
/// <see cref="ServerFixture"/> it starts no match: tests drive the world, the stage and the match start themselves.
/// </summary>
internal sealed class LobbyWorldFixture : IDisposable
{
    public readonly ManualClock Clock = new();
    public readonly LobbyServer Server;
    /// <summary>Packets since the last clear, and every packet sent (lobby states, starts and actors are looked up there).</summary>
    public readonly List<(int Peer, NetMessage Message)> Packets;
    public readonly List<(int Peer, NetMessage Message)> All;
    public readonly Tuning Start = new() { CountdownTime = 0, ResultsTime = 1, LobbyStageTime = 2 };
    private readonly ServerDriver driver;
    private readonly ActionSequence actions = new();

    public LobbyWorldFixture(params int[] guests)
    {
        Server = new LobbyServer(PacketTransports.CreateServer(), Clock) { Worlds = true };
        driver = new ServerDriver(Clock, Server, keepAll: true);
        Packets = driver.Packets;
        All = driver.All!;
        Connect(2);
        driver.CreateLobby();
        foreach (var guest in guests) { Connect(guest); Join(guest); }
    }

    public void Connect(int peer) => driver.Connect(peer, peer % Protocol.PlayerColors.Length, true);

    public void Join(int peer) => driver.Join(peer);
    public void Receive(NetWriter packet, int peer = 2) => driver.Receive(packet, peer);
    public void Receive(byte[] packet, int peer = 2) => driver.Receive(packet, peer);
    public void Advance(double seconds = 0) => driver.Advance(seconds);

    public IEnumerable<T> To<T>(int peer) => Packets.Where(p => p.Peer == peer).Select(p => p.Message.Payload).OfType<T>();
    private IEnumerable<T> Ever<T>(int peer) => All.Where(p => p.Peer == peer).Select(p => p.Message.Payload).OfType<T>();
    public LobbyInfo Lobby(int peer = 2) => Ever<LobbyInfo>(peer).Last();
    public MatchSetup World(int peer = 2) => Ever<MatchSetup>(peer).Last(s => s.ModeId == ModeRegistry.LobbyWorldId);
    public MatchSetup Match(int peer = 2) => Ever<MatchSetup>(peer).Last(s => s.ModeId != ModeRegistry.LobbyWorldId);
    public IReadOnlyList<BodyAuthority> Arrived(int peer = 2) => Ever<AuthorityMessage>(peer).Last(a => a.Request == 0 && a.Bodies.All(b => b.Rest)).Bodies;
    public PlayerProfile Actor(int peer, ActorKind kind = ActorKind.Human) =>
        Lobby(peer).Members.Last(m => m.OwnerPeerId == peer && m.Kind == kind);

    /// <summary>An actor message from a peer's actor in a run, with its next action id.</summary>
    public byte[] Act(int peer, int run, Msg type, object payload, PlayerProfile? actor = null)
    {
        actor ??= Actor(peer);
        return ActorMessages.Encode(type, run, actor.Id, actor.LifeId, actions.Next(actor.Id), payload);
    }

    public byte[] Pose(int peer, int run, PlayerProfile? actor = null) =>
        Act(peer, run, Msg.PlayerState, new PlayerStateMessage(0, new PlayerNetState { Position = Vector3.One }), actor);

    public void StartMatch(int peer = 2) => driver.StartMatch(Start, peer);

    public void Dispose() { Server.Free(); Start.Dispose(); }
}
