using Godot;
using Propane.Core;

namespace Propane.Tank;

/// <summary>A piece of burst tank shell. Its sooty inside glows hot for a few seconds after the blast.</summary>
public partial class DebrisBody : RigidBody3D
{
    private const float CoolDownTime = 3.5f;

    private MeshInstance3D mesh = null!;
    private float age;

    public static DebrisBody Create(DebrisPiece piece)
    {
        var body = new DebrisBody
        {
            Name = piece.Name,
            Mass = piece.Mass,
            CollisionLayer = Layers.Debris,
            CollisionMask = Layers.DynamicMask,
            ContinuousCd = piece.Mass < 0.6f,
            CenterOfMassMode = CenterOfMassModeEnum.Custom,
            CenterOfMass = piece.Centroid,
            PhysicsMaterialOverride = new PhysicsMaterial { Friction = 0.7f, Bounce = 0.25f },
            AngularDamp = 0.8f,
        };
        body.AddChild(new CollisionShape3D { Shape = piece.Shape });
        body.mesh = new MeshInstance3D { Mesh = piece.Mesh, Layers = RenderLayers.Actors };
        body.AddChild(body.mesh);
        GameAssets.ApplyTankMaterials(body.mesh);
        body.mesh.SetInstanceShaderParameter("heat", 1f);
        body.mesh.SetInstanceShaderParameter("soot", (float)GD.RandRange(0.3, 0.6));
        return body;
    }

    public override void _Process(double delta)
    {
        age += (float)delta;
        var heat = Mathf.Clamp(1f - age / CoolDownTime, 0, 1);
        mesh.SetInstanceShaderParameter("heat", heat);
        if (heat <= 0)
        {
            SetProcess(false);
        }
    }
}
