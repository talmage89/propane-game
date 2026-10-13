using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Godot;
using Propane.Modes;

namespace Propane.Dev.UnitTests;

/// <summary>Pure mode rules, phases and HUD presenters, headless: no nodes, transport or clocks.</summary>
[UnitSuite]
public static class ModeTests
{
    private static readonly ModeTuners Tuners = new(new Dictionary<string, double>
    {
        [TankBankRules.HitDropCount] = 1,
        [TankBankRules.RagdollDropCount] = 5,
        [TankBankRules.HitGraceTime] = 0.5,
    });

    private static readonly ModeClock Open = new(ModePhase.Round, 1, 10, 150);
    private static readonly ModeClock Expired = new(ModePhase.Round, 1, 150, 150);

    public static TankBankRules Bank(params int[] starts)
    {
        var rules = new TankBankRules();
        rules.Initialize(new ModeSetup(starts.Select((score, i) => new ModeActor(i + 1, $"P{i + 1}", true, score)).ToArray(),
            new ModeWorld(ModeWorldSource.SuburbPlan, 1, 20), Tuners, 7));
        return rules;
    }

    private static SpillTicket Ticket(int actor, uint id, uint life = 1) => new(actor, life, id);

    private static IReadOnlyList<ModeCommand> Spill(TankBankRules rules, SpillTicket ticket, SpillReason reason, int count, bool register = true)
    {
        var commands = rules.Handle(new SpillRequested(ticket, reason, count), Open).ToList();
        if (register && commands.OfType<SpillReserved>().Any()) commands.AddRange(rules.Handle(new SpillRegistered(ticket), Open));
        return commands;
    }

    public static void Register(TestRunner runner)
    {
        runner.Run("modes/tank bank credits every accepted detonation to the chain's initiator", () =>
        {
            var rules = Bank(0, 0);
            var credited = new[] { TankOrigin.Map, TankOrigin.Map, TankOrigin.Drop }
                .SelectMany(origin => rules.Handle(new TankDetonated(1, origin), Open)).ToArray();
            TestRunner.Assert(credited.OfType<ScoreChanged>().Select(c => (c.ActorId, c.Score, c.Delta))
                .SequenceEqual(new[] { (1, 1, 1), (1, 2, 1), (1, 3, 1) }), "chain credit did not bank each tank once");
            TestRunner.Assert(rules.Bank(2) == 0 && rules.StatsFor(1).Gained == 3 && rules.MapDetonations == 2 && rules.DropDetonations == 1,
                "chain credit leaked to another actor or lost its origin");
            TestRunner.Assert(rules.Conserved, "detonations broke conservation");
        });
        runner.Run("modes/hit grace is the victim owner's pure decision", () =>
        {
            var grace = 0.0;
            TestRunner.Assert(TankBankRules.TryOpenGrace(1, ref grace, 0.5) && grace == 1.5, "first hit did not open grace");
            TestRunner.Assert(!TankBankRules.TryOpenGrace(1.49, ref grace, 0.5) && grace == 1.5, "a hit inside grace spilled or extended it");
            TestRunner.Assert(TankBankRules.TryOpenGrace(1.5, ref grace, 0.5) && grace == 2, "grace did not end at its boundary");
            TestRunner.Assert(TankBankRules.SpillCount(5, 3, 1) == 2 && TankBankRules.SpillCount(1, 0, 0) == 0 && TankBankRules.SpillCount(1, 2, 5) == 0,
                "spill counts ignored the unreserved bank");
        });
        runner.Run("modes/throws and hits spill within their tuner and the bank", () =>
        {
            var rules = Bank(8, 2);
            var hit = Spill(rules, Ticket(1, 1), SpillReason.Hit, 2);
            TestRunner.Assert(hit.Single() is SpillCanceled { Reason: SpillCancel.Exceeds }, "hit spill exceeded HitDropCount");
            var thrown = Spill(rules, Ticket(1, 2), SpillReason.Thrown, 5);
            TestRunner.Assert(thrown.OfType<ScoreChanged>().Single() is { ActorId: 1, Score: 3, Delta: -5 } &&
                thrown.OfType<SpillCommitted>().Single() is { Count: 5, Reason: SpillReason.Thrown }, "throw did not debit five");
            TestRunner.Assert(Spill(rules, Ticket(2, 1), SpillReason.Thrown, 3).Single() is SpillCanceled { Reason: SpillCancel.Exceeds },
                "throw spilled more than the bank");
            TestRunner.Assert(Spill(rules, Ticket(2, 2), SpillReason.Thrown, 6).Single() is SpillCanceled { Reason: SpillCancel.Exceeds },
                "throw spilled more than RagdollDropCount");
            TestRunner.Assert(rules.StatsFor(1) is { LostToThrows: 5, LostToHits: 0 } && rules.Conserved, "throw statistics or audit wrong");
            Spill(rules, Ticket(2, 3), SpillReason.Hit, 1);
            TestRunner.Assert(rules.StatsFor(2).LostToHits == 1 && rules.Bank(2) == 1, "hit statistics wrong");
        });
        runner.Run("modes/concurrent spill tickets never spend the same bank twice", () =>
        {
            var rules = Bank(3);
            var a = rules.Handle(new SpillRequested(Ticket(1, 1), SpillReason.Thrown, 2), Open);
            var b = rules.Handle(new SpillRequested(Ticket(1, 2), SpillReason.Thrown, 2), Open);
            var c = rules.Handle(new SpillRequested(Ticket(1, 3), SpillReason.Hit, 1), Open);
            TestRunner.Assert(a.Single() is SpillReserved { Count: 2 } && b.Single() is SpillCanceled { Reason: SpillCancel.Exceeds } &&
                c.Single() is SpillReserved { Count: 1 }, "reservations overlapped");
            TestRunner.Assert(rules.Reserved(1) == 3 && rules.Bank(1) == 3, "reservation debited before registration");
            rules.Handle(new SpillRegistered(Ticket(1, 3)), Open);
            rules.Handle(new SpillRegistered(Ticket(1, 1)), Open);
            TestRunner.Assert(rules.Bank(1) == 0 && rules.Reserved(1) == 0 && rules.Spilled == 3 && rules.Conserved, "commits double-spent the bank");
        });
        runner.Run("modes/spill tickets are idempotent", () =>
        {
            var rules = Bank(5);
            var first = rules.Handle(new SpillRequested(Ticket(1, 4), SpillReason.Thrown, 2), Open);
            var again = rules.Handle(new SpillRequested(Ticket(1, 4), SpillReason.Thrown, 2), Open);
            TestRunner.Assert(first.Single() is SpillReserved { Count: 2 } && again.Single() is SpillReserved { Count: 2 } && rules.Reserved(1) == 2,
                "a repeated open ticket reserved twice");
            var commit = rules.Handle(new SpillRegistered(Ticket(1, 4)), Open);
            TestRunner.Assert(commit.Count == 2 && rules.Handle(new SpillRegistered(Ticket(1, 4)), Open).Count == 0, "a ticket committed twice");
            TestRunner.Assert(rules.Handle(new SpillRequested(Ticket(1, 4), SpillReason.Thrown, 2), Open).Count == 0 &&
                rules.Handle(new SpillRequested(Ticket(1, 3), SpillReason.Thrown, 1), Open).Count == 0, "a finished or older ticket ran again");
            TestRunner.Assert(rules.Handle(new SpillRequested(Ticket(1, 1, life: 2), SpillReason.Hit, 1), Open).Single() is SpillReserved,
                "a new life could not spill");
            TestRunner.Assert(rules.Bank(1) == 3, "idempotency changed the bank");
        });
        runner.Run("modes/expiry and refusal cancel tickets without a debit", () =>
        {
            var rules = Bank(5, 5);
            TestRunner.Assert(rules.Handle(new SpillRequested(Ticket(1, 1), SpillReason.Hit, 1), Expired).Single() is SpillCanceled { Reason: SpillCancel.Closed },
                "a spill after the deadline was accepted");
            TestRunner.Assert(rules.Handle(new SpillRequested(Ticket(1, 2), SpillReason.Hit, 1), Open with { Phase = ModePhase.Countdown }).Single()
                is SpillCanceled { Reason: SpillCancel.Closed }, "a spill during the countdown was accepted");
            rules.Handle(new SpillRequested(Ticket(1, 3), SpillReason.Thrown, 2), Open);
            TestRunner.Assert(rules.Handle(new SpillFailed(Ticket(1, 3)), Open).Single() is SpillCanceled { Reason: SpillCancel.Rejected } &&
                rules.Reserved(1) == 0, "a ledger refusal kept its reservation");
            rules.Handle(new SpillRequested(Ticket(2, 1), SpillReason.Thrown, 2), Open);
            var end = rules.Handle(new PhaseEntered(ModePhase.RoundEnd, RoundInfo.First), Expired);
            TestRunner.Assert(end.Count == 2 && end[0] is SpillCanceled { Reason: SpillCancel.Expired, Ticket.ActorId: 2 } && end[1] is EndMatch,
                "round end did not expire the open ticket before ending");
            TestRunner.Assert(rules.Handle(new SpillRegistered(Ticket(2, 1)), Expired).Count == 0 && rules.Bank(2) == 5 && rules.Conserved,
                "an expired ticket still debited");
        });
        runner.Run("modes/departure cancels tickets and keeps the bank in the standings", () =>
        {
            var rules = Bank(4, 6);
            rules.Handle(new SpillRequested(Ticket(2, 1), SpillReason.Hit, 1), Open);
            var left = rules.Handle(new ActorLeft(2), Open);
            TestRunner.Assert(left.Single() is SpillCanceled { Reason: SpillCancel.Departed }, "departure kept an open ticket");
            TestRunner.Assert(rules.Handle(new SpillRequested(Ticket(2, 2), SpillReason.Hit, 1), Open).Single() is SpillCanceled { Reason: SpillCancel.Departed },
                "a departed actor spilled");
            var standings = rules.Snapshot().Standings;
            TestRunner.Assert(standings.Select(s => (s.ActorId, s.Score, s.Present)).SequenceEqual(new[] { (2, 6, false), (1, 4, true) }),
                "departed bank left the standings or the ranking");
            TestRunner.Assert(rules.Handle(new TankDetonated(2, TankOrigin.Map), Open).OfType<ScoreChanged>().Single().Score == 7 && rules.Conserved,
                "an accepted late detonation by a departed actor was lost");
        });
        runner.Run("modes/standings keep roster order through ties", () =>
        {
            var rules = Bank(2, 5, 2, 5);
            TestRunner.Assert(rules.Snapshot().Standings.Select(s => s.ActorId).SequenceEqual(new[] { 2, 4, 1, 3 }), "ties reordered the roster");
        });
        runner.Run("modes/repeated rounds start fresh and publish their snapshot", () =>
        {
            var rules = Bank(0, 0);
            rules.Handle(new TankDetonated(1, TankOrigin.Map), Open);
            Spill(rules, Ticket(1, 1), SpillReason.Hit, 1);
            rules.Initialize(new ModeSetup(new[] { new ModeActor(1, "P1", true, 3), new ModeActor(2, "P2") }, new ModeWorld(ModeWorldSource.SuburbPlan), Tuners, 8));
            TestRunner.Assert(rules.Bank(1) == 3 && rules.MapDetonations == 0 && rules.Spilled == 0 && rules.Initial == 3 && rules.Conserved,
                "a new match kept the previous match's ledger");
            TestRunner.Assert(rules.Handle(new PhaseEntered(ModePhase.Round, RoundInfo.First), Open).Single() is PublishSnapshot,
                "a round start did not publish its banks");
            TestRunner.Assert(Spill(rules, Ticket(1, 1), SpillReason.Hit, 1).OfType<SpillCommitted>().Any(), "a new match refused a reused ticket");
        });
        runner.Run("modes/tuning changes are explicit inputs", () =>
        {
            var rules = Bank(8);
            rules.Handle(new TunersChanged(Tuners.With(TankBankRules.RagdollDropCount, 1)), Open);
            TestRunner.Assert(Spill(rules, Ticket(1, 1), SpillReason.Thrown, 2).Single() is SpillCanceled { Reason: SpillCancel.Exceeds },
                "rules kept the former drop count");
            TestRunner.Assert(Spill(rules, Ticket(1, 2), SpillReason.Thrown, 1).OfType<SpillCommitted>().Any(), "rules refused the new drop count");
        });
        runner.Run("modes/phase gates follow the round and its deadline", () =>
        {
            var rules = Bank(0);
            TestRunner.Assert(rules.Permits(ModeGate.World, Open) && !rules.Permits(ModeGate.World, Expired) &&
                !rules.Permits(ModeGate.World, Open with { Phase = ModePhase.Countdown }), "world gate wrong");
            TestRunner.Assert(rules.Permits(ModeGate.Pickup, Expired) && !rules.Permits(ModeGate.Pickup, Open with { Phase = ModePhase.Results }),
                "pickup gate changed from the round-only rule");
        });
        runner.Run("modes/conservation holds through a seeded event storm", () =>
        {
            var random = new Random(23);
            var rules = Bank(4, 0, 2, 0);
            var events = 0u;
            for (var step = 0; step < 5000; step++)
            {
                var actor = random.Next(1, 5);
                switch (random.Next(5))
                {
                    case 0: rules.Handle(new TankDetonated(actor, random.Next(2) == 0 ? TankOrigin.Map : TankOrigin.Drop), Open); break;
                    case 1: rules.Handle(new SpillRequested(Ticket(actor, ++events), (SpillReason)random.Next(2), random.Next(1, 7)), Open); break;
                    case 2:
                    case 3:
                        var tickets = Enumerable.Range(1, (int)events).Select(i => Ticket(actor, (uint)i)).ToArray();
                        if (tickets.Length > 0)
                        {
                            var ticket = tickets[random.Next(tickets.Length)];
                            rules.Handle(random.Next(4) == 0 ? new SpillFailed(ticket) : new SpillRegistered(ticket), Open);
                        }
                        break;
                    default: rules.Handle(new SpillRequested(Ticket(actor, events), SpillReason.Hit, 1), Open); break;
                }
                TestRunner.Assert(rules.Conserved && rules.Banks.Values.All(b => b >= 0) && rules.Banks.All(b => rules.Reserved(b.Key) <= b.Value),
                    $"conservation or reservation broke at step {step}");
            }
            TestRunner.Assert(rules.Spilled > 0 && rules.MapDetonations > 0, "storm exercised nothing");
        });
        runner.Run("modes/server report lines keep the audited log format", () =>
        {
            var rules = Bank(4, 4);
            rules.Handle(new TankDetonated(2, TankOrigin.Map), Open);
            Spill(rules, Ticket(1, 1), SpillReason.Thrown, 3);
            var report = rules.Snapshot().Report;
            var audit = report.Single(r => r.Kind == ModeReportKind.Audit).Text;
            TestRunner.Assert(Regex.IsMatch($"match 9: {audit}", @"match (\d+): bank audit initial=8 map=1 drops=0 spills=3 banked=6 expected=6 ok=True"),
                $"audit line changed: {audit}");
            TestRunner.Assert(report.Single(r => r.Kind == ModeReportKind.Standings).Text == "1=1 2=5", "standings line changed");
            TestRunner.Assert(report.Where(r => r.Kind == ModeReportKind.Summary).Select(r => r.Text).SequenceEqual(new[]
                { "match over, P2 5, P1 1", "tanks gained / lost to hits / lost to throws: P1 0/0/3, P2 1/0/0" }), "summary lines changed");
        });
        runner.Run("modes/sandbox has no score or spills and ends when asked", () =>
        {
            var rules = new SandboxRules();
            rules.Initialize(new ModeSetup(new[] { new ModeActor(1, "P1") }, new ModeWorld(ModeWorldSource.Void), ModeTuners.Empty, 1));
            TestRunner.Assert(rules.Handle(new TankDetonated(1, TankOrigin.Map), Open).Count == 0 &&
                rules.Handle(new SpillRequested(Ticket(1, 1), SpillReason.Hit, 1), Open).Single() is SpillCanceled &&
                rules.Handle(new PhaseEntered(ModePhase.RoundEnd, RoundInfo.First), Open).Single() is EndMatch &&
                rules.Snapshot().Standings.Single().Score == 0 && rules.Permits(ModeGate.World, Expired), "sandbox scored or gated");
        });
        runner.Run("modes/phases run lobby to results and decide rounds", () =>
        {
            var times = new PhaseTimes(20, 4, 10);
            var phases = new ModePhaseMachine();
            var steps = new List<ModePhase>();
            void Step(PhaseChange? change) { if (change is { } c) steps.Add(c.To); }
            Step(phases.Load(0, times, RoundTimer.Countdown, 150));
            Step(phases.Advance(1, times, false));
            Step(phases.Advance(2, times, true));
            TestRunner.Assert(phases.PhaseEnds == 6 && phases.Clock(5).Phase == ModePhase.Countdown, "countdown length wrong");
            Step(phases.Advance(6, times, true));
            TestRunner.Assert(phases.Round is { RoundId: 1, WorldRevision: 1, StartsAt: 6, Length: 150 } && phases.Clock(155.9).PlayOpen &&
                !phases.Clock(156).PlayOpen, "round deadline wrong");
            Step(phases.Advance(155, times, true));
            Step(phases.Advance(156, times, true));
            Step(phases.Decide(156, times, new EndMatch()));
            Step(phases.Advance(156, times, true));
            TestRunner.Assert(phases.PhaseEnds == 166, "results length wrong");
            Step(phases.Advance(165, times, true));
            Step(phases.Advance(166, times, true));
            TestRunner.Assert(steps.SequenceEqual(new[] { ModePhase.Loading, ModePhase.Countdown, ModePhase.Round, ModePhase.RoundEnd,
                ModePhase.MatchEnd, ModePhase.Results, ModePhase.Lobby }), $"phase order wrong: {string.Join(",", steps)}");
            var timeout = new ModePhaseMachine();
            timeout.Load(0, times, RoundTimer.Countdown, 60);
            TestRunner.Assert(timeout.Advance(19.9, times, false) == null && timeout.Advance(20, times, false)?.To == ModePhase.Countdown,
                "loading did not wait for its timeout");
        });
        runner.Run("modes/next rounds keep or rebuild the world as the mode asks", () =>
        {
            var times = new PhaseTimes(20, 4, 10);
            var phases = new ModePhaseMachine();
            phases.Load(0, times, RoundTimer.Countdown, 30);
            phases.Advance(0, times, true);
            phases.Advance(4, times, true);
            phases.Advance(34, times, true);
            var kept = phases.Decide(34, times, new NextRound(RoundTimer.CountUp, 0, RebuildWorld: false));
            TestRunner.Assert(kept is { To: ModePhase.Countdown, Round: { RoundId: 2, WorldRevision: 1, Timer: RoundTimer.CountUp } }, "kept world round wrong");
            phases.Advance(38, times, true);
            TestRunner.Assert(phases.Advance(10_000, times, true) == null && phases.Clock(10_000).PlayOpen, "a count-up round ended on a clock");
            TestRunner.Assert(phases.EndRound(10_001).To == ModePhase.RoundEnd, "rules could not end a count-up round");
            var rebuilt = phases.Decide(10_001, times, new NextRound(RoundTimer.Countdown, 30, RebuildWorld: true));
            TestRunner.Assert(rebuilt is { To: ModePhase.Loading, Round: { RoundId: 3, WorldRevision: 2 } }, "rebuilt world round wrong");
            TestRunner.Throws<InvalidOperationException>(() => phases.Decide(10_002, times, new EndMatch()));
        });
        runner.Run("modes/tank bank presenter supplies the bank caption, clock and arrows", () =>
        {
            var hud = new TankBankHud();
            var groups = new[] { Vector3.One };
            var scores = new Dictionary<int, int> { [1] = 1, [2] = 4 };
            var playing = hud.Present(new ModeHudInput(1, ModePhase.Round, 42, scores, groups, true));
            TestRunner.Assert(playing.Counters.Single() is { Value: 1, Caption: "TANK BANKED" } && playing.Timer is { Seconds: 42, Kind: RoundTimer.Countdown } &&
                playing.ObjectivesVisible && playing.Objectives.SequenceEqual(groups) && playing.Columns.Single() == "Tanks banked", "playing HUD wrong");
            var loading = hud.Present(new ModeHudInput(2, ModePhase.Loading, -1, scores, groups, true));
            TestRunner.Assert(loading.Counters.Single() is { Value: 4, Caption: "TANKS BANKED" } && loading.Timer == null && !loading.ObjectivesVisible,
                "loading HUD wrong");
            TestRunner.Assert(hud.Present(new ModeHudInput(1, ModePhase.Countdown, 0, scores, groups, false)) is { ObjectivesVisible: false, Timer: null } &&
                hud.Present(new ModeHudInput(1, ModePhase.Results, 0, scores, groups, true)) is { ObjectivesVisible: false, Timer.Seconds: 0 },
                "arrow toggle or ended clock wrong");
        });
    }
}
