using Godot;

namespace Propane.World.Plan;

/// <summary>The plan's ground frame, shared by native construction and server body placement.</summary>
public static class PlanCoordinates
{
    /// <summary>Height of the slab's top surface above the void floor.</summary>
    public const float GroundHeight = 0.12f;
    public static Vector3 ToWorld(Vector2 p, float elevation = 0) => new(p.X, GroundHeight + elevation, p.Y);
}
