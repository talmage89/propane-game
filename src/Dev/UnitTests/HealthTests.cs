using System;
using System.Collections.Generic;
using System.Linq;
using Propane.Modes;

namespace Propane.Dev.UnitTests;

/// <summary>
/// The optional health state and Short Fuse, headless: damage typing and falloff, attribution and chain credit,
/// regeneration, the get-up grace and invulnerability, deaths during a ragdoll and departures, report validation, and
/// the same reports delivered with drops, duplicates, reordering and late arrivals giving the same outcome.
/// </summary>
[UnitSuite]
public static class HealthTests
{
    private static readonly HealthRules Starting = new(MaxHealth: 100, BulletDamage: 7, BlastCenterDamage: 90, BlastEdgeDamage: 30,
        BlastRadius: 6.5, RegenPerSecond: 5, RegenDelay: 5, GetUpGrace: 0.75, CreditWindow: 8, ReportWindow: 0.25);

    private static ModeClock At(double now) => new(ModePhase.Round, 1, now, double.PositiveInfinity);

    private static HealthState Fresh(HealthRules? rules = null, int actors = 3, double start = 0)
    {
        var health = new HealthState(rules ?? Starting);
        health.BeginRound(Enumerable.Range(1, actors), start);
        return health;
    }

    private static BulletHitReported Shot(HealthState health, int shooter, int victim, uint id, double at) =>
        new(shooter, health.LifeOf(shooter), id, victim, health.LifeOf(victim), at);

    private static IReadOnlyList<ModeCommand> Blast(HealthState health, uint id, int detonator, double at, params (int Victim, double Distance)[] victims)
    {
        var commands = new List<ModeCommand>(health.Handle(new BlastAccepted(id, detonator, at), At(at)));
        foreach (var (victim, distance) in victims)
            commands.AddRange(health.Handle(new BlastHitReported(victim, health.LifeOf(victim), id, distance), At(at)));
        return commands;
    }

    /// <summary>Advances the clock with empty samples, as a runner's every step does, and returns the deaths it declared.</summary>
    private static List<ActorDied> Tick(HealthState health, double until, double from = 0, double step = 1 / 60.0)
    {
        var deaths = new List<ActorDied>();
        for (var t = from; t <= until + 1e-9; t += step)
            deaths.AddRange(health.Handle(new ActorsSampled(Array.Empty<ActorSample>()), At(t)).OfType<ActorDied>());
        return deaths;
    }

    private static DamageRefusal? Refusal(IReadOnlyList<ModeCommand> commands) => commands.OfType<DamageRefused>().FirstOrDefault()?.Reason;

    private static readonly ModeTuners FuseTuners = new(new Dictionary<string, double>
    {
        [ShortFuseRules.ShortFuseRoundSeconds] = 105,
        [ShortFuseRules.ShortFuseWinsToMatch] = 3,
        [ShortFuseRules.ShortFuseMaxHealth] = 100,
        [ShortFuseRules.ShortFuseBulletDamage] = 7,
        [ShortFuseRules.ShortFuseBlastCenterDamage] = 90,
        [ShortFuseRules.ShortFuseBlastEdgeDamage] = 30,
        [ShortFuseRules.RagdollRadius] = 6.5,
        [ShortFuseRules.ShortFuseRegenPerSecond] = 5,
        [ShortFuseRules.ShortFuseRegenDelay] = 5,
        [ShortFuseRules.ShortFuseGetUpGrace] = 0.75,
        [ShortFuseRules.ShortFuseCreditSeconds] = 8,
        [ShortFuseRules.ShortFuseReportWindow] = 0.25,
    });

    private static ShortFuseRules Fuse(int actors, double start = 10)
    {
        var rules = new ShortFuseRules();
        rules.Initialize(new ModeSetup(Enumerable.Range(1, actors).Select(i => new ModeActor(i, $"P{i}")).ToArray(),
            new ModeWorld(ModeWorldSource.SuburbPlan), FuseTuners, 5));
        rules.Handle(new PhaseEntered(ModePhase.Round, new RoundInfo(1, 1, RoundTimer.Countdown, start, 105)), new ModeClock(ModePhase.Round, 1, start, start + 105));
        return rules;
    }

    private static ModeClock FuseAt(double now, uint round = 1) => new(ModePhase.Round, round, now, double.PositiveInfinity);

    /// <summary>Kills <paramref name="victim"/> with a blast at the center and two bullets, then lets the window pass.</summary>
    private static List<ModeCommand> Kill(ShortFuseRules rules, int killer, int victim, uint blast, double at)
    {
        var commands = new List<ModeCommand>();
        commands.AddRange(rules.Handle(new BlastAccepted(blast, killer, at), FuseAt(at)));
        commands.AddRange(rules.Handle(new BlastHitReported(victim, rules.Health.LifeOf(victim), blast, 0), FuseAt(at)));
        for (uint shot = 0; shot < 2; shot++)
        {
            var when = at + 0.1 * (shot + 1);
            commands.AddRange(rules.Handle(new BulletHitReported(killer, rules.Health.LifeOf(killer), blast * 100 + shot, victim, rules.Health.LifeOf(victim), when), FuseAt(when)));
        }
        commands.AddRange(rules.Handle(new ActorsSampled(Array.Empty<ActorSample>()), FuseAt(at + 0.6)));
        return commands;
    }

    public static void Register(TestRunner runner)
    {
        runner.Run("health/damage is typed: bullets deal a flat amount, blasts fall off linearly to the throw radius and stop there", () =>
        {
            TestRunner.Assert(Starting.BlastDamage(0) == 90 && Starting.BlastDamage(6.5) == 30 && Math.Abs(Starting.BlastDamage(3.25) - 60) < 1e-9,
                "blast falloff from 90 at the center to 30 at the edge");
            TestRunner.Assert(Starting.BlastDamage(6.51) == 0 && Starting.BlastDamage(-1) == 0 && Starting.BlastDamage(double.NaN) == 0, "none beyond the edge");
            var health = Fresh();
            var shot = health.Handle(Shot(health, 1, 2, 1, 1), At(1));
            TestRunner.Assert(shot.SequenceEqual(new ModeCommand[] { new DamageApplied(2, 1, DamageKind.Bullet, 1, 7, 1) }), $"bullet: {string.Join("; ", shot)}");
            var blast = Blast(health, 1, 3, 2, (2, 3.25));
            TestRunner.Assert(blast.OfType<DamageApplied>().Single() is { Kind: DamageKind.Blast, AttackerId: 3, Amount: 60, At: 2 }, $"blast: {string.Join("; ", blast)}");
            TestRunner.Assert(Math.Abs(health.Health(2, 2) - 33) < 1e-9, $"health {health.Health(2, 2)}");
            TestRunner.Assert(Refusal(Blast(health, 2, 3, 3, (2, 7))) == DamageRefusal.OutOfRange, "a report from beyond the throw radius");
        });

        runner.Run("health/kill credit goes to the last other damager within the window, and a chain to its first detonator", () =>
        {
            var noRegen = Starting with { RegenPerSecond = 0 };
            var health = Fresh(noRegen);
            health.Handle(Shot(health, 1, 2, 1, 1), At(1));
            health.Handle(Shot(health, 1, 2, 2, 1.1), At(1.1));
            Blast(health, 1, 2, 5, (2, 0)); // Their own blast finishes them: 86 - 90.
            var died = Tick(health, 6, 5);
            TestRunner.Assert(died.SequenceEqual(new[] { new ActorDied(2, 1, DamageKind.Blast, 2, 1, 5) }), $"assisted self-kill: {string.Join("; ", died)}");

            health = Fresh(noRegen);
            health.Handle(Shot(health, 1, 2, 1, 1), At(1));
            health.Handle(Shot(health, 1, 2, 2, 1.1), At(1.1));
            Blast(health, 1, 2, 9.2, (2, 0));
            died = Tick(health, 10, 9.2);
            TestRunner.Assert(died.Single() is { KilledBy: 2, CreditedTo: 0 }, $"outside the window: {string.Join("; ", died)}");

            // A chain: player 3 set off the first tank; the blast that kills goes up later, after 3 itself died.
            health = Fresh(noRegen);
            Blast(health, 1, 1, 1, (3, 0));
            health.Handle(Shot(health, 1, 3, 1, 1.2), At(1.2));
            health.Handle(Shot(health, 1, 3, 2, 1.3), At(1.3));
            TestRunner.Assert(Tick(health, 2, 1.3).Single() is { VictimId: 3, CreditedTo: 1 }, "the detonator must die first");
            Blast(health, 2, 3, 2.5, (2, 0));
            Blast(health, 3, 3, 3, (2, 4.875));
            TestRunner.Assert(Math.Abs(health.Health(2, 3) - 0) < 1e-9 && health.IsAlive(2), "provisionally dead until the window passes");
            died = Tick(health, 4, 3);
            TestRunner.Assert(died.Single() is { VictimId: 2, Kind: DamageKind.Blast, KilledBy: 3, CreditedTo: 3, At: 3 }, $"chain credit: {string.Join("; ", died)}");
        });

        runner.Run("health/regenerates after the delay unhit, capped at full, and not at all when disabled", () =>
        {
            var health = Fresh();
            Blast(health, 1, 3, 0, (2, 0)); // 10 left.
            TestRunner.Assert(health.Health(2, 4.9) == 10 && health.Health(2, 5) == 10 && Math.Abs(health.Health(2, 6) - 15) < 1e-9, "5 HP/s after 5 s");
            TestRunner.Assert(health.Health(2, 30) == 100, "capped at full");
            health.Handle(Shot(health, 1, 2, 1, 6), At(6)); // A hit restarts the delay: 15 - 7 = 8.
            TestRunner.Assert(Math.Abs(health.Health(2, 10.9) - 8) < 1e-9 && Math.Abs(health.Health(2, 12) - 13) < 1e-9, "a hit restarts the delay");
            var off = Fresh(Starting with { RegenPerSecond = 0 });
            Blast(off, 1, 3, 0, (2, 0));
            TestRunner.Assert(off.Health(2, 100) == 10, "zero disables regeneration");
        });

        runner.Run("health/getting up opens a grace, and granted invulnerability holds until its end", () =>
        {
            var health = Fresh();
            health.Handle(new ActorsSampled(new[] { new ActorSample(2, 0, 0, 0, Down: true) }), At(1));
            TestRunner.Assert(Refusal(health.Handle(Shot(health, 1, 2, 1, 1.5), At(1.5))) == null, "a downed body takes damage");
            health.Handle(new ActorsSampled(new[] { new ActorSample(2, 0, 0, 0, Down: false) }), At(2));
            TestRunner.Assert(health.Invulnerable(2, 2.5) && Refusal(health.Handle(Shot(health, 1, 2, 2, 2.5), At(2.5))) == DamageRefusal.Invulnerable,
                "within the grace");
            TestRunner.Assert(Refusal(Blast(health, 1, 3, 2.7, (2, 0))) == DamageRefusal.Invulnerable, "blasts too");
            TestRunner.Assert(Refusal(health.Handle(Shot(health, 1, 2, 3, 2.76), At(2.76))) == null, "the grace ends after 0.75 s");
            var none = Fresh(Starting with { GetUpGrace = 0 });
            none.Handle(new ActorsSampled(new[] { new ActorSample(2, 0, 0, 0, Down: true) }), At(1));
            none.Handle(new ActorsSampled(new[] { new ActorSample(2, 0, 0, 0, Down: false) }), At(2));
            TestRunner.Assert(Refusal(none.Handle(Shot(none, 1, 2, 1, 2), At(2))) == null, "a zero grace protects nobody");
            health.Handle(new InvulnerabilityGranted(3, health.LifeOf(3), 5), At(3));
            TestRunner.Assert(Refusal(health.Handle(Shot(health, 1, 3, 4, 4.9), At(4.9))) == DamageRefusal.Invulnerable &&
                Refusal(health.Handle(Shot(health, 1, 3, 5, 5), At(5))) == null, "granted invulnerability");
        });

        runner.Run("health/a body dies during its ragdoll, stays dead when it would get up, and takes nothing more", () =>
        {
            var health = Fresh();
            health.Handle(new ActorsSampled(new[] { new ActorSample(2, 0, 0, 0, Down: true) }), At(1));
            Blast(health, 1, 1, 1, (2, 0));
            health.Handle(Shot(health, 1, 2, 1, 1.4), At(1.4));
            health.Handle(Shot(health, 1, 2, 2, 1.5), At(1.5));
            var died = Tick(health, 2, 1.5);
            TestRunner.Assert(died.Single() is { VictimId: 2, Kind: DamageKind.Bullet, KilledBy: 1, CreditedTo: 1, At: 1.5 }, $"death: {string.Join("; ", died)}");
            health.Handle(new ActorsSampled(new[] { new ActorSample(2, 0, 0, 0, Down: false) }), At(3));
            TestRunner.Assert(!health.IsAlive(2) && health.Health(2, 3) == 0 && !health.Invulnerable(2, 3.1), "no revival or grace");
            TestRunner.Assert(Refusal(health.Handle(Shot(health, 1, 2, 3, 3), At(3))) == DamageRefusal.Dead, "a dead body takes nothing");
            TestRunner.Assert(Refusal(health.Handle(Shot(health, 2, 1, 1, 3), At(3))) == DamageRefusal.AttackerLife, "a dead shooter deals nothing");
            var again = health.Respawn(new[] { 2 }, 4);
            TestRunner.Assert(again.SequenceEqual(new ModeCommand[] { new ActorRespawned(2, 2) }) && health.Health(2, 4) == 100, "a respawn advances the life id");
            TestRunner.Assert(Refusal(health.Handle(new BulletHitReported(1, health.LifeOf(1), 9, 2, 1, 4.1), At(4.1))) == DamageRefusal.VictimLife,
                "a report naming the old life");
        });

        runner.Run("health/departures: a dead actor leaving changes nothing; a living one ends its life without a death", () =>
        {
            var health = Fresh();
            Blast(health, 1, 1, 1, (2, 0));
            health.Handle(Shot(health, 1, 2, 1, 1.1), At(1.1));
            health.Handle(Shot(health, 1, 2, 2, 1.2), At(1.2));
            TestRunner.Assert(Tick(health, 2, 1.2).Count == 1, "the death");
            TestRunner.Assert(health.Handle(new ActorLeft(2), At(2)).Count == 0 && health.Deaths.Count == 1, "departure while dead");
            TestRunner.Assert(Refusal(health.Handle(Shot(health, 1, 2, 3, 2.1), At(2.1))) == DamageRefusal.Dead, "still dead after leaving");
            TestRunner.Assert(health.Handle(new ActorLeft(3), At(3)).Count == 0 && !health.IsAlive(3) && health.Deaths.Count == 1, "departure while alive");
            TestRunner.Assert(Refusal(health.Handle(Shot(health, 1, 3, 4, 3.1), At(3.1))) == DamageRefusal.VictimLife, "the departed take nothing");
            TestRunner.Assert(Refusal(health.Handle(Shot(health, 3, 1, 1, 3.1), At(3.1))) == DamageRefusal.AttackerLife, "nor deal it");
            TestRunner.Assert(Refusal(health.Handle(Shot(health, 3, 1, 2, 2.9), At(3.1))) == null, "a shot from before they left still lands");
            // Lethal before leaving, declared after: the death stands.
            var late = Fresh();
            Blast(late, 1, 1, 1, (2, 0));
            late.Handle(Shot(late, 1, 2, 1, 1.1), At(1.1));
            late.Handle(Shot(late, 1, 2, 2, 1.2), At(1.2));
            late.Handle(new ActorLeft(2), At(1.3));
            TestRunner.Assert(Tick(late, 2, 1.3).Single() is { VictimId: 2, At: 1.2 }, "a lethal hit before leaving");
        });

        runner.Run("health/reports are validated: duplicates, late, future, closed, unknown or stale actors, self hits and unknown blasts", () =>
        {
            var health = Fresh();
            TestRunner.Assert(Refusal(health.Handle(Shot(health, 1, 2, 1, 1), At(1))) == null, "first");
            TestRunner.Assert(Refusal(health.Handle(Shot(health, 1, 2, 1, 1), At(1.1))) == DamageRefusal.Duplicate, "the same shot twice");
            TestRunner.Assert(Refusal(health.Handle(Shot(health, 1, 2, 2, 1), At(1.3))) == DamageRefusal.Late, "older than the window");
            TestRunner.Assert(Refusal(health.Handle(Shot(health, 1, 2, 3, 2), At(1.5))) == DamageRefusal.Future, "from the future");
            TestRunner.Assert(Refusal(health.Handle(Shot(health, 1, 2, 4, 1.1), new ModeClock(ModePhase.RoundEnd, 1, 1.2, 1.2))) == DamageRefusal.Closed, "outside a round");
            TestRunner.Assert(Refusal(health.Handle(new BulletHitReported(9, 1, 1, 2, 1, 2), At(2))) == DamageRefusal.UnknownAttacker, "unknown shooter");
            TestRunner.Assert(Refusal(health.Handle(new BulletHitReported(1, 7, 5, 2, 1, 2), At(2))) == DamageRefusal.AttackerLife, "shooter's stale life");
            TestRunner.Assert(Refusal(health.Handle(new BulletHitReported(1, 1, 6, 9, 1, 2), At(2))) == DamageRefusal.UnknownVictim, "unknown victim");
            TestRunner.Assert(Refusal(health.Handle(new BulletHitReported(1, 1, 7, 2, 4, 2), At(2))) == DamageRefusal.VictimLife, "victim's stale life");
            TestRunner.Assert(Refusal(health.Handle(new BulletHitReported(1, 1, 8, 1, 1, 2), At(2))) == DamageRefusal.SelfHit, "self hit");
            // A victim's report waits for its blast (the causal gate), and fails when it never comes.
            TestRunner.Assert(health.Handle(new BlastHitReported(2, 1, 5, 1), At(3)).Count == 0 && health.Waiting == 1, "held for its blast");
            TestRunner.Assert(Refusal(health.Handle(new BlastHitReported(2, 1, 5, 1), At(3))) == DamageRefusal.Duplicate, "a held duplicate");
            var applied = health.Handle(new BlastAccepted(5, 3, 3.05), At(3.1));
            TestRunner.Assert(applied.OfType<DamageApplied>().Single() is { At: 3.05, AttackerId: 3 } && health.Waiting == 0, "released by the blast");
            health.Handle(new BlastHitReported(3, 1, 6, 1), At(4));
            TestRunner.Assert(Refusal(health.Handle(new ActorsSampled(Array.Empty<ActorSample>()), At(4.3))) == DamageRefusal.UnknownBlast, "expires");
            health.Handle(new BlastHitReported(3, 1, 7, 1), At(5));
            TestRunner.Assert(Refusal(health.Handle(new BlastRefused(7), At(5))) == DamageRefusal.UnknownBlast, "a refused blast");
            TestRunner.Assert(Refusal(health.Handle(new BlastHitReported(3, 1, 7, 1), At(5.1))) == DamageRefusal.Duplicate, "and refused once");
            health.Handle(new BlastAccepted(8, 3, 5), At(5));
            TestRunner.Assert(Refusal(health.Handle(new BlastHitReported(1, 1, 8, 1), At(5.3))) == DamageRefusal.Late, "a blast report after the window");
            health.Close(6);
            TestRunner.Assert(Refusal(health.Handle(Shot(health, 1, 2, 9, 6), At(6))) == DamageRefusal.Closed, "after the round");
            TestRunner.Assert(health.Audit().StartsWith("health audit accepted=2 ", StringComparison.Ordinal), health.Audit());
        });

        runner.Run("health/a death is declared once the window has passed it, and never for a round already closed", () =>
        {
            var health = Fresh();
            Blast(health, 1, 1, 1, (2, 0));
            health.Handle(Shot(health, 1, 2, 1, 1.1), At(1.1));
            health.Handle(Shot(health, 1, 2, 2, 1.2), At(1.2));
            TestRunner.Assert(health.Health(2, 1.2) == 0 && health.IsAlive(2), "provisional");
            TestRunner.Assert(Tick(health, 1.45, 1.2).Count == 0, "not before the window");
            TestRunner.Assert(Tick(health, 1.5, 1.46).Single().At == 1.2, "after it");
            var closed = Fresh();
            Blast(closed, 1, 1, 1, (2, 0));
            closed.Handle(Shot(closed, 1, 2, 1, 1.1), At(1.1));
            closed.Handle(Shot(closed, 1, 2, 2, 1.2), At(1.2));
            closed.Close(1.3);
            TestRunner.Assert(Tick(closed, 3, 1.3).Count == 0 && closed.Deaths.Count == 0, "a closed round declares nothing more");
        });

        runner.Run("health/the same reports with drops, duplicates, reordering and late arrivals give the same health, deaths and credit", () =>
        {
            var script = Script();
            var baseline = Deliver(script, seed: 0, faults: false);
            TestRunner.Assert(baseline.Deaths.Count >= 3 && baseline.Deaths.Any(d => d.Kind == DamageKind.Blast) && baseline.Deaths.Any(d => d.Kind == DamageKind.Bullet) &&
                baseline.Deaths.Any(d => d.CreditedTo != d.KilledBy && d.CreditedTo != 0), $"the script must exercise every kind: {string.Join("; ", baseline.Deaths)}");
            for (var seed = 1; seed <= 40; seed++)
            {
                var faulted = Deliver(script, seed, faults: true);
                TestRunner.Assert(faulted.Deaths.SequenceEqual(baseline.Deaths), $"seed {seed} deaths: {string.Join("; ", faulted.Deaths)} vs {string.Join("; ", baseline.Deaths)}");
                TestRunner.Assert(faulted.Health.SequenceEqual(baseline.Health), $"seed {seed} health: {string.Join(",", faulted.Health)} vs {string.Join(",", baseline.Health)}");
            }
            // Beyond the window a report is refused however it arrives, as if it had been lost.
            var lost = script.Where(r => r.Report is BulletHitReported { ShooterId: 4, VictimId: not 4 }).ToArray();
            var without = Deliver(script.Except(lost).ToList(), seed: 0, faults: false);
            for (var seed = 1; seed <= 10; seed++)
            {
                var late = Deliver(script, seed, faults: true, tooLate: lost);
                TestRunner.Assert(late.Deaths.SequenceEqual(without.Deaths) && late.Health.SequenceEqual(without.Health) && late.Late == lost.Length,
                    $"seed {seed} too late: {string.Join("; ", late.Deaths)} vs {string.Join("; ", without.Deaths)}, {late.Late} late");
            }
        });

        runner.Run("short fuse/the last one alive wins the round, a new suburb follows, and the first to three takes the match", () =>
        {
            var rules = Fuse(3);
            TestRunner.Assert(rules.Health.LifeOf(1) == 1 && rules.AliveCount == 3 && rules.Health.Health(2, 10) == 100, "a round starts everyone at full health");
            var first = Kill(rules, 1, 2, 1, 11);
            TestRunner.Assert(first.OfType<ActorDied>().Single() is { VictimId: 2, CreditedTo: 1 } && !first.OfType<RoundOver>().Any() && rules.Kills(1) == 1,
                "a death with two alive does not end the round");
            TestRunner.Assert(rules.SpectateTarget(2) == 1 && rules.SpectateTarget(1) == 1, "the dead watch their killer");
            var second = Kill(rules, 3, 1, 2, 12);
            TestRunner.Assert(second.OfType<RoundWon>().Single() == new RoundWon(1, 3) && second.OfType<RoundOver>().Count() == 1 && rules.Over,
                $"last one alive: {string.Join("; ", second)}");
            TestRunner.Assert(rules.SpectateTarget(2) == 3, "the dead follow the chain of killers");
            TestRunner.Assert(!rules.Permits(ModeGate.World, FuseAt(12.7)) && Kill(rules, 3, 3, 9, 13).Count == 0, "a decided round takes nothing more");
            var decision = rules.Handle(new PhaseEntered(ModePhase.RoundEnd, new RoundInfo(1, 1, RoundTimer.Countdown, 10, 105)), FuseAt(13));
            TestRunner.Assert(decision.SequenceEqual(new ModeCommand[] { new NextRound(RoundTimer.Countdown, 105, true) }), $"next suburb: {string.Join("; ", decision)}");
            TestRunner.Assert(ShortFuseHud.Banner(rules, 3) == "YOU WIN THE ROUND" && ShortFuseHud.Banner(rules, 1) == "P3 WINS THE ROUND", "round banner");
            for (uint round = 2; round <= 3; round++)
            {
                var start = round * 20.0;
                var respawned = rules.Handle(new PhaseEntered(ModePhase.Round, new RoundInfo(round, round, RoundTimer.Countdown, start, 105)), FuseAt(start, round));
                TestRunner.Assert(respawned.OfType<ActorRespawned>().Select(r => (r.ActorId, r.LifeId)).SequenceEqual(new[] { (1, round), (2, round), (3, round) }),
                    "every round respawns everyone with the next life");
                Kill(rules, 3, 1, round * 10, start + 1);
                Kill(rules, 3, 2, round * 10 + 1, start + 2);
                var end = rules.Handle(new PhaseEntered(ModePhase.RoundEnd, new RoundInfo(round, round, RoundTimer.Countdown, start, 105)), FuseAt(start + 3, round));
                TestRunner.Assert(end.OfType<EndMatch>().Any() == (round == 3) && rules.Wins(3) == round, $"round {round}: {string.Join("; ", end)}");
            }
            TestRunner.Assert(rules.MatchOver && rules.MatchWinner == 3 && ShortFuseHud.Banner(rules, 3) == "YOU WIN THE MATCH 3–0", "match");
            TestRunner.Assert(rules.Snapshot().Standings[0] is { ActorId: 3, Score: 3 } && rules.Kills(3) == 5 && rules.DeathsOf(1) == 3, "standings and kills");
            TestRunner.Assert(rules.Handle(new SpillRequested(new SpillTicket(1, 1, 1), SpillReason.Hit, 1), FuseAt(80)).Single() is SpillCanceled, "no bank");
        });

        runner.Run("short fuse/the limit goes to the healthiest survivor, a tie to nobody, and a departure can decide a round", () =>
        {
            var rules = Fuse(3);
            rules.Handle(new BulletHitReported(1, 1, 1, 2, 1, 11), FuseAt(11));
            var end = rules.Handle(new PhaseEntered(ModePhase.RoundEnd, new RoundInfo(1, 1, RoundTimer.Countdown, 10, 105)), FuseAt(115));
            TestRunner.Assert(end.OfType<RoundWon>().Single() == new RoundWon(1, 0), $"a tie at full health: {string.Join("; ", end)}");
            rules.Handle(new PhaseEntered(ModePhase.Round, new RoundInfo(2, 2, RoundTimer.Countdown, 120, 105)), FuseAt(120, 2));
            rules.Handle(new BulletHitReported(1, 2, 2, 2, 2, 224), FuseAt(224, 2));
            rules.Handle(new BulletHitReported(1, 2, 3, 3, 2, 224), FuseAt(224, 2));
            rules.Handle(new BulletHitReported(2, 2, 1, 3, 2, 224.1), FuseAt(224.1, 2));
            end = rules.Handle(new PhaseEntered(ModePhase.RoundEnd, new RoundInfo(2, 2, RoundTimer.Countdown, 120, 105)), FuseAt(225, 2));
            TestRunner.Assert(end.OfType<RoundWon>().Single() == new RoundWon(2, 1), $"the healthiest: {string.Join("; ", end)}");
            rules.Handle(new PhaseEntered(ModePhase.Round, new RoundInfo(3, 3, RoundTimer.Countdown, 230, 105)), FuseAt(230, 3));
            Kill(rules, 1, 2, 1, 231);
            var left = rules.Handle(new ActorLeft(3), FuseAt(232, 3));
            TestRunner.Assert(left.OfType<RoundWon>().Single() == new RoundWon(3, 1) && left.OfType<RoundOver>().Any(), $"the last opponent left: {string.Join("; ", left)}");
            TestRunner.Assert(ShortFuseHud.Line(rules, rules.RoundDeaths[0]) == "P1 shot P2", "feed line");
        });

        runner.Run("short fuse/the HUD shows health, the feed, spectating and the banner", () =>
        {
            var rules = Fuse(3);
            var hud = new ShortFuseHud();
            ModeHud Present(int me, double now) => hud.Present(new ModeHudInput(me, rules.Over ? ModePhase.RoundEnd : ModePhase.Round, 60, new Dictionary<int, int>(),
                Array.Empty<Godot.Vector3>(), false, rules, FuseAt(now)));
            rules.Handle(new BlastAccepted(1, 1, 11), FuseAt(11));
            rules.Handle(new BlastHitReported(2, 1, 1, 0), FuseAt(11));
            var hurt = Present(2, 11);
            TestRunner.Assert(hurt.Bar is { Value: 10, Max: 100, Hot: true } && hurt.Counters[1].Value == 3 && hurt.OutState == "", $"hurt: {hurt.Bar}");
            rules.Handle(new BulletHitReported(1, 1, 1, 2, 1, 11.1), FuseAt(11.1));
            rules.Handle(new BulletHitReported(1, 1, 2, 2, 1, 11.2), FuseAt(11.2));
            rules.Handle(new ActorsSampled(Array.Empty<ActorSample>()), FuseAt(11.5));
            var dead = Present(2, 11.5);
            TestRunner.Assert(dead.OutState == "SPECTATING P1" && dead.Bar is { Value: 0, Caption: "DEAD" } && dead.Feed!.Single() == "P1 shot P2",
                $"dead: {dead.OutState} {dead.Bar} {string.Join("|", dead.Feed!)}");
            TestRunner.Assert(Present(1, 11.5).Counters[2] is { Value: 1, Caption: "KILL" }, "the killer's count");
            rules.Handle(new BlastAccepted(2, 3, 12), FuseAt(12));
            rules.Handle(new BlastHitReported(3, 1, 2, 0), FuseAt(12));
            rules.Handle(new BlastAccepted(3, 3, 12.05), FuseAt(12.05));
            rules.Handle(new BlastHitReported(3, 1, 3, 0), FuseAt(12.05));
            rules.Handle(new ActorsSampled(Array.Empty<ActorSample>()), FuseAt(12.5));
            var won = Present(1, 12.5);
            TestRunner.Assert(won.Banner == "YOU WIN THE ROUND" && won.Feed![^1] == "P3 blew themself up", $"won: {won.Banner} {string.Join("|", won.Feed!)}");
        });
    }

    // ------------------------------------------------------------------ Delivery under faults

    private sealed record Scripted(double At, AcceptedEvent Report, bool Server);

    /// <summary>
    /// A melee of four over ten seconds: chip damage, a blast-then-bullet kill, a chain whose detonator dies before it
    /// goes up, an assisted self-kill, trades and a regeneration. Bullet reports come from shooters, blast reports from
    /// victims; the explosion acceptances are the server's own and arrive on time.
    /// </summary>
    private static List<Scripted> Script()
    {
        var script = new List<Scripted>();
        uint shot = 0;
        void Bullet(double at, int shooter, int victim) => script.Add(new Scripted(at, new BulletHitReported(shooter, 1, ++shot, victim, 1, at), false));
        void Explosion(double at, uint id, int detonator, params (int Victim, double Distance)[] victims)
        {
            script.Add(new Scripted(at, new BlastAccepted(id, detonator, at), true));
            foreach (var (victim, distance) in victims) script.Add(new Scripted(at, new BlastHitReported(victim, 1, id, distance), false));
        }
        for (var i = 0; i < 6; i++) Bullet(0.5 + i * 0.15, 4, 3); // 4 chips 3: 58 left.
        Explosion(1.5, 1, 1, (2, 1.0), (3, 6.0)); // 1 throws 2 (80.8) and grazes 3 (34.6 more: 23.4 left).
        Bullet(1.7, 1, 2); Bullet(1.8, 4, 2); Bullet(1.9, 1, 2); // 2 is down: 19.2 - 21, dead at 1.9, 1's kill.
        Bullet(2.0, 2, 1); // Fired after 2 died: counts for nothing.
        Explosion(2.4, 2, 3, (3, 2.0)); // 3 blows itself up (71.5), credited to 1, the last other to hurt it.
        Explosion(3.0, 3, 1, (4, 3.0)); // 4: 100 - 62.3 = 37.7.
        Bullet(3.1, 4, 1); Bullet(3.2, 4, 1); // 1: 86.
        Explosion(3.3, 4, 3, (1, 0.5)); // A chain 3 started goes up after 3 died: 1 takes 85.4 → 0.6, credited to 3.
        Bullet(3.4, 4, 1); // 4 finishes 1 at 3.4.
        Bullet(9.5, 4, 4); // A self hit: refused.
        Bullet(4.0, 1, 4); // Fired by a dead shooter: refused at once if the death is declared, else ignored in replay.
        return script;
    }

    private sealed record Outcome(IReadOnlyList<ActorDied> Deaths, IReadOnlyList<double> Health, int Late);

    /// <summary>
    /// Delivers the script: in order, or with each client report delayed within the window, a fifth of them dropped
    /// and resent, a fifth duplicated, so they reorder freely. <paramref name="tooLate"/> arrive past the window.
    /// The server ticks every step, as a runner does.
    /// </summary>
    private static Outcome Deliver(IReadOnlyList<Scripted> script, int seed, bool faults, IReadOnlyCollection<Scripted>? tooLate = null)
    {
        var random = new ModeRandom((ulong)seed * 7919 + 1);
        var window = Starting.ReportWindow;
        var arrivals = new List<(double At, int Order, AcceptedEvent Report)>();
        var order = 0;
        foreach (var item in script)
        {
            if (item.Server || !faults) { arrivals.Add((item.At, order++, item.Report)); continue; }
            if (tooLate != null && tooLate.Contains(item)) { arrivals.Add((item.At + window + 0.05 + random.Unit() * 0.5, order++, item.Report)); continue; }
            var roll = random.Unit();
            var delay = random.Unit() * window * 0.6;
            if (roll < 0.2) delay += window * 0.3; // Dropped: the resend arrives later, still within the window.
            arrivals.Add((item.At + delay, order++, item.Report));
            if (roll > 0.8) arrivals.Add((item.At + random.Unit() * window * 0.9, order++, item.Report)); // Duplicated.
        }
        var health = new HealthState(Starting);
        health.BeginRound(new[] { 1, 2, 3, 4 }, 0);
        var deaths = new List<ActorDied>();
        var queue = arrivals.OrderBy(a => a.At).ThenBy(a => a.Order).ToList();
        var next = 0;
        for (var tick = 0; tick <= 12 * 60; tick++)
        {
            var now = tick / 60.0;
            while (next < queue.Count && queue[next].At <= now)
                deaths.AddRange(health.Handle(queue[next++].Report, At(now)).OfType<ActorDied>());
            deaths.AddRange(health.Handle(new ActorsSampled(Array.Empty<ActorSample>()), At(now)).OfType<ActorDied>());
        }
        return new Outcome(deaths, new[] { 1, 2, 3, 4 }.Select(a => Math.Round(health.Health(a, 12), 9)).ToArray(), health.Refused(DamageRefusal.Late));
    }
}
