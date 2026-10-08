using System.Linq;
using Godot;
using Propane.Core;
using Propane.Fx;

namespace Propane.Player;

/// <summary>
/// The rifle model with its sockets. Its origin is the pistol grip; -Z points down the barrel. The player's rig
/// places it every frame; this node only owns the visuals and the muzzle flash.
/// </summary>
public partial class Rifle : Node3D
{
    private const float FlashDuration = 0.06f;

    private OmniLight3D flashLight = null!;
    private MeshInstance3D flashSprite = null!;
    private ShaderMaterial flashMaterial = null!;
    private float flashTimer;

    /// <summary>Socket transforms in rifle space, read from the model.</summary>
    public Transform3D ForegripLocal { get; private set; }

    public Transform3D MuzzleLocal { get; private set; }

    public Transform3D ButtLocal { get; private set; }

    public Vector3 MuzzlePosition => GlobalTransform * MuzzleLocal.Origin;

    public override void _Ready()
    {
        var model = GameAssets.Instantiate("res://assets/rifle/rifle.glb");
        AddChild(model);
        foreach (var node in model.FindChildren("Socket_*", owned: false).OfType<Node3D>())
        {
            var local = model.Transform * node.Transform;
            switch (node.Name.ToString())
            {
                case "Socket_Foregrip": ForegripLocal = local; break;
                case "Socket_Muzzle": MuzzleLocal = local; break;
                case "Socket_Butt": ButtLocal = local; break;
            }
        }
        foreach (var mesh in GameAssets.MeshInstances(model))
        {
            mesh.Layers = RenderLayers.Actors;
            for (var surface = 0; surface < mesh.Mesh.GetSurfaceCount(); surface++)
            {
                var name = mesh.Mesh.SurfaceGetMaterial(surface)?.ResourceName ?? "";
                mesh.SetSurfaceOverrideMaterial(surface, MaterialFor(name));
            }
        }

        flashMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/fx_glow.gdshader") };
        flashMaterial.SetShaderParameter("dot_texture", FxLibrary.SoftDotTexture);
        flashMaterial.SetShaderParameter("color", new Color(1f, 0.55f, 0.18f));
        flashMaterial.SetShaderParameter("intensity", 3f);
        flashMaterial.SetShaderParameter("coverage", 0.9f);
        flashMaterial.SetShaderParameter("core_heat", 1f);
        flashSprite = new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = Vector2.One * 0.42f, Material = flashMaterial },
            Transform = MuzzleLocal.Translated(new Vector3(0, 0, -0.06f)),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
        };
        AddChild(flashSprite);
        flashLight = new OmniLight3D
        {
            LightColor = new Color(1f, 0.7f, 0.4f),
            LightEnergy = 5f,
            OmniRange = 5f,
            Position = MuzzleLocal.Origin + new Vector3(0, 0, -0.1f),
            Visible = false,
        };
        AddChild(flashLight);
    }

    public override void _Process(double delta)
    {
        if (flashTimer <= 0)
        {
            return;
        }
        flashTimer -= (float)delta;
        var t = Mathf.Clamp(flashTimer / FlashDuration, 0, 1);
        flashMaterial.SetShaderParameter("opacity", t);
        flashLight.LightEnergy = 5f * t;
        if (flashTimer <= 0)
        {
            flashSprite.Visible = false;
            flashLight.Visible = false;
        }
    }

    public void Flash()
    {
        flashTimer = FlashDuration;
        flashSprite.Visible = true;
        flashLight.Visible = true;
        flashSprite.Scale = Vector3.One * (float)GD.RandRange(0.8, 1.2);
        flashSprite.RotateObjectLocal(Vector3.Back, (float)GD.RandRange(0, Mathf.Tau));
    }

    private static Material MaterialFor(string name) => name switch
    {
        "RifleMetal" => new StandardMaterial3D { AlbedoColor = new Color(0.075f, 0.08f, 0.085f), Metallic = 0.7f, Roughness = 0.42f },
        "RiflePolymer" => new StandardMaterial3D { AlbedoColor = new Color(0.14f, 0.145f, 0.15f), Roughness = 0.75f },
        "RifleAccent" => new StandardMaterial3D { AlbedoColor = new Color(0.86f, 0.87f, 0.88f), Roughness = 0.55f },
        "RifleLens" => new StandardMaterial3D
        {
            AlbedoColor = new Color(0.05f, 0.12f, 0.14f),
            Roughness = 0.1f,
            Metallic = 0.3f,
            EmissionEnabled = true,
            Emission = new Color(0.1f, 0.6f, 0.7f),
            EmissionEnergyMultiplier = 0.4f,
        },
        _ => new StandardMaterial3D { AlbedoColor = new Color(0.5f, 0.5f, 0.5f) },
    };
}
