using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Modes;

namespace Propane.Net;

/// <summary>
/// The fixed outer shape of every mode message (<see cref="Msg.ModeEvent"/>, <see cref="Msg.ModeCommand"/> and
/// <see cref="Msg.ModeSnapshot"/>). After the type byte and RunId (the match id) come RoundId, WorldRevision, ModeId,
/// SchemaVersion, ActorId, LifeId, EventId, Kind, Length and Length bytes of payload. A client event's EventId is
/// its actor's action sequence (shared with that actor's other reliable actions); a command's or snapshot's is the
/// run's monotonic command id. ActorId and LifeId name the reporter of an event, or the subject of a command
/// (0 when it has none). Only the registered payload codecs differ between modes.
/// </summary>
public sealed record ModeEnvelope(uint RoundId, uint WorldRevision, byte ModeId, ushort SchemaVersion, int ActorId, uint LifeId,
    uint EventId, ushort Kind, object Payload);

public enum ModeDirection : byte { Event, Command, Snapshot }

/// <summary>
/// Who may report an event: the owner of the actor in its header (checked with that actor's life and sequence), or
/// the match's current world authority, as one of its own actors (checked the same way).
/// </summary>
public enum ModeReporter : byte { ActorOwner, Server, WorldAuthority }

/// <summary>An accepted request an event can depend on, such as a peer's explosion request.</summary>
public readonly record struct ModeDependency(int Peer, uint Request);

/// <summary>
/// One registered payload: its direction, reporter, byte bound and typed codec. A kind may declare a causal
/// dependency (for example a victim report naming the blast that threw it); the server holds such an event until
/// the dependency is accepted, and fails it if the dependency is refused or never arrives.
/// </summary>
public sealed record ModeKind(ushort Id, string Name, ModeDirection Direction, ModeReporter Reporter, int MaxLength,
    Func<NetReader, object> Read, Action<NetWriter, object> Write, Func<object, ModeDependency?>? Dependency = null);

public sealed record ModeSchema(byte ModeId, ushort Version, IReadOnlyList<ModeKind> Kinds)
{
    public ModeKind Kind(ushort id, ModeDirection direction)
    {
        // Kind ids are numbered per direction: event 1 and command 1 are different payloads.
        return Kinds.FirstOrDefault(k => k.Id == id && k.Direction == direction)
            ?? throw new InvalidDataException($"unknown {direction} kind {id} in mode schema {ModeId}/{Version}");
    }
}

/// <summary>Tank Bank's payloads.</summary>
public sealed record TankBankSpill(SpillReason Reason, IReadOnlyList<BodyState> Tanks);
public sealed record TankBankScore(int Score, int Delta);
public sealed record TankBankRefused(IReadOnlyList<uint> Ids);
public sealed record TankBankBanks(IReadOnlyList<(int Actor, int Bank, bool Present)> Banks);

// King of the Cul-de-sac's own payloads; its bank kinds are Tank Bank's, and its Zone snapshot is a KingState.

/// <summary>The server asks the world authority to place zone <paramref name="Zone"/>'s fresh pile near the spot.</summary>
public sealed record KingPileWanted(int Zone, Vector3 Spot, int Count, float Reach, float Clearance);
/// <summary>The world authority's placement: the pile's transforms, or none when no ground within reach was clear.</summary>
public sealed record KingPilePlaced(int Zone, IReadOnlyList<Transform3D> Tanks);
/// <summary>The server registered the pile: its tanks, owned by the world authority, for everyone to build.</summary>
public sealed record KingPileSpawned(int Zone, int Owner, IReadOnlyList<BodyState> Tanks);

/// <summary>Payload codecs and explicit shipped schema constructors. Composition owns schema selection.</summary>
public static class ModeSchemas
{
    /// <summary>Wire bound on one spill: above the largest declared drop-count tuner (20), far below a packet.</summary>
    public const int MaxSpill = 64;
    public const int MaxPayload = ushort.MaxValue;

    public static class TankBankKinds
    {
        public const ushort Spill = 1;          // Event: a victim owner's placements for one ticket.
        public const ushort Score = 1;          // Command: an actor's bank changed.
        public const ushort SpillAccepted = 2;  // Command: a committed spill, to everyone including its owner.
        public const ushort SpillRefused = 3;   // Command: a canceled ticket's ids, to its owner only.
        public const ushort Banks = 1;          // Snapshot: every bank at round start.
    }

    /// <summary>King of the Cul-de-sac's kinds. The bank's are Tank Bank's, under the same ids, so its wire serves both.</summary>
    public static class KingKinds
    {
        public const ushort Spill = TankBankKinds.Spill;                 // Event: as Tank Bank's.
        public const ushort PilePlaced = 2;                              // Event: the world authority's pile placement.
        public const ushort Score = TankBankKinds.Score;                 // Command: as Tank Bank's.
        public const ushort SpillAccepted = TankBankKinds.SpillAccepted; // Command: as Tank Bank's.
        public const ushort SpillRefused = TankBankKinds.SpillRefused;   // Command: as Tank Bank's.
        public const ushort PileWanted = 4;                              // Command: to the world authority only.
        public const ushort PileSpawned = 5;                             // Command: a registered fresh pile, to everyone.
        public const ushort Banks = TankBankKinds.Banks;                 // Snapshot: as Tank Bank's.
        public const ushort Zone = 2;                                    // Snapshot: the zone, whenever it changes.
    }

    /// <summary>
    /// The lobby world's kinds: only King's fresh-pile exchange, under King's ids and payloads, so a peer places and
    /// builds a lobby refill exactly as it does a King pile. Its Zone field is the runner's request id.
    /// </summary>
    public static class LobbyKinds
    {
        public const ushort PilePlaced = KingKinds.PilePlaced;   // Event: the world authority's placement of a refill.
        public const ushort PileWanted = KingKinds.PileWanted;   // Command: to the world authority only.
        public const ushort PileSpawned = KingKinds.PileSpawned; // Command: a registered pile, to everyone.
    }

    private const int BodyBytes = 64;
    private const int TransformBytes = 28;
    private const int SpotBytes = 12;
    /// <summary>Wire bound on a round's zone index and on the zone's radius (meters).</summary>
    public const int MaxZone = ushort.MaxValue;
    public const float MaxZoneRadius = 1000;

    private static ModeSchema Schema(byte mode, ushort version, ModeKind[] kinds) => new(mode, version, Array.AsReadOnly(kinds));

    public static ModeSchema Sandbox() => Schema(ModeIds.Sandbox, 1, Array.Empty<ModeKind>());

    public static ModeSchema TankBank() => Schema(ModeIds.TankBank, ModeIds.TankBankSchema, new[]
        {
            new ModeKind(TankBankKinds.Spill, "Spill", ModeDirection.Event, ModeReporter.ActorOwner, 2 + MaxSpill * BodyBytes, ReadSpill, WriteSpill),
            new ModeKind(TankBankKinds.Score, "Score", ModeDirection.Command, ModeReporter.Server, 8,
                r => new TankBankScore(Nonnegative(r.Int()), r.Int()), (w, p) => { var s = (TankBankScore)p; w.Int(s.Score).Int(s.Delta); }),
            new ModeKind(TankBankKinds.SpillAccepted, "SpillAccepted", ModeDirection.Command, ModeReporter.Server, 2 + MaxSpill * BodyBytes, ReadSpill, WriteSpill),
            new ModeKind(TankBankKinds.SpillRefused, "SpillRefused", ModeDirection.Command, ModeReporter.Server, 1 + MaxSpill * 4, ReadRefused, WriteRefused),
            new ModeKind(TankBankKinds.Banks, "Banks", ModeDirection.Snapshot, ModeReporter.Server, 1 + Protocol.MaxWireActors * 9, ReadBanks, WriteBanks),
        });

    public static ModeSchema King() => Schema(ModeIds.King, 1, new[]
        {
            new ModeKind(KingKinds.Spill, "Spill", ModeDirection.Event, ModeReporter.ActorOwner, 2 + MaxSpill * BodyBytes, ReadSpill, WriteSpill),
            new ModeKind(KingKinds.PilePlaced, "PilePlaced", ModeDirection.Event, ModeReporter.WorldAuthority, 3 + MaxSpill * TransformBytes,
                ReadPilePlaced, WritePilePlaced),
            new ModeKind(KingKinds.Score, "Score", ModeDirection.Command, ModeReporter.Server, 8,
                r => new TankBankScore(Nonnegative(r.Int()), r.Int()), (w, p) => { var s = (TankBankScore)p; w.Int(s.Score).Int(s.Delta); }),
            new ModeKind(KingKinds.SpillAccepted, "SpillAccepted", ModeDirection.Command, ModeReporter.Server, 2 + MaxSpill * BodyBytes, ReadSpill, WriteSpill),
            new ModeKind(KingKinds.SpillRefused, "SpillRefused", ModeDirection.Command, ModeReporter.Server, 1 + MaxSpill * 4, ReadRefused, WriteRefused),
            new ModeKind(KingKinds.PileWanted, "PileWanted", ModeDirection.Command, ModeReporter.Server, 2 + SpotBytes + 1 + 8, ReadPileWanted, WritePileWanted),
            new ModeKind(KingKinds.PileSpawned, "PileSpawned", ModeDirection.Command, ModeReporter.Server, 7 + MaxSpill * BodyBytes,
                ReadPileSpawned, WritePileSpawned),
            new ModeKind(KingKinds.Banks, "Banks", ModeDirection.Snapshot, ModeReporter.Server, 1 + Protocol.MaxWireActors * 9, ReadBanks, WriteBanks),
            new ModeKind(KingKinds.Zone, "Zone", ModeDirection.Snapshot, ModeReporter.Server,
                2 + 2 * SpotBytes + 4 + 4 + 1 + Protocol.MaxWireActors * 4 + 16 + 1 + Protocol.MaxWireActors * 8 + 4, ReadZone, WriteZone),
        });

    public static ModeSchema Lobby() => Schema(ModeIds.Lobby, 1, new[]
        {
            new ModeKind(LobbyKinds.PilePlaced, "PilePlaced", ModeDirection.Event, ModeReporter.WorldAuthority, 3 + MaxSpill * TransformBytes,
                ReadPilePlaced, WritePilePlaced),
            new ModeKind(LobbyKinds.PileWanted, "PileWanted", ModeDirection.Command, ModeReporter.Server, 2 + SpotBytes + 1 + 8, ReadPileWanted, WritePileWanted),
            new ModeKind(LobbyKinds.PileSpawned, "PileSpawned", ModeDirection.Command, ModeReporter.Server, 7 + MaxSpill * BodyBytes,
                ReadPileSpawned, WritePileSpawned),
        });

    internal static ModeDirection Direction(Msg type) => type switch
    {
        Msg.ModeEvent => ModeDirection.Event,
        Msg.ModeCommand => ModeDirection.Command,
        Msg.ModeSnapshot => ModeDirection.Snapshot,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    internal static ModeEnvelope Read(NetReader r, Msg type, Func<byte, ushort, ModeSchema> schema)
    {
        var round = Id(r.UInt()); var revision = Id(r.UInt()); var mode = r.Byte(); var version = r.UShort();
        var actor = r.Int(); var life = r.UInt(); var eventId = Id(r.UInt()); var kindId = r.UShort(); var length = r.UShort();
        var direction = Direction(type);
        if (direction == ModeDirection.Event ? actor <= 0 || life == 0 : actor < 0 || (actor == 0) != (life == 0))
            throw new InvalidDataException("bad mode actor header");
        var kind = schema(mode, version).Kind(kindId, direction);
        if (length > kind.MaxLength || length > r.Remaining) throw new InvalidDataException($"oversized or truncated {kind.Name} payload ({length} bytes)");
        var payload = new NetReader(r.Raw(length), 0);
        var value = kind.Read(payload);
        payload.End();
        return new ModeEnvelope(round, revision, mode, version, actor, life, eventId, kindId, value);
    }

    internal static void Write(NetWriter w, Msg type, ModeEnvelope envelope, ModeSchema schema)
    {
        var kind = schema.Kind(envelope.Kind, Direction(type));
        var body = NetWriter.Payload();
        kind.Write(body, envelope.Payload);
        var payload = body.ToArray();
        if (payload.Length > kind.MaxLength) throw new InvalidDataException($"oversized {kind.Name} payload ({payload.Length} bytes)");
        w.UInt(envelope.RoundId).UInt(envelope.WorldRevision).Byte(envelope.ModeId).UShort(envelope.SchemaVersion)
            .Int(envelope.ActorId).UInt(envelope.LifeId).UInt(envelope.EventId).UShort(envelope.Kind).UShort((ushort)payload.Length).Raw(payload);
    }

    private static TankBankSpill ReadSpill(NetReader r)
    {
        var reason = (SpillReason)Range(r.Byte(), 0, (int)SpillReason.Thrown);
        var count = Range(r.Byte(), 1, MaxSpill);
        var tanks = new BodyState[count];
        for (var i = 0; i < count; i++) tanks[i] = MessageCodec.ReadBody(r);
        if (tanks.Select(t => t.Id).Distinct().Count() != count) throw new InvalidDataException("duplicate ids");
        return new TankBankSpill(reason, tanks);
    }

    private static void WriteSpill(NetWriter w, object payload)
    {
        var spill = (TankBankSpill)payload;
        w.Byte((byte)spill.Reason).Byte((byte)Range(spill.Tanks.Count, 1, MaxSpill));
        foreach (var tank in spill.Tanks) MessageCodec.WriteBody(w, tank);
    }

    private static TankBankRefused ReadRefused(NetReader r)
    {
        var ids = new uint[Range(r.Byte(), 1, MaxSpill)];
        for (var i = 0; i < ids.Length; i++) ids[i] = Id(r.UInt());
        if (ids.Distinct().Count() != ids.Length) throw new InvalidDataException("duplicate ids");
        return new TankBankRefused(ids);
    }

    private static void WriteRefused(NetWriter w, object payload)
    {
        var refused = (TankBankRefused)payload;
        w.Byte((byte)Range(refused.Ids.Count, 1, MaxSpill));
        foreach (var id in refused.Ids) w.UInt(id);
    }

    private static TankBankBanks ReadBanks(NetReader r)
    {
        var banks = new (int, int, bool)[Range(r.Byte(), 0, Protocol.MaxWireActors)];
        for (var i = 0; i < banks.Length; i++) banks[i] = (Range(r.Int(), 1, int.MaxValue), Nonnegative(r.Int()), r.Bool());
        if (banks.Select(b => b.Item1).Distinct().Count() != banks.Length) throw new InvalidDataException("duplicate ids");
        return new TankBankBanks(banks);
    }

    private static void WriteBanks(NetWriter w, object payload)
    {
        var banks = (TankBankBanks)payload;
        w.Byte((byte)Range(banks.Banks.Count, 0, Protocol.MaxWireActors));
        foreach (var (actor, bank, present) in banks.Banks) w.Int(actor).Int(bank).Bool(present);
    }

    private static KingPileWanted ReadPileWanted(NetReader r) => new(r.UShort(), MessageCodec.ReadVector(r), Range(r.Byte(), 1, MaxSpill),
        Range(r.Float(), 0, MaxZoneRadius), Range(r.Float(), 0, MaxZoneRadius));

    private static void WritePileWanted(NetWriter w, object payload)
    {
        var wanted = (KingPileWanted)payload;
        w.UShort((ushort)Range(wanted.Zone, 0, MaxZone)).Vec3(wanted.Spot).Byte((byte)Range(wanted.Count, 1, MaxSpill)).Float(wanted.Reach).Float(wanted.Clearance);
    }

    private static KingPilePlaced ReadPilePlaced(NetReader r)
    {
        var zone = (int)r.UShort();
        var tanks = new Transform3D[Range(r.Byte(), 0, MaxSpill)];
        for (var i = 0; i < tanks.Length; i++) tanks[i] = MessageCodec.ReadTransform(r);
        return new KingPilePlaced(zone, tanks);
    }

    private static void WritePilePlaced(NetWriter w, object payload)
    {
        var placed = (KingPilePlaced)payload;
        w.UShort((ushort)Range(placed.Zone, 0, MaxZone)).Byte((byte)Range(placed.Tanks.Count, 0, MaxSpill));
        foreach (var tank in placed.Tanks) w.Transform(tank);
    }

    private static KingPileSpawned ReadPileSpawned(NetReader r)
    {
        var zone = (int)r.UShort();
        var owner = Range(r.Int(), 1, int.MaxValue);
        var tanks = new BodyState[Range(r.Byte(), 1, MaxSpill)];
        for (var i = 0; i < tanks.Length; i++) tanks[i] = MessageCodec.ReadBody(r);
        if (tanks.Select(t => t.Id).Distinct().Count() != tanks.Length) throw new InvalidDataException("duplicate ids");
        if (tanks.Any(t => t.Id < BodySync.MapTankBase || t.Id >= BodySync.DropTankBase)) throw new InvalidDataException("pile tank outside the world's ids");
        return new KingPileSpawned(zone, owner, tanks);
    }

    private static void WritePileSpawned(NetWriter w, object payload)
    {
        var spawned = (KingPileSpawned)payload;
        w.UShort((ushort)Range(spawned.Zone, 0, MaxZone)).Int(spawned.Owner).Byte((byte)Range(spawned.Tanks.Count, 1, MaxSpill));
        foreach (var tank in spawned.Tanks) MessageCodec.WriteBody(w, tank);
    }

    /// <summary>
    /// King's zone: the state every peer reads (<see cref="KingState"/>), in round time. Decoding checks that it is one
    /// a server could hold: one occupant is the holder, several contest it, and none means nobody holds.
    /// </summary>
    private static KingState ReadZone(NetReader r)
    {
        var zone = (int)r.UShort();
        var spot = Spot(MessageCodec.ReadVector(r));
        var next = Spot(MessageCodec.ReadVector(r));
        var radius = Range(r.Float(), float.Epsilon, MaxZoneRadius);
        var holder = Nonnegative(r.Int());
        var occupants = new int[Range(r.Byte(), 0, Protocol.MaxWireActors)];
        for (var i = 0; i < occupants.Length; i++) occupants[i] = Range(r.Int(), 1, int.MaxValue);
        if (occupants.Distinct().Count() != occupants.Length) throw new InvalidDataException("duplicate occupants");
        if (holder != (occupants.Length == 1 ? occupants[0] : 0)) throw new InvalidDataException("zone holder disagrees with its occupants");
        var nextMint = Range(r.Float(), 0, float.MaxValue);
        var mintSeconds = Range(r.Float(), float.Epsilon, float.MaxValue);
        var moveAt = Range(r.Float(), 0, float.MaxValue);
        var warning = Range(r.Float(), 0, float.MaxValue);
        var minted = new KingMinted[Range(r.Byte(), 0, Protocol.MaxWireActors)];
        for (var i = 0; i < minted.Length; i++) minted[i] = new KingMinted(Range(r.Int(), 1, int.MaxValue), Nonnegative(r.Int()));
        if (minted.Select(m => m.ActorId).Distinct().Count() != minted.Length) throw new InvalidDataException("duplicate minted actors");
        var elapsed = Range(r.Float(), 0, float.MaxValue);
        return new KingState(zone, spot, next, radius, holder, occupants, nextMint, mintSeconds, moveAt, warning, minted, elapsed);
    }

    private static void WriteZone(NetWriter w, object payload)
    {
        var state = (KingState)payload;
        w.UShort((ushort)Range(state.Zone, 0, MaxZone)).Vec3(Point(state.Spot)).Vec3(Point(state.Next)).Float((float)state.Radius)
            .Int(state.Holder).Byte((byte)Range(state.Occupants.Count, 0, Protocol.MaxWireActors));
        foreach (var occupant in state.Occupants) w.Int(occupant);
        w.Float((float)state.NextMint).Float((float)state.MintSeconds).Float((float)state.MoveAt).Float((float)state.WarningSeconds)
            .Byte((byte)Range(state.Minted.Count, 0, Protocol.MaxWireActors));
        foreach (var entry in state.Minted) w.Int(entry.ActorId).Int(entry.Count);
        w.Float((float)state.Elapsed);
    }

    private static ModeSpot Spot(Vector3 at) => new(at.X, at.Y, at.Z);
    private static Vector3 Point(ModeSpot spot) => new((float)spot.X, (float)spot.Y, (float)spot.Z);
    private static float Range(float n, float min, float max) => n >= min && n <= max ? n : throw new InvalidDataException($"number out of range: {n}");
    private static int Range(int n, int min, int max) => n >= min && n <= max ? n : throw new InvalidDataException($"integer out of range: {n}");
    private static int Nonnegative(int n) => Range(n, 0, int.MaxValue);
    private static uint Id(uint id) => id != 0 ? id : throw new InvalidDataException("zero mode id");
}

/// <summary>
/// Server-side causal holding for events whose kind declares a dependency: bounded in count and age, released in
/// arrival order when the dependency is accepted, failed when it is refused, expires, or the round changes.
/// </summary>
public sealed class ModeEventGate(int capacity = 64, double maxAge = 2.0)
{
    public sealed record Held(int Peer, NetMessage Message, ModeDependency Dependency, double Since);
    private readonly List<Held> held = new();
    public int Count => held.Count;

    /// <summary>Holds an event; false (fail it now) when the gate is full.</summary>
    public bool Hold(int peer, NetMessage message, ModeDependency dependency, double now)
    {
        if (held.Count >= capacity) return false;
        held.Add(new Held(peer, message, dependency, now));
        return true;
    }

    public IReadOnlyList<Held> Release(ModeDependency dependency) => Take(h => h.Dependency == dependency);
    public IReadOnlyList<Held> Fail(ModeDependency dependency) => Take(h => h.Dependency == dependency);
    public IReadOnlyList<Held> Expire(double now) => Take(h => now - h.Since >= maxAge);
    public IReadOnlyList<Held> Clear() => Take(_ => true);

    private Held[] Take(Func<Held, bool> which)
    {
        var taken = held.Where(which).ToArray();
        held.RemoveAll(h => which(h));
        return taken;
    }
}

/// <summary>A client's command order: newer ids only. Duplicates and overtaken commands are dropped; gaps are normal.</summary>
public sealed class ModeCommandOrder
{
    public uint Last { get; private set; }
    public bool Accept(uint id)
    {
        if (id <= Last) return false;
        Last = id;
        return true;
    }
}
