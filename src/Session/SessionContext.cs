using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Godot;
using Propane.Core;
using Propane.Player;

namespace Propane.Session;

/// <summary>Owns actors, process-local views and adapters for one session. Retirement is synchronous and ordered.</summary>
public sealed class SessionContext : IDisposable
{
    private readonly List<ActorRuntime> actors = new();
    private readonly List<Node> views = new();
    private readonly Net.IdentitySequence actorIds = new();
    private readonly Dictionary<int, ActorRuntime> registry = new();
    private readonly HashSet<int> retired = new();
    private readonly List<Action> detach = new();
    private readonly List<Action> unsubscribe = new();
    private readonly List<Action> stop = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly List<Exception> errors = new();
    private readonly bool present;
    private bool stopped;
    private bool disposed;

    public SessionContext(Node owner, Tuning template, ISessionWorldSource source, ISessionMode mode,
        bool networked = false, bool present = true)
    {
        Source = source;
        Mode = mode;
        this.present = present;
        World = new WorldContext(owner, template, networked, attach: false);
        World.OnStopping(Stop);
    }

    internal void AttachWorld() => World.Attach(present);

    public WorldContext World { get; }
    public ISessionWorldSource Source { get; }
    public ISessionMode Mode { get; }
    public bool IsAlive => !stopped && World.IsAlive;
    public CancellationToken Cancellation => cancellation.Token;
    public IReadOnlyList<ActorRuntime> Actors => actors;
    public IEnumerable<ActorRuntime> OwnedActors => actors.Where(actor => !actor.Character.IsRemote);
    public IReadOnlyList<LocalView> LocalViews => views.OfType<LocalView>().ToArray();

    public ActorRuntime? Actor(int actorId) => registry.GetValueOrDefault(actorId);

    // Network ids come from the frozen server roster. Offline actors use the same nonrecycling registry.
    public ActorRuntime AddActor(PlayerCharacter character)
    {
        EnsureAlive();
        if (character.World != World) throw new ArgumentException("actor belongs to another world", nameof(character));
        var actor = character.Runtime;
        var id = character.ActorId;
        if (id == 0 && !World.Networked)
        {
            do { id = actorIds.Allocate(); } while (registry.ContainsKey(id) || retired.Contains(id));
            character.ActorId = id;
        }
        if (id <= 0 || registry.ContainsKey(id) || retired.Contains(id)) throw new ArgumentException("actor identity is absent or reused", nameof(character));
        registry.Add(id, actor);
        actors.Add(actor);
        World.Root.AddChild(character);
        if (!character.Initialized) throw new InvalidOperationException("actor did not complete initialization");
        return actor;
    }

    public void RemoveActor(ActorRuntime actor)
    {
        EnsureAlive();
        if (!actors.Remove(actor)) return;
        registry.Remove(actor.Character.ActorId);
        retired.Add(actor.Character.ActorId);
        foreach (var view in LocalViews.Where(view => view.Actor == actor.Character)) CloseView(view);
        if (GodotObject.IsInstanceValid(actor.Character)) actor.Character.Free();
        actor.Dispose();
    }

    public LocalView AddLocalView(ActorRuntime actor, Node parent, DeviceInput? input = null)
    {
        EnsureAlive();
        if (!actors.Contains(actor)) throw new ArgumentException("actor is not in this session", nameof(actor));
        return AddView(parent, new LocalView { Actor = actor.Character, Input = input, Name = "LocalView" });
    }

    public T AddView<T>(Node parent, T view, bool followsPause = false) where T : Node
    {
        EnsureAlive();
        views.Add(view);
        World.AddView(parent, view, followsPause);
        if (view is LocalView { Initialized: false }) throw new InvalidOperationException("local view did not complete initialization");
        return view;
    }

    public void CloseView(LocalView view)
    {
        EnsureAlive();
        if (views.Remove(view) && GodotObject.IsInstanceValid(view)) view.Free();
    }

    public void OnDetaching(Action callback) { EnsureAlive(); detach.Add(callback); }
    public void OnUnsubscribing(Action callback) { EnsureAlive(); unsubscribe.Add(callback); }
    public void OnStopping(Action callback) { EnsureAlive(); stop.Add(callback); }
    private void EnsureAlive() => ObjectDisposedException.ThrowIf(!IsAlive, this);
    private void Run(Action action) { try { action(); } catch (Exception error) { errors.Add(error); } }
    private void Drain(List<Action> actions)
    {
        for (var i = actions.Count - 1; i >= 0; i--) Run(actions[i]);
        actions.Clear();
    }

    private void Stop()
    {
        if (stopped) return;
        stopped = true;
        Run(cancellation.Cancel);
        Drain(detach);
        Drain(unsubscribe);
        Drain(stop);
        foreach (var actor in actors)
        {
            actor.IntentSource = null;
            if (GodotObject.IsInstanceValid(actor.Character)) actor.Character.ProcessMode = Node.ProcessModeEnum.Disabled;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        // World invalidation cancels timers, deferred navigation and physics before any adapters or nodes retire.
        Run(World.Invalidate);
        Stop();
        for (var i = views.Count - 1; i >= 0; i--)
            if (GodotObject.IsInstanceValid(views[i])) Run(views[i].Free);
        views.Clear();
        for (var i = actors.Count - 1; i >= 0; i--)
        {
            if (GodotObject.IsInstanceValid(actors[i].Character)) Run(actors[i].Character.Free);
            Run(actors[i].Dispose);
        }
        actors.Clear();
        registry.Clear();
        retired.Clear();
        Run(World.Dispose);
        cancellation.Dispose();
        if (errors.Count > 0) throw new AggregateException("session retirement failed", errors);
    }
}
