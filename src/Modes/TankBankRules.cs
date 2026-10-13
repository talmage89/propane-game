using System;
using System.Collections.Generic;
using System.Linq;

namespace Propane.Modes;

/// <summary>
/// Tank Bank's rules: every accepted detonation banks one tank for the chain's initiator; hits and throws spill
/// banked tanks through tickets; standings rank banks, departed actors included; the conservation audit proves
/// initial banks + detonated map tanks + re-detonated drops - spills = total banked. Pure: the ledger owns entity
/// existence, the victim's owner owns hit grace and drop placement (see the static helpers), and a runner owns the
/// clock and transport.
/// </summary>
public sealed class TankBankRules : IModeRules
{
    public const string HitDropCount = nameof(HitDropCount);
    public const string RagdollDropCount = nameof(RagdollDropCount);
    public const string HitGraceTime = nameof(HitGraceTime);
    /// <summary>The tuners these rules read, copied from the match's tuning.</summary>
    public static readonly IReadOnlyList<string> Tuners = new[] { HitDropCount, RagdollDropCount, HitGraceTime };

    public readonly record struct Stats(int Gained, int LostToHits, int LostToThrows);
    private sealed record Reservation(SpillReason Reason, int Count);

    private readonly List<ModeActor> actors = new(); // Roster order: ties keep it in the standings.
    private readonly Dictionary<int, int> banks = new();
    private readonly Dictionary<int, Stats> stats = new();
    private readonly Dictionary<SpillTicket, Reservation> open = new();
    private readonly Dictionary<int, (uint Life, uint Event)> resolved = new(); // Newest finished ticket per actor.
    private ModeTuners tuners = ModeTuners.Empty;
    private uint round = 1;

    public int MapDetonations { get; private set; }
    public int DropDetonations { get; private set; }
    /// <summary>Tanks a mode minted into banks (King of the Cul-de-sac's zone); always zero in Tank Bank.</summary>
    public int Minted { get; private set; }
    public IReadOnlyDictionary<int, int> Banks => banks;
    public int Bank(int actorId) => banks.GetValueOrDefault(actorId);
    public Stats StatsFor(int actorId) => stats.GetValueOrDefault(actorId);
    public int Reserved(int actorId) => open.Where(t => t.Key.ActorId == actorId).Sum(t => t.Value.Count);
    public int OpenTickets => open.Count;
    public int Initial => actors.Sum(a => a.StartScore);
    public int Spilled => stats.Values.Sum(s => s.LostToHits + s.LostToThrows);
    public int Banked => banks.Values.Sum();
    public int Expected => Initial + MapDetonations + DropDetonations + Minted - Spilled;
    public bool Conserved => Banked == Expected;

    public void Initialize(ModeSetup setup)
    {
        actors.Clear(); banks.Clear(); stats.Clear(); open.Clear(); resolved.Clear();
        MapDetonations = DropDetonations = Minted = 0;
        round = 1;
        tuners = setup.Tuners;
        foreach (var actor in setup.Roster) Join(actor);
    }

    public IReadOnlyList<ModeCommand> Handle(AcceptedEvent accepted, ModeClock clock) => accepted switch
    {
        TankDetonated detonation => Detonated(detonation),
        TankMinted minted => Mint(minted.ActorId),
        SpillRequested spill => Request(spill, clock),
        SpillRegistered registered => Commit(registered.Ticket),
        SpillFailed failed => Close(failed.Ticket, SpillCancel.Rejected),
        ActorJoined joined => Join(joined.Actor),
        ActorLeft left => Leave(left.ActorId),
        TunersChanged changed => Retune(changed.Tuners),
        PhaseEntered phase => Enter(phase),
        _ => Array.Empty<ModeCommand>(),
    };

    /// <summary>Play gates the world and spills; pickups follow the round alone, as the server always has.</summary>
    public bool Permits(ModeGate gate, ModeClock clock) =>
        gate == ModeGate.Pickup ? clock.Phase == ModePhase.Round : clock.PlayOpen;

    public ModeState Snapshot()
    {
        var standings = actors.Select(a => new ModeStanding(a.ActorId, a.Name, banks[a.ActorId], a.Present))
            .OrderByDescending(s => s.Score).ToArray();
        var report = new[]
        {
            new ModeReport(ModeReportKind.Audit, $"bank audit initial={Initial} map={MapDetonations} drops={DropDetonations} " +
                $"spills={Spilled} banked={Banked} expected={Expected} ok={Conserved}"),
            new ModeReport(ModeReportKind.Standings, string.Join(" ", actors.OrderBy(a => a.ActorId).Select(a => $"{a.ActorId}={banks[a.ActorId]}"))),
            new ModeReport(ModeReportKind.Summary, "match over, " + string.Join(", ", standings.Select(s => $"{s.Name} {s.Score}"))),
            new ModeReport(ModeReportKind.Summary, "tanks gained / lost to hits / lost to throws: " + string.Join(", ", actors.Select(a =>
            {
                var st = stats.GetValueOrDefault(a.ActorId);
                return $"{a.Name} {st.Gained}/{st.LostToHits}/{st.LostToThrows}";
            }))),
        };
        return new ModeState(round, standings, report);
    }

    // ------------------------------------------------------------------ Victim-owner helpers (pure)

    /// <summary>The most a spill of this reason may hold, from the copied tuners.</summary>
    public static int Limit(SpillReason reason, ModeTuners tuners) =>
        tuners.Int(reason == SpillReason.Thrown ? RagdollDropCount : HitDropCount);

    /// <summary>How many tanks a spill takes: what is wanted, never more than the unreserved bank.</summary>
    public static int SpillCount(int wanted, int bank, int reserved) => Math.Min(wanted, Math.Max(0, bank - reserved));

    /// <summary>
    /// The victim's owner applies hit grace on its own clock: a hit outside grace spills and opens a new grace
    /// period; a hit inside it spills nothing.
    /// </summary>
    public static bool TryOpenGrace(double now, ref double graceUntil, double graceTime)
    {
        if (now < graceUntil) return false;
        graceUntil = now + graceTime;
        return true;
    }

    // ------------------------------------------------------------------ Events

    private ModeCommand[] Detonated(TankDetonated detonation)
    {
        if (detonation.Origin == TankOrigin.Map) MapDetonations++;
        else DropDetonations++;
        if (!banks.ContainsKey(detonation.ActorId)) return Array.Empty<ModeCommand>();
        var st = stats.GetValueOrDefault(detonation.ActorId);
        stats[detonation.ActorId] = st with { Gained = st.Gained + 1 };
        return new ModeCommand[] { Change(detonation.ActorId, 1) };
    }

    private ModeCommand[] Mint(int actorId)
    {
        if (!banks.ContainsKey(actorId)) return Array.Empty<ModeCommand>();
        Minted++;
        var st = stats.GetValueOrDefault(actorId);
        stats[actorId] = st with { Gained = st.Gained + 1 };
        return new ModeCommand[] { Change(actorId, 1) };
    }

    private ModeCommand[] Request(SpillRequested spill, ModeClock clock)
    {
        var ticket = spill.Ticket;
        if (open.TryGetValue(ticket, out var existing)) return new ModeCommand[] { new SpillReserved(ticket, existing.Count) };
        if (resolved.TryGetValue(ticket.ActorId, out var last) && last.Life == ticket.LifeId && ticket.EventId <= last.Event)
        {
            return Array.Empty<ModeCommand>(); // Already finished: idempotent, never a second debit.
        }
        var actor = actors.FirstOrDefault(a => a.ActorId == ticket.ActorId);
        var reason = !clock.PlayOpen ? SpillCancel.Closed
            : actor is not { Present: true } ? SpillCancel.Departed
            : spill.Count < 1 || spill.Count > Limit(spill.Reason, tuners) ||
              spill.Count > banks[ticket.ActorId] - Reserved(ticket.ActorId) ? SpillCancel.Exceeds
            : (SpillCancel?)null;
        if (reason is { } refused)
        {
            Resolve(ticket);
            return new ModeCommand[] { new SpillCanceled(ticket, refused) };
        }
        open.Add(ticket, new Reservation(spill.Reason, spill.Count));
        return new ModeCommand[] { new SpillReserved(ticket, spill.Count) };
    }

    private ModeCommand[] Commit(SpillTicket ticket)
    {
        if (!open.Remove(ticket, out var reservation)) return Array.Empty<ModeCommand>();
        Resolve(ticket);
        var st = stats.GetValueOrDefault(ticket.ActorId);
        stats[ticket.ActorId] = reservation.Reason == SpillReason.Thrown
            ? st with { LostToThrows = st.LostToThrows + reservation.Count }
            : st with { LostToHits = st.LostToHits + reservation.Count };
        return new ModeCommand[] { Change(ticket.ActorId, -reservation.Count), new SpillCommitted(ticket, reservation.Reason, reservation.Count) };
    }

    private ModeCommand[] Close(SpillTicket ticket, SpillCancel reason)
    {
        if (!open.Remove(ticket)) return Array.Empty<ModeCommand>();
        Resolve(ticket);
        return new ModeCommand[] { new SpillCanceled(ticket, reason) };
    }

    private ModeCommand[] CloseAll(Func<SpillTicket, bool> which, SpillCancel reason) =>
        open.Keys.Where(which).ToArray().SelectMany(ticket => Close(ticket, reason)).ToArray();

    private ModeCommand[] Join(ModeActor actor)
    {
        if (banks.ContainsKey(actor.ActorId)) return Array.Empty<ModeCommand>();
        actors.Add(actor);
        banks[actor.ActorId] = Math.Max(0, actor.StartScore);
        return Array.Empty<ModeCommand>();
    }

    private ModeCommand[] Leave(int actorId)
    {
        var index = actors.FindIndex(a => a.ActorId == actorId);
        if (index < 0) return Array.Empty<ModeCommand>();
        actors[index] = actors[index] with { Present = false }; // The bank stays: standings and the audit keep it.
        return CloseAll(t => t.ActorId == actorId, SpillCancel.Departed);
    }

    private ModeCommand[] Retune(ModeTuners changed)
    {
        tuners = changed;
        return Array.Empty<ModeCommand>();
    }

    private ModeCommand[] Enter(PhaseEntered phase)
    {
        var expired = phase.Phase == ModePhase.Round ? Array.Empty<ModeCommand>() : CloseAll(_ => true, SpillCancel.Expired);
        round = phase.Round.RoundId;
        return phase.Phase switch
        {
            ModePhase.Round => new ModeCommand[] { new PublishSnapshot() },
            ModePhase.RoundEnd => expired.Append(new EndMatch()).ToArray(),
            _ => expired,
        };
    }

    private void Resolve(SpillTicket ticket)
    {
        if (!resolved.TryGetValue(ticket.ActorId, out var last) || last.Life != ticket.LifeId || ticket.EventId > last.Event)
            resolved[ticket.ActorId] = (ticket.LifeId, ticket.EventId);
    }

    private ScoreChanged Change(int actorId, int delta)
    {
        var score = banks[actorId];
        var next = Math.Max(0, score + delta);
        banks[actorId] = next;
        return new ScoreChanged(actorId, next, next - score);
    }
}
