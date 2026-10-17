using System.Collections.Generic;
using Propane.Core;
using Propane.Net;

namespace Propane.Dev.UnitTests.Fixtures;

/// <summary>
/// A LobbyServer driven by hand: bytes go in through its production receive handler, every packet it sends is decoded and
/// recorded, and time moves only when a test says so. No sockets, ports or wall-clock scheduling. The caller builds the
/// server (its identity and defaults differ per fixture) with <paramref name="clock"/>.
/// </summary>
internal sealed class ServerDriver
{
    public readonly ManualClock Clock;
    public readonly LobbyServer Server;
    /// <summary>The packets sent since the last clear.</summary>
    public readonly List<(int Peer, NetMessage Message)> Packets = new();
    /// <summary>Every packet sent, kept only when asked for: the lobby world fixture looks lobby states, starts and actors up here.</summary>
    public readonly List<(int Peer, NetMessage Message)>? All;

    public ServerDriver(ManualClock clock, LobbyServer server, bool keepAll = false)
    {
        Clock = clock;
        Server = server;
        if (keepAll) All = new();
        Server.SendOverride = (peer, bytes, _) =>
        {
            var message = MessageCodec.Decode(bytes, true);
            Packets.Add((peer, message));
            All?.Add((peer, message));
        };
    }

    public void Connect(int peer, int colorIndex, bool debug)
    {
        Server.OnConnected(peer);
        Receive(Handshake.Hello(peer, colorIndex, debug), peer);
    }

    public void CreateLobby(int peer = 2) => Receive(new NetWriter(Msg.CreateLobby), peer);

    public void Join(int peer, int lobby = 1) => Receive(new NetWriter(Msg.JoinLobby).Int(lobby), peer);

    public void StartMatch(Tuning tuning, int peer = 2) => Receive(new NetWriter(Msg.StartMatch).Snapshot(tuning.Snapshot()), peer);

    public void Receive(NetWriter packet, int peer = 2) => Receive(packet.ToArray(), peer);

    public void Receive(byte[] packet, int peer = 2) => Server.OnReceived(peer, packet);

    /// <summary>Move the clock on and run the phase tick, as one process frame does.</summary>
    public void Advance(double seconds = 0)
    {
        Clock.Seconds += seconds;
        Server.AdvanceMatches(Clock.Seconds);
    }
}
