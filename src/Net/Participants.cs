using System;
using System.Collections.Generic;
using System.Linq;

namespace Propane.Net;

/// <summary>Monotonic positive identities. Exhaustion fails before mutation; retired ids are never recycled.</summary>
public sealed class IdentitySequence(int first = 1, int ceiling = int.MaxValue)
{
    private long next = first > 0 && first <= ceiling ? first : throw new ArgumentOutOfRangeException(nameof(first));
    public bool Available => next <= ceiling;
    public int Allocate() => next <= ceiling ? (int)next++ : throw new InvalidOperationException("identity space exhausted");
}

public enum ActorKind : byte { Human, Bot }
public enum ControllerKind : byte { Device, Bot }
/// <summary>Versioned, bounded controller selection. Runtime bot configuration is supplied by A8.</summary>
public sealed record ControllerConfig(byte Version, ControllerKind Kind, byte Difficulty)
{
    public static readonly ControllerConfig Human = new(1, ControllerKind.Device, 0);
    public static readonly ControllerConfig Bot = new(1, ControllerKind.Bot, 1);
    public bool ValidFor(ActorKind kind) => kind is ActorKind.Human or ActorKind.Bot && Version == 1 && Difficulty <= 2 &&
        (kind == ActorKind.Human ? Kind == ControllerKind.Device && Difficulty == 0 : Kind == ControllerKind.Bot);
}

public sealed record ActorRecord(int ActorId, int OwnerPeerId, ActorKind Kind, string Name,
    AppearanceRecord Appearance, ControllerConfig ControllerConfig, bool Present, int AllocationSlot, uint LifeId);

/// <summary>Server-owned session roster. Spawn order is assigned at match start, never used for allocation.</summary>
public sealed class ParticipantRoster
{
    private readonly IdentitySequence actors;
    private readonly IdentitySequence slots;
    private readonly uint firstLife;
    private readonly Dictionary<int, ActorRecord> records = new();
    public ParticipantRoster(int firstActor = 1, int actorCeiling = int.MaxValue, int firstSlot = 0, uint firstLife = 1,
        uint firstRevision = 0)
    {
        Revision = firstRevision;
        this.firstLife = firstLife > 0 ? firstLife : throw new ArgumentOutOfRangeException(nameof(firstLife));
        actors = new(firstActor, actorCeiling);
        slots = new(firstSlot + 1, BodySync.MaxAllocationSlots);
    }
    public uint Revision { get; private set; }
    public bool CanAdd => actors.Available && slots.Available && Revision < uint.MaxValue;
    public IReadOnlyList<ActorRecord> Present => records.Values.Where(a => a.Present).OrderBy(a => a.ActorId).ToArray();
    public ActorRecord? Get(int actorId) => records.GetValueOrDefault(actorId);
    public bool Owns(int peer, int actor, uint life) => Get(actor) is { Present: true } a && a.OwnerPeerId == peer && a.LifeId == life;
    public ActorRecord Add(int peer, ActorKind kind, string name, AppearanceRecord appearance, ControllerConfig config)
    {
        if (peer <= 0 || !config.ValidFor(kind)) throw new ArgumentException("invalid actor owner or controller");
        var revision = checked(Revision + 1);
        if (!CanAdd) throw new InvalidOperationException("identity space exhausted");
        var slot = slots.Allocate() - 1;
        var actor = new ActorRecord(actors.Allocate(), peer, kind, Protocol.CleanName(name), appearance, config, true, slot, firstLife);
        records.Add(actor.ActorId, actor);
        Revision = revision;
        return actor;
    }
    public bool Update(int peer, int actorId, string name, AppearanceRecord appearance, ControllerConfig config)
    {
        if (Get(actorId) is not { Present: true } a || a.OwnerPeerId != peer || !config.ValidFor(a.Kind)) return false;
        var revision = checked(Revision + 1);
        records[actorId] = a with { Name = Protocol.CleanName(name), Appearance = appearance, ControllerConfig = config };
        Revision = revision;
        return true;
    }
    public bool Remove(int peer, int actorId)
    {
        if (Get(actorId) is not { Present: true } a || a.OwnerPeerId != peer) return false;
        var revision = checked(Revision + 1);
        records[actorId] = a with { Present = false };
        Revision = revision;
        return true;
    }
    public void RemovePeer(int peer)
    {
        var leaving = Present.Where(a => a.OwnerPeerId == peer).ToArray();
        if (leaving.Length == 0) return;
        var revision = checked(Revision + 1);
        foreach (var actor in leaving) records[actor.ActorId] = actor with { Present = false };
        Revision = revision;
    }
    public ActorRecord Respawn(int actorId)
    {
        if (Get(actorId) is not { Present: true } a) throw new ArgumentException("absent actor", nameof(actorId));
        var life = checked(a.LifeId + 1);
        var revision = checked(Revision + 1);
        records[actorId] = a = a with { LifeId = life };
        Revision = revision;
        return a;
    }
    /// <summary>Prepare every survivor's next round life atomically; no partial change on exhaustion.</summary>
    public void RespawnAll()
    {
        var next = Present.Select(a => a with { LifeId = checked(a.LifeId + 1) }).ToArray();
        if (next.Length == 0) return;
        var revision = checked(Revision + 1);
        foreach (var actor in next) records[actor.ActorId] = actor;
        Revision = revision;
    }
}
