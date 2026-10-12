using System;
using System.Collections.Generic;

namespace Propane.Net;

/// <summary>
/// One match's ordered handoff from the connection to its fully initialized scene. Starting another match or
/// leaving invalidates the old receiver and queue together. No process-global state or frame-count assumption.
/// </summary>
public sealed class MatchInbox
{
    internal const int MaxPackets = 4096;
    internal const int MaxBytes = 4 << 20;
    public sealed class OverflowException() : Exception("Loading the match exceeded the message buffer. Please reconnect.");

    private readonly Queue<(NetMessage Message, int Bytes)> pending = new();
    private Action<NetMessage>? receiver;
    private int pendingBytes;

    public int MatchId { get; private set; }
    public int PeakPackets { get; private set; }
    public int PeakBytes { get; private set; }
    public int Overflows { get; private set; }

    public void Begin(int id)
    {
        if (id <= 0) { throw new ArgumentOutOfRangeException(nameof(id)); }
        Clear();
        MatchId = id;
        PeakPackets = PeakBytes = Overflows = 0;
    }

    public void Clear()
    {
        MatchId = 0;
        receiver = null;
        pending.Clear();
        pendingBytes = 0;
    }

    public void Receive(NetMessage message, int wireBytes)
    {
        if (MatchId == 0 || message.MatchId != MatchId) { return; }
        if (receiver != null)
        {
            receiver(message);
            return;
        }
        if (wireBytes < 0 || pending.Count >= MaxPackets || wireBytes > MaxBytes - pendingBytes) { Overflows++; throw new OverflowException(); }
        pending.Enqueue((message, wireBytes));
        pendingBytes += wireBytes;
        PeakPackets = Math.Max(PeakPackets, pending.Count);
        PeakBytes = Math.Max(PeakBytes, pendingBytes);
    }

    /// <summary>Called only after the scene has all bodies, UI and callbacks. Replays each queued event once.</summary>
    public bool Attach(int id, Action<NetMessage> onMessage)
    {
        if (id != MatchId || id == 0) { return false; }
        if (receiver != null && receiver != onMessage) { throw new InvalidOperationException("match already has a receiver"); }
        receiver = onMessage;
        while (MatchId == id && receiver == onMessage && pending.TryDequeue(out var packet))
        {
            pendingBytes -= packet.Bytes;
            onMessage(packet.Message);
        }
        return true;
    }

    /// <summary>An old scene exiting cannot clear a newer match's pending events.</summary>
    public void Detach(int id, Action<NetMessage> onMessage)
    {
        if (MatchId == id && receiver == onMessage) { Clear(); }
    }
}
