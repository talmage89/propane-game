using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Propane.Net;

/// <summary>Local test faults, in seconds; loss is a fraction of outgoing unreliable messages only.</summary>
public sealed record PacketFaults(double Lag = 0, double Jitter = 0, double Loss = 0, double ReliableStall = 0);

/// <summary>
/// Seeded lag in both directions. Reliable order is per connection, independently in/out. ReliableStall adds a
/// retransmission-like delay only to reliable traffic. Owns copied packets and bounded queues; no native memory
/// survives a backend callback. Queued reliable sends retry backpressure without letting later reliable sends pass.
/// </summary>
public sealed class FaultPacketTransport : IPacketTransport
{
    private sealed class State
    {
        public readonly List<Pending> Incoming = new();
        public readonly List<Pending> Outgoing = new();
        public double LastReliableIn, LastReliableOut;
        public readonly PacketCounters Counters = new();
        public bool Active = true;
        public bool Ready;
    }
    private sealed record Pending(double At, long Sequence, TrafficClass Traffic, byte[] Bytes);
    private readonly IPacketTransport inner;
    private readonly IMonotonicClock clock;
    private readonly Random random;
    private readonly PacketFaults faults;
    private readonly int maxPackets;
    private readonly int maxBytes;
    private readonly Dictionary<ConnectionHandle, State> states = new();
    private long sequence;
    private double pollMs;

    public event Action<ConnectionHandle>? Connected;
    public event Action<ConnectionHandle, DisconnectReason>? Disconnected;
    public event Action<ConnectionHandle, TrafficClass, ReadOnlyMemory<byte>>? Packet;
    public int MaxPayloadSize => Math.Min(inner.MaxPayloadSize, maxBytes);

    public FaultPacketTransport(IPacketTransport inner, IMonotonicClock clock, int seed, PacketFaults faults,
        int maxPackets = 4096, int maxBytes = 4 << 20)
    {
        if (!double.IsFinite(faults.Lag) || faults.Lag < 0 || !double.IsFinite(faults.Jitter) || faults.Jitter < 0 ||
            !double.IsFinite(faults.Loss) || faults.Loss < 0 || faults.Loss > 1 ||
            !double.IsFinite(faults.ReliableStall) || faults.ReliableStall < 0 || maxPackets <= 0 || maxBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(faults));
        }
        this.inner = inner;
        this.clock = clock;
        random = new Random(seed);
        this.faults = faults;
        this.maxPackets = maxPackets;
        this.maxBytes = maxBytes;
        inner.Connected += connection =>
        {
            if (!states.TryGetValue(connection, out var state)) { states.Add(connection, state = new State()); }
            if (state.Active) { state.Ready = true; Connected?.Invoke(connection); }
        };
        inner.Disconnected += (connection, reason) =>
        {
            Clear(connection);
            try { Disconnected?.Invoke(connection, reason); }
            finally { states.Remove(connection); }
        };
        inner.Packet += (connection, traffic, bytes) =>
        {
            if (!states.TryGetValue(connection, out var state) || !state.Active) { return; }
            state.Counters.PacketsIn++;
            state.Counters.BytesIn += bytes.Length;
            if (!Fits(state.Incoming, bytes.Length))
            {
                if (traffic == TrafficClass.ReliableOrdered)
                {
                    Disconnect(connection, new DisconnectReason(DisconnectKind.TransportFailure,
                        "The simulated connection exceeded its reliable receive buffer. Please reconnect."));
                }
                else { state.Counters.Drops++; }
                return;
            }
            state.Incoming.Add(new Pending(Delayed(traffic, ref state.LastReliableIn), ++sequence, traffic, bytes.ToArray()));
        };
    }

    public OpenResult Listen(PacketEndpoint endpoint, int maxConnections)
    {
        Close();
        return inner.Listen(endpoint, maxConnections);
    }
    public OpenResult Connect(PacketEndpoint endpoint, out ConnectionHandle? connection)
    {
        Close();
        var result = inner.Connect(endpoint, out connection);
        if (result.Success && connection != null) { states.Add(connection, new State()); }
        return result;
    }

    public SendResult Send(ConnectionHandle connection, TrafficClass traffic, ReadOnlyMemory<byte> bytes)
    {
        if (!states.TryGetValue(connection, out var state) || !state.Active || !state.Ready) { return SendResult.Closed; }
        if (bytes.Length > MaxPayloadSize) { return SendResult.Oversized; }
        if (!Fits(state.Outgoing, bytes.Length)) { return SendResult.Backpressure; }
        state.Counters.PacketsOut++;
        state.Counters.BytesOut += bytes.Length;
        if (traffic != TrafficClass.ReliableOrdered && random.NextDouble() < faults.Loss)
        {
            state.Counters.Drops++;
            return SendResult.Success;
        }
        state.Outgoing.Add(new Pending(Delayed(traffic, ref state.LastReliableOut), ++sequence, traffic, bytes.ToArray()));
        return SendResult.Success;
    }

    private bool Fits(List<Pending> queue, int bytes) => queue.Count < maxPackets && bytes <= maxBytes - queue.Sum(p => p.Bytes.Length);
    private double Delayed(TrafficClass traffic, ref double lastReliable)
    {
        var at = clock.Seconds + faults.Lag + random.NextDouble() * faults.Jitter;
        if (traffic == TrafficClass.ReliableOrdered)
        {
            at = Math.Max(at + faults.ReliableStall, lastReliable);
            lastReliable = at;
        }
        return at;
    }

    public void Poll()
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            foreach (var (connection, state) in states.ToArray())
            {
                if (!state.Active) { continue; }
                var reliableBlocked = false;
                foreach (var pending in state.Outgoing.OrderBy(p => p.At).ThenBy(p => p.Sequence).ToArray())
                {
                    if (!state.Active || pending.At > clock.Seconds) { break; }
                    if (pending.Traffic == TrafficClass.ReliableOrdered && reliableBlocked) { continue; }
                    var result = inner.Send(connection, pending.Traffic, pending.Bytes);
                    if (result == SendResult.Backpressure)
                    {
                        if (pending.Traffic == TrafficClass.ReliableOrdered) { reliableBlocked = true; }
                        else { state.Outgoing.Remove(pending); state.Counters.Drops++; }
                    }
                    else if (result == SendResult.Success) { state.Outgoing.Remove(pending); }
                    else
                    {
                        Disconnect(connection, new DisconnectReason(DisconnectKind.TransportFailure,
                            $"The transport could not send an accepted packet ({result}). Please reconnect."));
                        break;
                    }
                }
            }
            inner.Poll();
            foreach (var (connection, state) in states.ToArray())
            {
                foreach (var pending in state.Incoming.OrderBy(p => p.At).ThenBy(p => p.Sequence).ToArray())
                {
                    if (!state.Active || pending.At > clock.Seconds) { break; }
                    state.Incoming.Remove(pending);
                    Packet?.Invoke(connection, pending.Traffic, pending.Bytes);
                }
            }
        }
        finally { pollMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds; }
    }

    private void Clear(ConnectionHandle connection)
    {
        if (!states.TryGetValue(connection, out var state)) { return; }
        state.Active = false;
        state.Incoming.Clear();
        state.Outgoing.Clear();
    }
    public void Disconnect(ConnectionHandle connection, DisconnectReason reason) { Clear(connection); inner.Disconnect(connection, reason); }
    public void Close()
    {
        foreach (var connection in states.Keys) { Clear(connection); }
        inner.Close();
    }
    public TransportDiagnostics Diagnostics(ConnectionHandle connection)
    {
        var backend = inner.Diagnostics(connection);
        if (!states.TryGetValue(connection, out var state)) { return backend; }
        var queues = state.Incoming.Concat(state.Outgoing);
        return state.Counters.Snapshot(pollMs, queues.Count() + backend.QueuedPackets,
            queues.Sum(p => (long)p.Bytes.Length) + backend.QueuedBytes,
            backend.RoundTripMilliseconds + faults.Lag * 2000, backend.NativeLoss) with
        { Drops = state.Counters.Drops + backend.Drops, NativeQueueBytes = backend.NativeQueueBytes };
    }
}
