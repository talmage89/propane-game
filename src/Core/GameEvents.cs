using System;
using System.Collections.Generic;
using Godot;

namespace Propane.Core;

/// <summary>Notifications confined to one world's lifetime.</summary>
public sealed class GameEvents
{
    private bool closed;
    public event Action<Vector3>? Explosion;
    public event Action<Vector3, IReadOnlyList<RigidBody3D>, uint>? LocalBlast;

    public void RaiseExplosion(Vector3 position) { if (!closed) { Explosion?.Invoke(position); } }
    public void RaiseLocalBlast(Vector3 position, IReadOnlyList<RigidBody3D> pushed, uint cause)
    {
        if (!closed) { LocalBlast?.Invoke(position, pushed, cause); }
    }
    internal void Clear() { closed = true; Explosion = null; LocalBlast = null; }
}
