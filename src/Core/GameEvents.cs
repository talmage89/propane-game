using System;
using Godot;

namespace Propane.Core;

/// <summary>Game-wide notifications, so effects and UI can react without references to their sources.</summary>
public static class GameEvents
{
    /// <summary>A tank exploded at a world position.</summary>
    public static event Action<Vector3>? Explosion;

    public static void RaiseExplosion(Vector3 position) => Explosion?.Invoke(position);
}
