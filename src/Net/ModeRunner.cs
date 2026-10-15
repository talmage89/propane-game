using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Propane.Modes;

namespace Propane.Net;

/// <summary>
/// What a server match offers a mode's wire adapter: delivery, atomic entity registration, the match's world
/// authority and tuning, and logs.
/// </summary>
public interface IModeHost
{
    void Broadcast(Msg type, ModeEnvelope envelope);
    void Send(int peer, Msg type, ModeEnvelope envelope);
    /// <summary>Registers a complete batch of actor-owned entities in the ledger, or throws and registers none.</summary>
    void RegisterEntities(int actorId, int peer, IReadOnlyList<BodyState> bodies);
    /// <summary>
    /// Registers new world tanks at these poses, resting and owned by the world authority, with ids after the plan's
    /// (they detonate as map tanks), and returns them as registered.
    /// </summary>
    IReadOnlyList<BodyState> SpawnWorldTanks(IReadOnlyList<Godot.Transform3D> poses);
    uint Life(int actorId);
    /// <summary>The peer that owns everything nobody has touched: the creator, or its successor.</summary>
    int WorldAuthority { get; }
    /// <summary>The match's tuning, for what a runner decides beside the rules (where a fresh pile may stand).</summary>
    Core.Tuning Tuning { get; }
    /// <summary>Logs a line about this match, prefixed with its id.</summary>
    void Log(string text);
    /// <summary>A body as the ledger last knew it (its owner's latest report), or null when it never existed.</summary>
    BodyAuthority? Body(uint id) => null;
    /// <summary>Tanks the ledger knows that have not exploded.</summary>
    int LiveTanks => 0;
}

/// <summary>
/// A mode's server-side wire adapter: turns its decoded events into accepted rule inputs, and the rules' commands
/// into envelopes. Registered per mode id in <see cref="ModeWires"/>.
/// </summary>
public interface IModeWire
{
    void Event(ModeRunner runner, IModeHost host, int peer, ModeEnvelope envelope, double now);
    void Execute(ModeRunner runner, IModeHost host, IReadOnlyList<ModeCommand> commands, double now);
    /// <summary>A held event whose dependency was refused, never arrived, or outlived its round.</summary>
    void Failed(ModeRunner runner, IModeHost host, int peer, ModeEnvelope envelope, double now);
}

public static class ModeWires
{
    private sealed class Silent : IModeWire
    {
        public void Event(ModeRunner runner, IModeHost host, int peer, ModeEnvelope envelope, double now) { }
        public void Execute(ModeRunner runner, IModeHost host, IReadOnlyList<ModeCommand> commands, double now) { }
        public void Failed(ModeRunner runner, IModeHost host, int peer, ModeEnvelope envelope, double now) { }
    }

    private static readonly Dictionary<byte, Func<IModeWire>> registered = new()
    {
        [ModeRegistry.TankBankId] = () => new TankBankWire(),
        [ModeRegistry.KingId] = () => new KingWire(),
        [ModeRegistry.LobbyWorldId] = () => new LobbyWire(),
    };

    public static IModeWire For(ModeDefinition mode) => registered.TryGetValue(mode.Id, out var create) ? create() : new Silent();

    /// <summary>Test seam: assertion suites register wires for synthetic modes.</summary>
    internal static void Register(byte modeId, Func<IModeWire> create) => registered[modeId] = create;
}

/// <summary>
/// One run's mode on the server: its definition, rules instance, phase machine, monotonic command ids and causal
/// gate. The lobby server owns transport and the ledger; everything mode-specific passes through here.
/// </summary>
public sealed class ModeRunner
{
    private uint nextCommand;
    public ModeDefinition Definition { get; }
    public IModeRules Rules { get; }
    public IModeWire Wire { get; }
    public ModePhaseMachine Phases { get; } = new();
    public ModeEventGate Gate { get; } = new();

    public ModeRunner(ModeDefinition definition, ModeSetup setup)
    {
        Definition = definition;
        Rules = definition.CreateRules();
        Rules.Initialize(setup);
        Wire = ModeWires.For(definition);
    }

    public ModeClock Clock(double now) => Phases.Clock(now);
    public bool Permits(ModeGate gate, double now) => Rules.Permits(gate, Clock(now));
    public IReadOnlyList<ModeCommand> Handle(AcceptedEvent accepted, double now) => Rules.Handle(accepted, Clock(now));

    /// <summary>An event belongs to this run's current round and world, in the schema this run speaks.</summary>
    public bool Current(ModeEnvelope envelope) => envelope.ModeId == Definition.Id && envelope.SchemaVersion == Definition.SchemaVersion &&
        envelope.RoundId == Phases.Round.RoundId && envelope.WorldRevision == Phases.Round.WorldRevision;

    public ModeEnvelope Envelope(ushort kind, int actorId, uint lifeId, object payload) => new(Phases.Round.RoundId,
        Phases.Round.WorldRevision, Definition.Id, Definition.SchemaVersion, actorId, lifeId, checked(++nextCommand), kind, payload);

    /// <summary>Feeds an accepted input to the rules and carries out what they decide, through the mode's wire.</summary>
    public IReadOnlyList<ModeCommand> Apply(IModeHost host, AcceptedEvent accepted, double now)
    {
        var commands = Handle(accepted, now);
        Wire.Execute(this, host, commands, now);
        return commands;
    }
}

/// <summary>
/// Tank Bank on the wire. A Spill event is a ticket (victim actor, life and event id) with its placements: the rules
/// reserve available bank, the ledger registers the complete batch, then the debit commits and everyone, the owner
/// included, receives the accepted spill. Any cancellation tells only the owner which predicted ids to remove.
/// </summary>
public sealed class TankBankWire : IModeWire
{
    private readonly Dictionary<SpillTicket, (int Peer, TankBankSpill Spill)> placements = new();

    public void Event(ModeRunner runner, IModeHost host, int peer, ModeEnvelope envelope, double now)
    {
        if (envelope.Payload is not TankBankSpill spill) return;
        var ticket = new SpillTicket(envelope.ActorId, envelope.LifeId, envelope.EventId);
        if (!runner.Current(envelope))
        {
            Refuse(runner, host, peer, ticket, spill, "spill from another round or world");
            return;
        }
        placements[ticket] = (peer, spill);
        Execute(runner, host, runner.Handle(new SpillRequested(ticket, spill.Reason, spill.Tanks.Count), now), now);
    }

    public void Execute(ModeRunner runner, IModeHost host, IReadOnlyList<ModeCommand> commands, double now)
    {
        foreach (var command in commands)
        {
            switch (command)
            {
                case ScoreChanged score:
                    host.Broadcast(Msg.ModeCommand, runner.Envelope(ModeSchemas.TankBankKinds.Score, score.ActorId, host.Life(score.ActorId),
                        new TankBankScore(score.Score, score.Delta)));
                    break;
                case SpillReserved reserved when placements.TryGetValue(reserved.Ticket, out var pending):
                    AcceptedEvent outcome;
                    try
                    {
                        host.RegisterEntities(reserved.Ticket.ActorId, pending.Peer, pending.Spill.Tanks);
                        outcome = new SpillRegistered(reserved.Ticket);
                    }
                    catch (InvalidDataException e)
                    {
                        host.Log($"refused Drop from {pending.Peer}: {e.Message}");
                        outcome = new SpillFailed(reserved.Ticket);
                    }
                    Execute(runner, host, runner.Handle(outcome, now), now);
                    break;
                case SpillCommitted committed when placements.Remove(committed.Ticket, out var accepted):
                    host.Broadcast(Msg.ModeCommand, runner.Envelope(ModeSchemas.TankBankKinds.SpillAccepted, committed.Ticket.ActorId,
                        committed.Ticket.LifeId, accepted.Spill));
                    break;
                case SpillCanceled canceled when placements.Remove(canceled.Ticket, out var refused):
                    Refuse(runner, host, refused.Peer, canceled.Ticket, refused.Spill, canceled.Reason switch
                    {
                        SpillCancel.Closed or SpillCancel.Exceeds => "drop outside play or exceeds bank/tuner",
                        SpillCancel.Rejected => "ledger refused the batch",
                        var other => $"ticket {other}",
                    }, log: canceled.Reason != SpillCancel.Rejected);
                    break;
                case PublishSnapshot:
                    var banks = runner.Rules.Snapshot().Standings.OrderBy(s => s.ActorId).Select(s => (s.ActorId, s.Score, s.Present)).ToArray();
                    host.Broadcast(Msg.ModeSnapshot, runner.Envelope(ModeSchemas.TankBankKinds.Banks, 0, 0, new TankBankBanks(banks)));
                    break;
            }
        }
    }

    public void Failed(ModeRunner runner, IModeHost host, int peer, ModeEnvelope envelope, double now)
    {
        if (envelope.Payload is TankBankSpill spill)
            Refuse(runner, host, peer, new SpillTicket(envelope.ActorId, envelope.LifeId, envelope.EventId), spill, "dependency failed");
    }

    private static void Refuse(ModeRunner runner, IModeHost host, int peer, SpillTicket ticket, TankBankSpill spill, string why, bool log = true)
    {
        // A fully decoded prediction can legitimately become invalid in flight (clock/tuning changes).
        // Tell only its owner which ids to remove; the ledger and bank remain untouched.
        host.Send(peer, Msg.ModeCommand, runner.Envelope(ModeSchemas.TankBankKinds.SpillRefused, ticket.ActorId, ticket.LifeId,
            new TankBankRefused(spill.Tanks.Select(t => t.Id).ToArray())));
        if (log) host.Log($"refused Drop from {peer}: {why}");
    }
}

/// <summary>
/// King of the Cul-de-sac on the wire. Its bank is Tank Bank's wire, unchanged: King's bank kinds share Tank Bank's ids.
/// It restates the zone (<see cref="KingState"/>) to everyone whenever more than its clocks changed. A zone move asks
/// the world authority, the one peer with physics that owns untouched tanks, to place a fresh pile on clear ground
/// beside the new spot; its answer is checked, registered as world tanks it owns, and sent to everyone to build. A new
/// world authority is asked again; a pile for an earlier zone, or after play closes, is never built.
/// </summary>
public sealed class KingWire : IModeWire
{
    private readonly TankBankWire bank = new();
    private (KingPileWanted Pile, int Peer)? wanted;
    private KingState? told;

    /// <summary>How far past its reach and how far above or below its spot a placed tank may stand, for the pile's own size.</summary>
    private const float PileSlack = 2.5f;

    public void Event(ModeRunner runner, IModeHost host, int peer, ModeEnvelope envelope, double now)
    {
        if (envelope.Payload is KingPilePlaced placed) Place(runner, host, peer, envelope, placed, now);
        else bank.Event(runner, host, peer, envelope, now);
    }

    public void Execute(ModeRunner runner, IModeHost host, IReadOnlyList<ModeCommand> commands, double now)
    {
        bank.Execute(runner, host, commands, now);
        foreach (var command in commands)
        {
            if (command is not ZoneMoved { Zone: > 0 } moved) continue;
            var tuning = host.Tuning;
            if (wanted is { } superseded) host.Log($"king pile for zone {superseded.Pile.Zone} never placed");
            var spot = new Godot.Vector3((float)moved.Spot.X, (float)moved.Spot.Y, (float)moved.Spot.Z);
            Ask(runner, host, new KingPileWanted(moved.Zone, spot, tuning.KingPileCount, tuning.KingPileReach, tuning.KingPileClearance));
        }
        if (wanted is { } pending)
        {
            if (!runner.Permits(ModeGate.World, now)) wanted = null; // Play closed: no pile.
            else if (pending.Peer != host.WorldAuthority) Ask(runner, host, pending.Pile); // The authority moved on.
        }
        Tell(runner, host, now);
    }

    public void Failed(ModeRunner runner, IModeHost host, int peer, ModeEnvelope envelope, double now) => bank.Failed(runner, host, peer, envelope, now);

    private void Ask(ModeRunner runner, IModeHost host, KingPileWanted pile)
    {
        wanted = (pile, host.WorldAuthority);
        host.Send(host.WorldAuthority, Msg.ModeCommand, runner.Envelope(ModeSchemas.KingKinds.PileWanted, 0, 0, pile));
    }

    /// <summary>The zone, to everyone, whenever its holder, occupants, mints, spot or dials changed.</summary>
    private void Tell(ModeRunner runner, IModeHost host, double now)
    {
        if ((runner.Rules as KingRules)?.State(runner.Clock(now)) is not { } state || told != null && told.Matches(state)) return;
        if (told == null || told.Zone != state.Zone)
            host.Log(FormattableString.Invariant($"king zone {state.Zone} spot {state.Spot.X:0.00},{state.Spot.Z:0.00}"));
        told = state;
        host.Broadcast(Msg.ModeSnapshot, runner.Envelope(ModeSchemas.KingKinds.Zone, 0, 0, state));
    }

    private void Place(ModeRunner runner, IModeHost host, int peer, ModeEnvelope envelope, KingPilePlaced placed, double now)
    {
        if (wanted is not { } pending || pending.Pile.Zone != placed.Zone || peer != host.WorldAuthority || !runner.Current(envelope) ||
            !runner.Permits(ModeGate.World, now))
        {
            host.Log($"king pile for zone {placed.Zone} from {peer} refused: not the one asked for");
            return;
        }
        var pile = pending.Pile;
        if (placed.Tanks.Count == 0)
        {
            wanted = null;
            host.Log($"king pile for zone {pile.Zone}: no clear ground within {pile.Reach:0.#} m");
            return;
        }
        var spot = pile.Spot;
        if (placed.Tanks.Count != pile.Count || placed.Tanks.Any(t => new Godot.Vector2(t.Origin.X - spot.X, t.Origin.Z - spot.Z).Length() > pile.Reach + PileSlack ||
                Math.Abs(t.Origin.Y - spot.Y) > PileSlack))
        {
            host.Log($"king pile for zone {pile.Zone} from {peer} refused: {placed.Tanks.Count} tanks, not {pile.Count} within reach");
            return;
        }
        wanted = null;
        var tanks = host.SpawnWorldTanks(placed.Tanks);
        host.Broadcast(Msg.ModeCommand, runner.Envelope(ModeSchemas.KingKinds.PileSpawned, 0, 0, new KingPileSpawned(pile.Zone, host.WorldAuthority, tanks)));
        host.Log($"king pile for zone {pile.Zone}: {tanks.Count} tanks {tanks[0].Id:x}..{tanks[^1].Id:x}");
    }
}
