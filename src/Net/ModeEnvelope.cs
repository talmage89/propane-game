using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

/// <summary>Who may report an event: the owner of the actor in its header (checked with that actor's life and sequence).</summary>
public enum ModeReporter : byte { ActorOwner, Server }

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

/// <summary>Every schema a build understands. Unknown schemas fail explicitly instead of being skipped.</summary>
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

    private const int BodyBytes = 64;

    private static readonly List<ModeSchema> registered = new()
    {
        new ModeSchema(ModeRegistry.SandboxId, 1, Array.Empty<ModeKind>()),
        new ModeSchema(ModeRegistry.TankBankId, 1, new[]
        {
            new ModeKind(TankBankKinds.Spill, "Spill", ModeDirection.Event, ModeReporter.ActorOwner, 2 + MaxSpill * BodyBytes, ReadSpill, WriteSpill),
            new ModeKind(TankBankKinds.Score, "Score", ModeDirection.Command, ModeReporter.Server, 8,
                r => new TankBankScore(Nonnegative(r.Int()), r.Int()), (w, p) => { var s = (TankBankScore)p; w.Int(s.Score).Int(s.Delta); }),
            new ModeKind(TankBankKinds.SpillAccepted, "SpillAccepted", ModeDirection.Command, ModeReporter.Server, 2 + MaxSpill * BodyBytes, ReadSpill, WriteSpill),
            new ModeKind(TankBankKinds.SpillRefused, "SpillRefused", ModeDirection.Command, ModeReporter.Server, 1 + MaxSpill * 4, ReadRefused, WriteRefused),
            new ModeKind(TankBankKinds.Banks, "Banks", ModeDirection.Snapshot, ModeReporter.Server, 1 + Protocol.MaxWireActors * 9, ReadBanks, WriteBanks),
        }),
    };

    public static ModeSchema Find(byte modeId, ushort version) =>
        registered.FirstOrDefault(s => s.ModeId == modeId && s.Version == version)
        ?? throw new InvalidDataException($"unknown mode schema {modeId}/{version}");

    /// <summary>Test seam: assertion suites register synthetic schemas (dependencies, oversize bounds).</summary>
    internal static void Register(ModeSchema schema)
    {
        registered.RemoveAll(s => s.ModeId == schema.ModeId && s.Version == schema.Version);
        registered.Add(schema);
    }

    internal static ModeDirection Direction(Msg type) => type switch
    {
        Msg.ModeEvent => ModeDirection.Event,
        Msg.ModeCommand => ModeDirection.Command,
        Msg.ModeSnapshot => ModeDirection.Snapshot,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    internal static ModeEnvelope Read(NetReader r, Msg type)
    {
        var round = Id(r.UInt()); var revision = Id(r.UInt()); var mode = r.Byte(); var version = r.UShort();
        var actor = r.Int(); var life = r.UInt(); var eventId = Id(r.UInt()); var kindId = r.UShort(); var length = r.UShort();
        var direction = Direction(type);
        if (direction == ModeDirection.Event ? actor <= 0 || life == 0 : actor < 0 || (actor == 0) != (life == 0))
            throw new InvalidDataException("bad mode actor header");
        var kind = Find(mode, version).Kind(kindId, direction);
        if (length > kind.MaxLength || length > r.Remaining) throw new InvalidDataException($"oversized or truncated {kind.Name} payload ({length} bytes)");
        var payload = new NetReader(r.Raw(length), 0);
        var value = kind.Read(payload);
        payload.End();
        return new ModeEnvelope(round, revision, mode, version, actor, life, eventId, kindId, value);
    }

    internal static void Write(NetWriter w, Msg type, ModeEnvelope envelope)
    {
        var kind = Find(envelope.ModeId, envelope.SchemaVersion).Kind(envelope.Kind, Direction(type));
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
