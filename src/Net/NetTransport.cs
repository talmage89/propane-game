using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Godot;

namespace Propane.Net;

/// <summary>ENet adapter. Control uses channel zero; reliable body checkpoints use three independent ordered channels.</summary>
[SuppressMessage("Design", "CA1001", Justification = "Close releases the ENet connection; the RefCounted peer remains reusable for reconnects.")]
public sealed class NetTransport : IPacketTransport
{
    private const int ServerId = 1;
    private readonly ENetMultiplayerPeer peer = new();
    private readonly ProtocolPeerMap peerIds;
    private readonly Dictionary<int, ConnectionHandle> connections = new();
    private readonly Dictionary<ConnectionHandle, int> backendIds = new();
    private readonly HashSet<ConnectionHandle> connected = new();
    private readonly Dictionary<ConnectionHandle, PacketCounters> counters = new();
    private readonly Queue<(ConnectionHandle Connection, DisconnectReason Reason)> disconnected = new();
    private readonly LoadingGrace grace = new();
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
            peer.GetPeer((int)id)?.SetTimeout(32, 3000, 8000);
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
        backendIds.Add(connection, id);
        peerIds.Issue(connection);
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

    internal static int Channel(TrafficClass traffic) => traffic switch
    {
        TrafficClass.ReliableBodyStates => 1,
        TrafficClass.ReliableBodyStates2 => 2,
        TrafficClass.ReliableBodyStates3 => 3,
        _ => 0,
    };

    public void SetLoading(ConnectionHandle connection, bool loading)
    {
        if (!connected.Contains(connection)) { return; }
        // Both ends need the longer minimum while the client constructs its world. Once it
        // reports ready, live players should not linger for the entire construction grace,
        // but the grace outlasts readiness long enough for ENet to retry what loading delayed.
        if (loading)
        {
            grace.Begin(connection, Now);
            SetTimeout(connection, loading: true);
        }
        else if (grace.End(connection, Now)) { SetTimeout(connection, loading: false); }
    }

    private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    private void SetTimeout(ConnectionHandle connection, bool loading)
    {
        peer.GetPeer(backendIds[connection])?.SetTimeout(32, loading ? 20000 : 3000, loading ? 30000 : 8000);
    }

    private static TrafficClass ReliableTraffic(int channel) => channel switch
    {
        1 => TrafficClass.ReliableBodyStates,
        2 => TrafficClass.ReliableBodyStates2,
        3 => TrafficClass.ReliableBodyStates3,
        _ => TrafficClass.ReliableOrdered,
    };

    public SendResult Send(ConnectionHandle connection, TrafficClass traffic, ReadOnlyMemory<byte> bytes)
    {
        if (!open || !connected.Contains(connection)) { return SendResult.Closed; }
        if (bytes.Length > MaxPayloadSize) { return SendResult.Oversized; }
        var id = backendIds[connection];
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
                foreach (var connection in grace.Expire(Now))
                {
                    if (connected.Contains(connection)) { SetTimeout(connection, loading: false); }
                }
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
                    Packet?.Invoke(connection, reliable ? ReliableTraffic(channel) :
                        bytes.Length > 0 && bytes[0] == (byte)Msg.BodyStates ? TrafficClass.UnreliableBodyStates : TrafficClass.UnreliablePlayerPoses, bytes);
                }
            }
            // Snapshot: a handler may close/reconnect; new notifications belong to the next poll.
            var count = disconnected.Count;
            while (count-- > 0 && disconnected.TryDequeue(out var item))
            {
                try { Disconnected?.Invoke(item.Connection, item.Reason); }
                finally { peerIds.Forget(item.Connection); backendIds.Remove(item.Connection); counters.Remove(item.Connection); }
            }
        }
        finally { pollMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds; }
    }

    public void Disconnect(ConnectionHandle connection, DisconnectReason reason)
    {
        if (!connections.ContainsValue(connection)) { return; }
        var id = backendIds[connection];
        // Keep the supplied reason locally. The remote application rejection travels in Msg.Rejected unchanged.
        if (open) { peer.DisconnectPeer(id); }
        Retire(connection, reason);
    }

    private void Retire(ConnectionHandle connection, DisconnectReason reason)
    {
        connections.Remove(backendIds[connection]);
        connected.Remove(connection);
        grace.Forget(connection);
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
        var target = open && connected.Contains(connection) ? peer.GetPeer(backendIds[connection]) : null;
        return stats.Snapshot(pollMs, rtt: target?.GetStatistic(ENetPacketPeer.PeerStatistic.RoundTripTime) ?? 0,
            nativeLoss: target == null ? null : target.GetStatistic(ENetPacketPeer.PeerStatistic.PacketLoss) / ENetPacketPeer.PacketLossScale) with
        {
            NativeThrottle = target?.GetStatistic(ENetPacketPeer.PeerStatistic.PacketThrottle) / ENetPacketPeer.PacketThrottleScale,
            NativeThrottleLimit = target?.GetStatistic(ENetPacketPeer.PeerStatistic.PacketThrottleLimit) / ENetPacketPeer.PacketThrottleScale,
            NativeRttVariance = target?.GetStatistic(ENetPacketPeer.PeerStatistic.RoundTripTimeVariance)
        };
    }
}

/// <summary>
/// When each connection's loading grace ends. ENet doubles a reliable command's retransmission timeout on every
/// unacknowledged send, without a cap. While a loading client does not service its socket, commands it loses (its
/// receive buffer overflows) back off to retry intervals as long as the time spent loading. Their next retry can then
/// come after readiness, where the established timeouts measure the interval since the command's last send and
/// declare the peer lost instead of resending. So the grace outlasts readiness by the time spent loading: a command
/// first sent while loading waits at most that long, plus its first retransmission timeout, for its next retry.
/// </summary>
internal sealed class LoadingGrace
{
    /// <summary>Seconds that cover a command's first retransmission timeout: a round trip plus four deviations.</summary>
    internal const double RetryMargin = 1;
    private readonly Dictionary<ConnectionHandle, double> started = new();
    private readonly Dictionary<ConnectionHandle, double> ends = new();

    /// <summary>Loading begins; this cancels a pending release from an earlier load.</summary>
    public void Begin(ConnectionHandle connection, double now)
    {
        started.TryAdd(connection, now);
        ends.Remove(connection);
    }

    /// <summary>Readiness, or leaving construction. True when the established timeouts apply now.</summary>
    public bool End(ConnectionHandle connection, double now)
    {
        if (started.Remove(connection, out var since))
        {
            ends[connection] = now + (now - since) + RetryMargin;
            return false;
        }
        return !ends.ContainsKey(connection);
    }

    /// <summary>Connections whose grace has run out by <paramref name="now"/>; each is reported once.</summary>
    public IReadOnlyList<ConnectionHandle> Expire(double now)
    {
        if (ends.Count == 0) { return Array.Empty<ConnectionHandle>(); }
        var due = new List<ConnectionHandle>();
        foreach (var (connection, end) in ends)
        {
            if (now >= end) { due.Add(connection); }
        }
        foreach (var connection in due) { ends.Remove(connection); }
        return due;
    }

    public void Forget(ConnectionHandle connection)
    {
        started.Remove(connection);
        ends.Remove(connection);
    }
}
