using System;
using System.Collections.Generic;
using System.Linq;

namespace Propane.Modes;

/// <summary>A slab lot: grid cell (<paramref name="I"/>, <paramref name="J"/>) and the band it dissolves with (-1: never).</summary>
public readonly record struct RingOutLot(int I, int J, int Band);

/// <summary>Where a lot is in its life at a moment of the round.</summary>
public enum LotStage : byte { Solid, Warning, Gone }

/// <summary>
/// Ring Out's slab: a square grid of lots centered on the origin whose rim dissolves one band at a time, outer ring
/// first, until the center lot alone remains. Band <c>b</c> warns <see cref="WarningSeconds"/> before it dissolves at
/// <c>(b + 1) * BandSeconds</c> of round time. Pure: the boundary at any moment is a function of round time and this
/// plan, so a runner never sends it.
/// </summary>
public sealed class RingOutPlan
{
    private readonly Dictionary<(int, int), RingOutLot> lots = new();

    public int Side { get; }
    public double LotSize { get; }
    public double BandSeconds { get; }
    public double WarningSeconds { get; }
    public int Bands { get; }
    public IReadOnlyCollection<RingOutLot> Lots => lots.Values;
    public double HalfWidth => Side * LotSize / 2;
    /// <summary>Round time at which the last band is gone and one lot remains.</summary>
    public double Collapsed => Bands == 0 ? 0 : DissolvesAt(Bands - 1);

    public RingOutPlan(int side, double lotSize, int bandLots, double bandSeconds, double warningSeconds, ulong seed)
    {
        Side = Math.Max(1, side | 1); // Odd, so one lot sits on the center.
        LotSize = Math.Max(1, lotSize);
        BandSeconds = Math.Max(0.1, bandSeconds);
        WarningSeconds = Math.Clamp(warningSeconds, 0, BandSeconds);
        var half = Side / 2;
        var random = new ModeRandom(seed);
        var band = 0;
        for (var ring = half; ring >= 1; ring--)
        {
            // The ring's lots in angular order from a seeded start, cut into consecutive arcs of about bandLots each.
            var ringLots = new List<(int I, int J)>();
            for (var i = -ring; i <= ring; i++)
            {
                for (var j = -ring; j <= ring; j++)
                    if (Math.Max(Math.Abs(i), Math.Abs(j)) == ring) ringLots.Add((i, j));
            }

            var start = random.Unit() * Math.Tau;
            ringLots.Sort((a, b) => Angle(a, start).CompareTo(Angle(b, start)));
            var count = Math.Max(1, (int)Math.Ceiling(ringLots.Count / (double)Math.Max(1, bandLots)));
            for (var k = 0; k < ringLots.Count; k++)
            {
                var (i, j) = ringLots[k];
                lots[(i, j)] = new RingOutLot(i, j, band + k * count / ringLots.Count);
            }
            band += count;
        }
        lots[(0, 0)] = new RingOutLot(0, 0, -1);
        Bands = band;
    }

    private static double Angle((int I, int J) lot, double start)
    {
        var angle = Math.Atan2(lot.J, lot.I) - start;
        return angle - Math.Floor(angle / Math.Tau) * Math.Tau;
    }

    public double DissolvesAt(int band) => band < 0 ? double.PositiveInfinity : (band + 1) * BandSeconds;
    public double WarnsAt(int band) => DissolvesAt(band) - WarningSeconds;

    /// <summary>World X/Z of a lot's center.</summary>
    public (double X, double Z) Center(RingOutLot lot) => (lot.I * LotSize, lot.J * LotSize);

    public RingOutLot? LotAt(double x, double z)
    {
        var i = (int)Math.Floor(x / LotSize + 0.5);
        var j = (int)Math.Floor(z / LotSize + 0.5);
        return lots.TryGetValue((i, j), out var lot) ? lot : null;
    }

    public LotStage Stage(RingOutLot lot, double elapsed) =>
        elapsed >= DissolvesAt(lot.Band) ? LotStage.Gone : elapsed >= WarnsAt(lot.Band) ? LotStage.Warning : LotStage.Solid;

    /// <summary>There is ground under this point at this round time.</summary>
    public bool Solid(double x, double z, double elapsed) => LotAt(x, z) is { } lot && Stage(lot, elapsed) != LotStage.Gone;

    /// <summary>Round time of the next dissolve after <paramref name="elapsed"/>, or infinity once one lot remains.</summary>
    public double NextDissolve(double elapsed)
    {
        if (Bands == 0 || elapsed >= Collapsed) return double.PositiveInfinity;
        return DissolvesAt(Math.Max(0, (int)Math.Floor(elapsed / BandSeconds)));
    }

    /// <summary>The band that dissolves next, or -1.</summary>
    public int NextBand(double elapsed) => double.IsInfinity(NextDissolve(elapsed)) ? -1 : Math.Max(0, (int)Math.Floor(elapsed / BandSeconds));
}

/// <summary>
/// Ring Out: sumo on a slab. Nobody has health; standing on dissolved ground or falling below the slab eliminates
/// you, and the last one standing wins. A faller is credited to whoever last knocked them down within the credit
/// window. Placement is by elimination order: actors out in the same step share a place. Pure: positions and throws
/// arrive as accepted inputs with the mode clock.
/// </summary>
public sealed class RingOutRules : IModeRules
{
    public const string RingOutLots = nameof(RingOutLots);
    public const string RingOutLotSize = nameof(RingOutLotSize);
    public const string RingOutBandLots = nameof(RingOutBandLots);
    public const string RingOutBandSeconds = nameof(RingOutBandSeconds);
    public const string RingOutWarningSeconds = nameof(RingOutWarningSeconds);
    public const string RingOutCreditSeconds = nameof(RingOutCreditSeconds);
    public const string RingOutFallDepth = nameof(RingOutFallDepth);
    public const string RingOutStandHeight = nameof(RingOutStandHeight);
    public const string RingOutRoundLimit = nameof(RingOutRoundLimit);
    public static readonly IReadOnlyList<string> Tuners = new[]
    {
        RingOutLots, RingOutLotSize, RingOutBandLots, RingOutBandSeconds, RingOutWarningSeconds, RingOutCreditSeconds,
        RingOutFallDepth, RingOutStandHeight, RingOutRoundLimit,
    };

    private readonly List<ModeActor> actors = new();
    private readonly HashSet<int> alive = new();
    private readonly HashSet<int> started = new();
    private readonly Dictionary<int, int> places = new();
    private readonly Dictionary<int, int> credits = new();
    private readonly Dictionary<int, (int By, double At)> lastThrow = new();
    private readonly List<ActorEliminated> eliminations = new();
    private ModeTuners tuners = ModeTuners.Empty;
    private ulong seed;
    private uint round = 1;
    private double? roundStart;

    public RingOutPlan Plan { get; private set; } = null!;
    public bool Over { get; private set; }
    public IReadOnlyList<ActorEliminated> Eliminations => eliminations;
    public int AliveCount => alive.Count;
    public bool IsAlive(int actorId) => alive.Contains(actorId);
    public int? Place(int actorId) => places.TryGetValue(actorId, out var place) ? place : null;
    public int Credits(int actorId) => credits.GetValueOrDefault(actorId);
    public string Name(int actorId) => actors.FirstOrDefault(a => a.ActorId == actorId)?.Name ?? $"#{actorId}";
    /// <summary>Every actor that started the round has a place.</summary>
    public bool PlacementsComplete => started.All(places.ContainsKey);
    /// <summary>Round time on this clock, or zero before the round starts.</summary>
    public double Elapsed(ModeClock clock) => roundStart is { } start ? Math.Max(0, clock.Now - start) : 0;

    /// <summary>Plain presentation values at this mode clock; no rule-owned collection escapes.</summary>
    public RingOutState State(ModeClock clock) => new(Plan, Over, AliveCount, Elapsed(clock),
        actors.Select(a => new RingOutActor(a.ActorId, a.Name, IsAlive(a.ActorId), Place(a.ActorId), Credits(a.ActorId))).ToArray(),
        eliminations.ToArray());

    public void Initialize(ModeSetup setup)
    {
        actors.Clear(); alive.Clear(); started.Clear(); places.Clear(); credits.Clear(); lastThrow.Clear(); eliminations.Clear();
        tuners = setup.Tuners;
        seed = setup.Seed;
        round = 1;
        roundStart = null;
        Over = false;
        foreach (var actor in setup.Roster) actors.Add(actor);
        Plan = BuildPlan();
    }

    private RingOutPlan BuildPlan() => new(tuners.Int(RingOutLots), tuners.Float(RingOutLotSize), tuners.Int(RingOutBandLots),
        tuners.Float(RingOutBandSeconds), tuners.Float(RingOutWarningSeconds), seed);

    public IReadOnlyList<ModeCommand> Handle(AcceptedEvent accepted, ModeClock clock) => accepted switch
    {
        ActorsSampled sampled => Sample(sampled, clock),
        ActorThrown thrown => Thrown(thrown, clock),
        ActorJoined joined => Join(joined.Actor),
        ActorLeft left => Leave(left.ActorId, clock),
        TunersChanged changed => Retune(changed.Tuners),
        PhaseEntered phase => Enter(phase),
        SpillRequested spill => new ModeCommand[] { new SpillCanceled(spill.Ticket, SpillCancel.Closed) },
        _ => Array.Empty<ModeCommand>(),
    };

    public bool Permits(ModeGate gate, ModeClock clock) => gate == ModeGate.Pickup ? clock.Phase == ModePhase.Round : clock.PlayOpen;

    public ModeState Snapshot()
    {
        var count = Math.Max(1, started.Count);
        var standings = actors.Select(a => new ModeStanding(a.ActorId, a.Name, Score(a.ActorId, count), a.Present))
            .OrderByDescending(s => s.Score).ToArray();
        var report = new[]
        {
            new ModeReport(ModeReportKind.Audit, $"ring out places {string.Join(" ", actors.OrderBy(a => a.ActorId).Select(a => $"{a.ActorId}={(places.TryGetValue(a.ActorId, out var p) ? p : 0)}"))} " +
                $"alive={alive.Count} complete={PlacementsComplete}"),
            new ModeReport(ModeReportKind.Standings, string.Join(" ", actors.OrderBy(a => a.ActorId).Select(a => $"{a.ActorId}={Score(a.ActorId, count)}"))),
            new ModeReport(ModeReportKind.Summary, "match over, " + string.Join(", ", actors.Where(a => started.Contains(a.ActorId))
                .OrderBy(a => places.GetValueOrDefault(a.ActorId, 1)).Select(a => $"{a.Name} #{places.GetValueOrDefault(a.ActorId, 1)}"))),
            new ModeReport(ModeReportKind.Summary, "knock-offs: " + string.Join(", ", actors.Select(a => $"{a.Name} {Credits(a.ActorId)}"))),
        };
        return new ModeState(round, standings, report);
    }

    /// <summary>Placement as a score, so standings rank it: first of N scores N - 1; survivors share first.</summary>
    private int Score(int actorId, int count) =>
        places.TryGetValue(actorId, out var place) ? count - place : alive.Contains(actorId) ? count - 1 : 0;

    // ------------------------------------------------------------------ Events

    private ModeCommand[] Sample(ActorsSampled sampled, ModeClock clock)
    {
        if (!clock.PlayOpen || roundStart == null || Over) return Array.Empty<ModeCommand>();
        var elapsed = Elapsed(clock);
        var fall = tuners.Float(RingOutFallDepth);
        var stand = tuners.Float(RingOutStandHeight);
        var outs = new List<(int Actor, EliminationCause Cause)>();
        foreach (var sample in sampled.Samples)
        {
            if (!alive.Contains(sample.ActorId) || outs.Any(o => o.Actor == sample.ActorId)) continue;
            if (sample.Y < -fall) outs.Add((sample.ActorId, EliminationCause.Fell));
            else if (sample.Y < stand && !Plan.Solid(sample.X, sample.Z, elapsed)) outs.Add((sample.ActorId, EliminationCause.Dissolved));
        }
        var commands = new List<ModeCommand>(Eliminate(outs, clock));
        if (elapsed >= tuners.Float(RingOutRoundLimit)) commands.AddRange(Finish(clock));
        else if (alive.Count <= (started.Count > 1 ? 1 : 0)) commands.AddRange(Finish(clock));
        return commands.ToArray();
    }

    private IEnumerable<ModeCommand> Eliminate(IReadOnlyList<(int Actor, EliminationCause Cause)> outs, ModeClock clock)
    {
        if (outs.Count == 0) yield break;
        foreach (var (actor, _) in outs) alive.Remove(actor);
        var place = alive.Count + 1; // Out together: they share the best place still open.
        var window = tuners.Float(RingOutCreditSeconds);
        foreach (var (actor, cause) in outs)
        {
            var by = cause != EliminationCause.Left && lastThrow.TryGetValue(actor, out var thrown) && thrown.By != 0 && thrown.By != actor &&
                clock.Now - thrown.At <= window ? thrown.By : 0;
            if (by != 0) credits[by] = credits.GetValueOrDefault(by) + 1;
            places[actor] = place;
            var eliminated = new ActorEliminated(actor, by, place, cause);
            eliminations.Add(eliminated);
            yield return eliminated;
        }
    }

    /// <summary>Survivors share first place and the round ends.</summary>
    private IEnumerable<ModeCommand> Finish(ModeClock clock)
    {
        if (Over) yield break;
        Over = true;
        foreach (var survivor in alive) places[survivor] = 1;
        alive.Clear();
        yield return new RoundOver();
    }

    private ModeCommand[] Thrown(ActorThrown thrown, ModeClock clock)
    {
        if (alive.Contains(thrown.VictimId)) lastThrow[thrown.VictimId] = (thrown.ByActorId, clock.Now);
        return Array.Empty<ModeCommand>();
    }

    private ModeCommand[] Join(ModeActor actor)
    {
        if (actors.Any(a => a.ActorId == actor.ActorId)) return Array.Empty<ModeCommand>();
        actors.Add(actor); // A late joiner spectates until the next round.
        return Array.Empty<ModeCommand>();
    }

    private ModeCommand[] Leave(int actorId, ModeClock clock)
    {
        var index = actors.FindIndex(a => a.ActorId == actorId);
        if (index < 0) return Array.Empty<ModeCommand>();
        actors[index] = actors[index] with { Present = false };
        if (!alive.Contains(actorId) || Over) return Array.Empty<ModeCommand>();
        var commands = Eliminate(new[] { (actorId, EliminationCause.Left) }, clock).ToList();
        if (roundStart != null && alive.Count <= (started.Count > 1 ? 1 : 0)) commands.AddRange(Finish(clock));
        return commands.ToArray();
    }

    private ModeCommand[] Retune(ModeTuners changed)
    {
        tuners = changed; // The plan is fixed for the round; it is rebuilt when the next one starts.
        return Array.Empty<ModeCommand>();
    }

    private ModeCommand[] Enter(PhaseEntered phase)
    {
        round = phase.Round.RoundId;
        switch (phase.Phase)
        {
            case ModePhase.Round:
                roundStart = phase.Round.StartsAt;
                Over = false;
                alive.Clear(); started.Clear(); places.Clear(); credits.Clear(); lastThrow.Clear(); eliminations.Clear();
                foreach (var actor in actors.Where(a => a.Present)) { alive.Add(actor.ActorId); started.Add(actor.ActorId); }
                Plan = BuildPlan();
                return new ModeCommand[] { new PublishSnapshot() };
            case ModePhase.RoundEnd:
                if (!Over && roundStart != null) { Over = true; foreach (var survivor in alive) places[survivor] = 1; alive.Clear(); }
                return new ModeCommand[] { new EndMatch() };
            default:
                return Array.Empty<ModeCommand>();
        }
    }
}
