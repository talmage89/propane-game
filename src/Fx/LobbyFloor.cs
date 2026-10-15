using System.Collections.Generic;
using Godot;
using Propane.Core;
using Propane.Modes;
using Propane.Tank;
using Propane.UI;

namespace Propane.Fx;

/// <summary>
/// The lobby world's markings on the void's floor, which keep its few meters readable the way the Lab's markers do:
/// an orange pad where each pile stands and comes back, a faint ring joining them, and the lobby's name written on the
/// floor in the middle, where everyone arrives facing it. Refilled piles build themselves out of a scan ring here, as
/// King's fresh piles do.
/// </summary>
public partial class LobbyFloor : Node3D
{
    public required WorldContext World { get; init; }
    /// <summary>The pile spots, from the lobby's layout.</summary>
    public required IReadOnlyList<ModeSpot> Spots { get; init; }
    /// <summary>Written on the floor in the middle (the lobby's name).</summary>
    public string Title { get; set; } = "";

    private readonly List<(PropaneTank Tank, Vector3 Center, float Age)> materializing = new();
    private Label3D title = null!;

    /// <summary>Refilled piles that have begun to build themselves.</summary>
    public int Materialized { get; private set; }

    public override void _Ready()
    {
        var tuning = World.Tuning;
        var pad = new StandardMaterial3D { AlbedoColor = UiStyle.Accent, Roughness = 1 };
        var line = new StandardMaterial3D
        {
            AlbedoColor = new Color(UiStyle.Ink, 0.12f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        };
        var reach = Mathf.Max(1f, tuning.LobbyPileReach * 0.45f);
        foreach (var spot in Spots)
        {
            AddChild(new MeshInstance3D
            {
                Name = "PilePad",
                Position = KingZone.Point(spot) + Vector3.Up * 0.008f,
                Mesh = new CylinderMesh { TopRadius = reach, BottomRadius = reach, Height = 0.012f, RadialSegments = 48 },
                MaterialOverride = pad,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            });
        }
        var ring = tuning.LobbyPileRing;
        AddChild(new MeshInstance3D
        {
            Name = "PileRing",
            Position = Vector3.Up * 0.006f,
            Scale = new Vector3(1, 0.02f, 1),
            Mesh = new TorusMesh { InnerRadius = ring - 0.06f, OuterRadius = ring + 0.06f, Rings = 128, RingSegments = 6 },
            MaterialOverride = line,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
        title = new Label3D
        {
            Name = "Title",
            Position = Vector3.Up * 0.02f,
            RotationDegrees = new Vector3(-90, 0, 0),
            FontSize = 160,
            PixelSize = 0.006f,
            Font = UiStyle.BoldFont,
            Modulate = new Color(UiStyle.Ink, 0.55f),
            OutlineSize = 0,
            Billboard = BaseMaterial3D.BillboardModeEnum.Disabled,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        AddChild(title);
    }

    /// <summary>A refilled pile builds itself around <paramref name="center"/>, growing out of a scan ring.</summary>
    public void Materialize(IEnumerable<PropaneTank> tanks, Vector3 center)
    {
        foreach (var tank in tanks)
        {
            SetReveal(tank, 1f, center, 0);
            materializing.Add((tank, center, 0));
        }
        Materialized++;
    }

    public override void _Process(double delta)
    {
        title.Text = Title.ToUpperInvariant();
        var seconds = Mathf.Max(0.05f, World.Tuning.LobbyMaterializeSeconds);
        for (var i = materializing.Count - 1; i >= 0; i--)
        {
            var (tank, center, age) = materializing[i];
            age += (float)delta;
            if (!IsInstanceValid(tank)) { materializing.RemoveAt(i); continue; }
            var done = age >= seconds;
            SetReveal(tank, done ? 0f : 1f, center, done ? 100000f : age / seconds * 3.5f);
            if (done) materializing.RemoveAt(i);
            else materializing[i] = (tank, center, age);
        }
    }

    private static void SetReveal(Node root, float side, Vector3 center, float radius)
    {
        foreach (var node in root.FindChildren("*", "GeometryInstance3D", owned: false)) Reveal(node, side, center, radius);
        Reveal(root, side, center, radius);
    }

    private static void Reveal(Node node, float side, Vector3 center, float radius)
    {
        if (node is not GeometryInstance3D geometry || geometry is GpuParticles3D) return;
        geometry.SetInstanceShaderParameter("reveal_side", side);
        geometry.SetInstanceShaderParameter("reveal_center", center);
        geometry.SetInstanceShaderParameter("reveal_radius", radius);
    }
}
