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
    private bool disposed;
    public double Now { get; private set; }
    public int PendingCount => pending.FindAll(timer => timer.Callback != null).Count;

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
        // Snapshot due work: callbacks may schedule or cancel timers, including disposing the world.
        var due = pending.FindAll(timer => timer.Callback == null || timer.Due <= Now);
        foreach (var timer in due)
        {
            pending.Remove(timer);
            var callback = timer.Callback;
            timer.Dispose();
            if (!disposed) { callback?.Invoke(); }
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
