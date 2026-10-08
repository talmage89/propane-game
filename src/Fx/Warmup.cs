using Godot;
using Propane.Core;
using Propane.Tank;

namespace Propane.Fx;

/// <summary>
/// Instantiates one of every effect, out of sight, while a suburb is materialising, so the first real shot and
/// explosion do not hitch while their shaders compile.
/// </summary>
public static class Warmup
{
    private const float Lifetime = 1.5f;

    public static void Run(Node3D parent, Vector3 at)
    {
        var root = new Node3D { Name = "Warmup" };
        parent.AddChild(root);
        root.GlobalPosition = at;

        var tank = PropaneTank.Create();
        tank.Freeze = true;
        root.AddChild(tank);
        root.AddChild(new VentJet { Name = "VentJet", Position = new Vector3(0, 0.3f, 0) });
        var debris = DebrisBody.Create(GameAssets.Debris[0]);
        debris.Freeze = true;
        root.AddChild(debris);

        ExplosionEffect.Create(at, at.Y);
        Spawn.Effect(Tracer.Create(at, at + Vector3.Forward * 5f));
        ImpactEffect.Spawn(at + Vector3.Up, Vector3.Up, metal: true, leaveDecal: false);
        ImpactEffect.Spawn(at + Vector3.Up, Vector3.Up, metal: false, leaveDecal: false);

        root.GetTree().CreateTimer(Lifetime).Timeout += root.QueueFree;
    }
}
