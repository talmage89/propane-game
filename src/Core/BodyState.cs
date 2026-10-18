using Godot;

namespace Propane.Core;

/// <summary>Gather presentation phases, shared by physical gathering and replication.</summary>
public enum GatherPhase : byte { Cancel, Glide, Settled }

public sealed record VentParameters(Vector3 Hole, Vector3 Normal, float Swirl);
public sealed record BodyState(uint Id, Transform3D Transform, Vector3 Linear, Vector3 Angular, uint Epoch = 1, uint Sequence = 0)
{
    /// <summary>Shared lifecycle; the enum name and numeric order remain stable for reference observations.</summary>
    public enum State { Intact, Venting, Exploded }
}
