using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Fx;
using Propane.Tank;
using Propane.World.Plan;

namespace Propane.World;

/// <summary>
/// One generated neighbourhood: a raised diorama slab with roads, houses, fenced backyards, props and tanks.
/// Built from a <see cref="SuburbPlan"/>.
/// </summary>
public partial class Suburb : Node3D
{
    /// <summary>Height of the slab's top surface above the void floor.</summary>
    public const float GroundHeight = 0.12f;

    private const float AsphaltLift = 0.004f;
    private const float SidewalkLift = 0.005f;
    private const float PavingLift = 0.006f;
    private const float PaintLift = 0.008f;
    private const float CurbHeight = 0.1f;
    private const float CurbWidth = 0.18f;
    private const float FencePanelLength = 2.4f;
    private const float FenceHeight = 1.7f;
    private const float PicketHeight = 1.1f;
    private const float FenceThickness = 0.12f;
    private const float DashLength = 3f;
    private const float DashGap = 3.5f;
    private const int MaxHullPoints = 56;
    private const float TankGroupRadius = 3f;

    /// <summary>Metadata on each loose prop body: its index in <see cref="SuburbPlan.Props"/>.</summary>
    public const string PlanIndexMeta = "plan_index";

    private static readonly Dictionary<string, Shape3D> HullCache = new();
    private static readonly Dictionary<string, Shape3D> TrimeshCache = new();

    private readonly List<PropaneTank> tanks = new();
    private SuburbPlan plan = null!;
    private SuburbSettings settings = null!;
    private float revealSide;

    public Node3D StaticRoot { get; private set; } = null!;

    public Node3D DynamicRoot { get; private set; } = null!;

    public SuburbPlan Plan => plan;

    public int TanksRemaining => tanks.Count(t => IsInstanceValid(t) && t.State != PropaneTank.TankState.Exploded);

    /// <summary>Centres of the groups of tanks still in play: tanks within a few metres of each other count as one.</summary>
    public List<Vector3> TankGroupCenters()
    {
        var live = LiveTanks.Select(t => t.GlobalPosition).ToList();
        var groups = new List<Vector3>();
        var seen = new bool[live.Count];
        for (var i = 0; i < live.Count; i++)
        {
            if (seen[i])
            {
                continue;
            }
            seen[i] = true;
            var members = new List<Vector3> { live[i] };
            for (var k = 0; k < members.Count; k++)
            {
                for (var j = 0; j < live.Count; j++)
                {
                    if (!seen[j] && members[k].DistanceTo(live[j]) < TankGroupRadius)
                    {
                        seen[j] = true;
                        members.Add(live[j]);
                    }
                }
            }
            groups.Add(members.Aggregate(Vector3.Zero, (a, b) => a + b) / members.Count + Vector3.Up * 0.3f);
        }
        return groups;
    }

    /// <summary>The tanks still in play.</summary>
    public IEnumerable<PropaneTank> LiveTanks => tanks.Where(t => IsInstanceValid(t) && t.State != PropaneTank.TankState.Exploded);

    /// <summary>Raised when a tank in this suburb explodes, with the number left.</summary>
    public event Action<int>? TankDestroyed;

    /// <summary>
    /// Builds the suburb. In a match (<paramref name="networked"/>), each tank takes its network id from its index in
    /// the plan before it enters the tree.
    /// </summary>
    public static Suburb Build(SuburbPlan plan, SuburbSettings settings, bool networked = false)
    {
        var suburb = new Suburb { Name = $"Suburb_{plan.Seed}" };
        suburb.plan = plan;
        suburb.settings = settings;
        suburb.StaticRoot = new Node3D { Name = "Static" };
        suburb.DynamicRoot = new Node3D { Name = "Dynamic" };
        suburb.AddChild(suburb.StaticRoot);
        suburb.AddChild(suburb.DynamicRoot);
        suburb.BuildGround();
        suburb.BuildRoads();
        suburb.BuildPavings();
        suburb.BuildHouses();
        suburb.BuildFences();
        suburb.BuildProps();
        suburb.BuildTanks(networked);
        // Debris, scorch marks and bullet holes spawned mid-transition take the suburb's current reveal tag.
        suburb.DynamicRoot.ChildEnteredTree += node => suburb.TagForReveal(node);
        return suburb;
    }

    /// <summary>World position of a plan point on the slab.</summary>
    public static Vector3 ToWorld(Vector2 p, float elevation = 0) => new(p.X, GroundHeight + elevation, p.Y);

    /// <summary>
    /// Tags every visual for the transition: 1 shows it only inside the reveal ring (the ring grows to materialise a
    /// suburb and shrinks to dissolve one), 0 shows it normally.
    /// </summary>
    public void SetRevealSide(float side)
    {
        revealSide = side;
        TagForReveal(this);
    }

    /// <summary>
    /// Hides what the reveal shader cannot cut (decals, particles, lights, vent jets) once it lies outside the ring,
    /// so nothing of a dissolving suburb lingers in the void.
    /// </summary>
    public void HideOutsideRing(Vector3 center, float radius) => HideOutside(this, center, radius);

    public static void HideOutside(Node root, Vector3 center, float radius)
    {
        foreach (var node in root.FindChildren("*", "Node3D", owned: false))
        {
            if (node is Decal or GpuParticles3D or Light3D or Tank.VentJet && node is Node3D spatial)
            {
                var p = spatial.GlobalPosition;
                spatial.Visible = new Vector2(p.X - center.X, p.Z - center.Z).Length() <= radius;
            }
        }
    }

    private void TagForReveal(Node root)
    {
        if (root is GeometryInstance3D self and not GpuParticles3D)
        {
            self.SetInstanceShaderParameter("reveal_side", revealSide);
        }
        foreach (var node in root.FindChildren("*", "GeometryInstance3D", owned: false))
        {
            if (node is GeometryInstance3D geometry && geometry is not GpuParticles3D)
            {
                geometry.SetInstanceShaderParameter("reveal_side", revealSide);
            }
        }
    }

    // ------------------------------------------------------------------ Ground

    private void BuildGround()
    {
        var b = plan.Bounds;
        var lawn = new MeshKit();
        lawn.Quad(new[] { b.Position, new Vector2(b.End.X, b.Position.Y), b.End, new Vector2(b.Position.X, b.End.Y) }, GroundHeight);
        AddMesh(lawn, GroundMaterial(0, new Color(0.44f, 0.6f, 0.3f), new Color(0.38f, 0.55f, 0.27f)), "Lawn");

        // The cut edge of the diorama slab.
        var edge = new MeshKit();
        var c = new[] { b.Position, new Vector2(b.End.X, b.Position.Y), b.End, new Vector2(b.Position.X, b.End.Y) };
        var center = new Vector3(b.GetCenter().X, 0, b.GetCenter().Y);
        for (var i = 0; i < 4; i++)
        {
            var p0 = new Vector3(c[i].X, 0, c[i].Y);
            var p1 = new Vector3(c[(i + 1) % 4].X, 0, c[(i + 1) % 4].Y);
            var mid = (p0 + p1) / 2f;
            var outward = new Vector3(mid.X - center.X, 0, mid.Z - center.Z).Normalized();
            outward = Mathf.Abs(outward.X) > Mathf.Abs(outward.Z) ? new Vector3(Mathf.Sign(outward.X), 0, 0) : new Vector3(0, 0, Mathf.Sign(outward.Z));
            edge.Wall(p0, p1, GroundHeight, outward);
        }
        AddMesh(edge, GroundMaterial(4, new Color(0.86f, 0.86f, 0.87f), new Color(0.78f, 0.79f, 0.8f)), "SlabEdge");

        var body = new StaticBody3D { Name = "SlabBody", CollisionLayer = Layers.World, CollisionMask = 0 };
        body.AddChild(new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new Vector3(b.Size.X, GroundHeight, b.Size.Y) },
            Position = new Vector3(b.GetCenter().X, GroundHeight / 2f, b.GetCenter().Y),
        });
        StaticRoot.AddChild(body);
    }

    // ------------------------------------------------------------------ Roads

    private void BuildRoads()
    {
        var asphalt = new MeshKit();
        var paint = new MeshKit();
        var sidewalk = new MeshKit();
        var curb = new MeshKit();
        var curbBody = new StaticBody3D { Name = "Curbs", CollisionLayer = Layers.World, CollisionMask = 0 };
        var half = settings.CarriagewayHalfWidth;
        var sidewalkCenter = half + settings.VergeWidth + settings.SidewalkWidth / 2f;
        var y = GroundHeight;

        foreach (var road in plan.Roads)
        {
            var d = road.Direction;
            var n = new Vector2(-d.Y, d.X);
            asphalt.Strip(road.A, road.B, half * 2f, y + AsphaltLift);

            foreach (var side in new[] { -1f, 1f })
            {
                foreach (var (t0, t1) in Spans(road, side * (half + CurbWidth / 2f), p => !OnOtherCarriageway(p, road, 0f) && !InBulb(p, 0f)))
                {
                    var a = road.A + d * t0 + n * side * (half + CurbWidth / 2f);
                    var e = road.A + d * t1 + n * side * (half + CurbWidth / 2f);
                    curb.Bar(a, e, CurbWidth, y, y + CurbHeight);
                    AddCurbCollider(curbBody, a, e);
                }
                foreach (var (t0, t1) in Spans(road, side * sidewalkCenter, p => !OnOtherCarriageway(p, road, 0f) && !InBulb(p, settings.VergeWidth)))
                {
                    sidewalk.Strip(road.A + d * t0 + n * side * sidewalkCenter, road.A + d * t1 + n * side * sidewalkCenter,
                        settings.SidewalkWidth, y + SidewalkLift);
                }
            }

            // Dashed centre line, kept out of intersections.
            foreach (var (t0, t1) in Spans(road, 0, p => !OnOtherCarriageway(p, road, 3f) && !InBulb(p, 1f)))
            {
                for (var t = t0 + 1f; t + DashLength < t1; t += DashLength + DashGap)
                {
                    paint.Strip(road.A + d * t, road.A + d * (t + DashLength), 0.16f, y + PaintLift);
                }
            }
        }

        foreach (var bulb in plan.CulDeSacs)
        {
            asphalt.Ring(bulb.Center, 0, bulb.Radius, y + AsphaltLift, 0, Mathf.Tau, 40);
            var entry = EntryAngle(bulb);
            var curbGap = Mathf.Asin(Mathf.Clamp(half / bulb.Radius, 0, 1)) + 0.02f;
            var walkRadius = bulb.Radius + settings.VergeWidth + settings.SidewalkWidth / 2f;
            var walkGap = Mathf.Asin(Mathf.Clamp(sidewalkCenter / walkRadius, 0, 1));
            const int pieces = 28;
            var start = entry + curbGap;
            var end = entry + Mathf.Tau - curbGap;
            for (var i = 0; i < pieces; i++)
            {
                var a0 = Mathf.Lerp(start, end, i / (float)pieces);
                var a1 = Mathf.Lerp(start, end, (i + 1) / (float)pieces);
                var r = bulb.Radius + CurbWidth / 2f;
                var p0 = bulb.Center + new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * r;
                var p1 = bulb.Center + new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * r;
                curb.Bar(p0, p1, CurbWidth, y, y + CurbHeight);
                AddCurbCollider(curbBody, p0, p1);
            }
            sidewalk.Ring(bulb.Center, walkRadius - settings.SidewalkWidth / 2f, walkRadius + settings.SidewalkWidth / 2f, y + SidewalkLift,
                entry + walkGap, entry + Mathf.Tau - walkGap, 40);
        }

        AddMesh(asphalt, GroundMaterial(1, new Color(0.22f, 0.225f, 0.235f), Colors.Black, 0.85f), "Asphalt");
        AddMesh(paint, GroundMaterial(3, new Color(0.92f, 0.78f, 0.25f), Colors.Black, 0.6f), "Paint");
        AddMesh(sidewalk, GroundMaterial(2, new Color(0.74f, 0.73f, 0.71f), Colors.Black, 0.85f, 1.5f), "Sidewalk");
        AddMesh(curb, GroundMaterial(2, new Color(0.8f, 0.79f, 0.77f), Colors.Black, 0.8f, 2.5f), "Curb");
        StaticRoot.AddChild(curbBody);
    }

    /// <summary>Parameter spans along a road's line, offset sideways, where <paramref name="keep"/> holds.</summary>
    private static List<(float, float)> Spans(RoadSegment road, float lateral, Func<Vector2, bool> keep)
    {
        const float step = 0.25f;
        var spans = new List<(float, float)>();
        var d = road.Direction;
        var n = new Vector2(-d.Y, d.X);
        float? start = null;
        for (var t = 0f; t <= road.Length + 0.001f; t += step)
        {
            var inside = keep(road.A + d * t + n * lateral);
            if (inside && start == null)
            {
                start = t;
            }
            else if (!inside && start != null)
            {
                if (t - step - start.Value > 0.3f)
                {
                    spans.Add((start.Value, t - step));
                }
                start = null;
            }
        }
        if (start != null && road.Length - start.Value > 0.3f)
        {
            spans.Add((start.Value, road.Length));
        }
        return spans;
    }

    private bool OnOtherCarriageway(Vector2 p, RoadSegment self, float pad)
    {
        foreach (var road in plan.Roads)
        {
            if (road == self)
            {
                continue;
            }
            var rect = new OrientedRect((road.A + road.B) / 2f, new Vector2(road.Length / 2f + pad, settings.CarriagewayHalfWidth + pad),
                Mathf.Atan2(road.Direction.Y, road.Direction.X));
            if (rect.Contains(p))
            {
                return true;
            }
        }
        return false;
    }

    private bool InBulb(Vector2 p, float pad)
    {
        foreach (var bulb in plan.CulDeSacs)
        {
            if (p.DistanceTo(bulb.Center) < bulb.Radius + pad)
            {
                return true;
            }
        }
        return false;
    }

    private float EntryAngle(CulDeSac bulb)
    {
        foreach (var road in plan.Roads)
        {
            if (road.B.DistanceTo(bulb.Center) < 0.5f)
            {
                var back = road.A - road.B;
                return Mathf.Atan2(back.Y, back.X);
            }
            if (road.A.DistanceTo(bulb.Center) < 0.5f)
            {
                var back = road.B - road.A;
                return Mathf.Atan2(back.Y, back.X);
            }
        }
        return 0;
    }

    private static void AddCurbCollider(StaticBody3D body, Vector2 a, Vector2 b)
    {
        var mid = (a + b) / 2f;
        var dir = (b - a).Normalized();
        body.AddChild(new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new Vector3(a.DistanceTo(b), CurbHeight, CurbWidth) },
            Transform = new Transform3D(new Basis(Vector3.Up, -Mathf.Atan2(dir.Y, dir.X)), new Vector3(mid.X, GroundHeight + CurbHeight / 2f, mid.Y)),
        });
    }

    // ------------------------------------------------------------------ Paving

    private void BuildPavings()
    {
        var driveway = new MeshKit();
        var walk = new MeshKit();
        var patio = new MeshKit();
        foreach (var paving in plan.Pavings)
        {
            var kit = paving.Kind switch { PavingKind.Driveway => driveway, PavingKind.Walk => walk, _ => patio };
            kit.Quad(paving.Area.Corners(), GroundHeight + PavingLift);
        }
        AddMesh(driveway, GroundMaterial(2, new Color(0.7f, 0.69f, 0.67f), Colors.Black, 0.85f, 3f), "Driveways");
        AddMesh(walk, GroundMaterial(2, new Color(0.78f, 0.74f, 0.68f), Colors.Black, 0.85f, 0.9f), "Walks");
        AddMesh(patio, GroundMaterial(2, new Color(0.66f, 0.58f, 0.5f), Colors.Black, 0.85f, 0.75f), "Patios");
    }

    // ------------------------------------------------------------------ Houses

    private void BuildHouses()
    {
        var houses = Catalog.Houses;
        foreach (var lot in plan.Lots)
        {
            if (lot.HouseModel < 0)
            {
                continue;
            }
            var info = houses[lot.HouseModel];
            var model = GameAssets.Instantiate(info.Path);
            model.Scale = Vector3.One * info.Scale;
            WorldMaterials.Apply(model, Catalog.HousePalettes[lot.HousePalette]);
            var body = new StaticBody3D { Name = "House", CollisionLayer = Layers.World, CollisionMask = 0 };
            body.AddChild(model);
            body.AddChild(new CollisionShape3D { Shape = Trimesh(info) });
            StaticRoot.AddChild(body);
            body.Position = ToWorld(lot.House.Center);
            body.Rotation = new Vector3(0, SuburbGenerator.YawFacing(lot.TowardStreet), 0);
        }
    }

    // ------------------------------------------------------------------ Fences

    private void BuildFences()
    {
        var privacy = Catalog.Prop("fence_privacy");
        var picket = Catalog.Prop("fence_picket");
        var post = Catalog.Prop("fence_post");
        foreach (var run in plan.Fences)
        {
            var length = run.A.DistanceTo(run.B);
            // Style and colour are fixed per run from its position, so they are stable for a seed.
            var hash = (uint)HashCode.Combine(Mathf.RoundToInt(run.A.X * 10), Mathf.RoundToInt(run.A.Y * 10), plan.Seed);
            var isPicket = hash % 10 < 3;
            var model = isPicket ? picket : privacy;
            var height = isPicket ? PicketHeight : FenceHeight;
            var tint = isPicket ? PicketTints[(hash / 10) % PicketTints.Length] : PrivacyTints[(hash / 10) % PrivacyTints.Length];
            var count = Mathf.Max(1, Mathf.RoundToInt(length / FencePanelLength));
            var panel = length / count;
            var dir = (run.B - run.A).Normalized();
            var yaw = -Mathf.Atan2(dir.Y, dir.X);
            for (var i = 0; i < count; i++)
            {
                var body = new Breakaway
                {
                    Name = "FencePanel",
                    Mass = (isPicket ? 14f : 28f) * panel / FencePanelLength,
                    CollisionLayer = Layers.Props,
                    CollisionMask = Layers.DynamicMask,
                    PhysicsMaterialOverride = new PhysicsMaterial { Friction = 0.7f, Bounce = 0.1f },
                };
                body.AddChild(new CollisionShape3D
                {
                    Shape = new BoxShape3D { Size = new Vector3(panel, height, FenceThickness) },
                    Position = new Vector3(panel / 2f, height / 2f, 0),
                });
                var mesh = GameAssets.Instantiate(model.Path);
                mesh.Scale = new Vector3(panel / FencePanelLength, 1, 1);
                WorldMaterials.Apply(mesh);
                SetTint(mesh, tint);
                body.AddChild(mesh);
                if (i == count - 1)
                {
                    // Close the run with its own post at the far end.
                    var end = GameAssets.Instantiate(post.Path);
                    end.Position = new Vector3(panel - 0.05f, 0, 0);
                    end.Scale = new Vector3(1, height / FenceHeight, 1);
                    WorldMaterials.Apply(end);
                    SetTint(end, tint);
                    body.AddChild(end);
                }
                DynamicRoot.AddChild(body);
                body.Position = ToWorld(run.A + dir * panel * i);
                body.Rotation = new Vector3(0, yaw, 0);
            }
        }
    }

    private static readonly Color[] PrivacyTints =
    {
        new(0.78f, 0.56f, 0.38f), new(0.66f, 0.62f, 0.57f), new(0.52f, 0.38f, 0.27f), new(0.86f, 0.66f, 0.47f), new(1.05f, 1.05f, 1.05f),
    };

    private static readonly Color[] PicketTints = { new(1.05f, 1.05f, 1.05f), new(1f, 0.97f, 0.9f), new(0.92f, 0.95f, 1f) };

    // ------------------------------------------------------------------ Props

    private void BuildProps()
    {
        for (var index = 0; index < plan.Props.Count; index++)
        {
            var prop = plan.Props[index];
            var info = GameCatalog.Resolve(prop.Model);
            var mesh = GameAssets.Instantiate(info.Path);
            var scale = info.Scale * prop.Scale;
            mesh.Scale = Vector3.One * scale;
            WorldMaterials.Apply(mesh);
            SetTint(mesh, prop.Tint);
            // A corner-pivoted model hangs under a holder so the prop's origin is the centre of its base.
            Node3D model = mesh;
            if (info.PivotOffset != Vector3.Zero)
            {
                model = new Node3D { Name = mesh.Name };
                model.AddChild(mesh);
                mesh.Position = info.PivotOffset * prop.Scale;
            }
            var position = ToWorld(prop.Position, prop.Elevation);
            var rotation = new Vector3(0, prop.Yaw, 0);

            switch (prop.Body)
            {
                case PropBody.Static:
                {
                    var shape = StaticShape(prop.Model, info, prop.Scale);
                    if (shape == null)
                    {
                        StaticRoot.AddChild(model);
                        model.Position = position;
                        model.Rotation = rotation;
                        break;
                    }
                    var body = new StaticBody3D { CollisionLayer = Layers.World, CollisionMask = 0 };
                    body.AddChild(model);
                    body.AddChild(shape.Value.Shape);
                    StaticRoot.AddChild(body);
                    body.Position = position;
                    body.Rotation = rotation;
                    break;
                }
                default:
                {
                    RigidBody3D body = prop.Body == PropBody.Breakaway ? new Breakaway() : new RigidBody3D();
                    body.Mass = prop.Mass;
                    body.CollisionLayer = Layers.Props;
                    body.CollisionMask = Layers.DynamicMask;
                    body.PhysicsMaterialOverride = new PhysicsMaterial { Friction = prop.Model.StartsWith("car:") ? 0.9f : 0.65f, Bounce = 0.15f };
                    body.CanSleep = true;
                    body.AddChild(model);
                    foreach (var shape in DynamicShapes(prop.Model, info, prop.Scale))
                    {
                        body.AddChild(shape);
                    }
                    DynamicRoot.AddChild(body);
                    body.Position = position;
                    body.Rotation = rotation;
                    body.Sleeping = true;
                    body.SetMeta(PlanIndexMeta, index);
                    break;
                }
            }
        }
    }

    /// <summary>Collision for anchored props: trunks for trees, poles for lamps, boxes for sheds; none for bushes.</summary>
    private static (CollisionShape3D Shape, bool)? StaticShape(string key, ModelInfo info, float scale)
    {
        if (key.StartsWith("nature:tree"))
        {
            var radius = 0.06f * scale;
            var height = info.Height * scale * 0.5f;
            return (new CollisionShape3D { Shape = new CylinderShape3D { Radius = radius, Height = height }, Position = new Vector3(0, height / 2f, 0) }, true);
        }
        if (key == "prop:street_lamp")
        {
            return (new CollisionShape3D { Shape = new CylinderShape3D { Radius = 0.12f, Height = 5.6f }, Position = new Vector3(0, 2.8f, 0) }, true);
        }
        if (key == "prop:shed")
        {
            var size = info.Bounds.Size * scale;
            return (new CollisionShape3D { Shape = new BoxShape3D { Size = size }, Position = info.Bounds.GetCenter() * scale }, true);
        }
        return null;
    }

    /// <summary>Collision for a loose prop: one convex hull, or for the pickup a compound that leaves its bed open.</summary>
    private static IEnumerable<CollisionShape3D> DynamicShapes(string key, ModelInfo info, float scale)
    {
        if (key == "prop:patio_umbrella")
        {
            // Weighted base, slim pole and canopy, rather than one hull filling the cone under the canopy.
            var points = ModelPoints(info, scale);
            var top = points.Max(p => p.Y);
            return new[]
            {
                new CollisionShape3D { Shape = new CylinderShape3D { Radius = 0.3f * scale, Height = 0.1f * scale }, Position = new Vector3(0, 0.05f * scale, 0) },
                new CollisionShape3D { Shape = new CylinderShape3D { Radius = 0.03f * scale, Height = 2.1f * scale }, Position = new Vector3(0, 1.15f * scale, 0) },
                new CollisionShape3D { Shape = HullOf($"{info.Path}@{scale}:canopy", () => points.Where(p => p.Y > top - 0.4f * scale)) },
            };
        }
        if (key != $"car:{Catalog.PickupModel}")
        {
            return new[] { new CollisionShape3D { Shape = Hull(info, scale) } };
        }
        var size = info.Scale * scale;
        List<Vector3>? modelPoints = null;
        List<Vector3> Points() => modelPoints ??= ModelPoints(info, scale);
        var floor = Catalog.PickupBedFloorModel * size;
        var front = Catalog.PickupBedFrontModel * size;
        var back = Catalog.PickupBedBackModel * size;
        var halfWidth = Catalog.PickupBedHalfWidthModel * size;
        var wall = Catalog.PickupBedWallModel * size;
        var wallHeight = (Catalog.PickupBedWallTopModel - Catalog.PickupBedFloorModel) * size;
        var wallY = floor + wallHeight / 2f;
        var bedLength = front - back;
        CollisionShape3D Box(Vector3 extent, Vector3 center) => new() { Shape = new BoxShape3D { Size = extent }, Position = center };
        return new[]
        {
            // Chassis and wheels up to the bed floor, then the cab and hood above it.
            new CollisionShape3D { Shape = HullOf($"{info.Path}@{size}:low", () => Points().Where(p => p.Y <= floor + 0.01f)) },
            new CollisionShape3D { Shape = HullOf($"{info.Path}@{size}:cab", () => Points().Where(p => p.Z >= front - 0.01f)) },
            Box(new Vector3(wall, wallHeight, bedLength), new Vector3(halfWidth - wall / 2f, wallY, (front + back) / 2f)),
            Box(new Vector3(wall, wallHeight, bedLength), new Vector3(-halfWidth + wall / 2f, wallY, (front + back) / 2f)),
            Box(new Vector3(halfWidth * 2f, wallHeight, wall * 0.6f), new Vector3(0, wallY, back + wall * 0.3f)),
        };
    }

    private static Shape3D Hull(ModelInfo info, float scale) => HullOf($"{info.Path}@{info.Scale * scale}", () => ModelPoints(info, scale));

    private static Shape3D HullOf(string key, System.Func<IEnumerable<Vector3>> points)
    {
        if (HullCache.TryGetValue(key, out var cached))
        {
            return cached;
        }
        Shape3D result = new ConvexPolygonShape3D { Points = GameAssets.FarthestPoints(points().ToList(), MaxHullPoints).ToArray() };
        HullCache[key] = result;
        return result;
    }

    /// <summary>Every vertex of a model, scaled into the prop's local space.</summary>
    private static List<Vector3> ModelPoints(ModelInfo info, float scale)
    {
        var points = new List<Vector3>();
        var node = GameAssets.Instantiate(info.Path);
        foreach (var mesh in GameAssets.MeshInstances(node))
        {
            var transform = RelativeTransform(mesh, node);
            for (var s = 0; s < mesh.Mesh.GetSurfaceCount(); s++)
            {
                foreach (var v in mesh.Mesh.SurfaceGetArrays(s)[(int)Mesh.ArrayType.Vertex].AsVector3Array())
                {
                    points.Add(transform * v * info.Scale * scale + info.PivotOffset * scale);
                }
            }
        }
        node.Free();
        return points;
    }

    private static Shape3D Trimesh(ModelInfo info)
    {
        if (TrimeshCache.TryGetValue(info.Path, out var cached))
        {
            return cached;
        }
        var faces = new List<Vector3>();
        var node = GameAssets.Instantiate(info.Path);
        foreach (var mesh in GameAssets.MeshInstances(node))
        {
            var transform = RelativeTransform(mesh, node);
            foreach (var v in mesh.Mesh.GetFaces())
            {
                faces.Add(transform * v * info.Scale);
            }
        }
        node.Free();
        var shape = new ConcavePolygonShape3D { Data = faces.ToArray(), BackfaceCollision = true };
        TrimeshCache[info.Path] = shape;
        return shape;
    }

    private static Transform3D RelativeTransform(Node3D node, Node3D root)
    {
        var transform = Transform3D.Identity;
        Node? n = node;
        while (n != null && n != root)
        {
            if (n is Node3D n3)
            {
                transform = n3.Transform * transform;
            }
            n = n.GetParent();
        }
        return transform;
    }

    // ------------------------------------------------------------------ Tanks

    private void BuildTanks(bool networked)
    {
        for (var index = 0; index < plan.Tanks.Count; index++)
        {
            var placement = plan.Tanks[index];
            var tank = PropaneTank.Create();
            if (networked)
            {
                tank.NetId = Net.BodySync.MapTankId(index);
            }
            DynamicRoot.AddChild(tank);
            tank.Position = ToWorld(placement.Position, placement.Elevation + 0.01f);
            tank.Rotation = new Vector3(0, placement.Yaw, 0);
            tank.Sleeping = true;
            tank.Detonated += OnTankDetonated;
            tanks.Add(tank);
        }
    }

    private void OnTankDetonated(PropaneTank tank) => TankDestroyed?.Invoke(TanksRemaining);

    /// <summary>The tanks in the plan, in plan order (freed ones included, so indices stay put).</summary>
    public IReadOnlyList<PropaneTank> MapTanks => tanks.GetRange(0, plan.Tanks.Count);

    /// <summary>Adds a tank that was not in the plan (one a player dropped in a match).</summary>
    public void AddTank(PropaneTank tank)
    {
        DynamicRoot.AddChild(tank);
        tank.Detonated += OnTankDetonated;
        tanks.Add(tank);
    }

    // ------------------------------------------------------------------ Helpers

    private void AddMesh(MeshKit kit, Material material, string name)
    {
        var mesh = kit.Commit(material);
        if (mesh == null)
        {
            return;
        }
        StaticRoot.AddChild(new MeshInstance3D { Name = name, Mesh = mesh, Layers = RenderLayers.Static });
    }

    private static ShaderMaterial GroundMaterial(int pattern, Color baseColor, Color altColor, float roughness = 0.9f, float jointSpacing = 1.6f)
    {
        var material = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/ground.gdshader") };
        material.SetShaderParameter("pattern", pattern);
        material.SetShaderParameter("base_color", baseColor);
        material.SetShaderParameter("alt_color", altColor);
        material.SetShaderParameter("noise_texture", FxLibrary.Noise);
        material.SetShaderParameter("roughness_value", roughness);
        material.SetShaderParameter("joint_spacing", jointSpacing);
        return material;
    }

    private static void SetTint(Node root, Color tint)
    {
        if (tint == Colors.White)
        {
            return;
        }
        foreach (var mesh in GameAssets.MeshInstances(root))
        {
            mesh.SetInstanceShaderParameter("instance_tint", tint);
        }
    }
}
