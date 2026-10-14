using Godot;
using Propane.Core;
using Propane.Fx;

namespace Propane.Tank;

/// <summary>A piece of burst tank shell. Its sooty inside glows hot for a few seconds after the blast.</summary>
public partial class DebrisBody : RigidBody3D
{
    private const float CoolDownTime = 3.5f;

    private static readonly StringName heatParameter = new("heat");
    private static readonly StringName sootParameter = new("soot");
    private static readonly PhysicsMaterial material = new() { Friction = 0.7f, Bounce = 0.25f };

    private MeshInstance3D mesh = null!;
    private float age;

    public static DebrisBody Create(WorldContext world, DebrisPiece piece, float? soot = null)
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
            PhysicsMaterialOverride = material,
            AngularDamp = 0.8f,
        };
        body.AddChild(new CollisionShape3D { Shape = piece.Shape });
        body.mesh = new MeshInstance3D { Mesh = piece.Mesh, Layers = RenderLayers.Actors };
        body.AddChild(body.mesh);
        GameAssets.ApplyTankMaterials(body.mesh);
        body.mesh.SetInstanceShaderParameter(heatParameter, 1f);
        body.mesh.SetInstanceShaderParameter(sootParameter, soot ?? world.Random.RandfRange(0.3f, 0.6f));
        return body;
    }

    public override void _Process(double delta)
    {
        using var profile = EffectProfile.Measure(EffectProfile.System.DebrisProcess);
        age += (float)delta;
        var heat = Mathf.Clamp(1f - age / CoolDownTime, 0, 1);
        mesh.SetInstanceShaderParameter(heatParameter, heat);
        if (heat <= 0)
        {
            SetProcess(false);
        }
    }
}
