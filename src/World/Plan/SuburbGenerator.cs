using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Propane.World.Plan;

/// <summary>Inputs the generator needs about the asset catalog, so it can be tested without loading models.</summary>
public interface IPlanCatalog
{
    /// <summary>House footprints (width across the front, depth), indexed like the house catalog.</summary>
    IReadOnlyList<Vector2> HouseFootprints { get; }

    int HousePaletteCount { get; }

    /// <summary>Scaled footprint (width along X, depth along Z) of a prop model key such as "prop:gas_grill".</summary>
    Vector2 PropFootprint(string model, float scale);
}

/// <summary>Generator settings. Distances in metres.</summary>
public sealed record SuburbSettings
{
    public float MapSize { get; init; } = 190f;
    public int TankCount { get; init; } = 30;

    /// <summary>Tanks stand in groups of this many, close enough that one going up sets off the rest.</summary>
    public int ClusterSizeMin { get; init; } = 1;

    public int ClusterSizeMax { get; init; } = 10;

    public float CarriagewayHalfWidth { get; init; } = 4f;
    public float VergeWidth { get; init; } = 1.3f;
    public float SidewalkWidth { get; init; } = 1.8f;
    public float LotFrontageMin { get; init; } = 17f;
    public float LotFrontageMax { get; init; } = 23f;
    public float LotDepthMin { get; init; } = 23f;
    public float LotDepthMax { get; init; } = 34f;
    public float CulDeSacRadius { get; init; } = 10.5f;

    /// <summary>How strongly tank groups are pulled toward the map's centre (0 spreads them evenly, as in single player).</summary>
    public float CentreBias { get; init; }

    /// <summary>Radius of the central area that tank groups and ammo favour when <see cref="CentreBias"/> is above 0.</summary>
    public float CentreRadius { get; init; } = 40f;

    /// <summary>Separate player spawn points to choose, for a multiplayer match. 0 for single player.</summary>
    public int PlayerSpawns { get; init; }

    /// <summary>Ammo pickup spots to choose, for a multiplayer match.</summary>
    public int AmmoSpots { get; init; }

    public float RightOfWayHalfWidth => CarriagewayHalfWidth + VergeWidth + SidewalkWidth;
}

/// <summary>
/// Builds a <see cref="SuburbPlan"/> from a seed: a road layout from one of a few templates, house lots packed
/// along every road frontage and around cul-de-sacs, houses, driveways, backyard fences with gate openings, yard
/// and street props, and tank spots chosen to spread groups of tanks across the map.
/// </summary>
public sealed class SuburbGenerator
{
    private const float FenceGateWidth = 1.7f;
    private const float CropMargin = 5f;

    private readonly SuburbSettings settings;
    private readonly IPlanCatalog catalog;
    private readonly Random rng;
    private SuburbPlan plan = null!;
    private readonly List<OrientedRect> roadAreas = new();
    private readonly List<OrientedRect> occupied = new();
    private readonly List<OrientedRect> obstacles = new();
    private readonly List<(Vector2 Position, float Yaw, float Elevation, string Reason)> tankSpots = new();
    private readonly List<(Vector2 A, Vector2 B)> fenceSegments = new();
    private readonly List<(Vector2 A, Vector2 B)> fenceOpenings = new();

    public SuburbGenerator(SuburbSettings settings, IPlanCatalog catalog, int seed)
    {
        this.settings = settings;
        this.catalog = catalog;
        rng = new Random(seed);
        Seed = seed;
    }

    public int Seed { get; }

    public SuburbPlan Generate()
    {
        var half = settings.MapSize / 2f;
        plan = new SuburbPlan { Seed = Seed, Bounds = new Rect2(-half, -half, settings.MapSize, settings.MapSize) };

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
        foreach (var lot in plan.Lots)
        {
            FurnishLot(lot);
        }
        MergeFences();
        PlaceStreetFurniture();
        ScatterParkland();
        ChooseTanks();
        ChooseSpawn();
        // Multiplayer additions come last, so single-player suburbs stay identical for a given seed.
        if (settings.PlayerSpawns > 0)
        {
            ChooseSpawns(settings.PlayerSpawns);
        }
        if (settings.AmmoSpots > 0)
        {
            ChooseAmmoSpots(settings.AmmoSpots);
        }
        return plan;
    }

    // ------------------------------------------------------------------ Roads

    private void LayOutRoads()
    {
        var half = settings.MapSize / 2f;
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
            if (!InsideMap(rect) || roadAreas.Any(r => r.Overlaps(rect.Shrunk(0.04f))) ||
                plan.Lots.Any(l => l.Bounds.Overlaps(rect.Shrunk(0.02f))))
            {
                continue;
            }
            plan.Lots.Add(new Lot { Bounds = rect, Seed = seed });
            return true;
        }
        return false;
    }

    private bool InsideMap(OrientedRect rect)
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

    // ------------------------------------------------------------------ Houses, yards and fences

    private void FurnishLot(Lot lot)
    {
        var r = new Random(lot.Seed);
        float R(float min, float max) => min + (float)r.NextDouble() * (max - min);
        bool Chance(double p) => r.NextDouble() < p;

        var width = lot.Bounds.HalfExtents.X * 2f;
        var depth = lot.Bounds.HalfExtents.Y * 2f;
        var halfWidth = width / 2f;

        // House: the biggest-variety fit that leaves a side yard for the driveway.
        var fits = Enumerable.Range(0, catalog.HouseFootprints.Count)
            .Where(i => catalog.HouseFootprints[i].X <= width - 5.5f && catalog.HouseFootprints[i].Y <= depth * 0.42f)
            .ToList();
        if (fits.Count == 0)
        {
            return;
        }
        lot.HouseModel = fits[r.Next(fits.Count)];
        lot.HousePalette = r.Next(catalog.HousePaletteCount);
        var footprint = catalog.HouseFootprints[lot.HouseModel];
        var setback = R(5.5f, 8f);
        var slack = width - footprint.X;
        lot.DrivewaySide = Chance(0.5) ? HouseSide.Left : HouseSide.Right;
        var sideSign = lot.DrivewaySide == HouseSide.Left ? -1f : 1f;
        // Shift the house away from the driveway so that side gets the room.
        var houseX = -sideSign * R(slack * 0.1f, slack * 0.3f);
        var houseCenter = new Vector2(houseX, setback + footprint.Y / 2f);
        lot.House = new OrientedRect(lot.LotToMap(houseCenter.X, houseCenter.Y), footprint / 2f, lot.Bounds.Angle);
        occupied.Add(lot.House.Grown(0.3f));
        var houseLeft = houseX - footprint.X / 2f;
        var houseRight = houseX + footprint.X / 2f;
        var houseBack = setback + footprint.Y;
        lot.ReturnDepth = setback + footprint.Y * 0.55f;

        var streetYaw = YawFacing(lot.TowardStreet);
        var intoLot = -lot.TowardStreet;

        // Driveway along the roomier side, from the street to just past the house front.
        var driveCenterX = sideSign > 0 ? (houseRight + halfWidth) / 2f : (houseLeft - halfWidth) / 2f;
        var gap = sideSign > 0 ? halfWidth - houseRight : houseLeft + halfWidth;
        var driveWidth = Mathf.Min(3.4f, gap - 0.6f);
        var driveLength = setback + 3f;
        AddPaving(lot, driveCenterX, driveLength / 2f, driveWidth, driveLength, PavingKind.Driveway);
        AddPaving(lot, houseX + R(-1f, 1f), setback / 2f, 1.3f, setback, PavingKind.Walk);

        // Car on the driveway, nose to the street or to the house.
        var carParked = Chance(0.6) && driveWidth > 2.4f;
        if (carParked)
        {
            var model = Catalog.CarModels[r.Next(Catalog.CarModels.Length)];
            var carYaw = Chance(0.5) ? streetYaw : YawFacing(intoLot);
            var carPos = lot.LotToMap(driveCenterX, R(3f, Mathf.Max(3.2f, setback - 1.5f)));
            AddProp($"car:{model}", carPos, carYaw, 1f, PropBody.Loose, 1200f, CarColor(r));
            if (model == Catalog.PickupModel)
            {
                // The bed is behind the cab, toward the car's back (-Z).
                var back = -FacingVector(carYaw);
                tankSpots.Add((carPos + back * Catalog.PickupBedCenterBehind, carYaw, Catalog.PickupBedFloor + 0.01f, "pickup bed"));
            }
        }

        // Mailbox at the curb by the driveway, bins by the garage.
        var mailX = driveCenterX + sideSign * (driveWidth / 2f + 0.6f);
        if (Mathf.Abs(mailX) < halfWidth - 0.4f)
        {
            AddProp("prop:mailbox", lot.LotToMap(mailX, 0.6f), streetYaw, 1f, PropBody.Breakaway, 12f, MailboxColor(r));
            tankSpots.Add((lot.LotToMap(mailX + sideSign * 0.9f, 1.1f), R(0, Mathf.Tau), 0f, "curb"));
        }
        // Bins: lined up on the driveway in front of the garage, or, with a car in the way, put out at the curb on
        // the house side of the driveway (bin day).
        var bins = r.Next(0, 3);
        for (var i = 0; i < bins; i++)
        {
            var bin = carParked
                ? lot.LotToMap(driveCenterX - sideSign * (driveWidth / 2f + 0.5f + i * 0.75f), 0.8f)
                : lot.LotToMap(driveCenterX + sideSign * (driveWidth / 2f - 0.45f), setback - 0.55f - i * 0.8f);
            var binYaw = carParked ? streetYaw : YawFacing(lot.Bounds.AxisX * -sideSign);
            AddProp("prop:wheelie_bin", bin, binYaw, 1f, PropBody.Loose, 14f, BinColor(r));
        }
        tankSpots.Add((lot.LotToMap(driveCenterX - sideSign * (driveWidth / 2f - 0.3f), setback + 0.6f), R(0, Mathf.Tau), 0f, "garage"));
        tankSpots.Add((lot.LotToMap(houseX + footprint.X * R(-0.3f, 0.3f), setback - 0.7f), R(0, Mathf.Tau), 0f, "porch"));

        // Front yard planting.
        var frontTrees = r.Next(0, 3);
        for (var i = 0; i < frontTrees; i++)
        {
            var x = -sideSign * R(halfWidth * 0.3f, halfWidth - 1.5f);
            var y = R(1.8f, setback - 1.2f);
            TryAddTree(lot.LotToMap(x, y), r);
        }
        var bushes = r.Next(1, 4);
        for (var i = 0; i < bushes; i++)
        {
            var x = Mathf.Lerp(houseLeft + 0.6f, houseRight - 0.6f, (float)r.NextDouble());
            AddProp($"nature:{Catalog.BushModels[r.Next(Catalog.BushModels.Length)]}", lot.LotToMap(x, setback - 0.6f), R(0, Mathf.Tau),
                R(1.6f, 2.4f), PropBody.Static, 0f, Colors.White);
        }

        // Backyard: patio behind the house, then lawn features.
        var patioDepth = Mathf.Min(4.5f, depth - houseBack - 3f);
        if (patioDepth > 2.5f)
        {
            var patioWidth = Mathf.Min(footprint.X * 0.75f, 9f);
            var patioX = houseX + R(-1f, 1f);
            AddPaving(lot, patioX, houseBack + patioDepth / 2f, patioWidth, patioDepth, PavingKind.Patio);
            var patioY = houseBack + patioDepth / 2f;
            // The grill takes one end of the patio and the heater the other.
            var grillSide = Chance(0.5) ? 1f : -1f;
            if (Chance(0.65))
            {
                var gx = patioX + patioWidth * 0.32f * grillSide;
                var grillPos = lot.LotToMap(gx, houseBack + 0.9f);
                AddProp("prop:gas_grill", grillPos, YawFacing(intoLot), 1f, PropBody.Loose, 45f, Colors.White);
                tankSpots.Add((lot.LotToMap(gx + 0.95f, houseBack + 0.9f), R(0, Mathf.Tau), 0f, "grill"));
            }
            if (Chance(0.6))
            {
                var tx = patioX - patioWidth * 0.12f;
                var tablePos = lot.LotToMap(tx, patioY + 0.3f);
                var table = Chance(0.5) ? "furniture:table" : "furniture:tableRound";
                AddProp(table, tablePos, R(0, Mathf.Tau), 1f, PropBody.Loose, 18f, Colors.White);
                // Chairs ring the table clear of its corners at any rotation.
                var reach = catalog.PropFootprint(table, 1f).Length() / 2f + 0.32f;
                var chairs = r.Next(2, 5);
                var start = R(0, Mathf.Tau);
                for (var i = 0; i < chairs; i++)
                {
                    var a = start + i * Mathf.Tau / chairs + R(-0.15f, 0.15f);
                    var p = tablePos + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * reach;
                    AddProp(Chance(0.5) ? "furniture:chair" : "furniture:chairCushion", p, YawFacing(tablePos - p), 1f, PropBody.Loose, 6f, Colors.White);
                }
                if (Chance(0.4))
                {
                    // A freestanding umbrella between two chairs, its canopy over the table.
                    var a = start + Mathf.Pi / chairs;
                    var umbrellaPos = tablePos + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * (reach + 0.05f);
                    AddProp("prop:patio_umbrella", umbrellaPos, R(0, Mathf.Tau), 1f, PropBody.Loose, 9f, UmbrellaColor(r));
                }
            }
            if (Chance(0.3))
            {
                var hx = patioX - patioWidth * 0.45f * grillSide;
                AddProp("prop:patio_heater", lot.LotToMap(hx, houseBack + 0.8f), R(0, Mathf.Tau), 1f, PropBody.Loose, 16f, Colors.White);
                tankSpots.Add((lot.LotToMap(hx + 0.7f, houseBack + 1.3f), R(0, Mathf.Tau), 0f, "patio heater"));
            }
        }

        var yardFront = houseBack + Mathf.Max(patioDepth, 0f) + 1f;
        var yardBack = depth - 1.2f;
        if (yardBack - yardFront > 3f)
        {
            if (Chance(0.35))
            {
                var sx = (Chance(0.5) ? -1 : 1) * (halfWidth - 2.1f);
                var shedPos = lot.LotToMap(sx, yardBack - 1.3f);
                if (TryClaim(new OrientedRect(shedPos, new Vector2(1.6f, 1.3f), lot.Bounds.Angle)))
                {
                    AddProp("prop:shed", shedPos, YawFacing(lot.TowardStreet), 1f, PropBody.Static, 0f, ShedColor(r));
                    tankSpots.Add((lot.LotToMap(sx - Mathf.Sign(sx) * 2.1f, yardBack - 1.0f), R(0, Mathf.Tau), 0f, "shed"));
                }
            }
            if (Chance(0.22))
            {
                var p = lot.LotToMap(R(-halfWidth + 2f, halfWidth - 2f), R(yardFront + 1f, yardBack - 1f));
                if (TryClaim(new OrientedRect(p, new Vector2(1f, 1f), 0)))
                {
                    AddProp("prop:kiddie_pool", p, R(0, Mathf.Tau), 1f, PropBody.Loose, 60f, PoolColor(r));
                }
            }
            if (Chance(0.35))
            {
                var p = lot.LotToMap(R(-halfWidth + 1.5f, halfWidth - 1.5f), R(yardFront, yardBack - 0.5f));
                if (TryClaim(new OrientedRect(p, new Vector2(0.65f, 0.65f), 0)))
                {
                    AddProp(Chance(0.5) ? "furniture:loungeChair" : "furniture:loungeChairRelax", p, YawFacing(lot.TowardStreet) + R(-0.6f, 0.6f), 1f,
                        PropBody.Loose, 9f, Colors.White);
                }
            }
            if (Chance(0.3))
            {
                var p = lot.LotToMap(R(-halfWidth + 1.5f, halfWidth - 1.5f), R(yardFront, yardBack - 0.5f));
                if (TryClaim(new OrientedRect(p, new Vector2(0.4f, 0.4f), 0)))
                {
                    AddProp("prop:cooler", p, R(0, Mathf.Tau), 1f, PropBody.Loose, 8f, CoolerColor(r));
                }
            }
            var backTrees = r.Next(0, 3);
            for (var i = 0; i < backTrees; i++)
            {
                TryAddTree(lot.LotToMap(R(-halfWidth + 1.8f, halfWidth - 1.8f), R(yardFront + 1f, yardBack - 0.8f)), r);
            }
            tankSpots.Add((lot.LotToMap(R(-halfWidth + 1.5f, halfWidth - 1.5f), R(yardFront, yardBack - 0.5f)), R(0, Mathf.Tau), 0f, "backyard"));
        }

        AddLotFences(lot, r, houseLeft, houseRight);
    }

    private void AddLotFences(Lot lot, Random r, float houseLeft, float houseRight)
    {
        var hw = lot.Bounds.HalfExtents.X;
        var depth = lot.Bounds.HalfExtents.Y * 2f;
        var ret = lot.ReturnDepth;
        Vector2 M(float x, float y) => lot.LotToMap(x, y);

        AddFence(M(-hw, depth), M(hw, depth));
        AddFence(M(-hw, ret), M(-hw, depth));
        AddFence(M(hw, ret), M(hw, depth));
        AddFence(M(-hw, ret), M(houseLeft, ret));
        AddFence(M(houseRight, ret), M(hw, ret));

        // Every backyard gets a gate opening, on the driveway side when there is room.
        var leftLength = houseLeft + hw;
        var rightLength = hw - houseRight;
        var preferLeft = lot.DrivewaySide == HouseSide.Left;
        var (len, start) = preferLeft ? (leftLength, -hw) : (rightLength, houseRight);
        if (len < FenceGateWidth + 0.8f)
        {
            (len, start) = preferLeft ? (rightLength, houseRight) : (leftLength, -hw);
        }
        if (len >= FenceGateWidth + 0.8f)
        {
            var gateCenter = start + len / 2f;
            fenceOpenings.Add((M(gateCenter - FenceGateWidth / 2f, ret), M(gateCenter + FenceGateWidth / 2f, ret)));
        }
        else
        {
            var x = preferLeft ? -hw : hw;
            fenceOpenings.Add((M(x, ret + 1f), M(x, ret + 1f + FenceGateWidth)));
        }
        // Some backyards also open to the one behind, so you can cut through.
        if (r.NextDouble() < 0.3)
        {
            var x = (float)(r.NextDouble() * 2 - 1) * (hw - 2f);
            fenceOpenings.Add((M(x - FenceGateWidth / 2f, depth), M(x + FenceGateWidth / 2f, depth)));
        }
    }

    private void AddFence(Vector2 a, Vector2 b)
    {
        if (a.DistanceTo(b) > 0.3f)
        {
            fenceSegments.Add((a, b));
        }
    }

    /// <summary>
    /// Neighbouring lots share boundaries, so fence segments are grouped by the line they lie on, unioned, and then
    /// the gate openings are cut out of the union.
    /// </summary>
    private void MergeFences()
    {
        var lines = new Dictionary<(int, int), List<(float, float)>>();
        var lineInfo = new Dictionary<(int, int), (Vector2 Dir, Vector2 Origin)>();
        (int, int) Key(Vector2 a, Vector2 b, out Vector2 dir, out float t0, out float t1, out Vector2 origin)
        {
            dir = (b - a).Normalized();
            // Canonical direction so opposite segments share a key.
            if (dir.X < -0.0001f || (Mathf.Abs(dir.X) <= 0.0001f && dir.Y < 0))
            {
                dir = -dir;
            }
            var normal = new Vector2(-dir.Y, dir.X);
            var offset = a.Dot(normal);
            var angleKey = Mathf.RoundToInt(Mathf.Atan2(dir.Y, dir.X) * 1000f);
            var offsetKey = Mathf.RoundToInt(offset * 20f);
            origin = normal * offset;
            t0 = (a - origin).Dot(dir);
            t1 = (b - origin).Dot(dir);
            if (t1 < t0)
            {
                (t0, t1) = (t1, t0);
            }
            return (angleKey, offsetKey);
        }

        foreach (var (a, b) in fenceSegments)
        {
            var key = Key(a, b, out var dir, out var t0, out var t1, out var origin);
            if (!lines.TryGetValue(key, out var list))
            {
                lines[key] = list = new List<(float, float)>();
                lineInfo[key] = (dir, origin);
            }
            list.Add((t0, t1));
        }
        var cuts = new Dictionary<(int, int), List<(float, float)>>();
        foreach (var (a, b) in fenceOpenings)
        {
            var key = Key(a, b, out _, out var t0, out var t1, out _);
            if (!cuts.TryGetValue(key, out var list))
            {
                cuts[key] = list = new List<(float, float)>();
            }
            list.Add((t0, t1));
        }

        foreach (var (key, intervals) in lines)
        {
            var merged = Union(intervals);
            if (cuts.TryGetValue(key, out var holes))
            {
                merged = Subtract(merged, Union(holes));
            }
            var (dir, origin) = lineInfo[key];
            foreach (var (t0, t1) in merged)
            {
                if (t1 - t0 > 0.4f)
                {
                    plan.Fences.Add(new FenceRun(origin + dir * t0, origin + dir * t1));
                }
            }
        }
    }

    private static List<(float, float)> Union(List<(float, float)> intervals)
    {
        var sorted = intervals.OrderBy(i => i.Item1).ToList();
        var result = new List<(float, float)>();
        foreach (var (a, b) in sorted)
        {
            if (result.Count > 0 && a <= result[^1].Item2 + 0.05f)
            {
                result[^1] = (result[^1].Item1, Mathf.Max(result[^1].Item2, b));
            }
            else
            {
                result.Add((a, b));
            }
        }
        return result;
    }

    private static List<(float, float)> Subtract(List<(float, float)> spans, List<(float, float)> holes)
    {
        var result = new List<(float, float)>();
        foreach (var (a, b) in spans)
        {
            var pieces = new List<(float, float)> { (a, b) };
            foreach (var (h0, h1) in holes)
            {
                var next = new List<(float, float)>();
                foreach (var (p0, p1) in pieces)
                {
                    if (h1 <= p0 || h0 >= p1)
                    {
                        next.Add((p0, p1));
                        continue;
                    }
                    if (h0 > p0)
                    {
                        next.Add((p0, h0));
                    }
                    if (h1 < p1)
                    {
                        next.Add((h1, p1));
                    }
                }
                pieces = next;
            }
            result.AddRange(pieces);
        }
        return result;
    }

    // ------------------------------------------------------------------ Streets and parkland

    private void PlaceStreetFurniture()
    {
        var verge = settings.CarriagewayHalfWidth + settings.VergeWidth * 0.5f;
        foreach (var road in plan.Roads)
        {
            var d = road.Direction;
            var n = new Vector2(-d.Y, d.X);
            var lampSide = rng.Next(2) == 0 ? 1f : -1f;
            for (var s = Range(6f, 14f); s < road.Length - 4f; s += Range(26f, 34f))
            {
                var p = road.A + d * s + n * verge * lampSide;
                if (IsClearOfOtherRoads(p, road, 1f))
                {
                    AddProp("prop:street_lamp", p, YawFacing(-n * lampSide), 1f, PropBody.Static, 0f, Colors.White);
                }
                lampSide = -lampSide;
            }
            for (var s = Range(15f, 40f); s < road.Length - 4f; s += Range(45f, 70f))
            {
                var side = rng.Next(2) == 0 ? 1f : -1f;
                var p = road.A + d * s + n * verge * side;
                if (IsClearOfOtherRoads(p, road, 1f))
                {
                    AddProp("prop:fire_hydrant", p, YawFacing(-n * side), 1f, PropBody.Breakaway, 90f, Colors.White);
                }
            }
            // Parked cars along the curb.
            for (var s = Range(10f, 30f); s < road.Length - 6f; s += Range(22f, 48f))
            {
                var side = rng.Next(2) == 0 ? 1f : -1f;
                var p = road.A + d * s + n * (settings.CarriagewayHalfWidth - 1.25f) * side;
                if (IsClearOfOtherRoads(p, road, 4f) && rng.NextDouble() < 0.6)
                {
                    var model = Catalog.CarModels[rng.Next(Catalog.CarModels.Length)];
                    var yaw = YawFacing(side > 0 ? -d : d);
                    AddProp($"car:{model}", p, yaw, 1f, PropBody.Loose, 1200f, CarColor(rng));
                    if (model == Catalog.PickupModel)
                    {
                        tankSpots.Add((p - FacingVector(yaw) * Catalog.PickupBedCenterBehind, yaw, Catalog.PickupBedFloor + 0.01f, "pickup bed"));
                    }
                }
            }
        }

        // Stop signs where roads meet.
        for (var i = 0; i < plan.Roads.Count; i++)
        {
            for (var j = i + 1; j < plan.Roads.Count; j++)
            {
                if (Intersection(plan.Roads[i], plan.Roads[j]) is not { } meet)
                {
                    continue;
                }
                var corner = settings.CarriagewayHalfWidth + settings.VergeWidth * 0.5f;
                var d = plan.Roads[i].Direction;
                var n = new Vector2(-d.Y, d.X);
                var p = meet + d * (corner + 2f) + n * corner;
                AddProp("prop:stop_sign", p, YawFacing(-d), 1f, PropBody.Breakaway, 25f, Colors.White);
            }
        }
    }

    /// <summary>Unclaimed lawn (outside lots and roads) gets trees and bushes, like a little park.</summary>
    private void ScatterParkland()
    {
        var attempts = (int)(settings.MapSize * settings.MapSize / 90f);
        for (var i = 0; i < attempts; i++)
        {
            var p = new Vector2(Range(plan.Bounds.Position.X + 2f, plan.Bounds.End.X - 2f), Range(plan.Bounds.Position.Y + 2f, plan.Bounds.End.Y - 2f));
            var probe = new OrientedRect(p, new Vector2(1.5f, 1.5f), 0);
            if (roadAreas.Any(r => r.Overlaps(probe)) || plan.Lots.Any(l => l.Bounds.Overlaps(probe)))
            {
                continue;
            }
            if (rng.NextDouble() < 0.6)
            {
                TryAddTree(p, rng);
            }
            else
            {
                AddProp($"nature:{Catalog.BushModels[rng.Next(Catalog.BushModels.Length)]}", p, Range(0, Mathf.Tau), Range(1.8f, 3f), PropBody.Static, 0f,
                    Colors.White);
            }
        }
    }

    // ------------------------------------------------------------------ Tanks and spawn

    private const float ClusterSpacing = 0.42f;

    /// <summary>
    /// Spreads groups of tanks across the map: greedy farthest-point picks among the story spots (grills, garages,
    /// pickup beds...), each filled with a group of tanks packed together, until the total is reached.
    /// </summary>
    private void ChooseTanks()
    {
        var candidates = tankSpots.Where(s => InsideMap(new OrientedRect(s.Position, new Vector2(0.4f, 0.4f), 0)) &&
                                              (s.Elevation > 0 || IsClearForTank(s.Position)))
            .OrderBy(_ => rng.Next()).ToList();
        var sites = new List<Vector2>();
        var minSize = Mathf.Clamp(settings.ClusterSizeMin, 1, settings.ClusterSizeMax);
        while (plan.Tanks.Count < settings.TankCount && candidates.Count > 0)
        {
            // A little noise keeps the spread from looking regular. With a centre bias, the first group goes nearest
            // the centre and later ones weigh closeness to it against spreading out.
            var spot = sites.Count == 0
                ? settings.CentreBias > 0 ? candidates.OrderBy(c => c.Position.DistanceTo(MapCenter)).First() : candidates[0]
                : candidates.OrderByDescending(c => sites.Min(t => t.DistanceTo(c.Position)) * SpotAppeal(c.Reason) *
                                                    CentreFactor(c.Position) * (0.75f + 0.5f * (float)rng.NextDouble())).First();
            candidates.Remove(spot);
            // Group sizes lean small, so a map has a few big stacks among pairs and loners.
            var size = minSize + (int)((settings.ClusterSizeMax - minSize + 1) * Mathf.Pow((float)rng.NextDouble(), 1.4f));
            size = Mathf.Min(size, settings.TankCount - plan.Tanks.Count);
            var placed = 0;
            foreach (var slot in ClusterSlots(spot))
            {
                if (placed >= size)
                {
                    break;
                }
                if (plan.Tanks.All(t => t.Position.DistanceTo(slot) > ClusterSpacing * 0.9f || Mathf.Abs(t.Elevation - spot.Elevation) > 0.5f))
                {
                    var reason = placed == 0 ? spot.Reason : $"{spot.Reason} group";
                    plan.Tanks.Add(new TankPlacement(slot, Range(0, Mathf.Tau), spot.Elevation, reason));
                    placed++;
                }
            }
            if (placed > 0)
            {
                sites.Add(spot.Position);
            }
        }
    }

    /// <summary>Where the tanks of a group can stand, nearest the spot first: a packed lattice, or the pickup's bed.</summary>
    private IEnumerable<Vector2> ClusterSlots((Vector2 Position, float Yaw, float Elevation, string Reason) spot)
    {
        if (spot.Elevation > 0)
        {
            // Rows across the bed, in the truck's frame (the spot's yaw is the truck's).
            var along = FacingVector(spot.Yaw);
            var across = new Vector2(along.Y, -along.X);
            var offsets = new List<Vector2>();
            foreach (var a in new[] { -0.6f, -0.2f, 0.2f, 0.6f })
            {
                foreach (var c in new[] { -0.6f, -0.2f, 0.2f, 0.6f })
                {
                    offsets.Add(new Vector2(a, c));
                }
            }
            return offsets.OrderBy(o => o.Length() + (float)rng.NextDouble() * 0.05f)
                .Select(o => spot.Position + along * o.X + across * o.Y).ToList();
        }
        // A hexagonal lattice around the spot, slightly jittered, kept to clear ground.
        var slots = new List<Vector2>();
        var rowStep = ClusterSpacing * Mathf.Sqrt(3f) / 2f;
        var turn = Range(0, Mathf.Tau);
        for (var row = -4; row <= 4; row++)
        {
            for (var col = -4; col <= 4; col++)
            {
                var local = new Vector2(col * ClusterSpacing + (row % 2 != 0 ? ClusterSpacing / 2f : 0f), row * rowStep);
                if (local.Length() > ClusterSpacing * 4f)
                {
                    continue;
                }
                var jitter = new Vector2(Range(-0.03f, 0.03f), Range(-0.03f, 0.03f));
                slots.Add(spot.Position + local.Rotated(turn) + jitter);
            }
        }
        return slots.OrderBy(p => p.DistanceTo(spot.Position))
            .Where(p => IsClearForTank(p) && InsideMap(new OrientedRect(p, new Vector2(0.4f, 0.4f), 0))).ToList();
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
    private bool IsClearForTank(Vector2 p)
    {
        var tank = new OrientedRect(p, new Vector2(0.22f, 0.22f), 0);
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

    /// <summary>1 inside the central area, falling away beyond it as strongly as the centre bias asks.</summary>
    private float CentreFactor(Vector2 p)
    {
        if (settings.CentreBias <= 0)
        {
            return 1f;
        }
        var radius = Mathf.Max(settings.CentreRadius, 1f);
        var beyond = Mathf.Max(0f, p.DistanceTo(MapCenter) - radius) / radius;
        var factor = 1f / (1f + settings.CentreBias * beyond);
        return factor * factor;
    }

    /// <summary>
    /// Player spawns for a match: points on the roads around the central area, clear of cars and tanks, spread as far
    /// apart as they can be, each facing the centre.
    /// </summary>
    private void ChooseSpawns(int count)
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
        if (candidates.Count == 0)
        {
            candidates.Add(plan.Spawn);
        }
        // Keep to the ring around the action when there is room; otherwise take the nearest points there are.
        var reach = Mathf.Max(settings.CentreRadius, 20f) * 1.4f;
        var near = candidates.Where(p => p.DistanceTo(MapCenter) <= reach).ToList();
        if (near.Count < count * 3)
        {
            near = candidates.OrderBy(p => p.DistanceTo(MapCenter)).Take(Math.Max(count * 6, near.Count)).ToList();
        }
        var chosen = new List<Vector2> { near.OrderByDescending(p => p.DistanceTo(MapCenter) + Range(0f, 6f)).First() };
        while (chosen.Count < count)
        {
            var next = near.OrderByDescending(p => chosen.Min(c => c.DistanceTo(p))).First();
            if (chosen.Min(c => c.DistanceTo(next)) < 0.5f)
            {
                // Fewer distinct points than players: reuse them in turn, nudged apart.
                next = chosen[chosen.Count % Math.Max(1, chosen.Count)] + new Vector2(1.2f, 0).Rotated(chosen.Count);
            }
            chosen.Add(next);
        }
        foreach (var p in chosen)
        {
            var toCentre = MapCenter - p;
            plan.Spawns.Add(new SpawnPoint(p, YawFacing(toCentre.LengthSquared() > 0.01f ? toCentre : Vector2.Up)));
        }
    }

    /// <summary>Ammo pickups on the sidewalks: clear spots, spread apart, favouring the central area like the tanks.</summary>
    private void ChooseAmmoSpots(int count)
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
                    if (plan.Bounds.Grow(-2f).HasPoint(p) && IsClearOfOtherRoads(p, road, 0.5f) && IsClearForPickup(p))
                    {
                        candidates.Add(p);
                    }
                }
            }
        }
        if (candidates.Count == 0)
        {
            return;
        }
        var chosen = new List<Vector2> { candidates.OrderBy(p => p.DistanceTo(MapCenter) + Range(0f, 10f)).First() };
        while (chosen.Count < count)
        {
            var best = candidates.OrderByDescending(p => chosen.Min(c => c.DistanceTo(p)) * CentreFactor(p) * (0.85f + 0.3f * (float)rng.NextDouble())).First();
            if (chosen.Min(c => c.DistanceTo(best)) < 4f)
            {
                break;
            }
            chosen.Add(best);
        }
        plan.AmmoSpots.AddRange(chosen);
    }

    /// <summary>A pickup (about a metre across) fits here clear of props, tanks and fences.</summary>
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

    // ------------------------------------------------------------------ Helpers

    private void AddPaving(Lot lot, float x, float y, float width, float length, PavingKind kind)
    {
        if (width < 0.5f || length < 0.5f)
        {
            return;
        }
        plan.Pavings.Add(new Paving(new OrientedRect(lot.LotToMap(x, y), new Vector2(width / 2f, length / 2f), lot.Bounds.Angle), kind));
    }

    private void AddProp(string model, Vector2 position, float yaw, float scale, PropBody body, float mass, Color tint, float elevation = 0)
    {
        plan.Props.Add(new PropPlacement(model, position, yaw, scale, body, mass, tint, elevation));
        if (model.StartsWith("prop:") || model.StartsWith("car:") || model.StartsWith("furniture:"))
        {
            // Model +Z is its front; in map space its local X axis is the facing rotated -90 degrees.
            var size = catalog.PropFootprint(model, scale);
            var facing = FacingVector(yaw);
            var angle = Mathf.Atan2(-facing.X, facing.Y);
            obstacles.Add(new OrientedRect(position, size / 2f, angle));
        }
    }

    private void TryAddTree(Vector2 position, Random r)
    {
        var probe = new OrientedRect(position, new Vector2(1.2f, 1.2f), 0);
        if (!TryClaim(probe))
        {
            return;
        }
        var model = Catalog.TreeModels[r.Next(Catalog.TreeModels.Length)];
        AddProp($"nature:{model}", position, (float)r.NextDouble() * Mathf.Tau, 4.2f + (float)r.NextDouble() * 2.6f, PropBody.Static, 0f, Colors.White);
    }

    private bool TryClaim(OrientedRect area)
    {
        if (occupied.Any(o => o.Overlaps(area)) || roadAreas.Any(r => r.Overlaps(area)))
        {
            return false;
        }
        occupied.Add(area);
        return true;
    }

    private bool IsClearOfOtherRoads(Vector2 p, RoadSegment self, float margin)
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

    private static Vector2? Intersection(RoadSegment a, RoadSegment b)
    {
        var da = a.Direction;
        var db = b.Direction;
        var cross = da.X * db.Y - da.Y * db.X;
        if (Mathf.Abs(cross) < 0.5f)
        {
            return null;
        }
        var diff = b.A - a.A;
        var t = (diff.X * db.Y - diff.Y * db.X) / cross;
        var u = (diff.X * da.Y - diff.Y * da.X) / cross;
        if (t < -0.5f || t > a.Length + 0.5f || u < -0.5f || u > b.Length + 0.5f)
        {
            return null;
        }
        return a.A + da * t;
    }

    /// <summary>World yaw that turns a model's +Z front toward a map-space direction.</summary>
    public static float YawFacing(Vector2 direction) => Mathf.Atan2(direction.X, direction.Y);

    /// <summary>The map-space direction a model's +Z front faces at a yaw.</summary>
    public static Vector2 FacingVector(float yaw) => new(Mathf.Sin(yaw), Mathf.Cos(yaw));

    private float Range(float min, float max) => min + (float)rng.NextDouble() * (max - min);

    private static Color CarColor(Random r) => Colors.White;

    private static Color MailboxColor(Random r) => Pick(r, new Color(0.85f, 0.85f, 0.85f), new Color(0.12f, 0.12f, 0.13f), new Color(0.15f, 0.3f, 0.55f));

    private static Color BinColor(Random r) => Pick(r, new Color(0.2f, 0.42f, 0.22f), new Color(0.15f, 0.28f, 0.55f), new Color(0.25f, 0.26f, 0.28f));

    private static Color UmbrellaColor(Random r) => Pick(r, new Color(0.85f, 0.3f, 0.2f), new Color(0.2f, 0.45f, 0.7f), new Color(0.9f, 0.85f, 0.7f), new Color(0.3f, 0.6f, 0.4f));

    private static Color ShedColor(Random r) => Pick(r, new Color(0.75f, 0.85f, 0.8f), new Color(0.9f, 0.85f, 0.75f), new Color(0.7f, 0.6f, 0.5f), new Color(0.8f, 0.82f, 0.9f));

    private static Color PoolColor(Random r) => Pick(r, new Color(0.3f, 0.55f, 0.95f), new Color(0.95f, 0.5f, 0.7f), new Color(0.4f, 0.85f, 0.6f));

    private static Color CoolerColor(Random r) => Pick(r, new Color(0.2f, 0.45f, 0.8f), new Color(0.8f, 0.2f, 0.15f), new Color(0.25f, 0.6f, 0.35f));

    private static Color Pick(Random r, params Color[] options) => options[r.Next(options.Length)];
}
