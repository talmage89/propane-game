using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.World;
using Propane.World.Plan;

namespace Propane.Dev;

/// <summary>
/// Headless checks of the suburb generator over many seeds. Exits with code 1 on any failure.
/// Run: godot --headless res://scenes/dev/generator_tests.tscn -- --seeds=200
/// </summary>
public partial class GeneratorTests : Node
{
    private const float GridCell = 0.25f;
    private static readonly (int X, int Y)[] Neighbors = { (1, 0), (-1, 0), (0, 1), (0, -1) };
    private int failures;
    private TankGeometry geometry = null!;
    private readonly Dictionary<string, int> failureKinds = new();

    public override void _Ready()
    {
        try { RunTests(); }
        catch (Exception error)
        {
            GD.PushError(error.ToString());
            GetTree().Quit(1);
        }
    }

    private void RunTests()
    {
        DevArgs.Setup();
        var count = (int)DevArgs.GetFloat("seeds", 120);
        var settings = SuburbPlans.SinglePlayer(new Core.Tuning());
        var catalog = new GameCatalog();
        geometry = new TankGeometry();
        var tankTotals = new List<int>();
        var lotTotals = new List<int>();
        var sizes = new List<Vector2>();
        var groupSizes = new List<int>();
        var reasons = new Dictionary<string, int>();
        var firstSeedFor = new Dictionary<string, int>();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        for (var seed = 1; seed <= count; seed++)
        {
            var plan = new SuburbGenerator(settings, catalog, seed).Generate();
            var again = new SuburbGenerator(settings, catalog, seed).Generate();
            Check(seed, "deterministic", Net.PlanCodec.Encode(plan).SequenceEqual(Net.PlanCodec.Encode(again)));
            CheckLots(seed, plan, settings);
            CheckTanks(seed, plan, settings);
            CheckSpawn(seed, plan, settings);
            CheckReachability(seed, plan, settings);
            tankTotals.Add(plan.Tanks.Count);
            foreach (var tank in plan.Tanks)
            {
                var reason = tank.Reason.Replace(" group", "");
                reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
                firstSeedFor.TryAdd(reason, seed);
            }
            lotTotals.Add(plan.Lots.Count);
            sizes.Add(plan.Bounds.Size);
            groupSizes.AddRange(GroupSizes(plan));
        }
        GD.Print($"[tests] {count} seeds in {timer.ElapsedMilliseconds} ms; lots {lotTotals.Min()}..{lotTotals.Max()} (avg {lotTotals.Average():0.0}); " +
                 $"tanks {tankTotals.Min()}..{tankTotals.Max()}; map {sizes.Min(v => v.X * v.Y) / 1000f:0.0}..{sizes.Max(v => v.X * v.Y) / 1000f:0.0} " +
                 $"thousand m2 (avg {sizes.Average(v => v.X):0} x {sizes.Average(v => v.Y):0} m)");
        GD.Print($"[tests] tank groups: {groupSizes.Count / (float)count:0.0} per map, sizes " +
                 string.Join(" ", groupSizes.GroupBy(g => g).OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Count()}")));
        GD.Print("[tests] tank spots: " + string.Join(", ", reasons.OrderByDescending(r => r.Value).Select(r => $"{r.Key} {r.Value} (seed {firstSeedFor[r.Key]})")));
        TestFormations(catalog);
        foreach (var players in new[] { 2, 8, 16, 24, 32 }) TestMatchSuburbs(count, players, catalog);
        foreach (var (kind, n) in failureKinds.OrderByDescending(k => k.Value))
        {
            GD.Print($"[tests] FAIL {kind}: {n}");
        }
        GD.Print(failures == 0 ? "[tests] ALL PASSED" : $"[tests] {failures} FAILURES");
        GetTree().Quit(failures == 0 ? 0 : 1);
    }

    private void Check(int seed, string what, bool ok, string detail = "")
    {
        if (ok)
        {
            return;
        }
        failures++;
        failureKinds[what] = failureKinds.GetValueOrDefault(what) + 1;
        if (failureKinds[what] <= 3)
        {
            GD.Print($"[tests] seed {seed}: {what} {detail}");
        }
    }

    /// <summary>
    /// Match suburbs: the same checks, plus a start for every player and the ammo spots, tank piles pulled toward the
    /// center, and the plan surviving the trip through <see cref="Net.PlanCodec"/> unchanged.
    /// </summary>
    private void TestMatchSuburbs(int count, int players, GameCatalog catalog)
    {
        var tuning = new Core.Tuning();
        var settings = SuburbPlans.Match(tuning, players);
        var timer = System.Diagnostics.Stopwatch.StartNew();

        var mpRadius = new List<float>();
        var spawnGaps = new List<float>();
        var ammoCounts = new List<int>();
        var planBytes = new List<int>();
        for (var seed = 1; seed <= count; seed++)
        {
            var plan = new SuburbGenerator(settings, catalog, seed).Generate();
            var again = new SuburbGenerator(settings, catalog, seed).Generate();
            Check(seed, "match: deterministic", Net.PlanCodec.Encode(plan).SequenceEqual(Net.PlanCodec.Encode(again)) &&
                                                plan.Spawns.SequenceEqual(again.Spawns) && plan.AmmoSpots.SequenceEqual(again.AmmoSpots));
            CheckLots(seed, plan, settings);
            CheckTanks(seed, plan, settings);
            CheckSpawn(seed, plan, settings);
            CheckReachability(seed, plan, settings);

            Check(seed, "match: supply settings", settings.TankCount == tuning.MpTankCount + tuning.MpTanksPerPlayer * players &&
                settings.AmmoSpots == tuning.PickupCount + tuning.PickupsPerPlayer * players &&
                settings.MapSize == tuning.MpMapSizeBase + tuning.MpMapSizePerPlayer * players &&
                settings.ClusterSizeMax == Mathf.Max(tuning.MpClusterSizeMin, tuning.MpClusterSizeMax) + (int)(tuning.MpPileSizePerPlayer * players));
            spawnGaps.Add(CheckMatchStarts(seed, plan, settings));

            mpRadius.Add(plan.Tanks.Average(t => t.Position.DistanceTo(plan.Bounds.GetCenter())));
            ammoCounts.Add(plan.AmmoSpots.Count);
            foreach (var spot in plan.AmmoSpots)
            {
                Check(seed, "match: ammo inside map", plan.Bounds.HasPoint(spot));
                Check(seed, "match: ammo clear of tanks", plan.Tanks.All(t => t.Position.DistanceTo(spot) > 2.5f));
                Check(seed, "match: ammo outside houses", !plan.Lots.Any(l => l.HouseModel >= 0 && l.House.Grown(0.5f).Contains(spot)));
                var nearestFence = plan.Fences.Select(f => DistanceToSegment(spot, f.A, f.B)).DefaultIfEmpty(99).Min();
                Check(seed, "match: ammo clear of fences", nearestFence > 0.6f, $"{nearestFence:0.00}");
            }

            var bytes = Net.PlanCodec.Encode(plan);
            planBytes.Add(bytes.Length);
            var decoded = Net.PlanCodec.Decode(bytes);
            Check(seed, "match: plan survives encoding", Net.PlanCodec.Encode(decoded).SequenceEqual(bytes) && decoded.Seed == plan.Seed &&
                                                         decoded.Spawns.SequenceEqual(plan.Spawns) && decoded.AmmoSpots.SequenceEqual(plan.AmmoSpots) &&
                                                         decoded.Pavings.SequenceEqual(plan.Pavings) && decoded.Bounds == plan.Bounds &&
                                                         decoded.Props.SequenceEqual(plan.Props) && decoded.Tanks.SequenceEqual(plan.Tanks) &&
                                                         decoded.Roads.SequenceEqual(plan.Roads) && decoded.Fences.SequenceEqual(plan.Fences) &&
                                                         decoded.CulDeSacs.SequenceEqual(plan.CulDeSacs));

        }
        GD.Print($"[tests] {players} players: tanks {settings.TankCount}, max pile {settings.ClusterSizeMax}, map input {settings.MapSize}, ammo {settings.AmmoSpots}");
        GD.Print($"[tests] match suburbs ({players} players): {count} seeds in {timer.ElapsedMilliseconds} ms; tanks average {mpRadius.Average():0.0} m from the center; " +
                 $"closest starts {spawnGaps.Min():0.0}..{spawnGaps.Max():0.0} m apart; " +
                 $"ammo spots {ammoCounts.Min()}..{ammoCounts.Max()}; plan {planBytes.Average() / 1024f:0.0} KB encoded (max {planBytes.Max() / 1024f:0.0})");
    }

    private float CheckMatchStarts(int seed, SuburbPlan plan, SuburbSettings settings)
    {
        Check(seed, "match: a start per player", plan.Spawns.Count == settings.PlayerSpawns, $"{plan.Spawns.Count}");
        var cars = plan.Props.Where(p => p.Model.StartsWith("car:")).Select(p => p.Position).ToList();
        foreach (var spawn in plan.Spawns)
        {
            Check(seed, "match: start inside slab", plan.Bounds.Grow(-4f).HasPoint(spawn.Position));
            Check(seed, "match: start on a road", plan.Roads.Any(r => r.RightOfWay(settings.CarriagewayHalfWidth).Contains(spawn.Position)));
            Check(seed, "match: start clear of cars", cars.All(c => c.DistanceTo(spawn.Position) > 6f));
            Check(seed, "match: start clear of tanks", plan.Tanks.All(t => t.Position.DistanceTo(spawn.Position) > 8f));
        }
        var gap = float.MaxValue;
        for (var i = 0; i < plan.Spawns.Count; i++)
        {
            for (var j = i + 1; j < plan.Spawns.Count; j++)
            {
                gap = Mathf.Min(gap, plan.Spawns[i].Position.DistanceTo(plan.Spawns[j].Position));
            }
        }
        Check(seed, "match: starts spread apart", gap >= settings.StartSpacing - 0.001f, $"{gap:0.0} m");
        Check(seed, "match: exact ammo supply", plan.AmmoSpots.Count == settings.AmmoSpots);
        return gap;
    }

    private void TestFormations(GameCatalog catalog)
    {
        foreach (var formation in Enum.GetValues<World.Plan.PileFormation>())
        {
            var tuning = new Core.Tuning();
            var spacing = new Vector3(tuning.PileSpacing, tuning.PileLayerHeight, tuning.PileDepthSpacing);
            foreach (var n in new[] { 0, 1, 2, 4, 5, 6, 9, 10, 14, 16, 20, 30, 64, 128 })
            {
                var transforms = World.Plan.PileBuilder.Build(formation, n, spacing);
                Check(n, "formation count", transforms.Count == n);
                Check(n, "formation deterministic", transforms.SequenceEqual(World.Plan.PileBuilder.Build(formation, n, spacing)));
                if (formation == PileFormation.Square && n >= 5 || formation == PileFormation.Triangular && n >= 4)
                    Check(n, "pyramid has an upper tier", transforms.Any(t => t.Origin.Y > PileBuilder.Radius + 0.001f), $"{formation} {n}");
                foreach (var t in transforms)
                {
                    Check(n, "formation finite", t.Origin.IsFinite() && t.Basis.X.IsFinite() && t.Origin.Y >= 0);
                    Check(n, "formation above ground", geometry.Bottom(t) >= -0.001f);
                    if (formation != PileFormation.Cluster && t.Origin.Y > World.Plan.PileBuilder.Radius + 0.001f)
                        Check(n, "formation supported by two tanks", transforms.Count(b =>
                            Mathf.Abs(t.Origin.Y - b.Origin.Y - spacing.Y) < 0.0001f && geometry.Overlaps(t, b, -0.005f)) >= 2, $"{formation} {t.Origin}");
                }
                for (var i = 0; i < transforms.Count; i++)
                    for (var j = i + 1; j < transforms.Count; j++)
                        if (transforms[i].Origin.DistanceTo(transforms[j].Origin) < 0.8f)
                            Check(n, "formation hulls do not intersect", !geometry.Overlaps(transforms[i], transforms[j]), $"{formation} {i}/{j}");
            }
            var settings = SuburbPlans.SinglePlayer(tuning) with
            {
                RowsWeight = formation == PileFormation.Rows ? 1 : 0,
                SquareWeight = formation == PileFormation.Square ? 1 : 0,
                TriangularWeight = formation == PileFormation.Triangular ? 1 : 0,
                TightWeight = formation == PileFormation.Cluster ? 1 : 0,
                ClusterSizeMin = 14, ClusterSizeMax = 14,
            };
            var modes = new[] { settings }.Concat(new[] { 2, 8, 16, 24, 32 }.Select(players => SuburbPlans.Match(tuning, players) with
            {
                RowsWeight = settings.RowsWeight, SquareWeight = settings.SquareWeight,
                TriangularWeight = settings.TriangularWeight, TightWeight = settings.TightWeight,
            }));
            foreach (var mode in modes)
            for (var seed = 1; seed <= 6; seed++)
            {
                var plan = new SuburbGenerator(mode, catalog, seed).Generate();
                Check(seed, "forced formation", plan.Tanks.All(t => t.Formation == formation));
                CheckTanks(seed, plan, mode);
                CheckLots(seed, plan, mode);
                if (mode.PlayerSpawns > 0) CheckMatchStarts(seed, plan, mode);
                CheckReachability(seed, plan, mode);
            }
        }
    }

    private void CheckLots(int seed, SuburbPlan plan, SuburbSettings settings)
    {
        var bounds = plan.Bounds.Grow(0.05f);
        var roads = plan.Roads.Select(r => r.RightOfWay(settings.RightOfWayHalfWidth)).ToList();
        Check(seed, "has lots", plan.Lots.Count >= 10, $"{plan.Lots.Count}");
        for (var i = 0; i < plan.Lots.Count; i++)
        {
            var lot = plan.Lots[i];
            Check(seed, "lot inside map", lot.Bounds.Corners().All(bounds.HasPoint));
            Check(seed, "lot clear of roads", !roads.Any(r => r.Overlaps(lot.Bounds.Shrunk(0.045f))));
            for (var j = i + 1; j < plan.Lots.Count; j++)
            {
                Check(seed, "lots do not overlap", !lot.Bounds.Overlaps(plan.Lots[j].Bounds.Shrunk(0.025f)));
            }
            if (lot.HouseModel >= 0)
            {
                Check(seed, "house inside lot", lot.House.Corners().All(c => lot.Bounds.Grown(0.05f).Contains(c)));
            }
        }
    }

    private void CheckTanks(int seed, SuburbPlan plan, SuburbSettings settings)
    {
        Check(seed, "tank count", plan.Tanks.Count == settings.TankCount, $"{plan.Tanks.Count}");
        foreach (var tank in plan.Tanks)
        {
            Check(seed, "tank inside map", plan.Bounds.HasPoint(tank.Position), $"{tank.Position}");
            var center = TankTransform(tank) * World.Plan.PileBuilder.LocalCenter;
            var footprint = tank.Formation == PileFormation.Cluster ? PileBuilder.Radius
                : Mathf.Sqrt(PileBuilder.Radius * PileBuilder.Radius + PileBuilder.Height * PileBuilder.Height / 4f);
            var mapCenter = new Vector2(center.X, center.Z);
            Check(seed, "tank hull inside map", plan.Bounds.Grow(-footprint).HasPoint(mapCenter), tank.Reason);
            Check(seed, "tank hull outside houses", !plan.Lots.Any(l => l.HouseModel >= 0 && l.House.Grown(footprint).Contains(mapCenter)), tank.Reason);
            var nearest = plan.Fences.Select(f => DistanceToSegment(new Vector2(center.X, center.Z), f.A, f.B)).DefaultIfEmpty(99).Min();
            Check(seed, "tank clear of fences", nearest > 0.3f, $"{nearest:0.00} {tank.Reason}");
        }
        for (var i = 0; i < plan.Tanks.Count; i++)
        {
            for (var j = i + 1; j < plan.Tanks.Count; j++)
            {
                var a = plan.Tanks[i]; var b = plan.Tanks[j];
                if (a.Position.DistanceTo(b.Position) < 0.8f && Mathf.Abs(a.Elevation - b.Elevation) < 0.6f)
                    Check(seed, "tanks do not overlap", !geometry.Overlaps(TankTransform(a), TankTransform(b)), $"{a.Formation} {i}/{j}");
            }
        }
    }

    private static Transform3D TankTransform(TankPlacement tank) => new(
        new Basis(Vector3.Up, tank.Yaw) * Basis.FromEuler(tank.Rotation), new Vector3(tank.Position.X, tank.Elevation, tank.Position.Y));

    private void CheckSpawn(int seed, SuburbPlan plan, SuburbSettings settings)
    {
        var onRoad = plan.Roads.Any(r => r.RightOfWay(settings.CarriagewayHalfWidth).Contains(plan.Spawn));
        Check(seed, "spawn on a road", onRoad);
        var nearestCar = plan.Props.Where(p => p.Model.StartsWith("car:")).Select(p => p.Position.DistanceTo(plan.Spawn)).DefaultIfEmpty(99).Min();
        Check(seed, "spawn clear of cars", nearestCar > 4f, $"{nearestCar:0.0}");
    }

    /// <summary>Flood fill from the spawn over a walkability grid; every tank must be reachable on foot.</summary>
    private void CheckReachability(int seed, SuburbPlan plan, SuburbSettings settings)
    {
        var margin = 6f;
        var origin = plan.Bounds.Position - new Vector2(margin, margin);
        var size = plan.Bounds.Size + new Vector2(margin, margin) * 2;
        var w = (int)(size.X / GridCell);
        var h = (int)(size.Y / GridCell);
        var blocked = new bool[w, h];
        (int, int) Cell(Vector2 p) => ((int)((p.X - origin.X) / GridCell), (int)((p.Y - origin.Y) / GridCell));
        void BlockRect(OrientedRect rect)
        {
            var corners = rect.Corners();
            var min = new Vector2(corners.Min(c => c.X), corners.Min(c => c.Y));
            var max = new Vector2(corners.Max(c => c.X), corners.Max(c => c.Y));
            var axisX = rect.AxisX;
            var axisY = rect.AxisY;
            var (x0, y0) = Cell(min);
            var (x1, y1) = Cell(max);
            for (var x = Math.Max(0, x0); x <= Math.Min(w - 1, x1); x++)
            {
                for (var y = Math.Max(0, y0); y <= Math.Min(h - 1, y1); y++)
                {
                    var center = origin + new Vector2((x + 0.5f) * GridCell, (y + 0.5f) * GridCell);
                    var relative = center - rect.Center;
                    if (Mathf.Abs(relative.Dot(axisX)) <= rect.HalfExtents.X && Mathf.Abs(relative.Dot(axisY)) <= rect.HalfExtents.Y)
                    {
                        blocked[x, y] = true;
                    }
                }
            }
        }

        foreach (var lot in plan.Lots.Where(l => l.HouseModel >= 0))
        {
            BlockRect(lot.House);
        }
        foreach (var fence in plan.Fences)
        {
            var dir = (fence.B - fence.A).Normalized();
            // Fence plus the player's radius on each side.
            BlockRect(new OrientedRect((fence.A + fence.B) / 2f, new Vector2(fence.A.DistanceTo(fence.B) / 2f + 0.1f, 0.25f), Mathf.Atan2(dir.Y, dir.X)));
        }
        foreach (var prop in plan.Props.Where(p => p.Model.StartsWith("car:") || p.Model == "prop:shed"))
        {
            var info = GameCatalog.Resolve(prop.Model);
            var facing = SuburbGenerator.FacingVector(prop.Yaw);
            BlockRect(new OrientedRect(prop.Position, info.Footprint * prop.Scale / 2f, Mathf.Atan2(-facing.X, facing.Y)));
        }

        var start = Cell(plan.Spawn);
        var reached = new bool[w, h];
        var queue = new Queue<(int, int)>();
        queue.Enqueue(start);
        reached[start.Item1, start.Item2] = true;
        while (queue.Count > 0)
        {
            var (x, y) = queue.Dequeue();
            foreach (var (dx, dy) in Neighbors)
            {
                var nx = x + dx;
                var ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= w || ny >= h || reached[nx, ny] || blocked[nx, ny])
                {
                    continue;
                }
                reached[nx, ny] = true;
                queue.Enqueue((nx, ny));
            }
        }
        foreach (var spawn in plan.Spawns)
        {
            var (sx, sy) = Cell(spawn.Position);
            Check(seed, "match: start reachable", reached[sx, sy] && !blocked[sx, sy]);
        }
        foreach (var tank in plan.Tanks)
        {
            var (tx, ty) = Cell(tank.Position);
            var ok = false;
            // The tank itself is not an obstacle; reaching within about a meter of it is enough.
            for (var dx = -4; dx <= 4 && !ok; dx++)
            {
                for (var dy = -4; dy <= 4 && !ok; dy++)
                {
                    var x = tx + dx;
                    var y = ty + dy;
                    ok = x >= 0 && y >= 0 && x < w && y < h && reached[x, y];
                }
            }
            Check(seed, "tank reachable", ok, $"{tank.Reason} at {tank.Position}");
        }
    }

    /// <summary>Sizes of the groups of tanks close enough to set each other off (within 1.2 m of a neighbor).</summary>
    private static IEnumerable<int> GroupSizes(SuburbPlan plan)
    {
        var tanks = plan.Tanks.Select(t => new Vector3(t.Position.X, t.Elevation, t.Position.Y)).ToList();
        var seen = new bool[tanks.Count];
        for (var i = 0; i < tanks.Count; i++)
        {
            if (seen[i])
            {
                continue;
            }
            var size = 0;
            var stack = new Stack<int>();
            stack.Push(i);
            seen[i] = true;
            while (stack.Count > 0)
            {
                var k = stack.Pop();
                size++;
                for (var j = 0; j < tanks.Count; j++)
                {
                    if (!seen[j] && tanks[k].DistanceTo(tanks[j]) < 1.2f)
                    {
                        seen[j] = true;
                        stack.Push(j);
                    }
                }
            }
            yield return size;
        }
    }

    private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var d = b - a;
        var t = Mathf.Clamp((p - a).Dot(d) / Mathf.Max(d.LengthSquared(), 0.0001f), 0, 1);
        return (a + d * t).DistanceTo(p);
    }

}
