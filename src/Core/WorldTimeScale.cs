using Godot;

namespace Propane.Core;

/// <summary>The application time scale is an exclusive lease: only one live offline world can slow the engine.</summary>
internal static class WorldTimeScale
{
    private static int worlds;
    private static WorldContext? owner;
    private static double releaseAt;

    internal static void Register()
    {
        worlds++;
        if (worlds > 1 && owner != null) { Release(owner); }
    }

    internal static void Unregister(WorldContext world)
    {
        Release(world);
        worlds--;
    }

    internal static bool TryAcquire(WorldContext world, float duration, float scale)
    {
        if (!world.IsAlive || world.Networked || world.Paused || worlds != 1) { return false; }
        owner = world;
        Engine.TimeScale = scale;
        // Hitstop is a presentation lease measured in real seconds; gameplay still advances with physics delta.
        releaseAt = Time.GetTicksUsec() / 1_000_000.0 + duration;
        return true;
    }

    internal static void Poll(WorldContext world)
    {
        if (owner == world && Time.GetTicksUsec() / 1_000_000.0 >= releaseAt) { Release(world); }
    }

    internal static void Release(WorldContext world)
    {
        if (owner != world) { return; }
        owner = null;
        Engine.TimeScale = 1;
    }
}
