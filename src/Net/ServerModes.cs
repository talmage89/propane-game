using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Modes;

namespace Propane.Net;

/// <summary>
/// Validated, presentation-free mode inputs selected by composition. Immutable and borrowed by runs; factories
/// create run-owned rules/wires. Tuners are names, not a resource or delegates into a client definition.
/// </summary>
public sealed class ServerMode
{
    public byte Id { get; }
    public string Name { get; }
    public ushort SchemaVersion => Schema.Version;
    public ModeWorldSource World { get; }
    public RoundTimer Timer { get; }
    public bool SamplesActors { get; }
    public IReadOnlyList<string> Tuners { get; }
    public ModeSchema Schema { get; }
    public Func<IModeRules> CreateRules { get; }
    public Func<IModeWire> CreateWire { get; }

    public ServerMode(byte id, string name, ModeWorldSource world, RoundTimer timer, bool samplesActors,
        IReadOnlyList<string> tuners, ModeSchema schema, Func<IModeRules> createRules, Func<IModeWire> createWire)
    {
        if (schema == null || schema.ModeId != id) throw new InvalidOperationException($"server mode {id}: missing or mismatched schema");
        Id = id;
        Name = name;
        World = world;
        Timer = timer;
        SamplesActors = samplesActors;
        Tuners = Array.AsReadOnly(tuners.ToArray());
        Schema = schema;
        CreateRules = createRules ?? throw new InvalidOperationException($"server mode {id}: missing rules factory");
        CreateWire = createWire ?? throw new InvalidOperationException($"server mode {id}: missing wire factory");
    }
}

/// <summary>
/// One server's immutable mode selection inputs, captured at construction. Explicit lobby order/eligibility and
/// default/roaming roles; no mutable registrations, presentation factories, resources, clocks or disposal.
/// </summary>
public sealed class ServerModes
{
    public ServerMode Default { get; }
    public ServerMode Roaming { get; }
    public IReadOnlyList<ServerMode> Lobby { get; }

    public ServerModes(ServerMode defaultMode, ServerMode roaming, IEnumerable<ServerMode> lobby)
    {
        Default = defaultMode ?? throw new ArgumentNullException(nameof(defaultMode));
        Roaming = roaming ?? throw new ArgumentNullException(nameof(roaming));
        Lobby = Array.AsReadOnly(lobby.ToArray());
        if (Lobby.Count == 0) throw new InvalidOperationException("server modes: missing lobby choices");
    }

    public ServerMode? ForLobby(byte id) => Lobby.FirstOrDefault(mode => mode.Id == id);
}

/// <summary>Live pile values sampled synchronously from the run's tuning, never a borrowed native resource.</summary>
public readonly record struct ModeWireTuners(int KingPileCount, float KingPileReach, float KingPileClearance,
    int LobbyPileCount, float LobbyPileReach, float LobbyPileClearance, Vector3 PileSpacing, float PileUprightLayerHeight);
