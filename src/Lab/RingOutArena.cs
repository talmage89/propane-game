using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Modes;
using Propane.Player;
using Propane.World;

namespace Propane.Lab;

/// <summary>
/// Ring Out's presenter: the slab's lots (each its own collider), the rim's warning pulse and dissolve, and the
/// gallery where eliminated players watch. It holds no rules: every frame it reads the stage of each lot from the
/// rules' plan at the runner's round time, so the boundary costs no traffic. The void's floor sits far below.
/// </summary>
public partial class RingOutArena : Node3D
{
    private const float Thickness = 1.5f;
    private static readonly Color WarnColor = new(1f, 0.32f, 0.12f);

    private sealed class Lot
    {
        public required MeshInstance3D Mesh;
        public required StaticBody3D Body;
        public required MeshInstance3D Warn;
        public LotStage Stage;
        public float Dissolve;
    }

    public required LabSession Lab { get; init; }
    private readonly Dictionary<(int, int), Lot> lots = new();
    private readonly List<PlayerCharacter> gallery = new();
    private StandardMaterial3D warnMaterial = null!;
    private PrototypeRunner? runner;
    private float clock;
    private Vector3 galleryCenter;

    public float HalfWidth { get; private set; }

    public override void _Ready()
    {
        var tuning = Lab.World.Tuning;
        var side = Mathf.Max(1, tuning.RingOutLots | 1);
        var size = tuning.RingOutLotSize;
        HalfWidth = side * size / 2;
        warnMaterial = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            AlbedoColor = new Color(WarnColor, 0),
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        var light = World.Suburb.GroundMaterial(2, new Color(0.86f, 0.86f, 0.87f), Colors.Black, 0.85f, 4f);
        var dark = World.Suburb.GroundMaterial(2, new Color(0.78f, 0.79f, 0.8f), Colors.Black, 0.85f, 4f);
        var half = side / 2;
        for (var i = -half; i <= half; i++)
        {
            for (var j = -half; j <= half; j++)
            {
                var center = new Vector3(i * size, 0, j * size);
                var mesh = new MeshInstance3D
                {
                    Name = $"Lot_{i}_{j}",
                    Mesh = new BoxMesh { Size = new Vector3(size - 0.06f, Thickness, size - 0.06f) },
                    MaterialOverride = (i + j) % 2 == 0 ? light : dark,
                    Position = center + Vector3.Down * (Thickness / 2),
                };
                AddChild(mesh);
                var body = new StaticBody3D { Name = $"LotBody_{i}_{j}", CollisionLayer = Layers.World | Layers.Floor, CollisionMask = 0, Position = mesh.Position };
                body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(size, Thickness, size) } });
                AddChild(body);
                var warn = new MeshInstance3D
                {
                    Name = $"LotWarn_{i}_{j}",
                    Mesh = new PlaneMesh { Size = new Vector2(size - 0.4f, size - 0.4f) },
                    MaterialOverride = warnMaterial,
                    Position = center + Vector3.Up * 0.025f,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                    Visible = false,
                };
                AddChild(warn);
                lots[(i, j)] = new Lot { Mesh = mesh, Body = body, Warn = warn };
            }
        }
        // The gallery: a floating ledge off one side, above the slab, facing its middle.
        galleryCenter = new Vector3(0, 7f, HalfWidth + 9f);
        var ledge = new MeshInstance3D
        {
            Name = "Gallery",
            Mesh = new BoxMesh { Size = new Vector3(14, 0.4f, 3) },
            MaterialOverride = light,
            Position = galleryCenter + Vector3.Down * 0.2f,
        };
        AddChild(ledge);
        var ledgeBody = new StaticBody3D { Name = "GalleryBody", CollisionLayer = Layers.World | Layers.Floor, CollisionMask = 0, Position = ledge.Position };
        ledgeBody.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(14, 0.4f, 3) } });
        AddChild(ledgeBody);
    }

    /// <summary>Where the actors start: evenly around the middle, between the piles, facing in.</summary>
    public (Vector3 Position, float Yaw) Spawn(int index, int count)
    {
        var tuning = Lab.World.Tuning;
        var radius = Mathf.Clamp(PileDistance(tuning) * 0.55f + 2f, 3f, HalfWidth - 3f);
        var angle = index * Mathf.Tau / Mathf.Max(1, count);
        var at = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * radius + Vector3.Up * 0.05f;
        return (at, Mathf.Atan2(at.X, at.Z));
    }

    /// <summary>The piles' ring, kept on the slab.</summary>
    public float PileDistance(Tuning tuning) => Mathf.Min(tuning.RingOutPileDistance, HalfWidth - 3f);

    public void Bind(PrototypeRunner bound)
    {
        runner = bound;
        runner.Decided += command =>
        {
            if (command is ActorEliminated eliminated && Lab.Session.Actor(eliminated.ActorId) is { } actor) Spectate(actor.Character);
        };
    }

    /// <summary>An eliminated player watches from the gallery, free to look around but not to move.</summary>
    private void Spectate(PlayerCharacter actor)
    {
        gallery.Add(actor);
        var slot = (gallery.Count - 1) % 6;
        var at = galleryCenter + new Vector3((slot - 2.5f) * 2.2f, 0.05f, 0);
        // Deferred: the elimination can arrive inside a physics callback of the very body being moved.
        Lab.World.Defer(() =>
        {
            if (!IsInstanceValid(actor)) return;
            actor.Teleport(at, Mathf.Pi);
            actor.InputLocked = true;
        });
    }

    public override void _PhysicsProcess(double delta)
    {
        if (runner?.Rules is not RingOutRules rules) return;
        var plan = rules.Plan;
        var elapsed = runner.Elapsed;
        foreach (var lot in plan.Lots)
        {
            if (!lots.TryGetValue((lot.I, lot.J), out var nodes)) continue;
            var stage = plan.Stage(lot, elapsed);
            if (stage == nodes.Stage) continue;
            nodes.Stage = stage;
            nodes.Warn.Visible = stage == LotStage.Warning;
            if (stage != LotStage.Gone) continue;
            // The ground goes at once; its look follows over the dissolve time.
            nodes.Body.ProcessMode = ProcessModeEnum.Disabled;
            nodes.Body.CollisionLayer = 0;
            nodes.Mesh.SetInstanceShaderParameter("reveal_side", 1f);
            nodes.Mesh.SetInstanceShaderParameter("reveal_center", new Vector3(lot.I * (float)plan.LotSize, 0, lot.J * (float)plan.LotSize));
            nodes.Dissolve = 0.0001f;
        }
    }

    public override void _Process(double delta)
    {
        if (runner == null) return;
        var tuning = Lab.World.Tuning;
        clock += (float)delta;
        var pulse = 0.5f + 0.5f * Mathf.Sin(clock * Mathf.Tau * tuning.LabWarningPulseHz);
        warnMaterial.AlbedoColor = new Color(WarnColor, 0.12f + 0.38f * pulse);
        var size = tuning.RingOutLotSize;
        foreach (var nodes in lots.Values)
        {
            if (nodes.Dissolve <= 0 || nodes.Dissolve >= 1) continue;
            nodes.Dissolve = Mathf.Min(1, nodes.Dissolve + (float)delta / Mathf.Max(0.05f, tuning.RingOutDissolveSeconds));
            nodes.Mesh.SetInstanceShaderParameter("reveal_radius", (1 - nodes.Dissolve) * size * 0.75f);
            if (nodes.Dissolve >= 1) nodes.Mesh.Visible = false;
        }
    }

    /// <summary>Lots still standing, for tests.</summary>
    public int SolidLots => lots.Values.Count(l => l.Stage != LotStage.Gone);
}
