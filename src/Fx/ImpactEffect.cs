using System.Collections.Generic;
using Godot;
using Propane.Core;

namespace Propane.Fx;

/// <summary>Bullet impacts: sparks on metal, a dust puff elsewhere, and a pooled bullet-hole decal on static surfaces.</summary>
public static class ImpactEffect
{
    private const int MaxDecals = 96;
    private static readonly Queue<Decal> Decals = new();

    public static void Spawn(Vector3 point, Vector3 normal, bool metal, bool leaveDecal)
    {
        var root = new Node3D { Name = "Impact" };
        Core.Spawn.Effect(root);
        root.GlobalPosition = point + normal * 0.02f;
        root.Basis = BasisAlong(normal);

        var sparks = new ParticleProcessMaterial
        {
            Direction = new Vector3(0, 0, 1),
            Spread = 55f,
            InitialVelocityMin = metal ? 4f : 2f,
            InitialVelocityMax = metal ? 9f : 5f,
            Gravity = new Vector3(0, -9.8f, 0),
            ScaleMin = 0.25f,
            ScaleMax = 0.5f,
            ScaleCurve = FxLibrary.ShrinkCurve(0.3f),
            ParticleFlagAlignY = true,
            LifetimeRandomness = 0.5f,
        };
        var sparkParticles = FxLibrary.Particles(FxLibrary.SparkMaterial, sparks, metal ? 14 : 5, 0.35f, oneShot: true);
        sparkParticles.Emitting = true;
        root.AddChild(sparkParticles);

        if (!metal)
        {
            var dust = new ParticleProcessMaterial
            {
                Direction = new Vector3(0, 0, 1),
                Spread = 30f,
                InitialVelocityMin = 0.6f,
                InitialVelocityMax = 1.6f,
                Gravity = new Vector3(0, 0.2f, 0),
                DampingMin = 2f,
                DampingMax = 3f,
                ScaleMin = 0.18f,
                ScaleMax = 0.3f,
                ScaleCurve = FxLibrary.GrowCurve(0.4f, 1.8f, 0.3f),
                AngleMin = 0,
                AngleMax = 360,
            };
            var dustParticles = FxLibrary.Particles(FxLibrary.DustMaterial, dust, 4, 0.9f, oneShot: true);
            dustParticles.Emitting = true;
            root.AddChild(dustParticles);
        }

        root.GetTree().CreateTimer(1.5f, processAlways: false).Timeout += root.QueueFree;

        if (leaveDecal)
        {
            AddDecal(point, normal);
        }
    }

    private static void AddDecal(Vector3 point, Vector3 normal)
    {
        while (Decals.Count > 0 && !GodotObject.IsInstanceValid(Decals.Peek()))
        {
            Decals.Dequeue();
        }
        if (Decals.Count >= MaxDecals)
        {
            Decals.Dequeue().QueueFree();
        }
        var along = BasisAlong(normal);
        // Decals project along local -Y, so point +Y out of the surface.
        var decal = new Decal
        {
            Size = new Vector3(0.09f, 0.1f, 0.09f),
            TextureAlbedo = FxLibrary.BulletHoleTexture,
            CullMask = RenderLayers.Static,
            UpperFade = 0.2f,
            LowerFade = 0.2f,
        };
        Core.Spawn.InWorld(decal);
        decal.GlobalTransform = new Transform3D(new Basis(along.Column0, along.Column2, -along.Column1), point);
        decal.RotateObjectLocal(Vector3.Up, (float)GD.RandRange(0, Mathf.Tau));
        Decals.Enqueue(decal);
    }

    /// <summary>A basis whose +Z is <paramref name="forward"/>.</summary>
    public static Basis BasisAlong(Vector3 forward)
    {
        var up = Mathf.Abs(forward.Dot(Vector3.Up)) > 0.95f ? Vector3.Right : Vector3.Up;
        var x = up.Cross(forward).Normalized();
        var y = forward.Cross(x).Normalized();
        return new Basis(x, y, forward);
    }
}
