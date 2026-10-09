using System.Collections.Generic;
using Godot;
using Propane.Core;
using Propane.Player;

namespace Propane.Net;

/// <summary>
/// Another player's recent states, played back a little behind real time so their motion stays smooth through
/// uneven packet arrival. Times are the sender's clock; the buffer learns the offset to the local clock from the
/// fastest packets and shows each player <see cref="Tuning.InterpolationDelay"/> behind that.
/// </summary>
public sealed class SnapshotBuffer
{
    private const int MaxSnapshots = 64;
    private const double MaxExtrapolation = 0.2;

    private readonly List<(double Time, PlayerNetState State)> snapshots = new();
    private double offset;
    private bool hasOffset;

    public bool HasData => snapshots.Count > 0;

    public void Add(double senderTime, PlayerNetState state, double localNow)
    {
        if (snapshots.Count > 0 && senderTime <= snapshots[^1].Time)
        {
            // Out of order or duplicate: unreliable packets can arrive late.
            return;
        }
        snapshots.Add((senderTime, state));
        if (snapshots.Count > MaxSnapshots)
        {
            snapshots.RemoveAt(0);
        }
        // Track the quickest delivery seen; drift slowly toward slower ones if the route gets longer.
        var sample = senderTime - localNow;
        if (!hasOffset || sample > offset)
        {
            offset = sample;
            hasOffset = true;
        }
        else
        {
            offset += (sample - offset) * 0.01;
        }
    }

    /// <summary>The state to show now.</summary>
    public PlayerNetState Sample(double localNow)
    {
        var renderTime = localNow + offset - Tuning.Current.InterpolationDelay;
        var count = snapshots.Count;
        if (count == 1 || renderTime <= snapshots[0].Time)
        {
            return snapshots[0].State;
        }
        var last = snapshots[count - 1];
        if (renderTime >= last.Time)
        {
            // Late packets: carry on along the last velocity for a moment.
            var extra = (float)System.Math.Min(renderTime - last.Time, MaxExtrapolation);
            var s = last.State;
            if (s.State == PlayerNetState.Mode.Active)
            {
                s.Position += s.Velocity * extra;
            }
            s.PelvisPosition += s.PelvisVelocity * extra;
            return s;
        }
        for (var i = count - 2; i >= 0; i--)
        {
            if (snapshots[i].Time <= renderTime)
            {
                var a = snapshots[i];
                var b = snapshots[i + 1];
                var t = (float)((renderTime - a.Time) / (b.Time - a.Time));
                // Drop what can no longer be needed.
                if (i > 4)
                {
                    snapshots.RemoveRange(0, i - 4);
                }
                return PlayerNetState.Lerp(a.State, b.State, t);
            }
        }
        return snapshots[0].State;
    }

    public void Clear()
    {
        snapshots.Clear();
        hasOffset = false;
    }
}
