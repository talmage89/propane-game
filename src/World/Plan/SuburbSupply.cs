using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using static Propane.World.Plan.SuburbGenerator;

namespace Propane.World.Plan;

/// <summary>Tank supply and player/ammo starts; owns candidate selection and formation-placement scratch.</summary>
internal sealed class SuburbSupply
{
    private readonly SuburbSettings settings;
    private readonly Random rng;
    private readonly IReadOnlyList<OrientedRect> occupied;
    private readonly IReadOnlyList<OrientedRect> obstacles;
    private readonly IReadOnlyList<(Vector2 Position, float Yaw, float Elevation, string Reason)> tankSpots;
    private readonly List<(Vector2 A, Vector2 B)> fenceSegments;
    private SuburbPlan plan = null!;

    public SuburbSupply(SuburbSettings settings, Random rng, IReadOnlyList<OrientedRect> occupied,
        IReadOnlyList<OrientedRect> obstacles, IReadOnlyList<(Vector2 Position, float Yaw, float Elevation, string Reason)> tankSpots,
        List<(Vector2 A, Vector2 B)> fenceSegments)
    {
        this.settings = settings;
        this.rng = rng;
        this.occupied = occupied;
        this.obstacles = obstacles;
        this.tankSpots = tankSpots;
        this.fenceSegments = fenceSegments;
    }

    private int Seed => plan.Seed;

    public void Generate(SuburbPlan plan)
    {
        this.plan = plan;
        ChooseTanks();
        ChooseSpawn();
    }

    // ------------------------------------------------------------------ Tanks and spawn

    /// <summary>Place complete formations atomically: a clipped pyramid must never lose a supporting tank.</summary>
    private void ChooseTanks()
    {
        var candidates = tankSpots.Where(s => s.Elevation == 0 && IsClearForTank(s.Position))
            .OrderBy(_ => rng.Next()).ToList();
        var sites = new List<Vector2>();
        var minSize = Mathf.Clamp(settings.ClusterSizeMin, 1, settings.ClusterSizeMax);
        while (plan.Tanks.Count < settings.TankCount && candidates.Count > 0)
        {
            var spot = sites.Count == 0
                ? settings.CenterBias > 0 ? candidates.OrderBy(c => c.Position.DistanceTo(MapCenter)).First() : candidates[0]
                : candidates.OrderByDescending(c => sites.Min(t => t.DistanceTo(c.Position)) * SpotAppeal(c.Reason) *
                    CenterFactor(c.Position) * (0.75f + 0.5f * (float)rng.NextDouble())).First();
            candidates.Remove(spot);
            var formation = ChooseFormation();
            var size = Math.Min(settings.TankCount - plan.Tanks.Count, minSize +
                (int)((settings.ClusterSizeMax - minSize + 1) * Mathf.Pow((float)rng.NextDouble(), 1.4f)));
            var yaw = Range(0, Mathf.Tau);
            var turn = new Basis(Vector3.Up, yaw);
            // Retry a smaller complete formation at cramped story spots. Trucks have too little stable
            // support for pyramids, so all formations use clear ground beside the yard's story props.
            for (; size > 0; size--)
            {
                var slots = PileBuilder.Build(formation, size,
                    new Vector3(settings.PileSpacing, settings.PileLayerHeight, settings.PileDepthSpacing), settings.PileUprightLayerHeight)
                    .Select(t => new Transform3D(turn * t.Basis, turn * t.Origin + new Vector3(spot.Position.X, 0, spot.Position.Y))).ToList();
                // A sideways vessel's footprint is longer than its diameter. Enclose the whole hull
                // in a circle so this clearance remains conservative at every yaw.
                var margin = formation != PileFormation.Cluster
                    ? Mathf.Sqrt(PileBuilder.Radius * PileBuilder.Radius + PileBuilder.Height * PileBuilder.Height / 4f)
                    : 0.22f;
                if (slots.Any(t => !IsClearForTank(new Vector2((t * PileBuilder.LocalCenter).X, (t * PileBuilder.LocalCenter).Z), margin) ||
                    !SuburbLayout.InsideMap(plan, new OrientedRect(new Vector2((t * PileBuilder.LocalCenter).X, (t * PileBuilder.LocalCenter).Z), new Vector2(margin, margin), 0)) ||
                    plan.Tanks.Any(k => k.Position.DistanceTo(new Vector2(t.Origin.X, t.Origin.Z)) < settings.PileSiteSpacing)))
                {
                    continue;
                }

                var pile = sites.Count;
                for (var i = 0; i < slots.Count; i++)
                {
                    var t = slots[i];
                    plan.Tanks.Add(new TankPlacement(new Vector2(t.Origin.X, t.Origin.Z), yaw, t.Origin.Y,
                        i == 0 ? spot.Reason : $"{spot.Reason} group", (turn.Inverse() * t.Basis).GetEuler(), pile, formation));
                }
                sites.Add(spot.Position);
                break;
            }
        }
        if (plan.Tanks.Count != settings.TankCount)
            throw new InvalidOperationException($"Seed {Seed}: only placed {plan.Tanks.Count}/{settings.TankCount} tanks.");
    }

    private PileFormation ChooseFormation()
    {
        var weights = new[] { settings.RowsWeight, settings.SquareWeight, settings.TriangularWeight, settings.TightWeight };
        var total = weights.Sum(w => Math.Max(w, 0));
        if (total <= 0) return PileFormation.Cluster;
        var pick = Range(0, total);
        for (var i = 0; i < weights.Length; i++)
        {
            pick -= Math.Max(weights[i], 0);
            if (pick < 0) return (PileFormation)i;
        }
        return PileFormation.Cluster;
    }

    /// <summary>How much a spot tells a story: tanks by the things that use them beat a tank dropped on a lawn.</summary>
    private static float SpotAppeal(string reason) => reason switch
    {
        "pickup bed" => 1.2f,
        "patio heater" => 1.1f,
        "backyard" => 0.8f,
        _ => 1f,
    };

    /// <summary>A tank (31 cm wide) fits here without touching houses, trees, props or fences.</summary>
    private bool IsClearForTank(Vector2 p, float margin = 0.22f)
    {
        var tank = new OrientedRect(p, new Vector2(margin, margin), 0);
        if (occupied.Any(o => o.Overlaps(tank)) || obstacles.Any(o => o.Overlaps(tank)))
        {
            return false;
        }
        foreach (var (a, b) in fenceSegments)
        {
            var t = Mathf.Clamp((p - a).Dot((b - a).Normalized()), 0, a.DistanceTo(b));
            if ((a + (b - a).Normalized() * t).DistanceTo(p) < 0.45f)
            {
                return false;
            }
        }
        return true;
    }

    private void ChooseSpawn()
    {
        // On a road near the middle of the map, clear of parked cars, facing along the road.
        var cars = plan.Props.Where(p => p.Model.StartsWith("car:")).Select(p => p.Position).ToList();
        var bestScore = float.MaxValue;
        foreach (var road in plan.Roads)
        {
            for (var t = 0f; t <= road.Length; t += 2f)
            {
                var p = road.A + road.Direction * t;
                if (cars.Any(c => c.DistanceTo(p) < 6f))
                {
                    continue;
                }
                var score = p.Length();
                if (score < bestScore)
                {
                    bestScore = score;
                    plan.Spawn = p;
                    plan.SpawnYaw = YawFacing(road.Direction * (p.Dot(road.Direction) > 0 ? -1 : 1));
                }
            }
        }
    }

    private Vector2 MapCenter => plan.Bounds.GetCenter();

    /// <summary>1 inside the central area, falling away beyond it as strongly as the center bias asks.</summary>
    private float CenterFactor(Vector2 p)
    {
        if (settings.CenterBias <= 0)
        {
            return 1f;
        }
        var radius = Mathf.Max(settings.CenterRadius, 1f);
        var beyond = Mathf.Max(0f, p.DistanceTo(MapCenter) - radius) / radius;
        var factor = 1f / (1f + settings.CenterBias * beyond);
        return factor * factor;
    }

    /// <summary>
    /// Player spawns for a match: points on the roads around the central area, clear of cars and tanks, spread as far
    /// apart as they can be, each facing the center.
    /// </summary>
    public void ChooseSpawns(int count)
    {
        var cars = plan.Props.Where(p => p.Model.StartsWith("car:")).Select(p => p.Position).ToList();
        var candidates = new List<Vector2>();
        foreach (var road in plan.Roads)
        {
            for (var t = 3f; t <= road.Length - 3f; t += 2f)
            {
                var p = road.A + road.Direction * t;
                if (plan.Bounds.Grow(-4f).HasPoint(p) && cars.All(c => c.DistanceTo(p) > 6f) && plan.Tanks.All(k => k.Position.DistanceTo(p) > 8f))
                {
                    candidates.Add(p);
                }
            }
        }
        List<Vector2> Select(List<Vector2> available)
        {
            var starts = new List<Vector2>();
            while (starts.Count < count && available.Count > 0)
            {
                var next = starts.Count == 0 ? available.OrderByDescending(p => p.DistanceTo(MapCenter)).First()
                    : available.OrderByDescending(p => starts.Min(c => c.DistanceTo(p))).First();
                starts.Add(next);
                available.RemoveAll(p => p.DistanceTo(next) < settings.StartSpacing);
            }
            return starts;
        }
        // Prefer the existing ring around the action; expand to all roads when more players need the space.
        var reach = Mathf.Max(settings.CenterRadius, 20f) * 1.4f;
        var chosen = Select(candidates.Where(p => p.DistanceTo(MapCenter) <= reach).ToList());
        if (chosen.Count != count) chosen = Select(candidates);
        if (chosen.Count != count)
            throw new InvalidOperationException($"Seed {Seed}: cannot fit {count} clear starts {settings.StartSpacing} m apart.");
        foreach (var p in chosen)
        {
            var toCenter = MapCenter - p;
            plan.Spawns.Add(new SpawnPoint(p, YawFacing(toCenter.LengthSquared() > 0.01f ? toCenter : Vector2.Up)));
        }
    }

    /// <summary>Ammo pickups on the sidewalks: clear spots, spread apart, favoring the central area like the tanks.</summary>
    public void ChooseAmmoSpots(int count)
    {
        var walk = settings.RightOfWayHalfWidth - settings.SidewalkWidth * 0.5f;
        var candidates = new List<Vector2>();
        foreach (var road in plan.Roads)
        {
            var n = new Vector2(-road.Direction.Y, road.Direction.X);
            for (var t = 4f; t <= road.Length - 4f; t += 3f)
            {
                foreach (var side in new[] { -1f, 1f })
                {
                    var p = road.A + road.Direction * t + n * walk * side;
                    if (plan.Bounds.Grow(-2f).HasPoint(p) && SuburbLayout.IsClearOfOtherRoads(settings, plan, p, road, 0.5f) && IsClearForPickup(p))
                    {
                        candidates.Add(p);
                    }
                }
            }
        }
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException($"Seed {Seed}: cannot place {count} ammo cans.");
        }
        var chosen = new List<Vector2> { candidates.OrderBy(p => p.DistanceTo(MapCenter) + Range(0f, 10f)).First() };
        while (chosen.Count < count)
        {
            var best = candidates.OrderByDescending(p => chosen.Min(c => c.DistanceTo(p)) * CenterFactor(p) * (0.85f + 0.3f * (float)rng.NextDouble())).First();
            if (chosen.Min(c => c.DistanceTo(best)) < 4f)
            {
                break;
            }
            chosen.Add(best);
        }
        if (chosen.Count != count)
            throw new InvalidOperationException($"Seed {Seed}: only placed {chosen.Count}/{count} ammo cans.");
        plan.AmmoSpots.AddRange(chosen);
    }

    /// <summary>A pickup (about a meter across) fits here clear of props, tanks and fences.</summary>
    private bool IsClearForPickup(Vector2 p)
    {
        var area = new OrientedRect(p, new Vector2(0.6f, 0.6f), 0);
        if (obstacles.Any(o => o.Overlaps(area)) || occupied.Any(o => o.Overlaps(area)))
        {
            return false;
        }
        if (plan.Tanks.Any(t => t.Position.DistanceTo(p) < 3f))
        {
            return false;
        }
        foreach (var (a, b) in fenceSegments)
        {
            var t = Mathf.Clamp((p - a).Dot((b - a).Normalized()), 0, a.DistanceTo(b));
            if ((a + (b - a).Normalized() * t).DistanceTo(p) < 0.8f)
            {
                return false;
            }
        }
        return true;
    }

    private float Range(float min, float max) => min + (float)rng.NextDouble() * (max - min);
}
