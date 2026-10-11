using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Propane.Net;

namespace Propane.Dev.UnitTests;

/// <summary>Deterministic message sockets for tests. No threads, ports, static registry or wall-clock scheduling.</summary>
internal sealed class LoopbackNetwork
{
    internal readonly Dictionary<PacketEndpoint, LoopbackTransport> Listeners = new();
    internal int NextPeer = 2;
    public LoopbackTransport Create(int maxPayload = 512 * 1024, int queuePackets = 4096, int queueBytes = 4 << 20) =>
        new(this, maxPayload, queuePackets, queueBytes);
}

internal sealed class LoopbackTransport : IPacketTransport
{
    private sealed class Link(LoopbackTransport remote, ConnectionHandle remoteHandle)
    {
        public readonly LoopbackTransport Remote = remote;
        public readonly ConnectionHandle RemoteHandle = remoteHandle;
        public bool Ready;
    }
    private sealed record Notification(ConnectionHandle Handle, TrafficClass Traffic = default, byte[]? Data = null,
        DisconnectReason? Reason = null);
    private readonly LoopbackNetwork network;
    private readonly Dictionary<ConnectionHandle, Link> links = new();
    private readonly Dictionary<ConnectionHandle, PacketCounters> counters = new();
    private readonly Queue<Notification> notifications = new();
    private readonly int maxPackets, maxBytes;
    private PacketEndpoint? listening;
    private int capacity;
    private double pollMs;
    public ProtocolPeerMap PeerIds { get; } = new();
    public bool BlockSends { get; set; }
    public TrafficClass? BlockTraffic { get; set; }
    public int MaxPayloadSize { get; }
    public event Action<ConnectionHandle>? Connected;
    public event Action<ConnectionHandle, DisconnectReason>? Disconnected;
    public event Action<ConnectionHandle, TrafficClass, ReadOnlyMemory<byte>>? Packet;

    internal LoopbackTransport(LoopbackNetwork network, int maxPayload, int maxPackets, int maxBytes)
    {
        this.network = network;
        MaxPayloadSize = maxPayload;
        this.maxPackets = maxPackets;
        this.maxBytes = maxBytes;
    }
    public OpenResult Listen(PacketEndpoint endpoint, int maxConnections)
    {
        Close();
        if (network.Listeners.ContainsKey(endpoint)) { return new OpenResult(false, "Address in use.", 1); }
        PeerIds.Clear();
        network.Listeners.Add(endpoint, this);
        listening = endpoint;
        capacity = maxConnections;
        return new OpenResult(true);
    }
    public OpenResult Connect(PacketEndpoint endpoint, out ConnectionHandle? connection)
    {
        Close();
        PeerIds.Clear();
        connection = null;
        if (!network.Listeners.TryGetValue(endpoint, out var remote)) { return new OpenResult(false, "No listener.", 2); }
        if (remote.links.Count >= remote.capacity) { return new OpenResult(false, "Listener full.", 3); }
        connection = new ConnectionHandle();
        var remoteHandle = new ConnectionHandle();
        links.Add(connection, new Link(remote, remoteHandle));
        remote.links.Add(remoteHandle, new Link(this, connection));
        counters.Add(connection, new PacketCounters());
        remote.counters.Add(remoteHandle, new PacketCounters());
        PeerIds.Bind(connection, 1);
        remote.PeerIds.Bind(remoteHandle, network.NextPeer++);
        notifications.Enqueue(new Notification(connection));
        remote.notifications.Enqueue(new Notification(remoteHandle));
        return new OpenResult(true);
    }
    public SendResult Send(ConnectionHandle connection, TrafficClass traffic, ReadOnlyMemory<byte> bytes)
    {
        if (!links.TryGetValue(connection, out var link) || !link.Ready) { return SendResult.Closed; }
        if (bytes.Length > MaxPayloadSize) { return SendResult.Oversized; }
        var remoteQueue = link.Remote.notifications.Where(n => n.Handle == link.RemoteHandle && n.Data != null).ToArray();
        if (BlockSends || BlockTraffic == traffic || remoteQueue.Length >= link.Remote.maxPackets || bytes.Length > link.Remote.maxBytes - remoteQueue.Sum(n => n.Data!.Length))
        {
            return SendResult.Backpressure;
        }
        var stats = counters[connection];
        stats.PacketsOut++;
        stats.BytesOut += bytes.Length;
        link.Remote.notifications.Enqueue(new Notification(link.RemoteHandle, traffic, bytes.ToArray()));
        return SendResult.Success;
    }
    public void Poll()
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var count = notifications.Count;
            while (count-- > 0 && notifications.TryDequeue(out var n))
            {
                if (n.Reason != null)
                {
                    try { Disconnected?.Invoke(n.Handle, n.Reason); }
                    finally { PeerIds.Forget(n.Handle); counters.Remove(n.Handle); }
                }
                else if (links.TryGetValue(n.Handle, out var link))
                {
                    if (n.Data == null) { link.Ready = true; Connected?.Invoke(n.Handle); }
                    else
                    {
                        var stats = counters[n.Handle];
                        stats.PacketsIn++;
                        stats.BytesIn += n.Data.Length;
                        try { Packet?.Invoke(n.Handle, n.Traffic, n.Data); }
                        finally { Array.Fill(n.Data, (byte)0xdd); } // Prove consumers obey the borrowed lifetime.
                    }
                }
            }
        }
        finally { pollMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds; }
    }
    public void Disconnect(ConnectionHandle connection, DisconnectReason reason)
    {
        if (!links.Remove(connection, out var link)) { return; }
        PeerIds.Retire(connection);
        notifications.Enqueue(new Notification(connection, Reason: reason));
        link.Remote.links.Remove(link.RemoteHandle);
        link.Remote.PeerIds.Retire(link.RemoteHandle);
        link.Remote.notifications.Enqueue(new Notification(link.RemoteHandle, Reason: reason));
    }
    public void Close()
    {
        foreach (var connection in links.Keys.ToArray()) { Disconnect(connection, DisconnectReason.LocalClose); }
        if (listening is { } endpoint) { network.Listeners.Remove(endpoint); listening = null; }
    }
    public TransportDiagnostics Diagnostics(ConnectionHandle connection)
    {
        var queue = notifications.Where(n => n.Handle == connection && n.Data != null).ToArray();
        return counters.TryGetValue(connection, out var stats) ? stats.Snapshot(pollMs, queue.Length, queue.Sum(n => (long)n.Data!.Length)) : default;
    }
}
