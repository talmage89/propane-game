using System.Collections.Generic;
using Godot;
using Propane.Core;

namespace Propane.Net;

/// <summary>
/// An ammo can on the sidewalk, turning slowly over a soft glowing disc. Walking into it takes it (if the server
/// agrees nobody got there first); some time later it builds back up from its base with the reveal glow.
/// </summary>
public partial class AmmoPickup : Node3D
{
    /// <summary>How close (metres, across the ground) a player must come to take it.</summary>
    public const float TakeRadius = 1.1f;

    private const float Height = 0.85f;
    private const float SpinSpeed = 1.1f;

    private static Shader? shader;

    private readonly List<GeometryInstance3D> parts = new();
    private Node3D can = null!;
    private MeshInstance3D disc = null!;
    private OmniLight3D light = null!;
    private float time;
    private float build = 1f;
    private float buildSpeed;

    public int Index { get; init; }

    /// <summary>Up and takeable, as far as this game knows.</summary>
    public bool Available { get; private set; } = true;

    /// <summary>This game asked the server for it and is waiting for the answer.</summary>
    public bool ClaimPending { get; set; }

    public override void _Ready()
    {
        shader ??= GD.Load<Shader>("res://shaders/pickup.gdshader");
        // A little larger than life, so it reads from down the street.
        can = new Node3D { Name = "Can", Position = new Vector3(0, 0.32f, 0), Scale = Vector3.One * 1.35f };
        AddChild(can);
        var body = Material(new Color(0.24f, 0.29f, 0.17f), 0.55f, 0.35f);
        var band = Material(new Color(0.98f, 0.76f, 0.12f), 0.5f, 0.1f, new Color(0.35f, 0.25f, 0.02f));
        var metal = Material(new Color(0.12f, 0.12f, 0.12f), 0.4f, 0.8f);
        Part(can, new BoxMesh { Size = new Vector3(0.46f, 0.3f, 0.2f) }, body, Vector3.Zero);
        Part(can, new BoxMesh { Size = new Vector3(0.47f, 0.07f, 0.21f) }, band, new Vector3(0, 0.03f, 0));
        Part(can, new BoxMesh { Size = new Vector3(0.48f, 0.035f, 0.22f) }, metal, new Vector3(0, 0.165f, 0));
        Part(can, new BoxMesh { Size = new Vector3(0.2f, 0.03f, 0.035f) }, metal, new Vector3(0, 0.215f, 0));
        Part(can, new BoxMesh { Size = new Vector3(0.03f, 0.05f, 0.035f) }, metal, new Vector3(-0.09f, 0.19f, 0));
        Part(can, new BoxMesh { Size = new Vector3(0.03f, 0.05f, 0.035f) }, metal, new Vector3(0.09f, 0.19f, 0));

        var discMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/pickup_disc.gdshader") };
        disc = new MeshInstance3D
        {
            Name = "Disc",
            Mesh = new PlaneMesh { Size = Vector2.One * 1.7f, Material = discMaterial },
            Position = new Vector3(0, 0.025f, 0),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(disc);
        light = new OmniLight3D { LightColor = new Color(1f, 0.8f, 0.4f), LightEnergy = 0.8f, OmniRange = 2.2f, Position = new Vector3(0, 0.5f, 0) };
        AddChild(light);
        time = Index * 1.7f;
        Apply();
    }

    public override void _Process(double delta)
    {
        var dt = (float)delta;
        time += dt;
        can.Rotation = new Vector3(0, time * SpinSpeed, 0);
        can.Position = new Vector3(0, 0.36f + Mathf.Sin(time * 2.2f) * 0.05f, 0);
        if (buildSpeed > 0 && build < 1f)
        {
            build = Mathf.Min(1f, build + dt * buildSpeed);
            Apply();
        }
    }

    /// <summary>Someone took it: it vanishes at once.</summary>
    public void Take()
    {
        Available = false;
        ClaimPending = false;
        build = 0f;
        buildSpeed = 0f;
        Apply();
    }

    /// <summary>It is back: it builds up from the base over <paramref name="seconds"/>.</summary>
    public void Reveal(float seconds)
    {
        Available = true;
        ClaimPending = false;
        build = 0f;
        buildSpeed = 1f / Mathf.Max(seconds, 0.01f);
        Apply();
    }

    private void Apply()
    {
        Visible = build > 0f;
        var baseY = GlobalPosition.Y;
        foreach (var part in parts)
        {
            part.SetInstanceShaderParameter("build", build);
            part.SetInstanceShaderParameter("build_base", baseY);
            part.SetInstanceShaderParameter("build_height", Height);
        }
        disc.SetInstanceShaderParameter("strength", build);
        light.LightEnergy = 0.8f * build;
    }

    private void Part(Node3D parent, Mesh mesh, Material material, Vector3 position)
    {
        var instance = new MeshInstance3D { Mesh = mesh, MaterialOverride = material, Position = position, Layers = RenderLayers.Actors };
        parent.AddChild(instance);
        parts.Add(instance);
    }

    private static ShaderMaterial Material(Color color, float roughness, float metallic, Color? emission = null)
    {
        var material = new ShaderMaterial { Shader = shader };
        material.SetShaderParameter("albedo_color", color);
        material.SetShaderParameter("roughness", roughness);
        material.SetShaderParameter("metallic", metallic);
        material.SetShaderParameter("emission_color", emission ?? Colors.Black);
        return material;
    }
}
