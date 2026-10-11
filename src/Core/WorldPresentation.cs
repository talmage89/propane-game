using System.Collections.Generic;
using Godot;

namespace Propane.Core;

/// <summary>The presentation viewport owns its live selection history; retired worlds are never restored.</summary>
internal partial class WorldPresentation : RefCounted
{
    private const string Metadata = "propane_world_presentation";
    private readonly List<WorldContext> worlds = new();
    private World3D? original;
    public Viewport View { get; private set; } = null!;

    public static WorldPresentation Acquire(Viewport view, WorldContext world)
    {
        var bridge = view.HasMeta(Metadata) ? view.GetMeta(Metadata).As<WorldPresentation>() : null;
        if (bridge == null)
        {
            bridge = new WorldPresentation { View = view, original = view.World3D };
            view.SetMeta(Metadata, bridge);
        }
        bridge.worlds.Add(world);
        view.World3D = world.Physics;
        return bridge;
    }

    public void Release(WorldContext world)
    {
        worlds.Remove(world);
        if (GodotObject.IsInstanceValid(View))
        {
            if (View.World3D == world.Physics)
            {
                View.World3D = worlds.Count > 0 ? worlds[^1].Physics : original;
            }
            if (worlds.Count == 0) { View.RemoveMeta(Metadata); }
        }
        if (worlds.Count == 0)
        {
            original = null;
            Dispose();
        }
    }
}
