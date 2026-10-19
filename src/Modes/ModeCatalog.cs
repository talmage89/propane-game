using System;
using System.Collections.Generic;
using System.Linq;
using Propane.Net;

namespace Propane.Modes;

/// <summary>
/// Existing network integration, assembled in composition. Definition includes rules/HUD/bot factories and supply;
/// client adapters own session policy. No declaration here grants a prototype online eligibility.
/// Nullable inputs allow completeness validation to diagnose malformed test bundles before publication.
/// </summary>
public sealed record ModeBundle(ModeDefinition? Definition, ModeSchema? Schema, Func<IModeWire>? CreateServerWire,
    Func<IClientModeReplica, IClientLobbyReplica, IModeClient>? CreateClient = null)
{
    internal void Validate()
    {
        var label = Definition is { } mode ? $"mode {mode.Id}/{mode.SchemaVersion} ({mode.Name})" : "mode bundle";
        void Require(bool present, string member)
        {
            if (!present) throw new InvalidOperationException($"{label}: missing {member}");
        }
        Require(Definition != null, "definition");
        Require(Definition!.CreateRules != null, "rules factory");
        Require(Definition.Supply != null, "supply");
        Require(Definition.Supply!.Suburb != null, "suburb factory");
        Require(Definition.CreateHud != null, "HUD factory");
        // No bot factory is legitimate (Sandbox); its silent client adapter is still explicit.
        Require(Definition.Tuners != null, "tuners");
        Require(Schema != null, "schema");
        Require(CreateServerWire != null, "server wire factory");
        Require(CreateClient != null, "client adapter factory");
        if (Schema!.ModeId != Definition.Id || Schema.Version != Definition.SchemaVersion)
            throw new InvalidOperationException($"{label}: schema identity {Schema.ModeId}/{Schema.Version} does not match definition");
        Require(Schema.Kinds != null, "schema kinds");
        foreach (var kind in Schema.Kinds!)
        {
            Require(kind != null, "schema kind");
            Require(kind!.Read != null, $"{kind.Name} reader");
            Require(kind.Write != null, $"{kind.Name} writer");
        }
    }
}

/// <summary>
/// Immutable ordered network catalog. Owns copied collection containers, borrows immutable definitions and codecs;
/// owns no rule/wire instances, resources, timers or disposal. Instances live for the process or a test scope.
/// </summary>
public sealed class ModeCatalog
{
    private readonly ModeBundle[] entries;
    public IReadOnlyList<ModeBundle> Bundles { get; }
    public IReadOnlyList<ModeDefinition> Definitions { get; }
    public PacketCodec Codec { get; }

    public ModeCatalog(IEnumerable<ModeBundle> bundles)
    {
        entries = bundles.ToArray();
        foreach (var bundle in entries)
        {
            if (bundle == null) throw new InvalidOperationException("mode catalog: missing bundle");
            bundle.Validate();
        }
        if (entries.GroupBy(b => (b.Definition!.Id, b.Definition.SchemaVersion)).Any(group => group.Count() != 1))
            throw new InvalidOperationException("mode catalog: duplicate mode identity");
        // Capture only schemas and fixed eligibility, never this catalog's HUD/bot/definition factories.
        var schemas = entries.ToDictionary(b => (b.Schema!.ModeId, b.Schema.Version), b => b.Schema!);
        Codec = new PacketCodec((id, version) => schemas.TryGetValue((id, version), out var schema) ? schema
            : throw new System.IO.InvalidDataException($"unknown mode schema {id}/{version}"),
            id => id is Core.ModeIds.TankBank or Core.ModeIds.King, Core.Tuning.ValidateSnapshot);
        Bundles = Array.AsReadOnly(entries);
        Definitions = Array.AsReadOnly(entries.Select(bundle => bundle.Definition!).ToArray());
    }

    public ModeBundle? Find(byte id, ushort version)
    {
        foreach (var bundle in entries)
            if (bundle.Definition!.Id == id && bundle.Definition.SchemaVersion == version) return bundle;
        return null;
    }

    internal ModeCatalog With(ModeBundle bundle)
    {
        bundle.Validate();
        var entries = Bundles.ToList();
        var index = entries.FindIndex(entry => entry.Definition!.Id == bundle.Definition!.Id &&
            entry.Definition.SchemaVersion == bundle.Definition.SchemaVersion);
        if (index < 0) entries.Add(bundle);
        else entries[index] = bundle;
        return new ModeCatalog(entries);
    }
}
