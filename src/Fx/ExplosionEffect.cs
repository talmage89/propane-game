using Godot;
using Propane.Core;

namespace Propane.Fx;

/// <summary>
/// The visual side of a tank blast: core flash, fireball billows, the pre-rendered flipbook, smoke column, embers,
/// a ground dust ring, a shockwave and a light flash. Frees itself once everything has faded.
/// </summary>
public partial class ExplosionEffect : Node3D
{
    private const float FlipbookDuration = 64f / 24f;
    private const float FlipbookWidth = 2.5f;
    private const float FlipbookHeight = 4f;
    private const float FlipbookScale = 1.35f;
    private const float CoreFlashDuration = 0.16f;
    private const float ShockwaveDuration = 0.45f;
    private const float GroundRingDuration = 0.55f;
    private const float Lifetime = 7f;

    /// <summary>Only a few blasts at a time cast flash shadows; in a chain reaction the rest light without them.</summary>
    private const int MaxShadowedFlashes = 3;

    private static Texture2D? flipbookTexture;
    private static int shadowedFlashes;
    private bool castsShadow;

    private float elapsed;
    private OmniLight3D flash = null!;
    private MeshInstance3D coreFlash = null!;
    private ShaderMaterial coreMaterial = null!;
    private MeshInstance3D flipbook = null!;
    private ShaderMaterial flipbookMaterial = null!;
    private MeshInstance3D shockwave = null!;
    private ShaderMaterial shockwaveMaterial = null!;
    private MeshInstance3D groundRing = null!;
    private ShaderMaterial groundRingMaterial = null!;
    private float groundHeight;

    /// <summary>Loads the textures an explosion uses, so the first one does not stall on disk.</summary>
    public static void Preload() => flipbookTexture ??= GD.Load<Texture2D>("res://assets/tank/T_Fireball_SubUV_8x8.png");

    /// <summary>Spawns an explosion centred at <paramref name="position"/>, with the ground at <paramref name="groundY"/>.</summary>
    public static ExplosionEffect Create(Vector3 position, float groundY)
    {
        var effect = new ExplosionEffect { Name = "Explosion" };
        effect.groundHeight = groundY;
        Spawn.Effect(effect);
        effect.GlobalPosition = position;
        return effect;
    }

    public override void _Ready()
    {
        var tuning = Tuning.Current;
        var scale = tuning.FireballScale;
        var groundOffset = groundHeight - GlobalPosition.Y;

        flash = new OmniLight3D
        {
            LightColor = new Color(1f, 0.62f, 0.3f),
            LightEnergy = tuning.FlashEnergy,
            OmniRange = tuning.FlashRange,
            OmniAttenuation = 1.4f,
            ShadowEnabled = castsShadow = shadowedFlashes < MaxShadowedFlashes,
            LightSize = 0.4f,
            Position = new Vector3(0, 0.8f * scale, 0),
        };
        AddChild(flash);
        if (castsShadow)
        {
            shadowedFlashes++;
        }

        coreMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/fx_glow.gdshader") };
        coreMaterial.SetShaderParameter("dot_texture", FxLibrary.SoftDotTexture);
        coreMaterial.SetShaderParameter("color", new Color(1f, 0.85f, 0.6f));
        coreMaterial.SetShaderParameter("intensity", 5f);
        coreFlash = new MeshInstance3D { Mesh = new QuadMesh { Size = Vector2.One, Material = coreMaterial } };
        AddChild(coreFlash);

        flipbookTexture ??= GD.Load<Texture2D>("res://assets/tank/T_Fireball_SubUV_8x8.png");
        flipbookMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/fx_flipbook.gdshader") };
        flipbookMaterial.SetShaderParameter("flipbook", flipbookTexture);
        flipbookMaterial.SetShaderParameter("emission", 2.2f);
        flipbook = new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = Vector2.One, Material = flipbookMaterial },
            Scale = new Vector3(FlipbookWidth, FlipbookHeight, 1) * scale * FlipbookScale,
            Position = new Vector3(0, groundOffset + FlipbookHeight * scale * FlipbookScale * 0.5f, 0),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            SortingOffset = -0.5f,
        };
        AddChild(flipbook);

        AddChild(CreateFireballCore(scale));
        AddChild(CreateFireballBillows(scale));
        AddChild(CreateSmoke(scale, tuning.SmokeAmount));
        AddChild(CreateEmbers(scale, tuning.EmberAmount));
        var dust = CreateDust(scale);
        dust.Position = new Vector3(0, groundOffset + 0.25f, 0);
        AddChild(dust);

        shockwaveMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/fx_shockwave.gdshader") };
        shockwave = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 0.5f, Height = 1f, RadialSegments = 32, Rings = 16, Material = shockwaveMaterial },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = tuning.ShockwaveScale > 0,
        };
        AddChild(shockwave);

        groundRingMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/fx_ground_ring.gdshader") };
        groundRingMaterial.SetShaderParameter("noise_texture", FxLibrary.Noise);
        groundRing = new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = Vector2.One * 2f, Material = groundRingMaterial },
            Position = new Vector3(0, groundOffset + 0.06f, 0),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = tuning.ShockwaveScale > 0,
        };
        AddChild(groundRing);

        UpdateLayers(0);
    }

    public override void _ExitTree()
    {
        if (castsShadow)
        {
            shadowedFlashes--;
            castsShadow = false;
        }
    }

    public override void _Process(double delta)
    {
        elapsed += (float)delta;
        UpdateLayers(elapsed);
        if (elapsed > Lifetime)
        {
            QueueFree();
        }
    }

    private void UpdateLayers(float t)
    {
        var tuning = Tuning.Current;
        var scale = tuning.FireballScale;

        // Light: a hard pop that decays with a long orange tail.
        var flashT = t / Mathf.Max(tuning.FlashDuration, 0.01f);
        flash.LightEnergy = tuning.FlashEnergy * Mathf.Pow(Mathf.Max(0, 1f - flashT), 2.2f)
                            + tuning.FlashEnergy * 0.08f * Mathf.Max(0, 1f - t / 2.4f);
        flash.LightColor = new Color(1f, 0.72f, 0.42f).Lerp(new Color(1f, 0.42f, 0.12f), Mathf.Clamp(flashT, 0, 1));
        flash.Visible = flash.LightEnergy > 0.01f;

        var core = Mathf.Clamp(t / CoreFlashDuration, 0, 1);
        coreFlash.Scale = Vector3.One * Mathf.Lerp(1.2f, 4.5f, Mathf.Sqrt(core)) * scale;
        coreMaterial.SetShaderParameter("opacity", 1f - core);
        coreFlash.Visible = core < 1f;

        var book = Mathf.Clamp((t - 0.03f) / FlipbookDuration, 0, 1);
        flipbookMaterial.SetShaderParameter("progress", book);
        // The flipbook's opening frames are a small dull ball that the particle fireball already covers, and its
        // later frames thin into a lone flame stem, so only the mushroom-forming middle shows.
        var bookIn = Mathf.SmoothStep(0.07f, 0.18f, book);
        var bookOut = 1f - Mathf.SmoothStep(0.22f, 0.34f, book);
        flipbookMaterial.SetShaderParameter("opacity", bookIn * bookOut * 0.9f);
        flipbookMaterial.SetShaderParameter("emission", Mathf.Lerp(3.2f, 1.1f, Mathf.SmoothStep(0.25f, 0.6f, book)));
        flipbook.Visible = book < 1f;

        var shock = Mathf.Clamp(t / ShockwaveDuration, 0, 1);
        shockwave.Scale = Vector3.One * Mathf.Lerp(0.5f, 18f, 1f - Mathf.Pow(1f - shock, 2.5f)) * tuning.ShockwaveScale;
        shockwaveMaterial.SetShaderParameter("progress", shock);
        shockwave.Visible = shock < 1f && tuning.ShockwaveScale > 0;

        var ring = Mathf.Clamp(t / GroundRingDuration, 0, 1);
        groundRing.Scale = Vector3.One * Mathf.Lerp(0.4f, 9f, 1f - Mathf.Pow(1f - ring, 3f)) * tuning.ShockwaveScale;
        groundRingMaterial.SetShaderParameter("progress", ring);
        groundRing.Visible = ring < 1f && tuning.ShockwaveScale > 0;
    }

    private static GpuParticles3D CreateFireballCore(float scale)
    {
        // A dense, slow ball that merges into one rolling mass.
        var process = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = 0.45f * scale,
            Direction = Vector3.Up,
            Spread = 180f,
            InitialVelocityMin = 1f * scale,
            InitialVelocityMax = 3.5f * scale,
            Gravity = new Vector3(0, 2.6f, 0),
            DampingMin = 2f,
            DampingMax = 4f,
            ScaleMin = 1.9f * scale,
            ScaleMax = 2.7f * scale,
            ScaleCurve = FxLibrary.GrowCurve(0.35f, 1.5f, 0.12f),
            AngleMin = 0,
            AngleMax = 360,
            AngularVelocityMin = -25,
            AngularVelocityMax = 25,
            LifetimeRandomness = 0.3f,
        };
        var particles = FxLibrary.Particles(FxLibrary.FireMaterial, process, 22, 1.25f, oneShot: true);
        particles.DrawOrder = GpuParticles3D.DrawOrderEnum.ViewDepth;
        particles.Emitting = true;
        return particles;
    }

    private static GpuParticles3D CreateFireballBillows(float scale)
    {
        // Faster lobes that punch outward and curl up, giving the ball a ragged silhouette.
        var process = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = 0.3f * scale,
            Direction = Vector3.Up,
            Spread = 110f,
            InitialVelocityMin = 5f * scale,
            InitialVelocityMax = 10f * scale,
            Gravity = new Vector3(0, 3.5f, 0),
            DampingMin = 8f,
            DampingMax = 12f,
            ScaleMin = 1.1f * scale,
            ScaleMax = 1.7f * scale,
            ScaleCurve = FxLibrary.GrowCurve(0.4f, 1.7f, 0.15f),
            AngleMin = 0,
            AngleMax = 360,
            AngularVelocityMin = -40,
            AngularVelocityMax = 40,
            LifetimeRandomness = 0.35f,
        };
        var particles = FxLibrary.Particles(FxLibrary.FireMaterial, process, 26, 1.6f, oneShot: true);
        particles.DrawOrder = GpuParticles3D.DrawOrderEnum.ViewDepth;
        particles.Emitting = true;
        return particles;
    }

    private static GpuParticles3D CreateSmoke(float scale, float amount)
    {
        var process = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = 0.7f * scale,
            Direction = Vector3.Up,
            Spread = 70f,
            InitialVelocityMin = 1.5f * scale,
            InitialVelocityMax = 5f * scale,
            Gravity = new Vector3(0.35f, 1.6f, 0.2f),
            DampingMin = 1.2f,
            DampingMax = 2.2f,
            ScaleMin = 2.0f * scale,
            ScaleMax = 3.2f * scale,
            ScaleCurve = FxLibrary.GrowCurve(0.45f, 1.8f, 0.3f),
            AngleMin = 0,
            AngleMax = 360,
            AngularVelocityMin = -12,
            AngularVelocityMax = 12,
            LifetimeRandomness = 0.4f,
        };
        var particles = FxLibrary.Particles(FxLibrary.SmokeMaterial, process, Mathf.RoundToInt(32 * amount), 6f, oneShot: true);
        particles.DrawOrder = GpuParticles3D.DrawOrderEnum.ViewDepth;
        particles.Explosiveness = 0.85f;
        particles.Emitting = amount > 0;
        return particles;
    }

    private static GpuParticles3D CreateEmbers(float scale, float amount)
    {
        var process = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = 0.3f,
            Direction = Vector3.Up,
            Spread = 100f,
            InitialVelocityMin = 7f * scale,
            InitialVelocityMax = 24f * scale,
            Gravity = new Vector3(0, -9.8f, 0),
            DampingMin = 0.4f,
            DampingMax = 1.5f,
            ScaleMin = 0.5f,
            ScaleMax = 1.1f,
            ScaleCurve = FxLibrary.ShrinkCurve(0.4f),
            ParticleFlagAlignY = true,
            LifetimeRandomness = 0.5f,
        };
        var particles = FxLibrary.Particles(FxLibrary.SparkMaterial, process, Mathf.RoundToInt(90 * amount), 2.2f, oneShot: true);
        particles.Emitting = amount > 0;
        return particles;
    }

    private static GpuParticles3D CreateDust(float scale)
    {
        var process = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Ring,
            EmissionRingAxis = Vector3.Up,
            EmissionRingRadius = 0.6f,
            EmissionRingInnerRadius = 0.3f,
            EmissionRingHeight = 0.1f,
            Direction = Vector3.Up,
            Spread = 10f,
            InitialVelocityMin = 0.2f,
            InitialVelocityMax = 0.8f,
            RadialVelocityMin = 7f * scale,
            RadialVelocityMax = 13f * scale,
            Gravity = new Vector3(0, 0.3f, 0),
            DampingMin = 5f,
            DampingMax = 8f,
            ScaleMin = 0.9f * scale,
            ScaleMax = 1.6f * scale,
            ScaleCurve = FxLibrary.GrowCurve(0.4f, 2.2f, 0.3f),
            AngleMin = 0,
            AngleMax = 360,
            LifetimeRandomness = 0.3f,
        };
        var particles = FxLibrary.Particles(FxLibrary.DustMaterial, process, 22, 2.6f, oneShot: true);
        particles.DrawOrder = GpuParticles3D.DrawOrderEnum.ViewDepth;
        particles.Emitting = true;
        return particles;
    }
}
