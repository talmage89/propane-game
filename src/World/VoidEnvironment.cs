using Godot;
using Propane.Core;

namespace Propane.World;

/// <summary>
/// The white VR-construct void: a pale sky, a warm low late-afternoon sun, and an infinite gridded floor. The floor's
/// collider and mesh follow <see cref="Follow"/> so the plane never ends, however far the player walks.
/// </summary>
public partial class VoidEnvironment : Node3D
{
    private const float FloorMeshSize = 1200f;
    private const float FloorSnap = 10f;

    private StaticBody3D floorBody = null!;
    private MeshInstance3D floorMesh = null!;

    /// <summary>The node the floor recenters on (normally the camera).</summary>
    public Node3D? Follow { get; set; }

    public DirectionalLight3D Sun { get; private set; } = null!;

    public Godot.Environment Environment { get; private set; } = null!;

    public override void _Ready()
    {
        var skyMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/void_sky.gdshader") };
        var environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky { SkyMaterial = skyMaterial, ProcessMode = Sky.ProcessModeEnum.Quality, RadianceSize = Sky.RadianceSizeEnum.Size256 },
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            AmbientLightSkyContribution = 1f,
            AmbientLightEnergy = 0.5f,
            ReflectedLightSource = Godot.Environment.ReflectionSource.Sky,
            TonemapMode = Godot.Environment.ToneMapper.Agx,
            TonemapExposure = 1.05f,
            TonemapAgxWhite = 3.4f,
            SsaoEnabled = true,
            SsaoRadius = 1.4f,
            SsaoIntensity = 1.6f,
            SsaoPower = 1.4f,
            SsaoDetail = 0.5f,
            SsaoLightAffect = 0.15f,
            SsilEnabled = true,
            SsilRadius = 4f,
            SsilIntensity = 0.6f,
            GlowEnabled = true,
            GlowIntensity = 0.55f,
            GlowStrength = 1f,
            GlowBloom = 0.02f,
            GlowHdrThreshold = 1.2f,
            GlowHdrScale = 2f,
            GlowBlendMode = Godot.Environment.GlowBlendModeEnum.Softlight,
            FogEnabled = true,
            FogMode = Godot.Environment.FogModeEnum.Depth,
            FogLightColor = new Color(1f, 0.9f, 0.8f),
            FogLightEnergy = 1f,
            FogDensity = 1f,
            FogDepthBegin = 120f,
            FogDepthEnd = 700f,
            FogDepthCurve = 1.6f,
            FogSkyAffect = 0f,
            FogSunScatter = 0.3f,
            AdjustmentEnabled = true,
            AdjustmentContrast = 1.04f,
            AdjustmentSaturation = 1.12f,
        };
        Environment = environment;
        AddChild(new WorldEnvironment { Environment = environment, Name = "WorldEnvironment" });

        // Golden hour: the sun sits about 8 degrees up, warm and low, so shadows stretch far across the white floor.
        Sun = new DirectionalLight3D
        {
            Name = "Sun",
            LightColor = new Color(1f, 0.6f, 0.32f),
            LightEnergy = 3.6f,
            LightAngularDistance = 1.2f,
            ShadowEnabled = true,
            ShadowBlur = 1.2f,
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel4Splits,
            DirectionalShadowMaxDistance = 140f,
            DirectionalShadowSplit1 = 0.06f,
            DirectionalShadowSplit2 = 0.16f,
            DirectionalShadowSplit3 = 0.42f,
            DirectionalShadowBlendSplits = true,
            DirectionalShadowFadeStart = 0.85f,
            ShadowBias = 0.03f,
            ShadowNormalBias = 1.2f,
        };
        AddChild(Sun);
        Sun.RotationDegrees = new Vector3(-8f, -38f, 0f);

        floorBody = new StaticBody3D { Name = "VoidFloor", CollisionLayer = Layers.Floor, CollisionMask = 0 };
        floorBody.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D { Plane = new Plane(Vector3.Up, 0) } });
        AddChild(floorBody);

        var floorMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/void_floor.gdshader") };
        floorMesh = new MeshInstance3D
        {
            Name = "VoidFloorMesh",
            Mesh = new PlaneMesh { Size = new Vector2(FloorMeshSize, FloorMeshSize), Material = floorMaterial },
            Position = new Vector3(0, -0.02f, 0),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(floorMesh);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (Follow == null || !IsInstanceValid(Follow))
        {
            return;
        }
        var p = Follow.GlobalPosition;
        // Snapping keeps the plane's transform stable; the grid is drawn in world space so it never slides.
        var snapped = new Vector3(Mathf.Snapped(p.X, FloorSnap), 0, Mathf.Snapped(p.Z, FloorSnap));
        floorBody.GlobalPosition = snapped;
        floorMesh.GlobalPosition = snapped + new Vector3(0, -0.02f, 0);
    }
}
