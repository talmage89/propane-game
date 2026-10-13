using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Modes;
using Propane.Tank;
using Propane.UI;

namespace Propane.Lab;

/// <summary>
/// King of the Cul-de-sac's presenter: the zone's beam and ground ring, tinted by its holder and pulsing red while
/// contested; a pale beam at the next spot while the move warns; and a fresh pile that materializes wherever the
/// zone lands. In the void it also lays out the pile spots (a ring with one in the middle); in a suburb the spots
/// are the suburb's own tank groups.
/// </summary>
public partial class KingArena : Node3D
{
    private static readonly Color Contest = new(0.95f, 0.2f, 0.15f);

    public required LabSession Lab { get; init; }
    private readonly List<Vector3> spots = new();
    private readonly List<(PropaneTank Tank, Vector3 Center, float Age)> materializing = new();
    private MeshInstance3D beam = null!;
    private MeshInstance3D ring = null!;
    private MeshInstance3D ghost = null!;
    private StandardMaterial3D beamMaterial = null!;
    private StandardMaterial3D ringMaterial = null!;
    private StandardMaterial3D ghostMaterial = null!;
    private PrototypeRunner? runner;
    private float clock;

    public IReadOnlyList<Vector3> SpotPositions => spots;
    public IReadOnlyList<ModeSpot> Spots => spots.Select(s => new ModeSpot(s.X, s.Y, s.Z)).ToArray();
    public int Materialized { get; private set; }

    /// <summary>Lays out the void's pile spots, or takes a suburb's groups. Runs in the scenario's setup.</summary>
    public void Layout(IReadOnlyList<Vector3>? suburbGroups)
    {
        var tuning = Lab.World.Tuning;
        if (suburbGroups != null)
        {
            spots.AddRange(suburbGroups.Select(g => new Vector3(g.X, World.Suburb.GroundHeight, g.Z)));
            return;
        }
        spots.Add(Vector3.Zero);
        for (var i = 0; i < tuning.KingSpots; i++)
        {
            var angle = i * Mathf.Tau / tuning.KingSpots;
            spots.Add(new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * tuning.KingSpotRing);
        }
        foreach (var spot in spots) Lab.AddPile(spot, tuning.KingPileCount);
    }

    /// <summary>Where the actors start: around the middle between the ring's spots, facing in.</summary>
    public (Vector3 Position, float Yaw) Spawn(int index, int count, Vector3? suburbSpawn, float suburbYaw)
    {
        if (suburbSpawn is { } start)
            return (start + new Vector3(index % 3 - 1, 0, index / 3) * 2.2f, suburbYaw);
        var tuning = Lab.World.Tuning;
        var angle = (index + 0.5f) * Mathf.Tau / Mathf.Max(1, count) + Mathf.Pi / Mathf.Max(3, tuning.KingSpots);
        var at = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * tuning.KingSpotRing * 0.55f + Vector3.Up * 0.05f;
        return (at, Mathf.Atan2(at.X, at.Z));
    }

    public override void _Ready()
    {
        var tuning = Lab.World.Tuning;
        beamMaterial = Glow(UiStyle.Accent, 0.16f);
        ringMaterial = Glow(UiStyle.Accent, 0.5f);
        ghostMaterial = Glow(Colors.White, 0.1f);
        beam = new MeshInstance3D
        {
            Name = "ZoneBeam",
            Mesh = new CylinderMesh { TopRadius = tuning.KingZoneRadius, BottomRadius = tuning.KingZoneRadius, Height = tuning.KingBeamHeight, CapTop = false, CapBottom = false },
            MaterialOverride = beamMaterial,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
        };
        AddChild(beam);
        ring = new MeshInstance3D
        {
            Name = "ZoneRing",
            Mesh = new TorusMesh { InnerRadius = tuning.KingZoneRadius - 0.18f, OuterRadius = tuning.KingZoneRadius },
            MaterialOverride = ringMaterial,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
        };
        AddChild(ring);
        ghost = new MeshInstance3D
        {
            Name = "NextZoneBeam",
            Mesh = new CylinderMesh { TopRadius = tuning.KingZoneRadius * 0.5f, BottomRadius = tuning.KingZoneRadius * 0.5f, Height = tuning.KingBeamHeight, CapTop = false, CapBottom = false },
            MaterialOverride = ghostMaterial,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
        };
        AddChild(ghost);
    }

    private static StandardMaterial3D Glow(Color color, float alpha) => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        BlendMode = BaseMaterial3D.BlendModeEnum.Add,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        AlbedoColor = new Color(color, alpha),
    };

    public void Bind(PrototypeRunner bound)
    {
        runner = bound;
        runner.Decided += command =>
        {
            if (command is ZoneMoved { Zone: > 0 } moved) Materialize(new Vector3((float)moved.Spot.X, (float)moved.Spot.Y, (float)moved.Spot.Z));
        };
    }

    /// <summary>A fresh pile builds itself beside the zone's new spot, growing out of a scan ring.</summary>
    private void Materialize(Vector3 spot)
    {
        var tuning = Lab.World.Tuning;
        var at = FreePoint(spot);
        foreach (var tank in Lab.AddPile(at, tuning.KingPileCount))
        {
            runner!.Track(tank);
            SetReveal(tank, 1f, at, 0);
            materializing.Add((tank, at, 0));
        }
        Materialized++;
    }

    /// <summary>Near the spot, clear of tanks and players, so a new pile never builds into something.</summary>
    private Vector3 FreePoint(Vector3 spot)
    {
        var tanks = Lab.LiveTanks.Select(t => t.GlobalPosition).ToArray();
        var actors = Lab.Session.Actors.Select(a => a.Character.GlobalPosition).ToArray();
        for (var radius = 0f; radius <= 9f; radius += 2.5f)
        {
            for (var i = 0; i < 8; i++)
            {
                var p = spot + new Vector3(Mathf.Cos(i * Mathf.Tau / 8), 0, Mathf.Sin(i * Mathf.Tau / 8)) * radius;
                if (tanks.All(t => BotsFlat(t - p) > 2.2f) && actors.All(a => BotsFlat(a - p) > 2.5f)) return p;
                if (radius == 0) break;
            }
        }
        return spot;
    }

    private static float BotsFlat(Vector3 v) => new Vector2(v.X, v.Z).Length();

    private static void SetReveal(Node root, float side, Vector3 center, float radius)
    {
        foreach (var node in root.FindChildren("*", "GeometryInstance3D", owned: false).Prepend(root))
        {
            if (node is not GeometryInstance3D geometry || geometry is GpuParticles3D) continue;
            geometry.SetInstanceShaderParameter("reveal_side", side);
            geometry.SetInstanceShaderParameter("reveal_center", center);
            geometry.SetInstanceShaderParameter("reveal_radius", radius);
        }
    }

    public override void _Process(double delta)
    {
        if (runner?.Rules is not KingRules rules || rules.Zone < 0) return;
        var tuning = Lab.World.Tuning;
        var dt = (float)delta;
        clock += dt;
        var pulse = 0.5f + 0.5f * Mathf.Sin(clock * Mathf.Tau * tuning.LabWarningPulseHz);
        var elapsed = rules.Elapsed(runner.Clock);
        var spot = Point(rules.SpotOf(rules.Zone));
        beam.Visible = ring.Visible = runner.IsPlaying;
        beam.Position = spot + Vector3.Up * (tuning.KingBeamHeight / 2);
        ring.Position = spot + Vector3.Up * 0.06f;
        var holder = rules.Holder != 0 ? Lab.Session.Actor(rules.Holder)?.Character : null;
        var color = rules.Contested ? Contest : holder?.BodyColor ?? UiStyle.Accent;
        var warning = rules.Warning(elapsed);
        var strength = rules.Contested || warning ? 0.35f + 0.65f * pulse : 1f;
        beamMaterial.AlbedoColor = new Color(color, 0.16f * strength);
        ringMaterial.AlbedoColor = new Color(color, 0.55f * strength);
        ghost.Visible = warning && runner.IsPlaying;
        if (ghost.Visible)
        {
            ghost.Position = Point(rules.SpotOf(rules.Zone + 1)) + Vector3.Up * (tuning.KingBeamHeight / 2);
            ghostMaterial.AlbedoColor = new Color(Colors.White, 0.06f + 0.1f * pulse);
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

    private static Vector3 Point(ModeSpot spot) => new((float)spot.X, (float)spot.Y, (float)spot.Z);
}
