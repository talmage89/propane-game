using System;
using System.Collections.Generic;
using System.Linq;

namespace Propane.Modes;

// Pure mode logic: no nodes, transport, wall clock or UI. Everything a mode's rules learn arrives as an explicit
// input (an accepted event with the current mode clock), and everything they decide leaves as plain commands.
// See docs/modes.md.

/// <summary>Match phases. Lobby membership persists across matches; Loading is infrastructure, not mode logic.</summary>
public enum ModePhase : byte { Lobby, Loading, Countdown, Round, RoundEnd, MatchEnd, Results }

/// <summary>How a round's clock reads: counting down to its end, counting up from its start, or not shown.</summary>
public enum RoundTimer : byte { None, Countdown, CountUp }

/// <summary>
/// One round: its id, the world revision it plays in, its timer and server-time parameters. Ids and revisions start
/// at 1 for a run's first round; a revision changes only when the mode asks for the world to be rebuilt.
/// </summary>
public sealed record RoundInfo(uint RoundId, uint WorldRevision, RoundTimer Timer, double StartsAt, double Length)
{
    public static readonly RoundInfo First = new(1, 1, RoundTimer.Countdown, 0, 0);
}

/// <summary>The runner's clock, sampled for each input: phase, round, server time and when the phase ends.</summary>
public readonly record struct ModeClock(ModePhase Phase, uint RoundId, double Now, double PhaseEnds)
{
    /// <summary>Play is open: a round is running and its deadline has not passed (on the server's clock).</summary>
    public bool PlayOpen => Phase == ModePhase.Round && Now < PhaseEnds;
}

/// <summary>What a runner asks before letting infrastructure act on a request.</summary>
public enum ModeGate : byte
{
    /// <summary>Lifecycle and ownership claims on world entities, gathers, melee: anything that changes the world.</summary>
    World,
    /// <summary>Taking a supply pickup.</summary>
    Pickup,
}

/// <summary>A participant as the rules see it. Teams, when a mode adds them, are ids plus predicates beside this.</summary>
public sealed record ModeActor(int ActorId, string Name, bool Present = true, int StartScore = 0);

/// <summary>Where a mode's world comes from: the server's transmitted suburb plan, or the empty construct.</summary>
public enum ModeWorldSource : byte { SuburbPlan, Void }

/// <summary>The world a run starts in, as plain facts. <paramref name="Spots"/> are points a mode may use, such as piles.</summary>
public sealed record ModeWorld(ModeWorldSource Source, uint Revision = 1, int MapTanks = 0, IReadOnlyList<ModeSpot>? Spots = null);

/// <summary>An immutable copy of the tuner values a mode's rules read, by tuner name.</summary>
public sealed class ModeTuners
{
    private readonly Dictionary<string, double> values;
    public ModeTuners(IReadOnlyDictionary<string, double> values) => this.values = new Dictionary<string, double>(values);
    public static readonly ModeTuners Empty = new(new Dictionary<string, double>());
    public IReadOnlyCollection<string> Names => values.Keys;
    public double Float(string name) => values.TryGetValue(name, out var value) ? value
        : throw new KeyNotFoundException($"mode tuner {name} was not copied");
    public int Int(string name) => checked((int)Math.Round(Float(name)));
    public ModeTuners With(string name, double value) => new(new Dictionary<string, double>(values) { [name] = value });
}

/// <summary>Everything rules start from: roster, world, copied tuners and a deterministic seed.</summary>
public sealed record ModeSetup(IReadOnlyList<ModeActor> Roster, ModeWorld World, ModeTuners Tuners, ulong Seed);

/// <summary>A server-accepted input. Physical facts, joins, leaves, tuning and phase changes all arrive this way.</summary>
public abstract record AcceptedEvent;
public sealed record ActorJoined(ModeActor Actor) : AcceptedEvent;
public sealed record ActorLeft(int ActorId) : AcceptedEvent;
public sealed record TunersChanged(ModeTuners Tuners) : AcceptedEvent;
public sealed record PhaseEntered(ModePhase Phase, RoundInfo Round) : AcceptedEvent;

/// <summary>Where a detonated tank came from: the suburb's plan or an earlier spill.</summary>
public enum TankOrigin : byte { Map, Drop }

/// <summary>The ledger accepted a detonation credited to this actor (the chain's initiator).</summary>
public sealed record TankDetonated(int ActorId, TankOrigin Origin) : AcceptedEvent;

/// <summary>A mode created one banked tank for this actor from nothing (a held zone). Tank Bank never mints.</summary>
public sealed record TankMinted(int ActorId) : AcceptedEvent;

/// <summary>Why a victim spills: a bullet hit, or being thrown (ragdolled).</summary>
public enum SpillReason : byte { Hit, Thrown }

/// <summary>
/// An idempotent spill identity: the victim actor, its life and the victim owner's actor-local event id.
/// </summary>
public readonly record struct SpillTicket(int ActorId, uint LifeId, uint EventId);

/// <summary>A victim's owner asks to spill <paramref name="Count"/> banked tanks, with its placements in hand.</summary>
public sealed record SpillRequested(SpillTicket Ticket, SpillReason Reason, int Count) : AcceptedEvent;
/// <summary>The ledger registered the ticket's complete batch of entities.</summary>
public sealed record SpillRegistered(SpillTicket Ticket) : AcceptedEvent;
/// <summary>The ledger refused the ticket's batch; nothing was registered.</summary>
public sealed record SpillFailed(SpillTicket Ticket) : AcceptedEvent;

/// <summary>A decision. Runners turn these into broadcasts, ledger operations and phase changes.</summary>
public abstract record ModeCommand;

/// <summary>An actor's primary score changed. <paramref name="Delta"/> is zero when only restating it.</summary>
public sealed record ScoreChanged(int ActorId, int Score, int Delta) : ModeCommand;

/// <summary>Bank is reserved for this ticket: register its entities, then report the outcome.</summary>
public sealed record SpillReserved(SpillTicket Ticket, int Count) : ModeCommand;
/// <summary>The debit committed: everyone may now see the spill.</summary>
public sealed record SpillCommitted(SpillTicket Ticket, SpillReason Reason, int Count) : ModeCommand;

public enum SpillCancel : byte { Closed, Exceeds, Duplicate, Departed, Rejected, Expired }
/// <summary>The ticket is void: its owner removes its predictions. Nothing was debited.</summary>
public sealed record SpillCanceled(SpillTicket Ticket, SpillCancel Reason) : ModeCommand;

/// <summary>Send the rules' snapshot to everyone (a round started).</summary>
public sealed record PublishSnapshot : ModeCommand;

/// <summary>The round is over and so is the match.</summary>
public sealed record EndMatch : ModeCommand;
/// <summary>The round is over; play another. A rebuilt world gets a new revision and loads again.</summary>
public sealed record NextRound(RoundTimer Timer, double Length, bool RebuildWorld) : ModeCommand;

/// <summary>One row of the standings, ranked by score; departed actors keep their row.</summary>
public sealed record ModeStanding(int ActorId, string Name, int Score, bool Present);

/// <summary>Where a report line belongs in a server log: the match's audit, its standings, or its lobby summary.</summary>
public enum ModeReportKind : byte { Audit, Standings, Summary }
public sealed record ModeReport(ModeReportKind Kind, string Text);

/// <summary>Plain state for snapshots, HUD presenters, bots and results. Reports are for server logs.</summary>
public sealed record ModeState(uint RoundId, IReadOnlyList<ModeStanding> Standings, IReadOnlyList<ModeReport> Report)
{
    public int Score(int actorId) => Standings.FirstOrDefault(s => s.ActorId == actorId)?.Score ?? 0;
}

/// <summary>A mode's rules. Rules run headless; the same instance serves a server match or a local Lab session.</summary>
public interface IModeRules
{
    void Initialize(ModeSetup setup);
    IReadOnlyList<ModeCommand> Handle(AcceptedEvent accepted, ModeClock clock);
    ModeState Snapshot();
    bool Permits(ModeGate gate, ModeClock clock);
}

/// <summary>The sandbox: no score, no spills, no end. Everything is always allowed.</summary>
public sealed class SandboxRules : IModeRules
{
    private readonly List<ModeActor> roster = new();
    public void Initialize(ModeSetup setup) { roster.Clear(); roster.AddRange(setup.Roster); }
    public IReadOnlyList<ModeCommand> Handle(AcceptedEvent accepted, ModeClock clock) => accepted switch
    {
        ActorJoined joined => Join(joined.Actor),
        ActorLeft left => Leave(left.ActorId),
        SpillRequested spill => new ModeCommand[] { new SpillCanceled(spill.Ticket, SpillCancel.Closed) },
        PhaseEntered { Phase: ModePhase.RoundEnd } => new ModeCommand[] { new EndMatch() },
        _ => Array.Empty<ModeCommand>(),
    };
    public ModeState Snapshot() => new(1, roster.Select(a => new ModeStanding(a.ActorId, a.Name, 0, a.Present)).ToArray(), Array.Empty<ModeReport>());
    public bool Permits(ModeGate gate, ModeClock clock) => true;

    private ModeCommand[] Join(ModeActor actor)
    {
        roster.RemoveAll(a => a.ActorId == actor.ActorId);
        roster.Add(actor);
        return Array.Empty<ModeCommand>();
    }

    private ModeCommand[] Leave(int actorId)
    {
        var index = roster.FindIndex(a => a.ActorId == actorId);
        if (index >= 0) roster[index] = roster[index] with { Present = false };
        return Array.Empty<ModeCommand>();
    }
}
