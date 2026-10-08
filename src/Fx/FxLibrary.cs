using Godot;

namespace Propane.Fx;

/// <summary>Shared textures, meshes and materials for particle effects, built once in code.</summary>
public static class FxLibrary
{
    private static NoiseTexture2D? noise;
    private static QuadMesh? quad;
    private static ShaderMaterial? fireMaterial;
    private static ShaderMaterial? ventFireMaterial;
    private static ShaderMaterial? smokeMaterial;
    private static ShaderMaterial? dustMaterial;
    private static ShaderMaterial? sparkMaterial;
    private static ShaderMaterial? ventSmokeMaterial;
    private static ImageTexture? scorchTexture;
    private static ImageTexture? bulletHoleTexture;
    private static ImageTexture? softDotTexture;

    public static NoiseTexture2D Noise => noise ??= new NoiseTexture2D
    {
        Width = 256,
        Height = 256,
        Seamless = true,
        GenerateMipmaps = true,
        Noise = new FastNoiseLite
        {
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            Frequency = 0.012f,
            FractalOctaves = 4,
            FractalGain = 0.55f,
        },
    };

    public static QuadMesh Quad => quad ??= new QuadMesh { Size = Vector2.One };

    public static ShaderMaterial FireMaterial => fireMaterial ??= Make("res://shaders/fx_fire.gdshader", m =>
    {
        m.SetShaderParameter("emission", 1.8f);
        m.SetShaderParameter("burnout", 0.55f);
    });

    public static ShaderMaterial VentFireMaterial => ventFireMaterial ??= Make("res://shaders/fx_fire.gdshader", m =>
    {
        m.SetShaderParameter("emission", 1.9f);
        m.SetShaderParameter("burnout", 0.8f);
        m.SetShaderParameter("erosion", 0.3f);
        m.SetShaderParameter("edge_softness", 0.85f);
        m.SetShaderParameter("blue_core", 0f);
        m.SetShaderParameter("alpha_scale", 0.9f);
        m.SetShaderParameter("softness", 0.15f);
    });

    public static ShaderMaterial SmokeMaterial => smokeMaterial ??= Make("res://shaders/fx_smoke.gdshader", m =>
    {
        m.SetShaderParameter("tint", new Color(0.3f, 0.29f, 0.28f));
        m.SetShaderParameter("density", 0.7f);
    });

    public static ShaderMaterial VentSmokeMaterial => ventSmokeMaterial ??= Make("res://shaders/fx_smoke.gdshader", m =>
    {
        m.SetShaderParameter("tint", new Color(0.4f, 0.39f, 0.38f));
        m.SetShaderParameter("density", 0.3f);
        m.SetShaderParameter("erosion", 0.25f);
        m.SetShaderParameter("ember_glow", 0f);
        m.SetShaderParameter("softness", 0.4f);
    });

    public static ShaderMaterial DustMaterial => dustMaterial ??= Make("res://shaders/fx_smoke.gdshader", m =>
    {
        m.SetShaderParameter("tint", new Color(0.55f, 0.52f, 0.47f));
        m.SetShaderParameter("density", 0.55f);
        m.SetShaderParameter("ember_glow", 0f);
        m.SetShaderParameter("erosion", 0.7f);
    });

    public static ShaderMaterial SparkMaterial => sparkMaterial ??= Make("res://shaders/fx_spark.gdshader", _ => { });

    /// <summary>A soft charred splotch for blast scorch decals.</summary>
    public static ImageTexture ScorchTexture => scorchTexture ??= BuildScorch(256);

    /// <summary>A small dark puncture with a bright torn rim.</summary>
    public static ImageTexture BulletHoleTexture => bulletHoleTexture ??= BuildBulletHole(64);

    /// <summary>A soft white radial dot for flashes and glows.</summary>
    public static ImageTexture SoftDotTexture => softDotTexture ??= BuildSoftDot(64);

    private static ShaderMaterial Make(string shaderPath, System.Action<ShaderMaterial> configure)
    {
        var material = new ShaderMaterial { Shader = GD.Load<Shader>(shaderPath) };
        material.SetShaderParameter("noise_texture", Noise);
        configure(material);
        return material;
    }

    /// <summary>A particle system that renders camera-facing quads with the given material.</summary>
    public static GpuParticles3D Particles(Material material, ParticleProcessMaterial process, int amount, float lifetime,
        bool oneShot)
    {
        var draw = (QuadMesh)Quad.Duplicate();
        draw.Material = material;
        return new GpuParticles3D
        {
            Amount = Mathf.Max(1, amount),
            Lifetime = lifetime,
            OneShot = oneShot,
            Explosiveness = oneShot ? 1f : 0f,
            LocalCoords = false,
            ProcessMaterial = process,
            DrawPass1 = draw,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            VisibilityAabb = new Aabb(new Vector3(-30, -10, -30), new Vector3(60, 60, 60)),
            FixedFps = 0,
            Interpolate = true,
        };
    }

    /// <summary>A curve that rises from <paramref name="start"/> to 1 with an ease-out shape.</summary>
    public static CurveTexture GrowCurve(float start, float end = 1f, float easeOut = 0.25f)
    {
        // Curves clamp their points to MaxValue (1 by default), which would silently cap growth.
        var curve = new Curve { MaxValue = Mathf.Max(1f, end * 1.25f) };
        curve.AddPoint(new Vector2(0, start), 0, (end - start) * 3f);
        curve.AddPoint(new Vector2(easeOut, start + (end - start) * 0.75f));
        curve.AddPoint(new Vector2(1, end));
        return new CurveTexture { Curve = curve };
    }

    /// <summary>A curve that holds at 1 then falls to 0.</summary>
    public static CurveTexture ShrinkCurve(float holdUntil = 0.5f)
    {
        var curve = new Curve();
        curve.AddPoint(new Vector2(0, 1));
        curve.AddPoint(new Vector2(holdUntil, 0.85f));
        curve.AddPoint(new Vector2(1, 0));
        return new CurveTexture { Curve = curve };
    }

    private static ImageTexture BuildScorch(int size)
    {
        var image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        var warp = new FastNoiseLite { NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth, Frequency = 0.012f, FractalOctaves = 3, Seed = 3 };
        var grain = new FastNoiseLite { NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth, Frequency = 0.06f, FractalOctaves = 3, Seed = 11 };
        var half = size / 2f;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                // Domain-warp the radius so the outline is irregular without any angular seams.
                var wx = warp.GetNoise2D(x, y) * 0.28f;
                var wy = warp.GetNoise2D(x + 517, y - 211) * 0.28f;
                var dx = (x - half) / half + wx;
                var dy = (y - half) / half + wy;
                var r = Mathf.Sqrt(dx * dx + dy * dy);
                var g = grain.GetNoise2D(x, y) * 0.5f + 0.5f;
                var body = 1f - Mathf.SmoothStep(0.25f, 0.9f, r);
                var core = 1f - Mathf.SmoothStep(0.0f, 0.4f, r);
                var alpha = Mathf.Clamp(body * (0.55f + g * 0.45f) + core * 0.25f, 0, 1) * 0.9f;
                var shade = 0.025f + g * 0.04f;
                image.SetPixel(x, y, new Color(shade, shade * 0.94f, shade * 0.88f, alpha));
            }
        }
        image.GenerateMipmaps();
        return ImageTexture.CreateFromImage(image);
    }

    private static ImageTexture BuildBulletHole(int size)
    {
        var image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        var half = size / 2f;
        var rng = new RandomNumberGenerator { Seed = 99 };
        var spokes = new float[24];
        for (var i = 0; i < spokes.Length; i++)
        {
            spokes[i] = rng.RandfRange(0.75f, 1.25f);
        }
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var dx = (x - half + 0.5f) / half;
                var dy = (y - half + 0.5f) / half;
                var r = Mathf.Sqrt(dx * dx + dy * dy);
                var angle = (Mathf.Atan2(dy, dx) / Mathf.Tau + 0.5f) * spokes.Length;
                var spoke = Mathf.Lerp(spokes[(int)angle % spokes.Length], spokes[((int)angle + 1) % spokes.Length], angle % 1f);
                var hole = 1f - Mathf.SmoothStep(0.16f, 0.22f, r);
                var rim = (1f - Mathf.SmoothStep(0.22f, 0.36f * spoke, r)) * (1f - hole);
                var soot = (1f - Mathf.SmoothStep(0.3f, 0.95f * spoke, r)) * 0.55f;
                var color = new Color(0.02f, 0.018f, 0.016f).Lerp(new Color(0.62f, 0.6f, 0.58f), rim * 0.9f);
                var alpha = Mathf.Clamp(Mathf.Max(hole, Mathf.Max(rim, soot)), 0, 1);
                image.SetPixel(x, y, new Color(color, alpha));
            }
        }
        image.GenerateMipmaps();
        return ImageTexture.CreateFromImage(image);
    }

    private static ImageTexture BuildSoftDot(int size)
    {
        var image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        var half = size / 2f;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var dx = (x - half + 0.5f) / half;
                var dy = (y - half + 0.5f) / half;
                var r = Mathf.Clamp(Mathf.Sqrt(dx * dx + dy * dy), 0, 1);
                var a = Mathf.Pow(1f - r, 2.2f);
                image.SetPixel(x, y, new Color(1, 1, 1, a));
            }
        }
        image.GenerateMipmaps();
        return ImageTexture.CreateFromImage(image);
    }
}
