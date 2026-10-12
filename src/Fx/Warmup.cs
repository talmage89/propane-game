using Godot;
using Propane.Core;
using Propane.Tank;

namespace Propane.Fx;

/// <summary>
/// Instantiates effect examples out of sight while assembling a session, priming shared resources before combat.
/// Culled geometry can still need a draw pipeline on its first visible use.
/// </summary>
public static class Warmup
{
    private const float Lifetime = 1.5f;

    public static void Run(WorldContext world, Node3D parent, Vector3 at)
    {
        // Preserve the match warm-up flash lifetime and its existing shadow-slot accounting.
        var root = RunEffects(world, parent, at, retireFlash: false);
        var tank = PropaneTank.Create(world);
        tank.Freeze = true;
        root.AddChild(tank);
        var debris = DebrisBody.Create(world, GameAssets.Debris[0]);
        debris.Freeze = true;
        root.AddChild(debris);
    }

    /// <summary>Warm presentation without adding physics bodies, for the Lab's existing fixture.</summary>
    public static Node3D RunEffects(WorldContext world, Node3D parent, Vector3 at) => RunEffects(world, parent, at, retireFlash: true);

    private static Node3D RunEffects(WorldContext world, Node3D parent, Vector3 at, bool retireFlash)
    {
        var root = new Node3D { Name = "Warmup" };
        parent.AddChild(root);
        root.GlobalPosition = at;
        root.AddChild(new VentJet { World = world, ContentSpawn = world.Spawn, Name = "VentJet", Position = new Vector3(0, 0.3f, 0) });
        var explosion = ExplosionEffect.Create(world, at, at.Y);
        world.Spawn.Effect(Tracer.Create(at, at + Vector3.Forward * 5f));
        ImpactEffect.Spawn(world, at + Vector3.Up, Vector3.Up, metal: true, leaveDecal: false);
        ImpactEffect.Spawn(world, at + Vector3.Up, Vector3.Up, metal: false, leaveDecal: false);

        // Lab had no warm-up flash before this path: retire it before it can affect a real blast's shadows.
        world.Schedule(root, Lifetime, () => { root.QueueFree(); if (retireFlash) explosion.QueueFree(); });
        return root;
    }
}
