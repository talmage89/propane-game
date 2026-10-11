using Godot;

namespace Propane.Core;

/// <summary>A world/content factory. Durable debris follows its originating content; effects follow the world.</summary>
public sealed class Spawn
{
    private readonly WorldContext world;
    public Node3D ContentRoot { get; }

    internal Spawn(WorldContext world, Node3D content)
    {
        this.world = world;
        ContentRoot = content;
    }

    public T InWorld<T>(T node) where T : Node
    {
        world.EnsureAlive();
        ContentRoot.AddChild(node);
        return node;
    }

    public T Effect<T>(T node) where T : Node
    {
        world.EnsureAlive();
        world.EffectsRoot.AddChild(node);
        world.RecordEffect(node, ContentRoot);
        return node;
    }

    public void Detach(Node3D node)
    {
        world.EnsureAlive();
        node.Reparent(world.EffectsRoot, keepGlobalTransform: true);
        world.RecordEffect(node, ContentRoot);
    }
}
