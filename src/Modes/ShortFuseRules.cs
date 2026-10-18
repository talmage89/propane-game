using System;
using System.Collections.Generic;
using System.Linq;

namespace Propane.Modes;

/// <summary>A round is decided: <paramref name="WinnerId"/> was last alive, or zero when nobody won it.</summary>
public sealed record RoundWon(uint RoundId, int WinnerId) : ModeCommand;

/// <summary>
/// Short Fuse: last player standing in a suburb of tanks. Everyone has health (<see cref="HealthState"/>): bullets
/// chip, blasts kill, and a thrown player is easy prey. The dead spectate; the last one alive wins the round, and the
/// first to <see cref="ShortFuseWinsToMatch"/> round wins takes the match, in a new suburb each round. A round that
/// reaches its limit goes to the survivor with the most health (a tie wins nobody the round). Pure: damage arrives as
/// shooter and victim reports with the mode clock, and health validates them.
/// </summary>
public sealed class ShortFuseRules : IModeRules
{
    public const string ShortFuseRoundSeconds = nameof(ShortFuseRoundSeconds);
    public const string ShortFuseWinsToMatch = nameof(ShortFuseWinsToMatch);
    public const string ShortFuseMaxHealth = nameof(ShortFuseMaxHealth);
    public const string ShortFuseBulletDamage = nameof(ShortFuseBulletDamage);
    public const string ShortFuseBlastCenterDamage = nameof(ShortFuseBlastCenterDamage);
    public const string ShortFuseBlastEdgeDamage = nameof(ShortFuseBlastEdgeDamage);
    /// <summary>The shared ragdoll radius: blast damage reaches as far as a blast throws a player.</summary>
    public const string RagdollRadius = nameof(RagdollRadius);
    public const string ShortFuseRegenPerSecond = nameof(ShortFuseRegenPerSecond);
    public const string ShortFuseRegenDelay = nameof(ShortFuseRegenDelay);
    public const string ShortFuseGetUpGrace = nameof(ShortFuseGetUpGrace);
    public const string ShortFuseCreditSeconds = nameof(ShortFuseCreditSeconds);
    public const string ShortFuseReportWindow = nameof(ShortFuseReportWindow);
    public static readonly IReadOnlyList<string> Tuners = new[]
    {
        ShortFuseRoundSeconds, ShortFuseWinsToMatch, ShortFuseMaxHealth, ShortFuseBulletDamage, ShortFuseBlastCenterDamage,
        ShortFuseBlastEdgeDamage, RagdollRadius, ShortFuseRegenPerSecond, ShortFuseRegenDelay, ShortFuseGetUpGrace, ShortFuseCreditSeconds,
        ShortFuseReportWindow,
    };

    private readonly List<ModeActor> actors = new();
    private readonly HashSet<int> started = new();
    private readonly Dictionary<int, int> wins = new();
    private readonly Dictionary<int, int> kills = new();
    private readonly Dictionary<int, int> deaths = new();
    private readonly List<ActorDied> roundDeaths = new();
    private readonly List<RoundWon> rounds = new();
    private ModeTuners tuners = ModeTuners.Empty;
    private uint round = 1;
    private double? roundStart;

    public HealthState Health { get; private set; } = new(new HealthRules(100, 0, 0, 0, 0, 0, 0, 0, 0, 0));
    /// <summary>The round is decided (or its limit reached); play is over until the next one.</summary>
    public bool Over { get; private set; }
    public bool MatchOver { get; private set; }
    public uint Round => round;
    public int WinsToMatch => Math.Max(1, tuners.Int(ShortFuseWinsToMatch));
    public double RoundLength => tuners.Float(ShortFuseRoundSeconds);
    public double MaxHealth => Health.Rules.MaxHealth;
    public IReadOnlyList<ActorDied> RoundDeaths => roundDeaths;
    public IReadOnlyList<RoundWon> Rounds => rounds;
    public IReadOnlyCollection<int> Starters => started;
    public int Wins(int actorId) => wins.GetValueOrDefault(actorId);
    public int Kills(int actorId) => kills.GetValueOrDefault(actorId);
    public int DeathsOf(int actorId) => deaths.GetValueOrDefault(actorId);
    public bool IsAlive(int actorId) => started.Contains(actorId) && Health.IsAlive(actorId);
    public int AliveCount => started.Count(Health.IsAlive);
    public string Name(int actorId) => actors.FirstOrDefault(a => a.ActorId == actorId)?.Name ?? $"#{actorId}";
    public IReadOnlyList<ModeActor> Actors => actors;
    /// <summary>Round time on this clock, or zero before the round starts.</summary>
    public double Elapsed(ModeClock clock) => roundStart is { } start ? Math.Max(0, clock.Now - start) : 0;
    public int? MatchWinner => MatchOver ? actors.Select(a => a.ActorId).Where(a => Wins(a) >= WinsToMatch).Cast<int?>().FirstOrDefault() : null;

    /// <summary>
    /// Whom a player out of the round watches: themself while alive; once dead, whoever got them while they live,
    /// else the lowest-numbered actor still alive (zero when nobody is). Pure, so every view agrees.
    /// </summary>
    public int SpectateTarget(int actorId)
    {
        if (IsAlive(actorId) || !started.Contains(actorId)) return IsAlive(actorId) ? actorId : FirstAlive();
        var seen = new HashSet<int>();
        var at = actorId;
        // Follow the chain of killers: if mine is dead too, watch whoever got them.
        while (seen.Add(at) && roundDeaths.LastOrDefault(d => d.VictimId == at) is { } death)
        {
            var by = death.CreditedTo != 0 ? death.CreditedTo : death.KilledBy;
            if (by == 0 || by == at) break;
            if (IsAlive(by)) return by;
            at = by;
        }
        return FirstAlive();
    }

    /// <summary>Copy facts at read time, preserving the mode-clock domain of health regeneration and shields.</summary>
    public ShortFuseState State(ModeClock clock) => new(Over, Round, WinsToMatch, MaxHealth, AliveCount, MatchWinner,
        actors.Select(a => new ShortFuseActor(a.ActorId, a.Name, started.Contains(a.ActorId), IsAlive(a.ActorId),
            Health.Health(a.ActorId, clock.Now), Health.Invulnerable(a.ActorId, clock.Now), Wins(a.ActorId), Kills(a.ActorId),
            SpectateTarget(a.ActorId))).ToArray(), roundDeaths.ToArray(), rounds.ToArray());

    private int FirstAlive() => started.Where(Health.IsAlive).DefaultIfEmpty(0).Min();

    public void Initialize(ModeSetup setup)
    {
        actors.Clear(); started.Clear(); wins.Clear(); kills.Clear(); deaths.Clear(); roundDeaths.Clear(); rounds.Clear();
        tuners = setup.Tuners;
        Health = new HealthState(BuildHealth());
        round = 1;
        roundStart = null;
        Over = MatchOver = false;
        actors.AddRange(setup.Roster);
    }

    private HealthRules BuildHealth() => new(tuners.Float(ShortFuseMaxHealth), tuners.Float(ShortFuseBulletDamage),
        tuners.Float(ShortFuseBlastCenterDamage), tuners.Float(ShortFuseBlastEdgeDamage), tuners.Float(RagdollRadius),
        tuners.Float(ShortFuseRegenPerSecond), tuners.Float(ShortFuseRegenDelay), tuners.Float(ShortFuseGetUpGrace),
        tuners.Float(ShortFuseCreditSeconds), tuners.Float(ShortFuseReportWindow));

    public IReadOnlyList<ModeCommand> Handle(AcceptedEvent accepted, ModeClock clock) => accepted switch
    {
        PhaseEntered phase => Enter(phase, clock),
        ActorJoined joined => Join(joined.Actor),
        ActorLeft left => Leave(left, clock),
        TunersChanged changed => Retune(changed.Tuners),
        SpillRequested spill => new ModeCommand[] { new SpillCanceled(spill.Ticket, SpillCancel.Closed) },
        BulletHitReported or BlastHitReported or BlastAccepted or BlastRefused or InvulnerabilityGranted or ActorsSampled =>
            Over || roundStart == null ? Array.Empty<ModeCommand>() : Decide(Health.Handle(accepted, clock), clock),
        _ => Array.Empty<ModeCommand>(),
    };

    public bool Permits(ModeGate gate, ModeClock clock) => gate == ModeGate.Pickup ? clock.Phase == ModePhase.Round : clock.PlayOpen && !Over;

    public ModeState Snapshot()
    {
        var standings = actors.Select(a => new ModeStanding(a.ActorId, a.Name, Wins(a.ActorId), a.Present))
            .OrderByDescending(s => s.Score).ToArray();
        var ordered = actors.OrderBy(a => a.ActorId).ToArray();
        var report = new[]
        {
            new ModeReport(ModeReportKind.Audit, Health.Audit()),
            new ModeReport(ModeReportKind.Audit, $"short fuse round={round} alive={AliveCount} over={Over} match_over={MatchOver} " +
                $"rounds={string.Join(",", rounds.Select(r => r.WinnerId))}"),
            new ModeReport(ModeReportKind.Standings, string.Join(" ", ordered.Select(a => $"{a.ActorId}={Wins(a.ActorId)}"))),
            new ModeReport(ModeReportKind.Summary, "match over, " + string.Join(", ", standings.Select(s => $"{s.Name} {s.Score}"))),
            new ModeReport(ModeReportKind.Summary, "kills: " + string.Join(", ", ordered.Select(a => $"{a.Name} {Kills(a.ActorId)}/{DeathsOf(a.ActorId)}"))),
        };
        return new ModeState(round, standings, report);
    }

    // ------------------------------------------------------------------ Events

    private ModeCommand[] Decide(IReadOnlyList<ModeCommand> decided, ModeClock clock)
    {
        var commands = new List<ModeCommand>(decided);
        foreach (var died in decided.OfType<ActorDied>())
        {
            roundDeaths.Add(died);
            deaths[died.VictimId] = DeathsOf(died.VictimId) + 1;
            if (died.CreditedTo != 0) kills[died.CreditedTo] = Kills(died.CreditedTo) + 1;
        }
        // Last one alive wins; two going down together leave nobody. A round started alone ends only at its limit.
        if (!Over && started.Count > 1 && AliveCount <= 1) commands.AddRange(Finish(FirstAlive(), clock));
        else if (!Over && started.Count == 1 && AliveCount == 0) commands.AddRange(Finish(0, clock));
        return commands.ToArray();
    }

    private IEnumerable<ModeCommand> Finish(int winner, ModeClock clock)
    {
        Over = true;
        Health.Close(clock.Now);
        if (winner != 0) wins[winner] = Wins(winner) + 1;
        var won = new RoundWon(round, winner);
        rounds.Add(won);
        yield return won;
        yield return new RoundOver();
    }

    private ModeCommand[] Join(ModeActor actor)
    {
        if (actors.Any(a => a.ActorId == actor.ActorId)) return Array.Empty<ModeCommand>();
        actors.Add(actor); // A late joiner spectates until the next round.
        return Array.Empty<ModeCommand>();
    }

    private ModeCommand[] Leave(ActorLeft left, ModeClock clock)
    {
        var index = actors.FindIndex(a => a.ActorId == left.ActorId);
        if (index < 0) return Array.Empty<ModeCommand>();
        actors[index] = actors[index] with { Present = false };
        if (Over || roundStart == null || !started.Contains(left.ActorId)) return Array.Empty<ModeCommand>();
        return Decide(Health.Handle(left, clock), clock);
    }

    private ModeCommand[] Retune(ModeTuners changed)
    {
        tuners = changed; // The round's length holds until the next round; health reads the new values at once.
        Health.Rules = BuildHealth();
        return Array.Empty<ModeCommand>();
    }

    private ModeCommand[] Enter(PhaseEntered phase, ModeClock clock)
    {
        round = phase.Round.RoundId;
        switch (phase.Phase)
        {
            case ModePhase.Round:
            {
                roundStart = phase.Round.StartsAt;
                Over = false;
                started.Clear();
                roundDeaths.Clear();
                foreach (var actor in actors.Where(a => a.Present)) started.Add(actor.ActorId);
                var commands = new List<ModeCommand>(Health.BeginRound(started.OrderBy(a => a), phase.Round.StartsAt)) { new PublishSnapshot() };
                return commands.ToArray();
            }
            case ModePhase.RoundEnd:
            {
                var commands = new List<ModeCommand>();
                if (!Over && roundStart != null)
                {
                    // The limit: the survivor with the most health takes it; a tie wins nobody the round.
                    var alive = started.Where(Health.IsAlive).Select(a => (Actor: a, Health: Health.Health(a, clock.Now))).ToArray();
                    var best = alive.Select(a => a.Health).DefaultIfEmpty(0).Max();
                    var top = alive.Where(a => a.Health >= best - 1e-9).ToArray();
                    commands.AddRange(Finish(top.Length == 1 ? top[0].Actor : 0, clock).OfType<RoundWon>());
                }
                if (actors.Any(a => Wins(a.ActorId) >= WinsToMatch) || actors.Count(a => a.Present) < 2 && rounds.Count > 0)
                {
                    MatchOver = true;
                    commands.Add(new EndMatch());
                }
                else
                {
                    commands.Add(new NextRound(RoundTimer.Countdown, RoundLength, RebuildWorld: true));
                }
                return commands.ToArray();
            }
            default:
                return Array.Empty<ModeCommand>();
        }
    }
}
