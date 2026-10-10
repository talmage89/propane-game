using Godot;

namespace Propane.Tank;

/// <summary>What a blast tells a non-rigid-body receiver such as the player.</summary>
/// <param name="Center">Blast center.</param>
/// <param name="Direction">Unit push direction, already biased upward.</param>
/// <param name="Distance">Distance from the center to the receiver.</param>
/// <param name="Falloff">0 at the edge of the blast, 1 at the center, after occlusion.</param>
public readonly record struct BlastInfo(Vector3 Center, Vector3 Direction, float Distance, float Falloff);

/// <summary>A node that reacts to blasts itself, rather than being pushed as a rigid body.</summary>
public interface IBlastReceiver
{
    public const string Group = "blast_receivers";

    Vector3 BlastTargetPosition { get; }

    void ReceiveBlast(BlastInfo blast);
}
