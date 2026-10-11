using System.Collections.Generic;
using Godot;

namespace Propane.Player;

/// <summary>A spherical reach clipped to a horizontal forward sector. Contact is sampled throughout the active
/// window; the facade also requires an unobstructed ray to the candidate. Duplicate shapes share one swing key.</summary>
public sealed class MeleeContact
{
    private readonly HashSet<ulong> struck = new();
    public void Begin() => struck.Clear();
    public bool TryHit(ulong victim) => struck.Add(victim);

    public static bool InArc(Vector3 offset, Vector3 forward, float range, float halfAngleDegrees)
    {
        if (offset.LengthSquared() > range * range) return false;
        var flat = new Vector3(offset.X, 0, offset.Z);
        if (flat.LengthSquared() <= 0.000001f) return false;
        return flat.Normalized().Dot(forward) >= Mathf.Cos(Mathf.DegToRad(halfAngleDegrees));
    }
}

public readonly record struct MeleeHit(uint Swing, GodotObject Collider);
