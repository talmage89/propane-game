using System;
using System.Collections.Generic;

namespace Propane.Core;

/// <summary>Simulation seconds, advanced once per world physics tick. Network authority uses its own clock.</summary>
public sealed class WorldClock : IDisposable
{
    private sealed class Pending : IDisposable
    {
        public double Due;
        public Action? Callback;
        public void Dispose() => Callback = null;
    }

    private readonly List<Pending> pending = new();
    // An active dispatch owns its snapshot until it returns, including across nested advances.
    private readonly List<List<Pending>> dueBuffers = new();
    private int dispatchDepth;
    private bool disposed;
    public double Now { get; private set; }
    public int PendingCount
    {
        get
        {
            var count = 0;
            var length = pending.Count;
            for (var i = 0; i < length; i++) { if (pending[i].Callback != null) { count++; } }
            return count;
        }
    }

    /// <summary>Cancel individually, or cancel all work when the world is invalidated.</summary>
    public IDisposable Schedule(double seconds, Action callback)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!double.IsFinite(seconds) || seconds < 0) { throw new ArgumentOutOfRangeException(nameof(seconds)); }
        var timer = new Pending { Due = Now + seconds, Callback = callback };
        pending.Add(timer);
        return timer;
    }

    public void Advance(double delta)
    {
        if (disposed) { return; }
        if (!double.IsFinite(delta) || delta < 0) { throw new ArgumentOutOfRangeException(nameof(delta)); }
        Now += delta;
        if (pending.Count == 0) { return; }
        // Snapshot due work: callbacks may schedule or cancel timers, including disposing the world.
        if (dispatchDepth == dueBuffers.Count) { dueBuffers.Add(new List<Pending>()); }
        var due = dueBuffers[dispatchDepth++];
        try
        {
            var pendingCount = pending.Count;
            for (var i = 0; i < pendingCount; i++)
            {
                var timer = pending[i];
                if (timer.Callback == null || timer.Due <= Now) { due.Add(timer); }
            }
            var dueCount = due.Count;
            for (var i = 0; i < dueCount; i++)
            {
                var timer = due[i];
                pending.Remove(timer);
                var callback = timer.Callback;
                timer.Dispose();
                if (!disposed) { callback?.Invoke(); }
            }
        }
        finally
        {
            // Release timer/closure references even when a callback throws or disposes the clock.
            due.Clear();
            dispatchDepth--;
        }
    }

    public void Dispose()
    {
        if (disposed) { return; }
        disposed = true;
        foreach (var timer in pending) { timer.Dispose(); }
        pending.Clear();
    }
}
