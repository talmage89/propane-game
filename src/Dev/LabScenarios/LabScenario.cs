using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using Propane.Core;
using Propane.Lab;

namespace Propane.Dev.LabCases;

/// <summary>Scene and scheduling services owned by the Lab harness, at their original call sites.</summary>
internal interface ILabScenarioHost
{
    Tuning Template { get; }
    CaptureDirector Capture { get; }
    Equivalence.LabTrace? Trace { get; }
    LabSession Enter(Propane.Lab.LabScenario scenario, LabEnvironment environment);
    void AddLab(LabSession lab);
    void Attach(Node node);
    SceneTree Tree { get; }
    Viewport Viewport { get; }
    Task Frames(int count);
    Task Collect();
    (int, int, int) Counts();
}

/// <summary>A single owned async case. Returning from its body is its completion obligation.</summary>
internal abstract class LabScenario(ILabScenarioHost host) : IDisposable
{
    private readonly List<Subscription> detach = new();
    private bool started;
    private bool completed;
    private bool disposed;
    protected Tuning template => host.Template;
    protected CaptureDirector capture => host.Capture;
    protected Equivalence.LabTrace? equivalence => host.Trace;
    protected LabSession Enter(Propane.Lab.LabScenario scenario, LabEnvironment environment) => host.Enter(scenario, environment);
    protected void AddLab(LabSession lab) => host.AddLab(lab);
    protected void AddChild(Node node) => host.Attach(node);
    protected SceneTree GetTree() => host.Tree;
    protected Viewport GetViewport() => host.Viewport;
    protected Task Frames(int count = 3) => host.Frames(count);
    protected Task Collect() => host.Collect();
    protected (int, int, int) Counts() => host.Counts();

    public async Task Execute()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (started) throw new InvalidOperationException("Lab scenario already started");
        started = true;
        await RunCore().ConfigureAwait(true);
        ObjectDisposedException.ThrowIf(disposed, this);
        completed = true;
    }

    protected abstract Task RunCore();

    public void CheckCompletion()
    {
        if (!completed) throw new InvalidOperationException("Lab scenario ended before completion");
    }

    // A case can detach at a fixture boundary without retaining that fixture until the entire case ends.
    protected IDisposable DetachOnDisposal(Action action)
    {
        var subscription = new Subscription(action, detach);
        detach.Add(subscription);
        return subscription;
    }

    private sealed class Subscription(Action action, List<Subscription> owner) : IDisposable
    {
        private Action? release = action;
        public void Dispose()
        {
            var callback = release;
            if (callback == null) return;
            release = null;
            owner.Remove(this);
            callback();
            GC.SuppressFinalize(this);
        }
    }
    protected Action Guard(Action action) => () => { if (!disposed) action(); };
    protected Action<T> Guard<T>(Action<T> action) => value => { if (!disposed) action(value); };

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        while (detach.Count > 0) detach[^1].Dispose();
        GC.SuppressFinalize(this);
    }
}
