using System.Collections.Generic;
using System.IO;
using System.Text;
using Godot;

namespace Propane.Net;

/// <summary>
/// Opaque sRGB bytes and optional cosmetic catalog ids. Missing or unsupported cosmetics render the base character.
/// Catalog ids are identifiers, never resource paths; rendering/entitlements remain a future catalog's job.
/// </summary>
public sealed record AppearanceRecord(byte Red, byte Green, byte Blue, string? ModelOrSkin = null,
    string? Outfit = null, string? Hat = null, string? GunSkin = null, string? BulletSkin = null)
{
    public Color Color => new(Red / 255f, Green / 255f, Blue / 255f, 1);
    public static AppearanceRecord FromColor(Color color) => new((byte)color.R8, (byte)color.G8, (byte)color.B8);
    public AppearanceRecord WithColor(Color color) => this with { Red = (byte)color.R8, Green = (byte)color.G8, Blue = (byte)color.B8 };
    public static AppearanceRecord FromPalette(int index) => FromColor(Protocol.PlayerColor(index));

    /// <summary>A valid old-client fallback; it does not constrain or replace the selected color.</summary>
    public int LegacyIndex
    {
        get
        {
            var best = 0;
            var distance = float.MaxValue;
            for (var i = 0; i < Protocol.PlayerColors.Length; i++)
            {
                var delta = Color - Protocol.PlayerColors[i];
                var squared = delta.R * delta.R + delta.G * delta.G + delta.B * delta.B;
                if (squared < distance) { best = i; distance = squared; }
            }
            return best;
        }
    }
}

/// <summary>
/// One codec for settings and every profile on the wire. Version 1: version/R/G/B/count bytes, then optional
/// tag:u8, length:u16, payload bytes. Unknown tags are skipped; known tags contain bounded ASCII catalog ids.
/// The enclosing message uses a length-prefixed byte array so later optional slots never change its shape.
/// </summary>
public static class AppearanceCodec
{
    public const byte Version = 1;
    public const int MaxRecordBytes = 1024;
    public const int MaxSlots = 16;
    public const int MaxSlotBytes = 64;
    public enum Slot : byte { ModelOrSkin = 1, Outfit = 2, Hat = 3, GunSkin = 4, BulletSkin = 5 }

    public static byte[] Encode(AppearanceRecord appearance)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(Version);
        writer.Write(appearance.Red); writer.Write(appearance.Green); writer.Write(appearance.Blue);
        // Explicit tags stay stable when a future slot is added; array/record member order is not wire identity.
        var slots = new (Slot Tag, string? Id)[]
        {
            (Slot.ModelOrSkin, appearance.ModelOrSkin), (Slot.Outfit, appearance.Outfit), (Slot.Hat, appearance.Hat),
            (Slot.GunSkin, appearance.GunSkin), (Slot.BulletSkin, appearance.BulletSkin),
        };
        var count = 0;
        foreach (var (_, id) in slots) { if (id != null) { ValidateId(id); count++; } }
        writer.Write((byte)count);
        foreach (var (tag, id) in slots)
        {
            if (id == null) { continue; }
            writer.Write((byte)tag); writer.Write((ushort)id.Length); writer.Write(Encoding.ASCII.GetBytes(id));
        }
        return stream.ToArray();
    }

    public static AppearanceRecord Decode(byte[] bytes)
    {
        if (bytes.Length is < 5 or > MaxRecordBytes) { throw new InvalidDataException("bad appearance length"); }
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(stream);
        if (reader.ReadByte() != Version) { throw new InvalidDataException("unsupported appearance version"); }
        var red = reader.ReadByte(); var green = reader.ReadByte(); var blue = reader.ReadByte();
        var count = NetReader.Count(reader.ReadByte(), MaxSlots);
        var ids = new string?[5];
        var seen = new HashSet<byte>();
        for (var i = 0; i < count; i++)
        {
            if (stream.Length - stream.Position < 3) { throw new InvalidDataException("truncated appearance slot"); }
            var tag = reader.ReadByte();
            if (tag == 0 || !seen.Add(tag)) { throw new InvalidDataException("bad appearance tag"); }
            var length = reader.ReadUInt16();
            if (length > MaxSlotBytes || length > stream.Length - stream.Position) { throw new InvalidDataException("bad appearance slot length"); }
            var payload = reader.ReadBytes(length);
            if (tag is >= (byte)Slot.ModelOrSkin and <= (byte)Slot.BulletSkin)
            {
                // ASCII decoding replaces non-ASCII bytes; ValidateId rejects the replacement character.
                var id = Encoding.ASCII.GetString(payload);
                ValidateId(id);
                ids[tag - 1] = id;
            }
        }
        if (stream.Position != stream.Length) { throw new InvalidDataException("trailing appearance payload"); }
        return new AppearanceRecord(red, green, blue, ids[0], ids[1], ids[2], ids[3], ids[4]);
    }

    private static void ValidateId(string id)
    {
        if (id.Length is < 1 or > MaxSlotBytes) { throw new InvalidDataException("bad cosmetic id length"); }
        foreach (var c in id)
        {
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-'))
            {
                throw new InvalidDataException("bad cosmetic catalog id");
            }
        }
    }
}
