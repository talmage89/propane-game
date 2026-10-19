using System;
using System.Collections.Generic;
using Propane.Core;
using Propane.Player;

namespace Propane.Net;

/// <summary>One actor's client attachment, disposed by its session ActorRegistration before native actor free.
/// Snapshot times are monotonic network seconds; SendTimer consumes Match's physics delta. No input reads.</summary>
public sealed class ClientActorReplication : IDisposable
{
    public ClientActorReplication(PlayerCharacter actor) => character = actor;
    private PlayerCharacter? character;
    public PlayerCharacter Character => character ?? throw new ObjectDisposedException(nameof(ClientActorReplication));
    // A temporary override or view assignment suspends the original source, as before extraction.
    private ReplicatedIntent? ActiveIntent => character?.Runtime.IntentSource as ReplicatedIntent;
    public SnapshotBuffer? Snapshots => ActiveIntent?.Snapshots;
    public uint ReplicationSequence { get; set; }
    public float SendTimer { get; set; }
    public uint ActionSequence { get; set; }
    public int SpawnSlot { get; set; }
    public int AllocationSlot { get; set; }
    public int DropCounter { get; set; }
    public bool AmmoFullShown { get; set; }
    public DropPredictions DropPredictions { get; } = new();
    internal readonly Dictionary<Msg, (uint Life, uint Action)> receivedActions = new();
    internal MeleeStartMessage? Melee { get; set; }
    private Action? unsubscribe;
    private bool disposed;
    internal bool IsSubscribed => unsubscribe != null && !disposed;
    internal void AttachInput()
    {
        var actor = Character;
        if (actor.IsRemote) actor.Runtime.IntentSource = new ReplicatedIntent(actor.World.Tuning);
    }
    internal void ApplySnapshot(double now) => ActiveIntent?.Apply(Character, now);
    public uint AllocateDropId()
    {
        var id = BodyIds.DropTankId(AllocationSlot, DropCounter);
        DropCounter = checked(DropCounter + 1);
        return id;
    }
    internal bool Accept(NetMessage message)
    {
        var previous = receivedActions.GetValueOrDefault(message.Type);
        if (previous.Life == message.LifeId && message.ActionId <= previous.Action) return false;
        receivedActions[message.Type] = (message.LifeId, message.ActionId);
        return true;
    }
    internal void Subscribe(Action detach)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (unsubscribe != null) throw new InvalidOperationException("actor replication is already subscribed");
        unsubscribe = detach;
    }
    internal void Unsubscribe()
    {
        var callback = unsubscribe;
        unsubscribe = null;
        callback?.Invoke();
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Unsubscribe();
        Melee = null;
        receivedActions.Clear();
        character = null;
    }
}
