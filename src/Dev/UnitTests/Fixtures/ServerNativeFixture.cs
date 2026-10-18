using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Net;

namespace Propane.Dev.UnitTests.Fixtures;

/// <summary>Real ENet sockets and codec, with manual run time and two clients that acknowledge construction.</summary>
internal sealed class ServerNativeFixture : IDisposable
{
    public readonly ManualClock Clock = new();
    public readonly LobbyServer Server;
    public readonly List<ServerRunFixture.Release> Runs = new();
    private readonly List<(IPacketTransport Wire, ConnectionHandle Connection)> clients = new();
    public readonly List<(int Client, NetMessage Message)> Packets = new();
    public readonly Tuning Tuning = new() { CountdownTime = 0, ResultsTime = 1, LobbyStageTime = 0 };

    public ServerNativeFixture(Func<ParticipantRoster> roster)
    {
        var port = (int)DevArgs.GetFloat("port", Protocol.DefaultPort + 1);
        Server = Propane.Modes.ModeRegistry.CreateServer(PacketTransports.CreateServer(), Clock);
        Server.Port = port;
        Server.Worlds = true;
        Server.RosterFactory = roster;
        Server.RunCreated = run => Runs.Add(new ServerRunFixture.Release(Server, run));
        TestRunner.Assert(Server.Start().Success, "native retirement server failed to bind");
        for (var i = 0; i < 2; i++)
        {
            var index = i;
            var wire = PacketTransports.CreateClient();
            wire.Connected += connection => TestRunner.Assert(wire.Send(connection, TrafficClass.ReliableOrdered,
                Handshake.Hello(index + 2, index, true).ToArray()) == SendResult.Success, "native Hello failed");
            wire.Packet += (_, _, bytes) => Packets.Add((index, MessageCodec.Decode(bytes.ToArray(), true)));
            TestRunner.Assert(wire.Connect(new PacketEndpoint("127.0.0.1", port), out var connection).Success && connection != null, "native client failed to connect");
            clients.Add((wire, connection!));
        }
        Wait(() => Packets.Count(p => p.Message.Type == Msg.Welcome) == 2);
        Send(0, new NetWriter(Msg.CreateLobby));
        Wait(() => Packets.Any(p => p.Client == 0 && p.Message.Type == Msg.LobbyState));
        var lobby = Packets.Where(p => p.Client == 0).Select(p => p.Message.Payload).OfType<LobbyInfo>().Last();
        Send(1, new NetWriter(Msg.JoinLobby).Int(lobby.Id));
        Wait(() => Packets.Any(p => p.Client == 1 && p.Message.Payload is LobbyInfo { Members.Count: 2 }));
    }

    public void Send(int client, NetWriter bytes) => TestRunner.Assert(clients[client].Wire.Send(clients[client].Connection,
        TrafficClass.ReliableOrdered, bytes.ToArray()) == SendResult.Success, "native send failed");

    public void Wait(Func<bool> complete)
    {
        var watch = Stopwatch.StartNew();
        while (!complete() && watch.Elapsed.TotalSeconds < 10)
        {
            Server._Process(0);
            foreach (var (wire, _) in clients) wire.Poll();
            OS.DelayMsec(1);
        }
        TestRunner.Assert(complete(), $"native retirement deadline expired at {Clock.Seconds}: " +
            string.Join(",", Packets.Select(p => $"{p.Client}/{p.Message.Type}/{p.Message.MatchId}")));
    }

    public void Start()
    {
        Send(0, new NetWriter(Msg.SetMatchLength).Float(10));
        Send(0, new NetWriter(Msg.StartMatch).Snapshot(Tuning.Snapshot()));
        Wait(() => Packets.Count(p => p.Message.Payload is MatchSetup s && s.ModeId != Propane.Modes.ModeRegistry.LobbyWorldId) == 2);
        var setup = Packets.Select(p => p.Message.Payload).OfType<MatchSetup>().Last();
        TestRunner.Assert(setup.Length == 10, $"native setup length {setup.Length}");
        var id = Runs.Last().Run.Id;
        for (var i = 0; i < clients.Count; i++) Send(i, new NetWriter(Msg.Loaded, id));
        Wait(() => Packets.Count(p => p.Message.MatchId == id && p.Message.Type == Msg.MatchGo) == 2);
    }

    public void Dispose()
    {
        foreach (var (wire, _) in clients) wire.Close();
        Server._ExitTree();
        Server.Free();
        Tuning.Dispose();
    }
}
