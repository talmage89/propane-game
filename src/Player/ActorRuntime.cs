using System;
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
    public IPlayerInputSource? IntentSource { get; set; }
    public double GraceUntil { get; set; }
    public double LastHitTime { get; set; } = -100;
    public int LastHitBy { get; set; }
    public int SpawnSlot { get; set; }
    public int DropCounter { get; set; }
    public DropPredictions DropPredictions { get; } = new();
    public SnapshotBuffer? Snapshots { get; set; }
    public void Dispose()
    {
        IntentSource = null;
        Aim.Dispose();
    }
}
