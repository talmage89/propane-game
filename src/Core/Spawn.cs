using Godot;

namespace Propane.Core;

/// <summary>
/// Where transient things (debris, decals, effects) are parented. Debris and scorch marks belong to the current suburb
/// so they disappear with it; short-lived effects go to a separate effects root.
/// </summary>
public static class Spawn
{
    private static Node3D? worldRoot;
    private static Node3D? effectsRoot;

    public static void SetWorldRoot(Node3D root) => worldRoot = root;

    public static void SetEffectsRoot(Node3D root) => effectsRoot = root;

    /// <summary>Adds a node that should live as long as the current suburb.</summary>
    public static T InWorld<T>(T node) where T : Node
    {
        (worldRoot ?? effectsRoot)!.AddChild(node);
        return node;
    }

    /// <summary>Adds a short-lived effect that frees itself.</summary>
    public static T Effect<T>(T node) where T : Node
    {
        (effectsRoot ?? worldRoot)!.AddChild(node);
        return node;
    }

    /// <summary>Moves an existing node under the effects root, keeping where it is, so it outlives its parent.</summary>
    public static void Detach(Node3D node) => node.Reparent(effectsRoot ?? worldRoot, keepGlobalTransform: true);
}
