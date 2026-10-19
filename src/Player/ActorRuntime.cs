using System;
using Propane.Core;

namespace Propane.Player;

/// <summary>Physical actor control state. Client replication attachments live outside simulation.</summary>
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
    public IPlayerInputSource? IntentSource { get; set; }
    public double GraceUntil { get; set; }
    public double LastHitTime { get; set; } = -100;
    public int LastHitBy { get; set; }
    public uint LifeId { get; set; } = 1;
    // A primary view requests full native poses; no camera or HUD reference enters simulation.
    internal bool HasLocalView { get; set; }
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
