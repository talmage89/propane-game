using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Text;
using Godot;

namespace Propane.Net;

/// <summary>
/// Message types. Clients only ever talk to the server. Forwarded match messages gain a sender after the match id;
/// Claim, Puncture and Explode are consumed requests whose ordered results arrive as Authority. Mode-specific traffic
/// travels inside the three fixed mode envelopes (ModeEvent, ModeCommand, ModeSnapshot).
/// </summary>
public enum Msg : byte
{
    // Client to server.
    Hello = 1,
    CreateLobby,
    JoinLobby,
    LeaveLobby,
    SetProfile,
    SetMatchLength,
    StartMatch,
    Loaded,
    ClaimPickup,
    Ping,
    SetCapacity,
    AddActor,
    UpdateActor,
    RemoveActor,
    ModeEvent,
    SetMode,
    CancelStart,

    // Server to client. Numbers stay fixed: retired messages leave gaps (40 was Score, 47 DropRefused).
    Welcome = 32,
    Rejected,
    LobbyList,
    LobbyState,
    MatchStart,
    Countdown,
    MatchGo,
    Clock,
    Notice = 41,
    PickupTaken,
    PickupRespawn,
    MatchEnd,
    PlayerLeft,
    Pong,
    Authority = 48,
    RosterState,
    ActorRequestResult,
    ModeCommand,
    ModeSnapshot,

    // Match traffic. Authority requests are resolved by the server; the other messages are relayed. 71 was Drop.
    PlayerState = 64,
    Shot,
    Puncture,
    Explode,
    Claim,
    BodyStates,
    BodyRest,
    Ragdoll = 72,
    GetUp,
    TuningSet,
    Gather,
    MeleeStart,
    MeleeContact,
}

public static class Protocol
{
    /// <summary>
    /// Bumped whenever a message changes shape. Clients and server must agree exactly, and a release that changes it
    /// must bump at least the minor version (tools/release/check_version.sh enforces this). Hello, Welcome and
    /// Rejected never change shape (fields may only be appended), so any build can be told why it was turned away.
    /// </summary>
    public const int Version = 3;

    public const int DefaultPort = 24680;

    public const int MaxNameLength = 16;
    public const int TestedActorCeiling = 16;
    public const int MaxWireActors = 64;
    public const int MaxWirePeers = 64;
    public static bool IsActorMessage(Msg type) => type is Msg.PlayerState or Msg.Shot or Msg.MeleeStart or
        Msg.MeleeContact or Msg.Puncture or Msg.Explode or Msg.Ragdoll or Msg.GetUp or Msg.ClaimPickup;

    public static bool IsRelay(Msg type) => type is >= Msg.PlayerState and <= Msg.MeleeContact && Enum.IsDefined(type);

    /// <summary>The fixed outer mode envelope; see <see cref="ModeEnvelope"/>.</summary>
    public static bool IsMode(Msg type) => type is Msg.ModeEvent or Msg.ModeCommand or Msg.ModeSnapshot;

    public static bool IsMatch(Msg type) => IsRelay(type) || type is Msg.Loaded or Msg.ClaimPickup or Msg.MatchStart or
        Msg.Countdown or Msg.MatchGo or Msg.Clock or Msg.PickupTaken or Msg.PickupRespawn or Msg.MatchEnd or Msg.PlayerLeft or Msg.Authority || IsMode(type);

    // Wire safety limits, not gameplay tuners. Gameplay counts are also checked against the match's tuning.
    public const int MaxPacketBytes = 1 << 20;
    public const int MaxCollection = 4096;
    public const int MaxBodyStates = 20;

    /// <summary>Quick presets and legacy Hello fallback; players may choose any opaque sRGB color.</summary>
    public static readonly Color[] PlayerColors =
    {
        new(0.92f, 0.44f, 0.16f), // orange
        new(0.2f, 0.52f, 0.95f), // blue
        new(0.3f, 0.78f, 0.32f), // green
        new(0.9f, 0.22f, 0.3f), // red
        new(0.62f, 0.36f, 0.92f), // purple
        new(0.98f, 0.8f, 0.15f), // yellow
        new(0.15f, 0.8f, 0.78f), // teal
        new(0.95f, 0.42f, 0.72f), // pink
    };

    public static readonly string[] ColorNames = { "Orange", "Blue", "Green", "Red", "Purple", "Yellow", "Teal", "Pink" };

    public static Color PlayerColor(int index) => PlayerColors[Mathf.PosMod(index, PlayerColors.Length)];

    /// <summary>
    /// A name the server accepts exactly as sent for a roster request: 1 to <see cref="MaxNameLength"/> UTF-16 units,
    /// no surrounding spaces, and only printable letters, digits, punctuation, symbols and spaces.
    /// </summary>
    public static bool ValidActorName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength || name.Trim() != name) return false;
        foreach (var c in name)
        {
            switch (char.GetUnicodeCategory(c))
            {
                case UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse or
                    UnicodeCategory.OtherNotAssigned or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator:
                    return false;
                case UnicodeCategory.SpaceSeparator when c != ' ':
                    return false;
            }
        }
        return true;
    }

    public static bool SameName(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public static string CleanName(string? name)
    {
        var trimmed = (name ?? "").Trim();
        var builder = new StringBuilder();
        foreach (var c in trimmed)
        {
            if (!char.IsControl(c))
            {
                builder.Append(c);
            }
        }
        var clean = builder.ToString();
        if (clean.Length > MaxNameLength)
        {
            clean = clean[..MaxNameLength];
        }
        return clean.Length == 0 ? "Player" : clean;
    }
}

/// <summary>Builds one message: a type byte and little-endian fields.</summary>
[SuppressMessage("Design", "CA1001", Justification = "The writer and MemoryStream hold only managed byte arrays, with no file or socket handles.")]
public sealed class NetWriter
{
    private readonly MemoryStream stream = new();
    private readonly BinaryWriter writer;

    public NetWriter(Msg type, int matchId = 0, int actorId = 0, uint lifeId = 0, uint actionId = 0)
    {
        writer = new BinaryWriter(stream, Encoding.UTF8);
        writer.Write((byte)type);
        if (Protocol.IsMatch(type))
        {
            if (matchId <= 0) { throw new ArgumentOutOfRangeException(nameof(matchId)); }
            writer.Write(matchId);
        }
        if (Protocol.IsActorMessage(type))
        {
            writer.Write(actorId);
            writer.Write(lifeId);
            writer.Write(actionId);
        }
    }

    private NetWriter() => writer = new BinaryWriter(stream, Encoding.UTF8);

    /// <summary>A bare payload writer, without a type byte or header: mode envelope payloads.</summary>
    public static NetWriter Payload() => new();

    public NetWriter Raw(byte[] v) { writer.Write(v); return this; }

    public NetWriter Byte(byte v) { writer.Write(v); return this; }

    public NetWriter Bool(bool v) { writer.Write(v); return this; }

    public NetWriter Short(short v) { writer.Write(v); return this; }

    public NetWriter UShort(ushort v) { writer.Write(v); return this; }

    public NetWriter Int(int v) { writer.Write(v); return this; }

    public NetWriter UInt(uint v) { writer.Write(v); return this; }

    public NetWriter Float(float v) { writer.Write(v); return this; }

    public NetWriter Double(double v) { writer.Write(v); return this; }

    public NetWriter String(string v) { writer.Write(v); return this; }

    public NetWriter Vec2(Vector2 v) { writer.Write(v.X); writer.Write(v.Y); return this; }

    public NetWriter Vec3(Vector3 v) { writer.Write(v.X); writer.Write(v.Y); writer.Write(v.Z); return this; }

    public NetWriter Quat(Quaternion q) { writer.Write(q.X); writer.Write(q.Y); writer.Write(q.Z); writer.Write(q.W); return this; }

    public NetWriter Bytes(byte[] v) { writer.Write(v.Length); writer.Write(v); return this; }

    public NetWriter Appearance(AppearanceRecord appearance) => Bytes(AppearanceCodec.Encode(appearance));

    public NetWriter Transform(Transform3D t) => Vec3(t.Origin).Quat(t.Basis.Orthonormalized().GetRotationQuaternion());

    /// <summary>Only the three scalar tuner types cross the wire; never arbitrary serialized Godot objects.</summary>
    public NetWriter Scalar(Variant value)
    {
        Byte((byte)value.VariantType);
        return value.VariantType switch
        {
            Variant.Type.Bool => Bool(value.AsBool()),
            Variant.Type.Int => Int(checked((int)value.AsInt64())),
            Variant.Type.Float => Double(value.AsDouble()),
            _ => throw new InvalidDataException("unsupported tuner type"),
        };
    }

    public NetWriter Snapshot(Godot.Collections.Dictionary values)
    {
        Int(values.Count);
        foreach (var (name, value) in values) { String(name.AsString()).Scalar(value); }
        return this;
    }

    public byte[] ToArray()
    {
        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>Bytes written so far, including the type byte.</summary>
    public long Length => stream.Length;
}

/// <summary>Reads the fields of one message after its type byte.</summary>
[SuppressMessage("Design", "CA1001", Justification = "The reader wraps only a managed byte-array MemoryStream, with no file or socket handles.")]
public sealed class NetReader
{
    private readonly BinaryReader reader;

    public NetReader(byte[] data, int offset = 1)
    {
        if (offset < 0 || offset > data.Length) { throw new InvalidDataException("bad message offset"); }
        reader = new BinaryReader(new MemoryStream(data, offset, data.Length - offset), new UTF8Encoding(false, true));
    }

    public bool AtEnd => reader.BaseStream.Position >= reader.BaseStream.Length;

    public long Remaining => reader.BaseStream.Length - reader.BaseStream.Position;

    public void End()
    {
        if (!AtEnd) { throw new InvalidDataException("trailing payload"); }
    }

    public byte Byte() => reader.ReadByte();

    public bool Bool() => Byte() switch { 0 => false, 1 => true, _ => throw new InvalidDataException("bad boolean") };

    public short Short() => reader.ReadInt16();

    public ushort UShort() => reader.ReadUInt16();

    public int Int() => reader.ReadInt32();

    public uint UInt() => reader.ReadUInt32();

    public float Float()
    {
        var value = reader.ReadSingle();
        if (!float.IsFinite(value)) { throw new InvalidDataException("nonfinite float"); }
        return value;
    }

    public double Double()
    {
        var value = reader.ReadDouble();
        if (!double.IsFinite(value)) { throw new InvalidDataException("nonfinite double"); }
        return value;
    }

    public string String()
    {
        var length = reader.Read7BitEncodedInt();
        if (length < 0 || length > 1024 || length > Remaining) { throw new InvalidDataException("bad string length"); }
        return new UTF8Encoding(false, true).GetString(reader.ReadBytes(length));
    }

    public Vector2 Vec2() => new(Float(), Float());

    public Vector3 Vec3() => new(Float(), Float(), Float());

    public Quaternion Quat()
    {
        var q = new Quaternion(Float(), Float(), Float(), Float());
        if (q.LengthSquared() is < 0.9f or > 1.1f) { throw new InvalidDataException("bad rotation quaternion"); }
        return q.Normalized();
    }

    public Variant Scalar() => (Variant.Type)Byte() switch
    {
        Variant.Type.Bool => Bool(),
        Variant.Type.Int => Int(),
        Variant.Type.Float => Double(),
        _ => throw new InvalidDataException("unsupported tuner type"),
    };

    public Godot.Collections.Dictionary Snapshot()
    {
        var values = new Godot.Collections.Dictionary();
        var count = Count(Int(), Protocol.MaxCollection);
        for (var i = 0; i < count; i++)
        {
            var name = String();
            if (values.ContainsKey(name)) { throw new InvalidDataException("duplicate tuner"); }
            values[name] = Scalar();
        }
        Core.Tuning.ValidateSnapshot(values, complete: true);
        return values;
    }

    public static int Count(int count, int max, int min = 0)
    {
        if (count < min || count > max) { throw new InvalidDataException($"bad count {count}"); }
        return count;
    }

    /// <summary>Exactly <paramref name="length"/> bytes, already bounded by the caller.</summary>
    public byte[] Raw(int length)
    {
        if (length < 0 || length > Remaining) { throw new InvalidDataException("truncated payload"); }
        return reader.ReadBytes(length);
    }

    public AppearanceRecord Appearance() => AppearanceCodec.Decode(Bytes(AppearanceCodec.MaxRecordBytes));

    public byte[] Bytes(int maxLength = Protocol.MaxPacketBytes)
    {
        var length = reader.ReadInt32();
        if (length < 0 || length > maxLength || length > Remaining)
        {
            throw new InvalidDataException("bad byte array length");
        }
        return reader.ReadBytes(length);
    }

    public Transform3D Transform()
    {
        var origin = Vec3();
        return new Transform3D(new Basis(Quat()), origin);
    }
}
