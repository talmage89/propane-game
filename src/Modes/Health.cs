using System;
using System.Collections.Generic;
using System.Linq;

namespace Propane.Modes;

// The optional health state: typed damage, attribution, invulnerability, death and respawn, as accepted inputs and
// commands. Damage arrives as reports along the game's authority lines (a shooter reports its bullet hit; a victim
// reports the blast that threw it, naming that blast) and the pure state here validates them. A mode composes it, as
// King composes Tank Bank's bank. See docs/modes.md.

/// <summary>What dealt damage.</summary>
public enum DamageKind : byte { Bullet, Blast }

/// <summary>
/// The numbers health reads, copied from a mode's tuners. Blast damage falls linearly from
/// <paramref name="BlastCenterDamage"/> at the center to <paramref name="BlastEdgeDamage"/> at
/// <paramref name="BlastRadius"/>, and is none beyond. <paramref name="RegenPerSecond"/> zero disables regeneration.
/// A report older than <paramref name="ReportWindow"/> is late, and a death is final once the window has passed it.
/// </summary>
public sealed record HealthRules(double MaxHealth, double BulletDamage, double BlastCenterDamage, double BlastEdgeDamage, double BlastRadius,
    double RegenPerSecond, double RegenDelay, double GetUpGrace, double CreditWindow, double ReportWindow)
{
    /// <summary>Blast damage at a distance from the center: linear falloff to the edge, none beyond.</summary>
    public double BlastDamage(double distance) =>
        !double.IsFinite(distance) || distance < 0 || distance > BlastRadius ? 0
        : BlastRadius <= 0 ? BlastCenterDamage : BlastCenterDamage + (BlastEdgeDamage - BlastCenterDamage) * (distance / BlastRadius);
}

/// <summary>
/// A shooter's report: its bullet <paramref name="ShotId"/> (the shooter's own sequence) hit a victim at
/// <paramref name="At"/> on the mode clock. Both lives are as the shooter saw them.
/// </summary>
public sealed record BulletHitReported(int ShooterId, uint ShooterLife, uint ShotId, int VictimId, uint VictimLife, double At) : AcceptedEvent;

/// <summary>
/// A victim's report: blast <paramref name="BlastId"/> threw it from <paramref name="Distance"/> meters. The blast must
/// be accepted (<see cref="BlastAccepted"/>); until then the report waits, as the server's causal gate holds it.
/// </summary>
public sealed record BlastHitReported(int VictimId, uint VictimLife, uint BlastId, double Distance) : AcceptedEvent;

/// <summary>
/// The world accepted an explosion: when, and the chain's first detonator (zero when nobody set it off). A runner
/// sends it before the victim reports that name it, or the reports wait for it.
/// </summary>
public sealed record BlastAccepted(uint BlastId, int DetonatorId, double At) : AcceptedEvent;

/// <summary>The world refused an explosion: reports naming it are refused.</summary>
public sealed record BlastRefused(uint BlastId) : AcceptedEvent;

/// <summary>Damage to this life is ignored until <paramref name="Until"/> (spawn protection, for instance).</summary>
public sealed record InvulnerabilityGranted(int ActorId, uint LifeId, double Until) : AcceptedEvent;

/// <summary>Why a damage report changed nothing.</summary>
public enum DamageRefusal : byte
{
    Duplicate, Late, Future, Closed, UnknownAttacker, AttackerLife, UnknownVictim, VictimLife, Dead, SelfHit, Invulnerable,
    UnknownBlast, OutOfRange,
}

/// <summary>A report was accepted: this life takes the damage at <paramref name="At"/> (before its death, if any).</summary>
public sealed record DamageApplied(int VictimId, uint LifeId, DamageKind Kind, int AttackerId, double Amount, double At) : ModeCommand;

/// <summary>A report was refused; <paramref name="ReporterId"/> sent it.</summary>
public sealed record DamageRefused(int ReporterId, DamageKind Kind, DamageRefusal Reason) : ModeCommand;

/// <summary>
/// A life ended at <paramref name="At"/>. <paramref name="KilledBy"/> dealt the last damage (zero: nobody);
/// <paramref name="CreditedTo"/> is the last other actor to damage it within the credit window, or zero.
/// </summary>
public sealed record ActorDied(int VictimId, uint LifeId, DamageKind Kind, int KilledBy, int CreditedTo, double At) : ModeCommand;

/// <summary>An actor starts a new life at full health. Life ids only advance.</summary>
public sealed record ActorRespawned(int ActorId, uint LifeId) : ModeCommand;

/// <summary>
/// Health for every actor, pure. Accepted damage is kept per life and replayed in event-time order, so the order in
/// which reports arrive never matters: a report older than the window is refused, so once the window has passed a
/// moment everything before it is known, and a death there is declared and final. Until then a life's health reads
/// what has arrived (a HUD may show zero a moment before the death is declared). Damage by an attacker who was
/// already dead by then, or to a victim already dead, counts for nothing in the replay.
/// </summary>
public sealed class HealthState
{
    private sealed class Life
    {
        public required int Actor;
        public required uint Id;
        public required double Start;
        /// <summary>Declared death, or departure: the life takes and deals nothing from here.</summary>
        public double End = double.PositiveInfinity;
        public bool Died;
        /// <summary>Its round is over: no death is declared for it any more.</summary>
        public bool Closed;
        public bool Down;
        public readonly List<(double From, double Until)> Shields = new();
        public bool Shielded(double at) => Shields.Any(s => at >= s.From && at < s.Until);
    }

    private readonly record struct Hit(double At, DamageKind Kind, int Attacker, uint AttackerLife, ulong Key, int Victim, uint VictimLife, double Amount);

    private readonly record struct Outcome(double Health, double At, double LastHit, Hit? Fatal, int Credit);

    private readonly Dictionary<int, uint> current = new();
    private readonly Dictionary<(int, uint), Life> lives = new();
    private readonly List<Hit> hits = new();
    private readonly HashSet<(int, ulong)> seenShots = new();
    private readonly HashSet<(int, uint)> seenBlasts = new();
    private readonly Dictionary<uint, (int Detonator, double At)> blasts = new();
    private readonly HashSet<uint> refusedBlasts = new();
    private readonly List<(BlastHitReported Report, double Arrived)> waiting = new();
    private readonly List<ActorDied> deaths = new();
    private readonly Dictionary<DamageRefusal, int> refusals = new();
    private Dictionary<(int, uint), Outcome> replay = new();
    private bool dirty;
    private double openedAt = double.NegativeInfinity;
    private double closedAt = double.PositiveInfinity;

    public HealthRules Rules { get; set; }
    /// <summary>Deaths declared so far, in order.</summary>
    public IReadOnlyList<ActorDied> Deaths => deaths;
    public int Accepted { get; private set; }
    public int Refused(DamageRefusal reason) => refusals.GetValueOrDefault(reason);
    public int RefusedTotal => refusals.Values.Sum();
    /// <summary>Blast reports waiting for their blast.</summary>
    public int Waiting => waiting.Count;

    public HealthState(HealthRules rules) => Rules = rules;

    public uint LifeOf(int actor) => current.GetValueOrDefault(actor);
    /// <summary>The actor has a life that has not ended (a provisionally dead one still counts until declared).</summary>
    public bool IsAlive(int actor) => Current(actor) is { Died: false } life && double.IsPositiveInfinity(life.End);
    public bool IsDown(int actor) => Current(actor)?.Down ?? false;
    public bool Invulnerable(int actor, double at) => Current(actor) is { } life && life.Shielded(at);

    /// <summary>Health now, from everything that has arrived: zero once dead or provisionally dead.</summary>
    public double Health(int actor, double now)
    {
        if (Current(actor) is not { } life || life.Died || !double.IsPositiveInfinity(life.End)) return 0;
        if (life.Closed) now = Math.Min(now, closedAt);
        var outcome = Replayed(life);
        return outcome.Fatal != null ? 0 : Regenerate(outcome.Health, outcome.At, now, outcome.LastHit);
    }

    private Life? Current(int actor) => current.TryGetValue(actor, out var id) ? lives[(actor, id)] : null;

    /// <summary>
    /// A round starts at <paramref name="now"/>: everyone listed respawns, intake opens, and damage from before it is
    /// refused as closed. Report and blast ids belong to their round, so a runner may number each round from one.
    /// </summary>
    public IReadOnlyList<ModeCommand> BeginRound(IEnumerable<int> actors, double now)
    {
        foreach (var life in lives.Values) life.Closed = true;
        hits.Clear(); seenShots.Clear(); seenBlasts.Clear(); blasts.Clear(); refusedBlasts.Clear(); waiting.Clear();
        openedAt = now;
        closedAt = double.PositiveInfinity;
        return Respawn(actors, now);
    }

    /// <summary>A new life for each actor at full health from <paramref name="now"/>; life ids only advance.</summary>
    public IReadOnlyList<ModeCommand> Respawn(IEnumerable<int> actors, double now)
    {
        var commands = new List<ModeCommand>();
        foreach (var actor in actors)
        {
            if (Current(actor) is { } previous)
            {
                previous.Closed = true;
                if (double.IsPositiveInfinity(previous.End)) previous.End = now;
            }
            var id = checked(LifeOf(actor) + 1);
            current[actor] = id;
            lives[(actor, id)] = new Life { Actor = actor, Id = id, Start = now };
            commands.Add(new ActorRespawned(actor, id));
        }
        dirty = true;
        return commands;
    }

    /// <summary>
    /// Closes intake: the round ended. Nothing more is accepted, and a death the window has not yet passed is never
    /// declared, so how a round ends never depends on which late reports happened to arrive.
    /// </summary>
    public void Close(double now)
    {
        openedAt = double.PositiveInfinity;
        closedAt = now;
        waiting.Clear();
        foreach (var life in lives.Values) life.Closed = true;
    }

    public IReadOnlyList<ModeCommand> Handle(AcceptedEvent accepted, ModeClock clock)
    {
        var commands = new List<ModeCommand>();
        switch (accepted)
        {
            case BulletHitReported shot:
                commands.Add(Bullet(shot, clock));
                break;
            case BlastAccepted blast:
                if (!blasts.ContainsKey(blast.BlastId) && !refusedBlasts.Contains(blast.BlastId))
                    blasts[blast.BlastId] = (blast.DetonatorId, blast.At);
                foreach (var held in waiting.Where(w => w.Report.BlastId == blast.BlastId).ToArray())
                {
                    waiting.Remove(held);
                    commands.Add(Blast(held.Report, clock));
                }
                break;
            case BlastRefused refused:
                if (!blasts.ContainsKey(refused.BlastId)) refusedBlasts.Add(refused.BlastId);
                foreach (var held in waiting.Where(w => w.Report.BlastId == refused.BlastId).ToArray())
                {
                    waiting.Remove(held);
                    seenBlasts.Add((held.Report.VictimId, held.Report.BlastId));
                    commands.Add(Refuse(held.Report.VictimId, DamageKind.Blast, DamageRefusal.UnknownBlast));
                }
                break;
            case BlastHitReported report:
                if (blasts.ContainsKey(report.BlastId) || refusedBlasts.Contains(report.BlastId) || seenBlasts.Contains((report.VictimId, report.BlastId)))
                    commands.Add(Blast(report, clock));
                else if (waiting.Any(w => w.Report.VictimId == report.VictimId && w.Report.BlastId == report.BlastId))
                    commands.Add(Refuse(report.VictimId, DamageKind.Blast, DamageRefusal.Duplicate));
                else
                    waiting.Add((report, clock.Now));
                break;
            case InvulnerabilityGranted granted:
                if (lives.TryGetValue((granted.ActorId, granted.LifeId), out var shielded) && granted.Until > clock.Now)
                    shielded.Shields.Add((clock.Now, granted.Until));
                break;
            case ActorsSampled sampled:
                Sample(sampled, clock);
                break;
            case ActorLeft left:
                if (Current(left.ActorId) is { } leaving && !leaving.Died && double.IsPositiveInfinity(leaving.End))
                {
                    leaving.End = clock.Now;
                    dirty = true;
                }
                break;
        }
        foreach (var held in waiting.Where(w => clock.Now - w.Arrived > Rules.ReportWindow).ToArray())
        {
            // The causal hold ends with the window: a blast never accepted by then is unknown.
            waiting.Remove(held);
            seenBlasts.Add((held.Report.VictimId, held.Report.BlastId));
            commands.Add(Refuse(held.Report.VictimId, DamageKind.Blast, DamageRefusal.UnknownBlast));
        }
        commands.AddRange(Settle(clock));
        return commands;
    }

    private void Sample(ActorsSampled sampled, ModeClock clock)
    {
        foreach (var sample in sampled.Samples)
        {
            if (Current(sample.ActorId) is not { Died: false } life || !double.IsPositiveInfinity(life.End)) continue;
            // Getting up opens the grace: the body is back under its player's control.
            if (life.Down && !sample.Down && Rules.GetUpGrace > 0) life.Shields.Add((clock.Now, clock.Now + Rules.GetUpGrace));
            life.Down = sample.Down;
        }
    }

    // ------------------------------------------------------------------ Reports

    private ModeCommand Bullet(BulletHitReported shot, ModeClock clock)
    {
        if (!seenShots.Add((shot.ShooterId, shot.ShotId))) return Refuse(shot.ShooterId, DamageKind.Bullet, DamageRefusal.Duplicate);
        if (shot.ShooterId == shot.VictimId) return Refuse(shot.ShooterId, DamageKind.Bullet, DamageRefusal.SelfHit);
        if (!lives.TryGetValue((shot.ShooterId, shot.ShooterLife), out var shooter))
            return Refuse(shot.ShooterId, DamageKind.Bullet, current.ContainsKey(shot.ShooterId) ? DamageRefusal.AttackerLife : DamageRefusal.UnknownAttacker);
        if (shot.At < shooter.Start || shot.At >= shooter.End) return Refuse(shot.ShooterId, DamageKind.Bullet, DamageRefusal.AttackerLife);
        return Damage(shot.ShooterId, DamageKind.Bullet, shot.At, shot.ShooterId, shot.ShooterLife, shot.VictimId, shot.VictimLife, Rules.BulletDamage,
            (ulong)shot.ShotId, clock);
    }

    private ModeCommand Blast(BlastHitReported report, ModeClock clock)
    {
        if (!seenBlasts.Add((report.VictimId, report.BlastId))) return Refuse(report.VictimId, DamageKind.Blast, DamageRefusal.Duplicate);
        if (!blasts.TryGetValue(report.BlastId, out var blast)) return Refuse(report.VictimId, DamageKind.Blast, DamageRefusal.UnknownBlast);
        var amount = Rules.BlastDamage(report.Distance);
        if (amount <= 0) return Refuse(report.VictimId, DamageKind.Blast, DamageRefusal.OutOfRange);
        var attackerLife = 0u;
        if (blast.Detonator != 0)
        {
            if (!current.ContainsKey(blast.Detonator)) return Refuse(report.VictimId, DamageKind.Blast, DamageRefusal.UnknownAttacker);
            // A chain outlives its detonator: the blast is credited to the life that set it off, whenever it goes up.
            attackerLife = lives.Values.Where(l => l.Actor == blast.Detonator && l.Start <= blast.At).Select(l => l.Id).DefaultIfEmpty(LifeOf(blast.Detonator)).Max();
        }
        return Damage(report.VictimId, DamageKind.Blast, blast.At, blast.Detonator, attackerLife, report.VictimId, report.VictimLife, amount,
            (1UL << 32) | report.BlastId, clock);
    }

    private ModeCommand Damage(int reporter, DamageKind kind, double at, int attacker, uint attackerLife, int victimId, uint victimLife,
        double amount, ulong key, ModeClock clock)
    {
        if (!double.IsFinite(at) || at > clock.Now) return Refuse(reporter, kind, DamageRefusal.Future);
        if (clock.Now - at > Rules.ReportWindow) return Refuse(reporter, kind, DamageRefusal.Late);
        if (clock.Phase != ModePhase.Round || at < openedAt) return Refuse(reporter, kind, DamageRefusal.Closed);
        if (!current.ContainsKey(victimId)) return Refuse(reporter, kind, DamageRefusal.UnknownVictim);
        if (!lives.TryGetValue((victimId, victimLife), out var victim) || at < victim.Start) return Refuse(reporter, kind, DamageRefusal.VictimLife);
        if (at >= victim.End) return Refuse(reporter, kind, victim.Died && victim.Id == LifeOf(victimId) ? DamageRefusal.Dead : DamageRefusal.VictimLife);
        if (victim.Shielded(at)) return Refuse(reporter, kind, DamageRefusal.Invulnerable);
        hits.Add(new Hit(at, kind, attacker, attackerLife, key, victimId, victimLife, amount));
        Accepted++;
        dirty = true;
        return new DamageApplied(victimId, victimLife, kind, attacker, amount, at);
    }

    private DamageRefused Refuse(int reporter, DamageKind kind, DamageRefusal reason)
    {
        refusals[reason] = refusals.GetValueOrDefault(reason) + 1;
        return new DamageRefused(reporter, kind, reason);
    }

    // ------------------------------------------------------------------ Replay

    /// <summary>
    /// Every accepted hit in event-time order (ties broken by content, never arrival). A hit counts while its victim
    /// lives and its attacker had not died before it; a life whose health reaches zero dies at that hit.
    /// </summary>
    private Dictionary<(int, uint), Outcome> Replay()
    {
        var health = new Dictionary<(int, uint), Outcome>();
        Outcome State(Life life) => health.TryGetValue((life.Actor, life.Id), out var state) ? state
            : new Outcome(Rules.MaxHealth, life.Start, double.NegativeInfinity, null, 0);
        var lastOther = new Dictionary<(int, uint), (int Actor, double At)>();
        foreach (var hit in hits.OrderBy(h => h.At).ThenBy(h => h.Kind).ThenBy(h => h.Attacker).ThenBy(h => h.Key).ThenBy(h => h.Victim))
        {
            var life = lives[(hit.Victim, hit.VictimLife)];
            var state = State(life);
            if (state.Fatal != null || hit.At >= life.End && !life.Died) continue;
            if (hit.Kind == DamageKind.Bullet && lives.TryGetValue((hit.Attacker, hit.AttackerLife), out var shooter) &&
                (health.TryGetValue((shooter.Actor, shooter.Id), out var theirs) && theirs.Fatal is { } fatal && fatal.At < hit.At ||
                 !shooter.Died && hit.At >= shooter.End))
            {
                continue; // A shot fired after its shooter died or left. A chain outlives its detonator.
            }
            var value = Regenerate(state.Health, state.At, hit.At, state.LastHit) - hit.Amount;
            if (hit.Attacker != 0 && hit.Attacker != hit.Victim) lastOther[(life.Actor, life.Id)] = (hit.Attacker, hit.At);
            var credit = lastOther.TryGetValue((life.Actor, life.Id), out var other) && hit.At - other.At <= Rules.CreditWindow ? other.Actor : 0;
            health[(life.Actor, life.Id)] = new Outcome(Math.Max(0, value), hit.At, hit.At, value <= 0 ? hit : null, credit);
        }
        return health;
    }

    private Outcome Replayed(Life life)
    {
        if (dirty) { replay = Replay(); dirty = false; }
        return replay.TryGetValue((life.Actor, life.Id), out var outcome) ? outcome : new Outcome(Rules.MaxHealth, life.Start, double.NegativeInfinity, null, 0);
    }

    /// <summary>Health after regenerating from <paramref name="from"/> to <paramref name="to"/>, given the last hit.</summary>
    private double Regenerate(double value, double from, double to, double lastHit)
    {
        if (Rules.RegenPerSecond <= 0 || to <= from) return Math.Min(value, Rules.MaxHealth);
        var begins = Math.Max(from, lastHit + Rules.RegenDelay);
        return to <= begins ? value : Math.Min(Rules.MaxHealth, value + (to - begins) * Rules.RegenPerSecond);
    }

    /// <summary>Declares every death the window has passed, in event-time order.</summary>
    private IEnumerable<ModeCommand> Settle(ModeClock clock)
    {
        if (dirty) { replay = Replay(); dirty = false; }
        var settled = clock.Now - Rules.ReportWindow;
        var due = replay.Where(r => r.Value.Fatal is { } fatal && fatal.At < settled && lives[r.Key] is { Died: false, Closed: false })
            .OrderBy(r => r.Value.Fatal!.Value.At).ThenBy(r => r.Key.Item1).ToArray();
        if (due.Length == 0) return Array.Empty<ModeCommand>();
        var declared = new List<ModeCommand>();
        foreach (var (key, outcome) in due)
        {
            var life = lives[key];
            var fatal = outcome.Fatal!.Value;
            life.Died = true;
            life.End = fatal.At;
            var died = new ActorDied(life.Actor, life.Id, fatal.Kind, fatal.Attacker, outcome.Credit, fatal.At);
            deaths.Add(died);
            declared.Add(died);
        }
        return declared;
    }

    /// <summary>One audit line for server logs and tests.</summary>
    public string Audit() => string.Join(" ", new[] { $"health audit accepted={Accepted} refused={RefusedTotal}" }
        .Concat(refusals.OrderBy(r => r.Key).Select(r => $"{r.Key}={r.Value}")).Append($"waiting={waiting.Count} deaths={deaths.Count}"));
}
