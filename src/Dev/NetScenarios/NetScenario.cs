using System;
using System.Collections.Generic;
using Godot;
using Propane.Net;
using Propane.Player;
using Propane.ReleaseSmoke;
using Propane.Tank;

namespace Propane.Dev.NetScenarios;

/// <summary>Connection-owned services; scenario state never lives on the host.</summary>
internal interface INetScenarioHost
{
    NetClient Net { get; }
    CaptureDirector Director { get; }
    string Role { get; }
    int Players { get; }
    float MatchClock { get; }
    SceneTree Tree { get; }
    bool InTree { get; }
    void Attach(Node node);
    SignalAwaiter AwaitSignal(GodotObject source, StringName signal);
    void Log(string text);
    void Fail(string text);
}

/// <summary>
/// An ordinary connection-owned scenario. Assembly and reset remain at the host's original callback positions.
/// Retirement detaches match subscriptions and invalidates queued timers/render continuations before another match.
/// </summary>
internal abstract class NetScenario : IDisposable
{
    private readonly INetScenarioHost host;
    protected readonly NetClient net;
    protected readonly CaptureDirector director;
    protected readonly string role;
    protected readonly int players;
    private readonly List<Action> detach = new();
    private long epoch;
    private bool disposed;
    private bool active;
    protected bool scenarioPassed;
    protected bool scenarioFailed;

    protected NetScenario(INetScenarioHost host)
    {
        this.host = host;
        net = host.Net;
        director = host.Director;
        role = host.Role;
        players = host.Players;
    }

    protected float MatchClock => host.MatchClock;
    protected long Epoch => epoch;
    protected bool IsCurrent(long captured) => !disposed && captured == epoch;
    protected void DetachOnRetirement(Action action) => detach.Add(action);
    protected void Log(string text) => host.Log(text);
    protected SceneTree GetTree() => host.Tree;
    protected bool IsInsideTree() => !disposed && host.InTree;
    protected SignalAwaiter ToSignal(GodotObject source, StringName signal) => host.AwaitSignal(source, signal);
    protected void Attach(Node node) => host.Attach(node);

    // Capture before registering a callback, never when it is delivered: old delivery must not enter a new run.
    protected Action Guard(Action callback)
    {
        var captured = epoch;
        return () => { if (IsCurrent(captured)) callback(); };
    }

    // Lobby captures belong to the connection; returning from a match must not retire them.
    protected Action GuardConnection(Action callback) => () => { if (!disposed) callback(); };

    public void BeginAssembly(Match match)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        RetireMatch();
        active = true;
        Preparing(match);
    }

    public virtual void Initialize() { }
    protected void Fail(string text) => host.Fail(text);

    public virtual void BeforeInbox(Match match) { }
    protected virtual void Preparing(Match match) { }

    public void AfterAssembly(Match match)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        scenarioPassed = scenarioFailed = false;
        Assembled(match);
    }

    protected virtual void Assembled(Match match) { }
    public virtual bool Tick(Match match, float dt) { if (active && !disposed) Run(match, dt); return true; }
    protected virtual void Run(Match match, float dt) { }
    public virtual void LobbyChanged(LobbyInfo lobby) { }
    public virtual void MatchStarting(MatchSetup setup) { }
    public virtual float ConstructionDelay(float delay) => delay;

    // Generic assertions run before navigation telemetry; capacity's final checks run after it, as before.
    public virtual bool CheckCompletion(NetScenarioCompletion requirement)
    {
        if (requirement == NetScenarioCompletion.Assertions)
            ScenarioAssert(scenarioPassed, "scenario ended before all assertions passed");
        return true;
    }

    public virtual void Complete(Match match) { }

    public void RetireMatch()
    {
        active = false;
        epoch++;
        for (var i = detach.Count - 1; i >= 0; i--) detach[i]();
        detach.Clear();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        RetireMatch();
        GC.SuppressFinalize(this);
    }

    protected void ScenarioAssert(bool condition, string failure)
    {
        if (condition) return;
        scenarioFailed = true;
        host.Fail(failure);
    }

    protected static bool IsDrop(PropaneTank tank) => NetScenarioTools.IsDrop(tank);
    protected static void AimAt(PlayerCharacter player, Vector3 point) => NetScenarioTools.AimAt(player, point);
    protected static string Vector(Vector3 value) => NetScenarioTools.Vector(value);
    protected static double TankCheckTime() => NetScenarioTools.TankCheckTime();
}
