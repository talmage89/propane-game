using System;

namespace Propane.Net;

/// <summary>Backend-owned identity. Reference equality; a reconnect always gets a fresh handle.</summary>
public sealed class ConnectionHandle;

public enum TrafficClass
{
    ReliableOrdered,
    // Includes disposable ping/clock samples, which also used ENet's unreliable channel zero.
    UnreliablePlayerPoses,
    UnreliableBodyStates,
}

public enum SendResult { Success, Closed, Oversized, Backpressure }
public enum DisconnectKind { ApplicationRejected, Timeout, TransportFailure, LocalClose }

/// <summary>BackendCode is null when the backend cannot supply one; Detail is safe to show in the connection UI.</summary>
public sealed record DisconnectReason(DisconnectKind Kind, string Detail, int? BackendCode = null)
{
    public static readonly DisconnectReason LocalClose = new(DisconnectKind.LocalClose, "Connection closed.");
}

/// <summary>Address is interpreted by the backend (ENet: hostname/IP). Discovery and invites are separate services.</summary>
public readonly record struct PacketEndpoint(string Address, int Port);
public readonly record struct OpenResult(bool Success, string? Detail = null, int? BackendCode = null);

/// <summary>
/// Payload counts, not UDP frames/retransmissions. Queue counts are managed packets; native queue bytes and loss
/// are nullable when unavailable. PollMilliseconds is the last whole transport poll, shared by its connections.
/// </summary>
public readonly record struct TransportDiagnostics(long PacketsIn, long BytesIn, long PacketsOut, long BytesOut,
    double RoundTripMilliseconds, int QueuedPackets, long QueuedBytes, long Drops, double PollMilliseconds,
    long? NativeQueueBytes = null, double? NativeLoss = null);

/// <summary>
/// Main-thread message transport. Every callback runs only inside Poll (including local disconnect/close).
/// Send copies/consumes bytes before returning Success; Success means accepted, not delivered. A non-success
/// send accepts nothing. Packet memory is borrowed for the duration of the callback: copy it before retaining.
/// ReliableOrdered preserves send order per connection; the other classes may reorder or disappear. No ordering
/// is promised across classes. Disconnect/Close invalidate handles immediately and discard queued packets;
/// notifications are deferred to Poll. Diagnostics remain available through Disconnected, then expire.
/// </summary>
public interface IPacketTransport
{
    event Action<ConnectionHandle>? Connected;
    event Action<ConnectionHandle, DisconnectReason>? Disconnected;
    event Action<ConnectionHandle, TrafficClass, ReadOnlyMemory<byte>>? Packet;
    int MaxPayloadSize { get; }
    OpenResult Listen(PacketEndpoint endpoint, int maxConnections);
    OpenResult Connect(PacketEndpoint endpoint, out ConnectionHandle? connection);
    void Poll();
    SendResult Send(ConnectionHandle connection, TrafficClass traffic, ReadOnlyMemory<byte> bytes);
    void Disconnect(ConnectionHandle connection, DisconnectReason reason);
    void Close();
    TransportDiagnostics Diagnostics(ConnectionHandle connection);
}

public interface IMonotonicClock
{
    double Seconds { get; }
}

public sealed class MonotonicClock : IMonotonicClock
{
    public static readonly MonotonicClock Instance = new();
    // Keep the existing process-relative wire clock; it is independent of Engine.TimeScale.
    public static double Now => Godot.Time.GetTicksUsec() / 1_000_000.0;
    public double Seconds => Now;
}

internal sealed class PacketCounters
{
    public long PacketsIn, BytesIn, PacketsOut, BytesOut, Drops;
    public TransportDiagnostics Snapshot(double pollMs, int queued = 0, long bytes = 0, double rtt = 0,
        double? nativeLoss = null) => new(PacketsIn, BytesIn, PacketsOut, BytesOut, rtt, queued, bytes, Drops, pollMs,
            NativeLoss: nativeLoss);
}
