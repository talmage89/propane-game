using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Godot;
using Propane.World.Plan;

namespace Propane.Net;

/// <summary>
/// Packs a whole <see cref="SuburbPlan"/> into bytes and back. The server generates each match's suburb and sends it
/// whole rather than just the seed: the generator's trigonometry can differ in the last bit between an Apple Silicon
/// Mac and an x86 Linux machine, and one flipped comparison would give the players different suburbs.
/// </summary>
public static class PlanCodec
{
    private const int FormatVersion = 1;

    public static byte[] Encode(SuburbPlan plan)
    {
        using var raw = new MemoryStream();
        using (var w = new BinaryWriter(raw))
        {
            w.Write(FormatVersion);
            w.Write(plan.Seed);
            Rect(w, plan.Bounds);

            w.Write(plan.Roads.Count);
            foreach (var road in plan.Roads)
            {
                Vec2(w, road.A);
                Vec2(w, road.B);
            }
            w.Write(plan.CulDeSacs.Count);
            foreach (var bulb in plan.CulDeSacs)
            {
                Vec2(w, bulb.Center);
                w.Write(bulb.Radius);
            }
            w.Write(plan.Lots.Count);
            foreach (var lot in plan.Lots)
            {
                Oriented(w, lot.Bounds);
                w.Write(lot.Seed);
                w.Write(lot.HouseModel);
                w.Write(lot.HousePalette);
                Oriented(w, lot.House);
                w.Write(lot.ReturnDepth);
                w.Write((byte)lot.DrivewaySide);
            }
            w.Write(plan.Fences.Count);
            foreach (var fence in plan.Fences)
            {
                Vec2(w, fence.A);
                Vec2(w, fence.B);
            }
            w.Write(plan.Pavings.Count);
            foreach (var paving in plan.Pavings)
            {
                Oriented(w, paving.Area);
                w.Write((byte)paving.Kind);
            }

            // Prop models repeat a lot, so they go through a string table.
            var names = new List<string>();
            var index = new Dictionary<string, int>();
            foreach (var prop in plan.Props)
            {
                if (!index.ContainsKey(prop.Model))
                {
                    index[prop.Model] = names.Count;
                    names.Add(prop.Model);
                }
            }
            w.Write(names.Count);
            foreach (var name in names)
            {
                w.Write(name);
            }
            w.Write(plan.Props.Count);
            foreach (var prop in plan.Props)
            {
                w.Write(index[prop.Model]);
                Vec2(w, prop.Position);
                w.Write(prop.Yaw);
                w.Write(prop.Scale);
                w.Write((byte)prop.Body);
                w.Write(prop.Mass);
                w.Write(prop.Tint.R);
                w.Write(prop.Tint.G);
                w.Write(prop.Tint.B);
                w.Write(prop.Tint.A);
                w.Write(prop.Elevation);
            }

            w.Write(plan.Tanks.Count);
            foreach (var tank in plan.Tanks)
            {
                Vec2(w, tank.Position);
                w.Write(tank.Yaw);
                w.Write(tank.Elevation);
                w.Write(tank.Reason);
            }
            Vec2(w, plan.Spawn);
            w.Write(plan.SpawnYaw);
            w.Write(plan.Spawns.Count);
            foreach (var spawn in plan.Spawns)
            {
                Vec2(w, spawn.Position);
                w.Write(spawn.Yaw);
            }
            w.Write(plan.AmmoSpots.Count);
            foreach (var spot in plan.AmmoSpots)
            {
                Vec2(w, spot);
            }
        }

        using var packed = new MemoryStream();
        using (var deflate = new DeflateStream(packed, CompressionLevel.Optimal))
        {
            var bytes = raw.ToArray();
            deflate.Write(bytes, 0, bytes.Length);
        }
        return packed.ToArray();
    }

    public static SuburbPlan Decode(byte[] data)
    {
        using var packed = new MemoryStream(data);
        using var deflate = new DeflateStream(packed, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        deflate.CopyTo(raw);
        raw.Position = 0;
        using var r = new BinaryReader(raw);

        var version = r.ReadInt32();
        if (version != FormatVersion)
        {
            throw new InvalidDataException($"plan format {version}, expected {FormatVersion}");
        }
        var plan = new SuburbPlan { Seed = r.ReadInt32(), Bounds = Rect(r) };
        for (int i = 0, n = r.ReadInt32(); i < n; i++)
        {
            plan.Roads.Add(new RoadSegment(Vec2(r), Vec2(r)));
        }
        for (int i = 0, n = r.ReadInt32(); i < n; i++)
        {
            plan.CulDeSacs.Add(new CulDeSac(Vec2(r), r.ReadSingle()));
        }
        for (int i = 0, n = r.ReadInt32(); i < n; i++)
        {
            var lot = new Lot { Bounds = Oriented(r), Seed = r.ReadInt32() };
            lot.HouseModel = r.ReadInt32();
            lot.HousePalette = r.ReadInt32();
            lot.House = Oriented(r);
            lot.ReturnDepth = r.ReadSingle();
            lot.DrivewaySide = (HouseSide)r.ReadByte();
            plan.Lots.Add(lot);
        }
        for (int i = 0, n = r.ReadInt32(); i < n; i++)
        {
            plan.Fences.Add(new FenceRun(Vec2(r), Vec2(r)));
        }
        for (int i = 0, n = r.ReadInt32(); i < n; i++)
        {
            plan.Pavings.Add(new Paving(Oriented(r), (PavingKind)r.ReadByte()));
        }
        var names = new List<string>();
        for (int i = 0, n = r.ReadInt32(); i < n; i++)
        {
            names.Add(r.ReadString());
        }
        for (int i = 0, n = r.ReadInt32(); i < n; i++)
        {
            var model = names[r.ReadInt32()];
            var position = Vec2(r);
            var yaw = r.ReadSingle();
            var scale = r.ReadSingle();
            var body = (PropBody)r.ReadByte();
            var mass = r.ReadSingle();
            var tint = new Color(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            var elevation = r.ReadSingle();
            plan.Props.Add(new PropPlacement(model, position, yaw, scale, body, mass, tint, elevation));
        }
        for (int i = 0, n = r.ReadInt32(); i < n; i++)
        {
            plan.Tanks.Add(new TankPlacement(Vec2(r), r.ReadSingle(), r.ReadSingle(), r.ReadString()));
        }
        plan.Spawn = Vec2(r);
        plan.SpawnYaw = r.ReadSingle();
        for (int i = 0, n = r.ReadInt32(); i < n; i++)
        {
            plan.Spawns.Add(new SpawnPoint(Vec2(r), r.ReadSingle()));
        }
        for (int i = 0, n = r.ReadInt32(); i < n; i++)
        {
            plan.AmmoSpots.Add(Vec2(r));
        }
        return plan;
    }

    private static void Vec2(BinaryWriter w, Vector2 v)
    {
        w.Write(v.X);
        w.Write(v.Y);
    }

    private static Vector2 Vec2(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle());

    private static void Rect(BinaryWriter w, Rect2 rect)
    {
        Vec2(w, rect.Position);
        Vec2(w, rect.Size);
    }

    private static Rect2 Rect(BinaryReader r) => new(Vec2(r), Vec2(r));

    private static void Oriented(BinaryWriter w, OrientedRect rect)
    {
        Vec2(w, rect.Center);
        Vec2(w, rect.HalfExtents);
        w.Write(rect.Angle);
    }

    private static OrientedRect Oriented(BinaryReader r) => new(Vec2(r), Vec2(r), r.ReadSingle());
}
