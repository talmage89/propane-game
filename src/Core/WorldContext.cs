using System;
using System.Collections.Generic;
using Godot;
using Propane.Player;
using Propane.Tank;

namespace Propane.Core;

/// <summary>One simulation and its services. Construction precedes actors; disposal invalidates work before freeing nodes.</summary>
public sealed class WorldContext : IDisposable
{
    private readonly SubViewport viewport;
    private readonly List<(Node Node, Node.ProcessModeEnum Mode, bool FollowsPause)> views = new();
    private readonly List<Action> stopping = new();
    private readonly List<(Action Callback, bool WhilePaused)> deferred = new();
    private readonly Dictionary<Node, Node3D> effectOrigins = new();
    private readonly HashSet<IBlastReceiver> receivers = new();
    private WorldPresentation? presentation;
    private bool disposed;
    private readonly List<Exception> stopErrors = new();

    public WorldContext(Node owner, Tuning template, bool networked = false, bool present = false)
    {
        ViewRoot = owner;
        Tuning = template.Copy();
        Networked = networked;
        Physics = new World3D();
        Root = new Node3D { Name = "World", ProcessMode = Node.ProcessModeEnum.Pausable };
        EffectsRoot = new Node3D { Name = "Effects" };
        ContentRoot = new Node3D { Name = "Content" };
        Spawn = new Spawn(this, ContentRoot);
        viewport = new SubViewport
        {
            Name = "WorldSpace",
            World3D = Physics,
            Size = Vector2I.One,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled
        };
        viewport.AddChild(Root);
        Root.AddChild(EffectsRoot);
        Root.AddChild(ContentRoot);
        viewport.AddChild(new WorldDriver { World = this, ProcessMode = Node.ProcessModeEnum.Always });
        WorldTimeScale.Register();
        try
        {
            owner.AddChild(viewport);
            if (present) { Present(owner.GetViewport()); }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public Node ViewRoot { get; }
    public Node3D Root { get; }
    public Node3D ContentRoot { get; private set; }
    public Node3D EffectsRoot { get; }
    public Viewport Viewport => presentation?.View ?? viewport;
    public World3D Physics { get; }
    public Tuning Tuning { get; }
    public bool Networked { get; }
    public bool IsAlive { get; private set; } = true;
    public bool Paused { get; private set; }
    public WorldClock Clock { get; } = new();
    public GameEvents Events { get; } = new();
    public WorldReveal Reveal { get; } = new();
    public Spawn Spawn { get; private set; }
    public IReadOnlyCollection<IBlastReceiver> BlastReceivers => receivers;
    internal List<DroppedMagazine> Magazines { get; } = new();
    internal Queue<Decal> Decals { get; } = new();
    internal int ShadowedFlashes { get; set; }

    /// <summary>The current application viewport views this simulation; the simulation always has its own physics space.</summary>
    public void Present(Viewport view)
    {
        EnsureAlive();
        if (presentation != null) { throw new InvalidOperationException("world already presented"); }
        presentation = WorldPresentation.Acquire(view, this);
    }

    public T AddView<T>(Node parent, T node, bool followsPause = false) where T : Node
    {
        EnsureAlive();
        views.Add((node, node.ProcessMode, followsPause));
        if (Paused && followsPause) { node.ProcessMode = Node.ProcessModeEnum.Disabled; }
        parent.AddChild(node);
        return node;
    }

    /// <summary>Connection polling and UI stay outside Root; both simulation processing and its space stop.</summary>
    public void SetPaused(bool paused)
    {
        EnsureAlive();
        if (Paused == paused) { return; }
        Paused = paused;
        if (paused) { ReleaseTimeScale(); }
        Root.ProcessMode = paused ? Node.ProcessModeEnum.Disabled : Node.ProcessModeEnum.Pausable;
        PhysicsServer3D.SpaceSetActive(Physics.Space, !paused);
        foreach (var view in views)
        {
            if (view.FollowsPause && GodotObject.IsInstanceValid(view.Node))
            { view.Node.ProcessMode = paused ? Node.ProcessModeEnum.Disabled : view.Mode; }
        }
    }

    public bool TryHitstop(float duration, float scale) => WorldTimeScale.TryAcquire(this, duration, scale);
    public void ReleaseTimeScale() => WorldTimeScale.Release(this);
    internal void PollTimeScale() => WorldTimeScale.Poll(this);

    public void SetContentRoot(Node3D root)
    {
        EnsureAlive();
        ContentRoot = root;
        Spawn = new Spawn(this, root);
    }

    public Spawn ForContent(Node3D root) => new(this, root);
    public void Register(IBlastReceiver receiver) { EnsureAlive(); receivers.Add(receiver); }
    internal bool IsRegistered(IBlastReceiver receiver) => receivers.Contains(receiver);
    public void Unregister(IBlastReceiver receiver) => receivers.Remove(receiver);
    public void OnStopping(Action stop) { EnsureAlive(); stopping.Add(stop); }
    public void Defer(Action callback, bool whilePaused = false) { EnsureAlive(); deferred.Add((callback, whilePaused)); }

    internal void RecordEffect(Node node, Node3D content)
    {
        effectOrigins.Add(node, content);
        node.TreeExiting += () => effectOrigins.Remove(node);
    }

    /// <summary>Timers cannot call a freed or retired source, even if another world has reused its position.</summary>
    public IDisposable Schedule(Node source, double seconds, Action callback)
    {
        EnsureAlive();
        IDisposable timer = null!;
        void Cancel()
        {
            if (GodotObject.IsInstanceValid(source)) { source.TreeExiting -= Cancel; }
            timer.Dispose();
        }
        timer = Clock.Schedule(seconds, () =>
        {
            Cancel();
            if (IsAlive && GodotObject.IsInstanceValid(source) && source.IsInsideTree() && !source.IsQueuedForDeletion()) { callback(); }
        });
        source.TreeExiting += Cancel;
        return new Cancellation(Cancel);
    }

    private sealed class Cancellation(Action cancel) : IDisposable
    {
        private Action? cancel = cancel;
        public void Dispose() { var action = cancel; cancel = null; action?.Invoke(); }
    }

    public void RetireContent(Node3D content)
    {
        EnsureAlive();
        foreach (var (effect, origin) in new Dictionary<Node, Node3D>(effectOrigins))
        {
            if (origin == content || (GodotObject.IsInstanceValid(origin) && content.IsAncestorOf(origin))) { effect.Free(); }
        }
        content.Free();
    }

    internal void Advance(double delta)
    {
        if (IsAlive && !Paused && Root.CanProcess()) { Clock.Advance(delta); }
    }

    internal void FlushDeferred()
    {
        if (!IsAlive || deferred.Count == 0) { return; }
        var callbacks = deferred.FindAll(work => work.WhilePaused || (!Paused && Root.CanProcess()));
        foreach (var work in callbacks) { deferred.Remove(work); }
        foreach (var work in callbacks) { if (IsAlive) { work.Callback(); } }
    }

    public void Construct(Action build)
    {
        EnsureAlive();
        try { build(); }
        catch { Dispose(); throw; }
    }

    public void EnsureAlive() => ObjectDisposedException.ThrowIf(!IsAlive, this);

    public void Invalidate()
    {
        if (!IsAlive) { return; }
        IsAlive = false;
        if (GodotObject.IsInstanceValid(Root)) { Root.ProcessMode = Node.ProcessModeEnum.Disabled; }
        PhysicsServer3D.SpaceSetActive(Physics.Space, false);
        WorldTimeScale.Unregister(this);
        Clock.Dispose();
        Reveal.Dispose();
        deferred.Clear();
        Events.Clear();
        receivers.Clear();
        foreach (var stop in stopping)
        {
            try { stop(); }
            catch (Exception error) { stopErrors.Add(error); }
        }
        stopping.Clear();
    }

    public void Dispose()
    {
        if (disposed) { return; }
        disposed = true;
        Invalidate();
        for (var i = views.Count - 1; i >= 0; i--) { if (GodotObject.IsInstanceValid(views[i].Node)) { views[i].Node.Free(); } }
        views.Clear();
        presentation?.Release(this);
        presentation = null;
        if (GodotObject.IsInstanceValid(viewport)) { viewport.Free(); }
        effectOrigins.Clear();
        Magazines.Clear();
        Decals.Clear();
        ShadowedFlashes = 0;
        Tuning.Dispose();
        Physics.Dispose();
        if (stopErrors.Count > 0) { throw new AggregateException("world stop adapters failed", stopErrors); }
    }

}

/// <summary>Tree signals keep the emitter alive when a callback synchronously retires this world's driver.</summary>
internal partial class WorldDriver : Node
{
    public required WorldContext World { get; init; }
    private SceneTree tree = null!;
    public override void _Ready()
    {
        tree = GetTree();
        tree.PhysicsFrame += PhysicsTick;
        tree.ProcessFrame += ProcessTick;
    }
    private void PhysicsTick()
    {
        if (World.IsAlive) { World.Advance(GetPhysicsProcessDeltaTime()); }
    }
    private void ProcessTick()
    {
        World.PollTimeScale();
        World.FlushDeferred();
    }
    public override void _ExitTree()
    {
        tree.PhysicsFrame -= PhysicsTick;
        tree.ProcessFrame -= ProcessTick;
        World.Invalidate();
    }
}
