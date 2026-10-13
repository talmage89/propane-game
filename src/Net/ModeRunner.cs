using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Propane.Modes;

namespace Propane.Net;

/// <summary>What a server match offers a mode's wire adapter: delivery, atomic entity registration and logs.</summary>
public interface IModeHost
{
    void Broadcast(Msg type, ModeEnvelope envelope);
    void Send(int peer, Msg type, ModeEnvelope envelope);
    /// <summary>Registers a complete batch of actor-owned entities in the ledger, or throws and registers none.</summary>
    void RegisterEntities(int actorId, int peer, IReadOnlyList<BodyState> bodies);
    uint Life(int actorId);
    void Log(string text);
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

    private static readonly Dictionary<byte, Func<IModeWire>> registered = new() { [ModeRegistry.TankBankId] = () => new TankBankWire() };

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
