using System;
using Propane.Core;
using Propane.Net;

namespace Propane.Player;

/// <summary>Actor-local control and replication state, independent of devices and rendered views.</summary>
public sealed class ActorRuntime : IDisposable
{
    public ActorRuntime(PlayerCharacter character)
    {
        Character = character;
        Aim = new LogicalAim(character.World);
    }

    public PlayerCharacter Character { get; }
    public LogicalAim Aim { get; }
    public AmmoState? Ammo { get; set; }
    public PlayerIntent Intent { get; set; }
    public PlayerStateResolver Resolver { get; } = new();
    public PlayerState ResolvedState { get; set; }
    public uint MeleeSequence { get; set; }
    // Poses use a separate wire sequence stream from reliable actions, retaining their sampling timestamps.
    public uint ReplicationSequence { get; set; }
    public float SendTimer { get; set; }
    public IPlayerInputSource? IntentSource { get; set; }
    public double GraceUntil { get; set; }
    public double LastHitTime { get; set; } = -100;
    public int LastHitBy { get; set; }
    public int SpawnSlot { get; set; }
    public int AllocationSlot { get; set; }
    public uint LifeId { get; set; } = 1;
    public uint ActionSequence { get; set; }
    public bool AmmoFullShown { get; set; }
    public int DropCounter { get; set; }
    public uint AllocateDropId()
    {
        var id = BodyIds.DropTankId(AllocationSlot, DropCounter);
        DropCounter = checked(DropCounter + 1);
        return id;
    }
    public DropPredictions DropPredictions { get; } = new();
    public SnapshotBuffer? Snapshots => (IntentSource as ReplicatedIntent)?.Snapshots;
    private bool disposed;
    internal bool IsDisposed => disposed;
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        IntentSource = null;
        Aim.Dispose();
    }
}
