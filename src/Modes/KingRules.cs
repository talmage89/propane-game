using System;
using System.Collections.Generic;
using System.Linq;
using Propane.World;
using Propane.World.Plan;

namespace Propane.Modes;

/// <summary>
/// The zone moved to a new spot: a runner materializes a fresh pile on clear ground beside it (zone 0, the round's
/// first, sits on a pile already).
/// </summary>
public sealed record ZoneMoved(int Zone, ModeSpot Spot) : ModeCommand;

/// <summary>
/// King of the Cul-de-sac: Tank Bank's bank and spills, plus a zone that sits on a pile. One upright actor alone in
/// the zone mints a banked tank every mint interval; a contested zone mints nothing, and any change of holder
/// (contest, leaving, being thrown, a relocation) restarts the count. The zone relocates on a fixed period to a pile
/// spot within the move range of the last, chosen by the seed, so where it is at any moment is a pure function of the
/// round clock and the spots. Occupancy comes from sampled positions, so a thrown holder loses it. Pure.
/// </summary>
public sealed class KingRules : IModeRules
{
    public const string KingZoneRadius = nameof(KingZoneRadius);
    public const string KingZoneHeight = nameof(KingZoneHeight);
    public const string KingMintSeconds = nameof(KingMintSeconds);
    public const string KingMoveSeconds = nameof(KingMoveSeconds);
    public const string KingWarningSeconds = nameof(KingWarningSeconds);
    public const string KingMoveMin = nameof(KingMoveMin);
    public const string KingMoveMax = nameof(KingMoveMax);
    public static readonly IReadOnlyList<string> Tuners = TankBankRules.Tuners.Concat(new[]
    {
        KingZoneRadius, KingZoneHeight, KingMintSeconds, KingMoveSeconds, KingWarningSeconds, KingMoveMin, KingMoveMax,
    }).ToArray();

    /// <summary>
    /// The mint pace a lobby or the Lab's setup page may choose: seconds per tank, in steps. One definition for the
    /// lobby's setting, its validation on the server and the Lab dial it maps to.
    /// </summary>
    public const double MintPaceMin = 0.5, MintPaceMax = 10, MintPaceStep = 0.5, MintPaceDefault = 2;

    /// <summary>A mint pace on the lobby's steps: inside the range and a whole number of steps.</summary>
    public static bool ValidMintPace(double seconds) => seconds is >= MintPaceMin and <= MintPaceMax &&
        Math.Abs(seconds / MintPaceStep - Math.Round(seconds / MintPaceStep)) < 1e-6;

    private readonly TankBankRules bank = new();
    private readonly List<ModeSpot> spots = new();
    private readonly List<int> schedule = new(); // Spot index of each zone, in order.
    private readonly Dictionary<int, int> minted = new();
    private readonly HashSet<int> present = new();
    private ModeTuners tuners = ModeTuners.Empty;
    private ModeTuners latest = ModeTuners.Empty;
    private ModeRandom random;
    private ulong seed;
    private double? roundStart;
    private double nextMint; // Mode time of the holder's next tank.

    /// <summary>The bank, spills and audit, exactly as Tank Bank keeps them.</summary>
    public TankBankRules Bank => bank;
    public IReadOnlyList<ModeSpot> Spots => spots;
    /// <summary>The zone in force (its index in the schedule), or -1 before the round starts.</summary>
    public int Zone { get; private set; } = -1;
    /// <summary>The lone upright occupant, or zero when the zone is empty or contested.</summary>
    public int Holder { get; private set; }
    public bool Contested { get; private set; }
    /// <summary>Actors in the zone at the latest sample.</summary>
    public IReadOnlyList<int> Occupants { get; private set; } = Array.Empty<int>();
    public int MintedBy(int actorId) => minted.GetValueOrDefault(actorId);
    public double Elapsed(ModeClock clock) => roundStart is { } start ? Math.Max(0, clock.Now - start) : 0;
    public double MoveSeconds => Math.Max(1, tuners.Float(KingMoveSeconds));
    public double Radius => tuners.Float(KingZoneRadius);
    private double MintInterval => Math.Max(0.05, tuners.Float(KingMintSeconds));

    /// <summary>A suburb's pile spots: each of the plan's piles at its tanks' mean position on the ground.</summary>
    public static IReadOnlyList<ModeSpot> SpotsFrom(SuburbPlan plan) => plan.Tanks.GroupBy(t => t.Pile).OrderBy(g => g.Key)
        .Select(g => new ModeSpot(g.Average(t => (double)t.Position.X), Suburb.GroundHeight, g.Average(t => (double)t.Position.Y))).ToArray();

    /// <summary>
    /// The zone as every peer should see it at this clock, or null before the round's first sample places it. This is
    /// what a networked runner sends in its Zone snapshot.
    /// </summary>
    public KingState? State(ModeClock clock)
    {
        if (Zone < 0 || roundStart is not { } start) return null;
        return new KingState(Zone, SpotOf(Zone), SpotOf(Zone + 1), Radius, Holder, Occupants, Holder != 0 ? nextMint - start : 0,
            MintInterval, (Zone + 1) * MoveSeconds, tuners.Float(KingWarningSeconds),
            minted.Where(m => m.Value > 0).OrderBy(m => m.Key).Select(m => new KingMinted(m.Key, m.Value)).ToArray(), Elapsed(clock));
    }

    public void Initialize(ModeSetup setup)
    {
        bank.Initialize(setup);
        tuners = latest = setup.Tuners;
        present.Clear();
        present.UnionWith(setup.Roster.Where(a => a.Present).Select(a => a.ActorId));
        seed = setup.Seed;
        spots.Clear();
        spots.AddRange(setup.World.Spots ?? Array.Empty<ModeSpot>());
        if (spots.Count == 0) spots.Add(new ModeSpot(0, 0, 0));
        Reset();
    }

    private void Reset()
    {
        random = new ModeRandom(seed);
        schedule.Clear();
        schedule.Add(random.Below(spots.Count));
        minted.Clear();
        roundStart = null;
        Zone = -1;
        Holder = 0;
        Contested = false;
        Occupants = Array.Empty<int>();
    }

    /// <summary>Zone <paramref name="zone"/>'s spot. The schedule extends deterministically as far as asked.</summary>
    public ModeSpot SpotOf(int zone)
    {
        while (schedule.Count <= zone) schedule.Add(Next(schedule[^1]));
        return spots[schedule[Math.Max(0, zone)]];
    }

    /// <summary>The zone in force at a round time.</summary>
    public int ZoneAt(double elapsed) => (int)Math.Floor(Math.Max(0, elapsed) / MoveSeconds);

    /// <summary>The zone is about to move: inside the warning before its period ends.</summary>
    public bool Warning(double elapsed) => elapsed - ZoneAt(elapsed) * MoveSeconds >= MoveSeconds - tuners.Float(KingWarningSeconds);

    /// <summary>Seconds until the zone in force at <paramref name="elapsed"/> moves.</summary>
    public double UntilMove(double elapsed) => (ZoneAt(elapsed) + 1) * MoveSeconds - elapsed;

    private int Next(int from)
    {
        if (spots.Count == 1) return from;
        var near = tuners.Float(KingMoveMin);
        var far = Math.Max(near, tuners.Float(KingMoveMax));
        var here = spots[from];
        var others = Enumerable.Range(0, spots.Count).Where(i => i != from).ToArray();
        var inRange = others.Where(i => Distance(here, spots[i]) is var d && d >= near && d <= far).ToArray();
        if (inRange.Length > 0) return inRange[random.Below(inRange.Length)];
        // None in range: the spot whose distance misses the range by least.
        return others.OrderBy(i => Distance(here, spots[i]) is var d && d < near ? near - d : d - far).ThenBy(i => i).First();
    }

    private static double Distance(ModeSpot a, ModeSpot b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    public IReadOnlyList<ModeCommand> Handle(AcceptedEvent accepted, ModeClock clock) => accepted switch
    {
        ActorsSampled sampled => Sample(sampled, clock),
        ActorJoined joined => Join(joined, clock),
        ActorLeft left => Leave(left, clock),
        TunersChanged changed => Retune(changed, clock),
        PhaseEntered phase => Enter(phase, clock),
        TankMinted => Array.Empty<ModeCommand>(), // Only the zone mints.
        _ => bank.Handle(accepted, clock),
    };

    public bool Permits(ModeGate gate, ModeClock clock) => bank.Permits(gate, clock);

    public ModeState Snapshot()
    {
        var state = bank.Snapshot();
        var report = state.Report.Select(line => line.Kind == ModeReportKind.Audit
            ? new ModeReport(ModeReportKind.Audit, $"king audit initial={bank.Initial} map={bank.MapDetonations} drops={bank.DropDetonations} " +
                $"minted={bank.Minted} spills={bank.Spilled} banked={bank.Banked} expected={bank.Expected} ok={bank.Conserved}")
            : line).Append(new ModeReport(ModeReportKind.Audit, "king minted " +
                string.Join(" ", state.Standings.OrderBy(s => s.ActorId).Select(s => $"{s.ActorId}={MintedBy(s.ActorId)}"))))
            .Append(new ModeReport(ModeReportKind.Summary, "tanks minted in the zone: " +
                string.Join(", ", state.Standings.OrderBy(s => s.ActorId).Select(s => $"{s.Name} {MintedBy(s.ActorId)}")))).ToArray();
        return state with { Report = report };
    }

    // ------------------------------------------------------------------ Events

    private ModeCommand[] Sample(ActorsSampled sampled, ModeClock clock)
    {
        if (!clock.PlayOpen || roundStart == null) return Array.Empty<ModeCommand>();
        var commands = new List<ModeCommand>();
        var elapsed = Elapsed(clock);
        var zone = ZoneAt(elapsed);
        if (zone != Zone)
        {
            Zone = zone;
            Release();
            commands.Add(new ZoneMoved(zone, SpotOf(zone)));
        }
        var spot = SpotOf(zone);
        var radius = Radius;
        var height = tuners.Float(KingZoneHeight);
        Occupants = sampled.Samples.Where(s => !s.Down && present.Contains(s.ActorId) && Math.Abs(s.Y - spot.Y) <= height &&
                (s.X - spot.X) * (s.X - spot.X) + (s.Z - spot.Z) * (s.Z - spot.Z) <= radius * radius)
            .Select(s => s.ActorId).Distinct().Order().ToArray();
        Contested = Occupants.Count > 1;
        var lone = Occupants.Count == 1 ? Occupants[0] : 0;
        if (lone != Holder)
        {
            Holder = lone;
            nextMint = clock.Now + MintInterval;
        }
        // A holder mints on every interval held without a break; samples may be coarser than the interval.
        while (Holder != 0 && clock.Now >= nextMint)
        {
            minted[Holder] = MintedBy(Holder) + 1;
            commands.AddRange(bank.Handle(new TankMinted(Holder), clock));
            nextMint += MintInterval;
        }
        return commands.ToArray();
    }

    private void Release()
    {
        Holder = 0;
        Contested = false;
        Occupants = Array.Empty<int>();
    }

    private IReadOnlyList<ModeCommand> Join(ActorJoined joined, ModeClock clock)
    {
        if (joined.Actor.Present) present.Add(joined.Actor.ActorId);
        return bank.Handle(joined, clock);
    }

    private IReadOnlyList<ModeCommand> Leave(ActorLeft left, ModeClock clock)
    {
        present.Remove(left.ActorId);
        // A departing holder loses the zone at once; whoever stays inside holds or contests from the next sample.
        if (Occupants.Contains(left.ActorId)) Release();
        return bank.Handle(left, clock);
    }

    private IReadOnlyList<ModeCommand> Retune(TunersChanged changed, ModeClock clock)
    {
        // The schedule is the round's plan: timing tuners apply from the next round, so the zone never jumps.
        latest = changed.Tuners;
        tuners = roundStart == null ? latest
            : new[] { KingMoveSeconds, KingMoveMin, KingMoveMax }.Aggregate(latest, (t, name) => t.With(name, tuners.Float(name)));
        return bank.Handle(new TunersChanged(tuners), clock);
    }

    private IReadOnlyList<ModeCommand> Enter(PhaseEntered phase, ModeClock clock)
    {
        if (phase.Phase == ModePhase.Round)
        {
            tuners = latest;
            Reset();
            roundStart = phase.Round.StartsAt;
        }
        else if (phase.Phase != ModePhase.Countdown)
        {
            Release();
        }
        return bank.Handle(phase, clock);
    }
}
