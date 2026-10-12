using Godot;
using Propane.Core;
using Propane.Tank;

namespace Propane.Fx;

/// <summary>
/// Instantiates one of every effect, out of sight, while a suburb is materializing, so the first real shot and
/// explosion do not hitch while their shaders compile.
/// </summary>
public static class Warmup
{
    private const float Lifetime = 1.5f;

    public static void Run(WorldContext world, Node3D parent, Vector3 at)
    {
        var root = RunEffects(world, parent, at);
        var tank = PropaneTank.Create(world);
        tank.Freeze = true;
        root.AddChild(tank);
        var debris = DebrisBody.Create(world, GameAssets.Debris[0]);
        debris.Freeze = true;
        root.AddChild(debris);
    }

    /// <summary>Warm presentation without adding physics bodies, for the Lab's existing fixture.</summary>
    public static Node3D RunEffects(WorldContext world, Node3D parent, Vector3 at)
    {
        var root = new Node3D { Name = "Warmup" };
        parent.AddChild(root);
        root.GlobalPosition = at;
        root.AddChild(new VentJet { World = world, ContentSpawn = world.Spawn, Name = "VentJet", Position = new Vector3(0, 0.3f, 0) });
        var explosion = ExplosionEffect.Create(world, at, at.Y);
        world.Spawn.Effect(Tracer.Create(at, at + Vector3.Forward * 5f));
        ImpactEffect.Spawn(world, at + Vector3.Up, Vector3.Up, metal: true, leaveDecal: false);
        ImpactEffect.Spawn(world, at + Vector3.Up, Vector3.Up, metal: false, leaveDecal: false);

        // Retire the flash too: an invisible warm-up must not reserve a shadow slot during the first real blast.
        world.Schedule(root, Lifetime, () => { root.QueueFree(); explosion.QueueFree(); });
        return root;
    }
}
