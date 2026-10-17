using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Propane.World.Plan;

/// <summary>Road templates, lot packing and content cropping. Owns the road clearance areas.</summary>
internal sealed class SuburbLayout
{
    private const float CropMargin = 5f;
    private readonly SuburbSettings settings;
    private readonly Random rng;
    private SuburbPlan plan = null!;
    private readonly List<OrientedRect> roadAreas = new();

    public SuburbLayout(SuburbSettings settings, Random rng)
    {
        this.settings = settings;
        this.rng = rng;
    }

    public IReadOnlyList<OrientedRect> RoadAreas => roadAreas;

    public void Generate(SuburbPlan plan)
    {
        this.plan = plan;
        LayOutRoads();
        foreach (var road in plan.Roads)
        {
            roadAreas.Add(road.RightOfWay(settings.RightOfWayHalfWidth));
        }
        foreach (var bulb in plan.CulDeSacs)
        {
            var r = bulb.Radius + settings.VergeWidth + settings.SidewalkWidth;
            roadAreas.Add(new OrientedRect(bulb.Center, new Vector2(r, r), 0));
        }

        PackLots();
        CropToContent();
    }

    private void LayOutRoads()
    {
        var half = settings.MapSize / 2f;
        if (Strip)
        {
            plan.Roads.Add(new RoadSegment(new Vector2(-settings.StripLength / 2f, 0), new Vector2(settings.StripLength / 2f, 0)));
            return;
        }
        if (settings.PlayerSpawns > 0)
        {
            // A connected street grid grows with the match. Keep road frontage density as the slab grows,
            // rather than stretching a fixed number of streets into an increasingly empty map.
            var streets = Math.Max(2, (int)(settings.MapSize / settings.RoadSpacing));
            for (var i = 0; i < streets; i++)
            {
                var offset = -half + settings.MapSize * (i + 1) / (streets + 1);
                plan.Roads.Add(new RoadSegment(new Vector2(-half, offset), new Vector2(half, offset)));
                plan.Roads.Add(new RoadSegment(new Vector2(offset, -half), new Vector2(offset, half)));
            }
            return;
        }
        switch (rng.Next(3))
        {
            case 0:
                SpineTemplate(half);
                break;
            case 1:
                GridTemplate(half);
                break;
            default:
                LoopTemplate(half);
                break;
        }
    }

    /// <summary>A through road with side streets that end at the map edge or in a cul-de-sac.</summary>
    private void SpineTemplate(float half)
    {
        var spineZ = Range(-14f, 14f);
        var horizontal = rng.Next(2) == 0;
        Vector2 P(float along, float across) => horizontal ? new Vector2(along, across) : new Vector2(across, along);

        plan.Roads.Add(new RoadSegment(P(-half, spineZ), P(half, spineZ)));
        var branchCount = rng.Next(2, 4);
        var spacing = settings.MapSize / branchCount;
        for (var i = 0; i < branchCount; i++)
        {
            var along = -half + spacing * (i + 0.5f) + Range(-6f, 6f);
            foreach (var side in new[] { -1f, 1f })
            {
                if (rng.NextDouble() < 0.15)
                {
                    continue;
                }
                var start = spineZ + side * settings.CarriagewayHalfWidth;
                var toEdge = half - side * start;
                var bulb = rng.NextDouble() < 0.55 && toEdge > 60f;
                var length = bulb ? toEdge - Range(28f, 36f) : toEdge;
                var end = start + side * length;
                plan.Roads.Add(new RoadSegment(P(along, start), P(along, end)));
                if (bulb)
                {
                    plan.CulDeSacs.Add(new CulDeSac(P(along, end), settings.CulDeSacRadius));
                }
            }
        }
    }

    /// <summary>Two parallel streets crossed by two others, all running to the edges.</summary>
    private void GridTemplate(float half)
    {
        var a = Range(36f, 44f);
        var b = Range(32f, 42f);
        var offsetX = Range(-8f, 8f);
        var offsetZ = Range(-8f, 8f);
        plan.Roads.Add(new RoadSegment(new Vector2(-half, -b + offsetZ), new Vector2(half, -b + offsetZ)));
        plan.Roads.Add(new RoadSegment(new Vector2(-half, b + offsetZ), new Vector2(half, b + offsetZ)));
        plan.Roads.Add(new RoadSegment(new Vector2(-a + offsetX, -half), new Vector2(-a + offsetX, half)));
        if (rng.NextDouble() < 0.7)
        {
            plan.Roads.Add(new RoadSegment(new Vector2(a + offsetX, -half), new Vector2(a + offsetX, half)));
        }
        else
        {
            // A side street that stops in a cul-de-sac instead of the second through road.
            var end = Range(-20f, 20f);
            plan.Roads.Add(new RoadSegment(new Vector2(a + offsetX, half), new Vector2(a + offsetX, end)));
            plan.CulDeSacs.Add(new CulDeSac(new Vector2(a + offsetX, end), settings.CulDeSacRadius));
        }
    }

    /// <summary>A rectangular loop with houses inside and out, and spurs out to the edges.</summary>
    private void LoopTemplate(float half)
    {
        var a = Range(40f, 48f);
        var b = Range(38f, 46f);
        var c = new Vector2(Range(-6f, 6f), Range(-6f, 6f));
        var corners = new[] { c + new Vector2(-a, -b), c + new Vector2(a, -b), c + new Vector2(a, b), c + new Vector2(-a, b) };
        for (var i = 0; i < 4; i++)
        {
            plan.Roads.Add(new RoadSegment(corners[i], corners[(i + 1) % 4]));
        }
        // One or two spurs from the middle of a side to the edge.
        var sides = Enumerable.Range(0, 4).OrderBy(_ => rng.Next()).Take(rng.Next(1, 3));
        foreach (var side in sides)
        {
            var mid = (corners[side] + corners[(side + 1) % 4]) / 2f;
            var outward = (mid - c).Normalized();
            outward = Mathf.Abs(outward.X) > Mathf.Abs(outward.Y) ? new Vector2(Mathf.Sign(outward.X), 0) : new Vector2(0, Mathf.Sign(outward.Y));
            var edge = outward.X != 0 ? new Vector2(outward.X * half, mid.Y) : new Vector2(mid.X, outward.Y * half);
            plan.Roads.Add(new RoadSegment(mid, edge));
        }
    }

    // ------------------------------------------------------------------ Lots

    private void PackLots()
    {
        var lotSeed = rng.Next();
        foreach (var road in plan.Roads)
        {
            var d = road.Direction;
            foreach (var n in new[] { new Vector2(-d.Y, d.X), new Vector2(d.Y, -d.X) })
            {
                var s = 0f;
                while (s < road.Length - settings.LotFrontageMin)
                {
                    var frontage = Range(settings.LotFrontageMin, settings.LotFrontageMax);
                    frontage = Mathf.Min(frontage, road.Length - s);
                    if (frontage < settings.LotFrontageMin)
                    {
                        break;
                    }
                    var frontCenter = road.A + d * (s + frontage / 2f) + n * settings.RightOfWayHalfWidth;
                    if (TryPlaceLot(frontCenter, n, frontage, lotSeed++))
                    {
                        s += frontage;
                    }
                    else
                    {
                        s += 2f;
                    }
                }
            }
        }

        foreach (var bulb in plan.CulDeSacs)
        {
            var reach = bulb.Radius + settings.VergeWidth + settings.SidewalkWidth;
            var incoming = plan.Roads.Where(r => r.B.DistanceTo(bulb.Center) < 0.5f || r.A.DistanceTo(bulb.Center) < 0.5f)
                .Select(r => r.B.DistanceTo(bulb.Center) < 0.5f ? (r.A - r.B).Normalized() : (r.B - r.A).Normalized())
                .FirstOrDefault();
            var baseAngle = Mathf.Atan2(incoming.Y, incoming.X);
            // Lots fan out around the far side of the turnaround.
            foreach (var offset in new[] { 110f, 150f, 180f, 210f, 250f })
            {
                var angle = baseAngle + Mathf.DegToRad(offset);
                var outward = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                TryPlaceLot(bulb.Center + outward * (reach + 0.5f), outward, Range(15f, 18f), lotSeed++);
            }
        }
    }

    private bool TryPlaceLot(Vector2 frontCenter, Vector2 inward, float frontage, int seed)
    {
        var angle = Mathf.Atan2(-inward.X, inward.Y);
        for (var depth = Range(settings.LotDepthMax - 4f, settings.LotDepthMax); depth >= settings.LotDepthMin; depth -= 1.5f)
        {
            var rect = new OrientedRect(frontCenter + inward * depth / 2f, new Vector2(frontage / 2f, depth / 2f), angle);
            if (!InsideMap(plan, rect) || roadAreas.Any(r => r.Overlaps(rect.Shrunk(0.04f))) ||
                plan.Lots.Any(l => l.Bounds.Overlaps(rect.Shrunk(0.02f))))
            {
                continue;
            }
            plan.Lots.Add(new Lot { Bounds = rect, Seed = seed });
            return true;
        }
        return false;
    }

    internal static bool InsideMap(SuburbPlan plan, OrientedRect rect)
    {
        var b = plan.Bounds.Grow(0.01f);
        return rect.Corners().All(b.HasPoint);
    }

    /// <summary>
    /// Shrinks the chunk to the built-up area plus a margin of lawn, so no map has big empty fields. Roads that ran
    /// to the old edge are cut at the new one.
    /// </summary>
    private void CropToContent()
    {
        if (plan.Lots.Count == 0)
        {
            return;
        }
        var corners = plan.Lots.SelectMany(l => l.Bounds.Corners()).ToList();
        foreach (var bulb in plan.CulDeSacs)
        {
            var r = bulb.Radius + settings.VergeWidth + settings.SidewalkWidth;
            corners.Add(bulb.Center - new Vector2(r, r));
            corners.Add(bulb.Center + new Vector2(r, r));
        }
        var min = new Vector2(corners.Min(c => c.X), corners.Min(c => c.Y)) - new Vector2(CropMargin, CropMargin);
        var max = new Vector2(corners.Max(c => c.X), corners.Max(c => c.Y)) + new Vector2(CropMargin, CropMargin);
        min = min.Max(plan.Bounds.Position);
        max = max.Min(plan.Bounds.End);
        if (Strip)
        {
            // A strip keeps its whole length, end to end, so the next one joins it exactly.
            min.X = -settings.StripLength / 2f;
            max.X = settings.StripLength / 2f;
        }
        var cropped = new Rect2(min, max - min);
        plan.Bounds = cropped;

        var kept = new List<RoadSegment>();
        foreach (var road in plan.Roads)
        {
            if (ClipToRect(road.A, road.B, cropped) is { } clipped && clipped.A.DistanceTo(clipped.B) > 1f)
            {
                kept.Add(new RoadSegment(clipped.A, clipped.B));
            }
        }
        plan.Roads.Clear();
        plan.Roads.AddRange(kept);
        roadAreas.Clear();
        foreach (var road in plan.Roads)
        {
            roadAreas.Add(road.RightOfWay(settings.RightOfWayHalfWidth));
        }
        foreach (var bulb in plan.CulDeSacs)
        {
            var r = bulb.Radius + settings.VergeWidth + settings.SidewalkWidth;
            roadAreas.Add(new OrientedRect(bulb.Center, new Vector2(r, r), 0));
        }
    }

    /// <summary>Liang-Barsky clip of a segment to a rectangle.</summary>
    private static (Vector2 A, Vector2 B)? ClipToRect(Vector2 a, Vector2 b, Rect2 rect)
    {
        var t0 = 0f;
        var t1 = 1f;
        var d = b - a;
        var p = new[] { -d.X, d.X, -d.Y, d.Y };
        var q = new[] { a.X - rect.Position.X, rect.End.X - a.X, a.Y - rect.Position.Y, rect.End.Y - a.Y };
        for (var i = 0; i < 4; i++)
        {
            if (Mathf.Abs(p[i]) < 1e-6f)
            {
                if (q[i] < 0)
                {
                    return null;
                }
                continue;
            }
            var t = q[i] / p[i];
            if (p[i] < 0)
            {
                t0 = Mathf.Max(t0, t);
            }
            else
            {
                t1 = Mathf.Min(t1, t);
            }
        }
        return t0 < t1 ? (a + d * t0, a + d * t1) : null;
    }

    private bool Strip => settings.StripLength > 0;

    internal static bool IsClearOfOtherRoads(SuburbSettings settings, SuburbPlan plan, Vector2 p, RoadSegment self, float margin)
    {
        foreach (var road in plan.Roads)
        {
            if (road == self)
            {
                continue;
            }
            if (road.RightOfWay(settings.RightOfWayHalfWidth + margin).Contains(p))
            {
                return false;
            }
        }
        foreach (var bulb in plan.CulDeSacs)
        {
            if (p.DistanceTo(bulb.Center) < bulb.Radius + settings.VergeWidth + settings.SidewalkWidth + margin)
            {
                return false;
            }
        }
        return true;
    }

    private float Range(float min, float max) => min + (float)rng.NextDouble() * (max - min);
}
