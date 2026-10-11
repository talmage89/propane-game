using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Godot;

namespace Propane.Net;

/// <summary>ENet adapter. Control uses channel zero; reliable body checkpoints use an independent ordered channel.</summary>
[SuppressMessage("Design", "CA1001", Justification = "Close releases the ENet connection; the RefCounted peer remains reusable for reconnects.")]
public sealed class NetTransport : IPacketTransport
{
    private const int ServerId = 1;
    private readonly ENetMultiplayerPeer peer = new();
    private readonly ProtocolPeerMap peerIds;
    private readonly Dictionary<int, ConnectionHandle> connections = new();
    private readonly HashSet<ConnectionHandle> connected = new();
    private readonly Dictionary<ConnectionHandle, PacketCounters> counters = new();
    private readonly Queue<(ConnectionHandle Connection, DisconnectReason Reason)> disconnected = new();
    private bool open;
    private bool server;
    private double pollMs;

    public event Action<ConnectionHandle>? Connected;
    public event Action<ConnectionHandle, DisconnectReason>? Disconnected;
    public event Action<ConnectionHandle, TrafficClass, ReadOnlyMemory<byte>>? Packet;
    // Godot ENetMultiplayerPeer advertises 16 MiB internally (not exposed by its C# API).
    public int MaxPayloadSize => 1 << 24;

    public NetTransport(ProtocolPeerMap peerIds)
    {
        this.peerIds = peerIds;
        peer.PeerConnected += id =>
        {
            // A healthy builder may block its main thread for the server's 20 s loading grace.
            // Configure both ends: ENet's RTT multiplier can trip its minimum before the maximum.
            peer.GetPeer((int)id)?.SetTimeout(32, 20000, 30000);
            var connection = connections.GetValueOrDefault((int)id) ?? Add((int)id);
            connected.Add(connection);
            Connected?.Invoke(connection);
        };
        peer.PeerDisconnected += id =>
        {
            if (connections.TryGetValue((int)id, out var connection))
            {
                // Godot's ENetMultiplayerPeer discards ENet event.data and exposes no timeout cause.
                Retire(connection, new DisconnectReason(DisconnectKind.TransportFailure,
                    server ? "Lost the connection to the client. ENet did not supply a disconnect reason." :
                    "Lost the connection to the server. ENet did not supply a disconnect reason."));
            }
        };
    }

    private ConnectionHandle Add(int id)
    {
        var connection = new ConnectionHandle();
        connections.Add(id, connection);
        counters.Add(connection, new PacketCounters());
        peerIds.Bind(connection, id);
        return connection;
    }

    public OpenResult Listen(PacketEndpoint endpoint, int maxConnections)
    {
        Close();
        peerIds.Clear();
        peer.SetBindIP(string.IsNullOrEmpty(endpoint.Address) ? "*" : endpoint.Address);
        var error = peer.CreateServer(endpoint.Port, maxConnections);
        server = true;
        open = error == Error.Ok;
        return new OpenResult(open, open ? null : error.ToString(), (int)error);
    }

    public OpenResult Connect(PacketEndpoint endpoint, out ConnectionHandle? connection)
    {
        Close();
        peerIds.Clear();
        var error = peer.CreateClient(endpoint.Address, endpoint.Port);
        server = false;
        open = error == Error.Ok;
        connection = open ? Add(ServerId) : null;
        return new OpenResult(open, open ? null : error.ToString(), (int)error);
    }

    internal static int Channel(TrafficClass traffic) => traffic == TrafficClass.ReliableBodyStates ? 1 : 0;

    public SendResult Send(ConnectionHandle connection, TrafficClass traffic, ReadOnlyMemory<byte> bytes)
    {
        if (!open || !connected.Contains(connection)) { return SendResult.Closed; }
        if (bytes.Length > MaxPayloadSize) { return SendResult.Oversized; }
        var id = peerIds.PeerId(connection);
        if (peer.GetPeer(id) is not { } target || target.GetState() != ENetPacketPeer.PeerState.Connected) { return SendResult.Closed; }
        peer.SetTargetPeer(id);
        peer.TransferChannel = Channel(traffic);
        peer.TransferMode = traffic.IsReliable() ? MultiplayerPeer.TransferModeEnum.Reliable : MultiplayerPeer.TransferModeEnum.Unreliable;
        var error = peer.PutPacket(bytes.ToArray());
        // Godot exposes no native send-buffer limit. Admission errors it does return still reject the send.
        if (error != Error.Ok) { return error is Error.Unconfigured or Error.InvalidParameter ? SendResult.Closed : SendResult.Backpressure; }
        var stats = counters[connection];
        stats.PacketsOut++;
        stats.BytesOut += bytes.Length;
        return SendResult.Success;
    }

    public void Poll()
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            if (open)
            {
                peer.Poll();
                if (peer.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Disconnected)
                {
                    // Connecting failures do not emit PeerDisconnected in Godot. Established losses were retired above.
                    foreach (var connection in new List<ConnectionHandle>(connections.Values))
                    {
                        Retire(connection, new DisconnectReason(server ? DisconnectKind.TransportFailure : DisconnectKind.Timeout,
                            server ? "ENet stopped listening." : "The server did not answer. Check the address, the port and that the server is running."));
                    }
                    open = false;
                }
                while (open && peer.GetAvailablePacketCount() > 0)
                {
                    var id = peer.GetPacketPeer();
                    var reliable = peer.GetPacketMode() == MultiplayerPeer.TransferModeEnum.Reliable;
                    var channel = peer.GetPacketChannel();
                    var bytes = peer.GetPacket();
                    if (!connections.TryGetValue(id, out var connection) || !connected.Contains(connection)) { continue; }
                    var stats = counters[connection];
                    stats.PacketsIn++;
                    stats.BytesIn += bytes.Length;
                    Packet?.Invoke(connection, reliable ? channel == 1 ? TrafficClass.ReliableBodyStates : TrafficClass.ReliableOrdered :
                        bytes.Length > 0 && bytes[0] == (byte)Msg.BodyStates ? TrafficClass.UnreliableBodyStates : TrafficClass.UnreliablePlayerPoses, bytes);
                }
            }
            // Snapshot: a handler may close/reconnect; new notifications belong to the next poll.
            var count = disconnected.Count;
            while (count-- > 0 && disconnected.TryDequeue(out var item))
            {
                try { Disconnected?.Invoke(item.Connection, item.Reason); }
                finally { peerIds.Forget(item.Connection); counters.Remove(item.Connection); }
            }
        }
        finally { pollMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds; }
    }

    public void Disconnect(ConnectionHandle connection, DisconnectReason reason)
    {
        if (!connections.ContainsValue(connection)) { return; }
        var id = peerIds.PeerId(connection);
        // Keep the supplied reason locally. The remote application rejection travels in Msg.Rejected unchanged.
        if (open) { peer.DisconnectPeer(id); }
        Retire(connection, reason);
    }

    private void Retire(ConnectionHandle connection, DisconnectReason reason)
    {
        connections.Remove(peerIds.PeerId(connection));
        connected.Remove(connection);
        peerIds.Retire(connection);
        disconnected.Enqueue((connection, reason));
    }

    public void Close()
    {
        foreach (var connection in new List<ConnectionHandle>(connections.Values)) { Retire(connection, DisconnectReason.LocalClose); }
        if (open) { open = false; peer.Close(); }
    }

    public TransportDiagnostics Diagnostics(ConnectionHandle connection)
    {
        if (!counters.TryGetValue(connection, out var stats)) { return default; }
        var target = open && connected.Contains(connection) ? peer.GetPeer(peerIds.PeerId(connection)) : null;
        return stats.Snapshot(pollMs, rtt: target?.GetStatistic(ENetPacketPeer.PeerStatistic.RoundTripTime) ?? 0,
            nativeLoss: target == null ? null : target.GetStatistic(ENetPacketPeer.PeerStatistic.PacketLoss) / ENetPacketPeer.PacketLossScale) with
        {
            NativeThrottle = target?.GetStatistic(ENetPacketPeer.PeerStatistic.PacketThrottle) / ENetPacketPeer.PacketThrottleScale,
            NativeThrottleLimit = target?.GetStatistic(ENetPacketPeer.PeerStatistic.PacketThrottleLimit) / ENetPacketPeer.PacketThrottleScale,
            NativeRttVariance = target?.GetStatistic(ENetPacketPeer.PeerStatistic.RoundTripTimeVariance)
        };
    }
}
