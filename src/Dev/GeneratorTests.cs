using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
    private int failures;
    private readonly Dictionary<string, int> failureKinds = new();

    public override void _Ready()
    {
        DevArgs.Setup();
        var count = (int)DevArgs.GetFloat("seeds", 120);
        var settings = new SuburbSettings();
        var catalog = new GameCatalog();
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
            Check(seed, "deterministic", Fingerprint(plan) == Fingerprint(again));
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
            Check(seed, "tank outside houses", !plan.Lots.Any(l => l.HouseModel >= 0 && l.House.Grown(0.2f).Contains(tank.Position)), tank.Reason);
            if (tank.Elevation == 0)
            {
                var nearest = plan.Fences.Select(f => DistanceToSegment(tank.Position, f.A, f.B)).DefaultIfEmpty(99).Min();
                Check(seed, "tank clear of fences", nearest > 0.3f, $"{nearest:0.00} {tank.Reason}");
            }
        }
        for (var i = 0; i < plan.Tanks.Count; i++)
        {
            for (var j = i + 1; j < plan.Tanks.Count; j++)
            {
                // Tanks are 31 cm across.
                Check(seed, "tanks do not overlap", plan.Tanks[i].Position.DistanceTo(plan.Tanks[j].Position) > 0.34f ||
                                                     plan.Tanks[i].Elevation != plan.Tanks[j].Elevation);
            }
        }
    }

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
            var (x0, y0) = Cell(min);
            var (x1, y1) = Cell(max);
            for (var x = Math.Max(0, x0); x <= Math.Min(w - 1, x1); x++)
            {
                for (var y = Math.Max(0, y0); y <= Math.Min(h - 1, y1); y++)
                {
                    var center = origin + new Vector2((x + 0.5f) * GridCell, (y + 0.5f) * GridCell);
                    if (rect.Contains(center))
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
            foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
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
        foreach (var tank in plan.Tanks)
        {
            var (tx, ty) = Cell(tank.Position);
            var ok = false;
            // The tank itself is not an obstacle; reaching within about a metre of it is enough.
            for (var dx = -4; dx <= 4 && !ok; dx++)
            {
                for (var dy = -4; dy <= 4 && !ok; dy++)
                {
                    var x = tx + dx;
                    var y = ty + dy;
                    ok = x >= 0 && y >= 0 && x < w && y < h && reached[x, y];
                }
            }
            Check(seed, "tank reachable", ok || tank.Elevation > 0, $"{tank.Reason} at {tank.Position}");
        }
    }

    /// <summary>Sizes of the groups of tanks close enough to set each other off (within 1.2 m of a neighbour).</summary>
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

    private static string Fingerprint(SuburbPlan plan)
    {
        var sb = new StringBuilder();
        foreach (var lot in plan.Lots)
        {
            sb.Append($"{lot.Bounds.Center:F3}{lot.HouseModel}{lot.HousePalette};");
        }
        foreach (var prop in plan.Props)
        {
            sb.Append($"{prop.Model}{prop.Position:F3}{prop.Yaw:F3};");
        }
        foreach (var tank in plan.Tanks)
        {
            sb.Append($"{tank.Position:F3};");
        }
        foreach (var fence in plan.Fences)
        {
            sb.Append($"{fence.A:F2}{fence.B:F2};");
        }
        return sb.ToString();
    }
}
