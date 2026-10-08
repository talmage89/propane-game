using Godot;
using Propane.Core;
using Propane.Fx;

namespace Propane.Tank;

/// <summary>
/// The burning jet out of a punctured tank. Its local +Z axis points out of the hole. Flames are simulated in
/// world space, so they trail and curl as the tank skids and spins.
/// </summary>
public partial class VentJet : Node3D
{
    private const float FlameLifetime = 0.36f;
    private const float SmokeLifetime = 4.5f;

    private GpuParticles3D flames = null!;
    private GpuParticles3D smoke = null!;
    private OmniLight3D light = null!;
    private MeshInstance3D core = null!;
    private ShaderMaterial coreMaterial = null!;
    private MeshInstance3D jet = null!;
    private ShaderMaterial jetMaterial = null!;
    private float time;
    private float ignition;
    private Vector3 smokeOffset;

    public override void _Ready()
    {
        var scale = Tuning.Current.VentFlameScale;

        // Many small, short-lived puffs overlap into one continuous tongue of flame. They live in world space,
        // so a spinning tank sprays a curling spiral rather than a rigid stick.
        var flameProcess = new ParticleProcessMaterial
        {
            Direction = new Vector3(0, 0, 1),
            Spread = 6f,
            InitialVelocityMin = 3f * scale,
            InitialVelocityMax = 4.2f * scale,
            // Buoyancy curls the tip of the jet upward.
            Gravity = new Vector3(0, 6f, 0),
            DampingMin = 4f,
            DampingMax = 7f,
            ScaleMin = 0.14f * scale,
            ScaleMax = 0.22f * scale,
            ScaleCurve = FxLibrary.GrowCurve(0.6f, 2.8f, 0.4f),
            AngleMin = 0,
            AngleMax = 360,
            AngularVelocityMin = -180,
            AngularVelocityMax = 180,
            LifetimeRandomness = 0.35f,
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = 0.01f,
        };
        flames = FxLibrary.Particles(FxLibrary.VentFireMaterial, flameProcess, 160, FlameLifetime, oneShot: false);
        flames.DrawOrder = GpuParticles3D.DrawOrderEnum.ViewDepth;
        flames.Emitting = true;
        flames.Position = new Vector3(0, 0, 0.2f * scale);
        AddChild(flames);

        // The smoke column rises straight up whichever way the jet points, so it shows over fences and roofs:
        // the one cue to where a venting tank has got to. Its emitter follows the jet tip but never rotates.
        var smokeProcess = new ParticleProcessMaterial
        {
            Direction = Vector3.Up,
            Spread = 18f,
            InitialVelocityMin = 1.1f,
            InitialVelocityMax = 1.8f,
            // A light breeze leans the column.
            Gravity = new Vector3(0.3f, 0.45f, 0.18f),
            ScaleMin = 0.35f * Mathf.Sqrt(scale),
            ScaleMax = 0.55f * Mathf.Sqrt(scale),
            ScaleCurve = FxLibrary.GrowCurve(0.35f, 4f, 0.5f),
            AngleMin = 0,
            AngleMax = 360,
            AngularVelocityMin = -25,
            AngularVelocityMax = 25,
            LifetimeRandomness = 0.3f,
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = 0.15f,
        };
        smoke = FxLibrary.Particles(FxLibrary.VentSmokeMaterial, smokeProcess, 90, SmokeLifetime, oneShot: false);
        smoke.DrawOrder = GpuParticles3D.DrawOrderEnum.ViewDepth;
        smoke.TopLevel = true;
        smoke.Emitting = true;
        AddChild(smoke);
        smokeOffset = new Vector3(0, 0, 0.9f * scale);
        smoke.GlobalPosition = GlobalTransform * smokeOffset;

        coreMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/fx_glow.gdshader") };
        coreMaterial.SetShaderParameter("dot_texture", FxLibrary.SoftDotTexture);
        coreMaterial.SetShaderParameter("color", new Color(0.45f, 0.6f, 1f));
        coreMaterial.SetShaderParameter("intensity", 6f);
        core = new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = Vector2.One * 0.16f, Material = coreMaterial },
            Position = new Vector3(0, 0, 0.05f),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(core);

        jetMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/fx_jet.gdshader") };
        jetMaterial.SetShaderParameter("noise_texture", FxLibrary.Noise);
        jetMaterial.SetShaderParameter("length_m", 0.75f * scale);
        jetMaterial.SetShaderParameter("width_m", 0.26f * scale);
        jet = new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = Vector2.One, Material = jetMaterial },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            ExtraCullMargin = 1.5f,
            SortingOffset = 0.2f,
        };
        AddChild(jet);

        light = new OmniLight3D
        {
            LightColor = new Color(1f, 0.6f, 0.28f),
            OmniRange = 5.5f,
            OmniAttenuation = 1.6f,
            // Unshadowed: a group of venting tanks would otherwise render a shadow cubemap each, every frame.
            ShadowEnabled = false,
            Position = new Vector3(0, 0, 0.45f * scale),
        };
        AddChild(light);
    }

    public override void _Process(double delta)
    {
        time += (float)delta;
        smoke.GlobalPosition = GlobalTransform * smokeOffset;
        ignition = Mathf.Min(1f, ignition + (float)delta * 6f);
        // Layered sines give an irregular flicker without a noise lookup.
        var flicker = 0.78f + 0.12f * Mathf.Sin(time * 31f) + 0.07f * Mathf.Sin(time * 53f + 1.3f) + 0.05f * Mathf.Sin(time * 97f);
        light.LightEnergy = Tuning.Current.VentLightEnergy * flicker * ignition;
        core.Scale = Vector3.One * (0.85f + 0.3f * flicker);
        jetMaterial.SetShaderParameter("flicker", (0.75f + 0.35f * flicker) * ignition);
        coreMaterial.SetShaderParameter("opacity", ignition);
    }

    /// <summary>Stops the jet. The smoke already in the air drifts on and fades, even after the tank is gone.</summary>
    public void Extinguish()
    {
        smoke.Emitting = false;
        Spawn.Detach(smoke);
        var lingering = smoke;
        lingering.GetTree().CreateTimer(SmokeLifetime, processAlways: false).Timeout += () =>
        {
            if (IsInstanceValid(lingering))
            {
                lingering.QueueFree();
            }
        };
        QueueFree();
    }
}
