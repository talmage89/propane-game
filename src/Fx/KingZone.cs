using System;
using System.Collections.Generic;
using Godot;
using Propane.Core;
using Propane.Modes;
using Propane.Tank;
using Propane.UI;

namespace Propane.Fx;

/// <summary>
/// King of the Cul-de-sac's zone as a peer draws it from <see cref="KingState"/>, never from rules it runs: a beam and
/// a ground ring on the zone, tinted by its holder and pulsing red while contested; both pulse while the move warns,
/// when a pale beam marks the next spot. Fresh piles build themselves out of a scan ring here too. The Lab and a match
/// share it, so the zone looks the same offline and online.
/// </summary>
public partial class KingZone : Node3D
{
    private static readonly Color Contest = new(0.95f, 0.2f, 0.15f);

    public required WorldContext World { get; init; }
    /// <summary>The zone to draw now (already at the current round time), or null to hide it.</summary>
    public required Func<KingState?> State { get; init; }
    /// <summary>An actor's body color, for the holder's tint; null when it is unknown.</summary>
    public required Func<int, Color?> ActorColor { get; init; }

    private readonly List<(PropaneTank Tank, Vector3 Center, float Age)> materializing = new();
    private MeshInstance3D beam = null!;
    private MeshInstance3D ring = null!;
    private MeshInstance3D disc = null!;
    private MeshInstance3D ghost = null!;
    private StandardMaterial3D beamMaterial = null!;
    private StandardMaterial3D ringMaterial = null!;
    private StandardMaterial3D discMaterial = null!;
    private StandardMaterial3D ghostMaterial = null!;
    private float shapedRadius = -1;
    private float shapedHeight = -1;
    private float clock;

    /// <summary>Fresh piles that have begun to build themselves here.</summary>
    public int Materialized { get; private set; }

    public override void _Ready()
    {
        beamMaterial = Glow(UiStyle.Accent, World.Tuning.KingBeamAlpha);
        ringMaterial = Glow(UiStyle.Accent, 0.9f);
        discMaterial = Glow(UiStyle.Accent, 0.2f);
        ghostMaterial = Glow(Colors.White, 0.2f);
        beam = Part("ZoneBeam", beamMaterial);
        ring = Part("ZoneRing", ringMaterial);
        disc = Part("ZoneFloor", discMaterial);
        ghost = Part("NextZoneBeam", ghostMaterial);
    }

    private MeshInstance3D Part(string name, Material material)
    {
        var part = new MeshInstance3D
        {
            Name = name,
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
        };
        AddChild(part);
        return part;
    }

    private static StandardMaterial3D Glow(Color color, float alpha) => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        AlbedoColor = new Color(color, alpha),
    };

    /// <summary>The meshes follow the zone's radius, which a match's server sets, and this peer's beam height.</summary>
    private void Shape(float radius, float height)
    {
        if (Mathf.IsEqualApprox(radius, shapedRadius) && Mathf.IsEqualApprox(height, shapedHeight)) return;
        shapedRadius = radius;
        shapedHeight = height;
        beam.Mesh = new CylinderMesh { TopRadius = radius, BottomRadius = radius, Height = height, CapTop = false, CapBottom = false };
        ring.Mesh = new TorusMesh { InnerRadius = Mathf.Max(0.01f, radius - 0.18f), OuterRadius = radius };
        disc.Mesh = new CylinderMesh { TopRadius = radius, BottomRadius = radius, Height = 0.02f };
        ghost.Mesh = new CylinderMesh { TopRadius = radius * 0.5f, BottomRadius = radius * 0.5f, Height = height, CapTop = false, CapBottom = false };
    }

    /// <summary>A fresh pile builds itself around <paramref name="center"/>, growing out of a scan ring.</summary>
    public void Materialize(IEnumerable<PropaneTank> tanks, Vector3 center)
    {
        foreach (var tank in tanks)
        {
            SetReveal(tank, 1f, center, 0);
            materializing.Add((tank, center, 0));
        }
        Materialized++;
    }

    private static void SetReveal(Node root, float side, Vector3 center, float radius)
    {
        foreach (var node in root.FindChildren("*", "GeometryInstance3D", owned: false))
            Reveal(node, side, center, radius);
        Reveal(root, side, center, radius);
    }

    private static void Reveal(Node node, float side, Vector3 center, float radius)
    {
        if (node is not GeometryInstance3D geometry || geometry is GpuParticles3D) return;
        geometry.SetInstanceShaderParameter("reveal_side", side);
        geometry.SetInstanceShaderParameter("reveal_center", center);
        geometry.SetInstanceShaderParameter("reveal_radius", radius);
    }

    public override void _Process(double delta)
    {
        var tuning = World.Tuning;
        var dt = (float)delta;
        clock += dt;
        var state = State();
        beam.Visible = ring.Visible = disc.Visible = state != null;
        ghost.Visible = state is { Warning: true };
        if (state != null)
        {
            Shape((float)state.Radius, tuning.KingBeamHeight);
            var pulse = 0.5f + 0.5f * Mathf.Sin(clock * Mathf.Tau * tuning.LabWarningPulseHz);
            var spot = Point(state.Spot);
            beam.Position = spot + Vector3.Up * (tuning.KingBeamHeight / 2);
            ring.Position = spot + Vector3.Up * 0.06f;
            disc.Position = spot + Vector3.Up * 0.03f;
            var color = state.Contested ? Contest : state.Holder != 0 ? ActorColor(state.Holder) ?? UiStyle.Accent : UiStyle.Accent;
            var strength = state.Contested || state.Warning ? 0.35f + 0.65f * pulse : 1f;
            beamMaterial.AlbedoColor = new Color(color, tuning.KingBeamAlpha * strength);
            ringMaterial.AlbedoColor = new Color(color, 0.9f * strength);
            discMaterial.AlbedoColor = new Color(color, 0.22f * strength);
            if (ghost.Visible)
            {
                ghost.Position = Point(state.Next) + Vector3.Up * (tuning.KingBeamHeight / 2);
                ghostMaterial.AlbedoColor = new Color(UiStyle.Accent.Lightened(0.5f), (0.25f + 0.5f * pulse) * tuning.KingBeamAlpha);
            }
        }
        for (var i = materializing.Count - 1; i >= 0; i--)
        {
            var (tank, center, age) = materializing[i];
            age += dt;
            if (!IsInstanceValid(tank)) { materializing.RemoveAt(i); continue; }
            var done = age >= tuning.KingMaterializeSeconds;
            SetReveal(tank, done ? 0f : 1f, center, done ? 100000f : age / Mathf.Max(0.05f, tuning.KingMaterializeSeconds) * 3.5f);
            if (done) materializing.RemoveAt(i);
            else materializing[i] = (tank, center, age);
        }
    }

    public static Vector3 Point(ModeSpot spot) => new((float)spot.X, (float)spot.Y, (float)spot.Z);
}
