using System.Collections.Generic;
using System.Linq;
using Propane.Modes;

namespace Propane.Dev.UnitTests;

/// <summary>The Lab prototypes' pure rules and presenters, headless: Ring Out's slab and placements, King's zone and mints.</summary>
[UnitSuite]
public static class PrototypeModeTests
{
    private static readonly ModeTuners RingTuners = new(new Dictionary<string, double>
    {
        [RingOutRules.RingOutLots] = 5,
        [RingOutRules.RingOutLotSize] = 16,
        [RingOutRules.RingOutBandLots] = 4,
        [RingOutRules.RingOutBandSeconds] = 15,
        [RingOutRules.RingOutWarningSeconds] = 4,
        [RingOutRules.RingOutCreditSeconds] = 6,
        [RingOutRules.RingOutFallDepth] = 2,
        [RingOutRules.RingOutStandHeight] = 0.6,
        [RingOutRules.RingOutRoundLimit] = 180,
    });

    private static readonly ModeTuners KingTuners = new(new Dictionary<string, double>
    {
        [TankBankRules.HitDropCount] = 1,
        [TankBankRules.RagdollDropCount] = 5,
        [TankBankRules.HitGraceTime] = 0.5,
        [KingRules.KingZoneRadius] = 6,
        [KingRules.KingZoneHeight] = 3,
        [KingRules.KingMintSeconds] = 2,
        [KingRules.KingMoveSeconds] = 45,
        [KingRules.KingWarningSeconds] = 3,
        [KingRules.KingMoveMin] = 15,
        [KingRules.KingMoveMax] = 25,
    });

    private static ModeClock At(double now) => new(ModePhase.Round, 1, now, double.PositiveInfinity);

    private static RingOutRules Ring(int actors, double start = 100, ulong seed = 7, ModeTuners? tuners = null)
    {
        var rules = new RingOutRules();
        rules.Initialize(new ModeSetup(Enumerable.Range(1, actors).Select(i => new ModeActor(i, $"P{i}")).ToArray(),
            new ModeWorld(ModeWorldSource.Void), tuners ?? RingTuners, seed));
        rules.Handle(new PhaseEntered(ModePhase.Round, new RoundInfo(1, 1, RoundTimer.CountUp, start, 0)), At(start));
        return rules;
    }

    private static IReadOnlyList<ModeCommand> Sample(IModeRules rules, double now, params ActorSample[] samples) =>
        rules.Handle(new ActorsSampled(samples), At(now));

    private static ActorSample Standing(int actor, double x = 0, double z = 0, double y = 0) => new(actor, x, y, z);

    private static readonly ModeSpot[] Spots =
    {
        new(0, 0, 0), new(20, 0, 0), new(0, 0, 20), new(-20, 0, 0), new(0, 0, -20), new(60, 0, 60),
    };

    private static KingRules King(int actors, ulong seed = 3, IReadOnlyList<ModeSpot>? spots = null)
    {
        var rules = new KingRules();
        rules.Initialize(new ModeSetup(Enumerable.Range(1, actors).Select(i => new ModeActor(i, $"P{i}")).ToArray(),
            new ModeWorld(ModeWorldSource.Void, 1, 0, spots ?? Spots), KingTuners, seed));
        rules.Handle(new PhaseEntered(ModePhase.Round, new RoundInfo(1, 1, RoundTimer.Countdown, 0, 180)), new ModeClock(ModePhase.Round, 1, 0, 180));
        return rules;
    }

    private static ModeClock KingAt(double now) => new(ModePhase.Round, 1, now, 180);

    private static IReadOnlyList<ModeCommand> KingSample(KingRules rules, double now, params ActorSample[] samples) =>
        rules.Handle(new ActorsSampled(samples), KingAt(now));

    private static ActorSample InZone(KingRules rules, int actor, double dx = 0, bool down = false)
    {
        var spot = rules.SpotOf(rules.Zone < 0 ? 0 : rules.Zone);
        return new ActorSample(actor, spot.X + dx, spot.Y, spot.Z, down);
    }

    public static void Register(TestRunner runner)
    {
        runner.Run("prototypes/ring out's rim dissolves band by band on a fixed schedule until one lot remains", () =>
        {
            var plan = new RingOutPlan(5, 16, 4, 15, 4, 7);
            TestRunner.Assert(plan.Lots.Count == 25 && plan.Bands == 6 && plan.HalfWidth == 40, $"plan shape {plan.Lots.Count} lots, {plan.Bands} bands");
            TestRunner.Assert(plan.LotAt(0, 0) is { Band: -1 } && plan.Lots.Count(l => l.Band < 0) == 1, "the center lot must never dissolve");
            for (var band = 0; band < plan.Bands; band++)
            {
                var lots = plan.Lots.Where(l => l.Band == band).ToArray();
                TestRunner.Assert(lots.Length == 4, $"band {band} holds {lots.Length} lots");
                var ring = lots.Select(l => System.Math.Max(System.Math.Abs(l.I), System.Math.Abs(l.J))).Distinct().ToArray();
                TestRunner.Assert(ring.Length == 1 && ring[0] == (band < 4 ? 2 : 1), $"band {band} mixes rings or dissolves inside out");
            }
            var first = plan.Lots.First(l => l.Band == 0);
            TestRunner.Assert(plan.Stage(first, 10.99) == LotStage.Solid && plan.Stage(first, 11) == LotStage.Warning &&
                plan.Stage(first, 15) == LotStage.Gone, "band 0 must warn at 11 s and dissolve at 15 s");
            var (x, z) = plan.Center(first);
            TestRunner.Assert(plan.Solid(x, z, 14.9) && !plan.Solid(x, z, 15) && !plan.Solid(40.1, 0, 0) && plan.Solid(39.9, 0, 0),
                "solid ground must follow the schedule and the slab's edge");
            TestRunner.Assert(plan.Collapsed == 90 && plan.NextDissolve(0) == 15 && plan.NextDissolve(89.9) == 90 &&
                double.IsPositiveInfinity(plan.NextDissolve(90)) && plan.NextBand(29) == 1 && plan.NextBand(30) == 2, "next dissolve times");
            TestRunner.Assert(plan.Solid(0, 0, 1e6), "the last lot must stay");
            var again = new RingOutPlan(5, 16, 4, 15, 4, 7);
            TestRunner.Assert(plan.Lots.All(l => again.LotAt(l.I * 16, l.J * 16)!.Value.Band == l.Band), "the same seed must give the same plan");
            var other = Enumerable.Range(8, 6).Select(s => new RingOutPlan(5, 16, 4, 15, 4, (ulong)s))
                .Any(p => plan.Lots.Any(l => p.LotAt(l.I * 16, l.J * 16)!.Value.Band != l.Band));
            TestRunner.Assert(other, "seeds never changed the band order");
            var single = new RingOutPlan(3, 16, 3, 15, 4, 1);
            TestRunner.Assert(single.Bands == 3 && single.Lots.Where(l => l.Band >= 0).GroupBy(l => l.Band).All(g => g.Count() is 2 or 3),
                "an uneven ring must split into near-equal bands");
        });

        runner.Run("prototypes/ring out eliminates fallers and anyone standing where the rim dissolved", () =>
        {
            var rules = Ring(3);
            var lot = rules.Plan.Lots.First(l => l.Band == 0);
            var (x, z) = rules.Plan.Center(lot);
            TestRunner.Assert(Sample(rules, 101, Standing(1, x, z), Standing(2), Standing(3)).Count == 0, "nobody should be out at the start");
            TestRunner.Assert(Sample(rules, 112, Standing(1, x, z)).Count == 0 && rules.IsAlive(1), "a warning lot still holds its player");
            TestRunner.Assert(Sample(rules, 115, new ActorSample(1, x, 1.5, z)).Count == 0, "a body in the air over a gone lot is not out yet");
            var dissolved = Sample(rules, 115.1, Standing(1, x, z), Standing(2), Standing(3));
            TestRunner.Assert(dissolved.SequenceEqual(new ModeCommand[] { new ActorEliminated(1, 0, 3, EliminationCause.Dissolved) }),
                $"standing on dissolved ground: {string.Join("; ", dissolved)}");
            var fell = Sample(rules, 116, Standing(2, 0, 0, -2.5), Standing(3));
            TestRunner.Assert(fell.SequenceEqual(new ModeCommand[] { new ActorEliminated(2, 0, 2, EliminationCause.Fell), new RoundOver() }),
                $"falling below the slab, then the last one standing: {string.Join("; ", fell)}");
            TestRunner.Assert(rules.Over && rules.Place(3) == 1 && rules.Place(2) == 2 && rules.Place(1) == 3 && rules.PlacementsComplete,
                "placements must follow elimination order");
            TestRunner.Assert(Sample(rules, 117, Standing(3, 0, 0, -9)).Count == 0, "a decided round takes no more samples");
            var standings = rules.Snapshot().Standings;
            TestRunner.Assert(standings.Select(s => (s.ActorId, s.Score)).SequenceEqual(new[] { (3, 2), (2, 1), (1, 0) }), "standings rank placements");
            TestRunner.Assert(rules.Snapshot().Report.Any(r => r.Kind == ModeReportKind.Audit && r.Text.EndsWith("complete=True")), "audit");
        });

        runner.Run("prototypes/ring out credits a faller to whoever threw them within the window", () =>
        {
            var rules = Ring(5);
            rules.Handle(new ActorThrown(2, 1), At(101));
            rules.Handle(new ActorThrown(3, 1), At(101));
            rules.Handle(new ActorThrown(4, 4), At(101)); // Their own blast.
            rules.Handle(new ActorThrown(5, 1), At(101));
            rules.Handle(new ActorThrown(5, 0), At(102)); // A later throw with nobody to blame replaces the credit.
            var credited = Sample(rules, 107, Standing(2, 0, 0, -3));
            var late = Sample(rules, 107.01, Standing(3, 0, 0, -3));
            var self = Sample(rules, 107.02, Standing(4, 0, 0, -3));
            var replaced = Sample(rules, 107.03, Standing(5, 0, 0, -3));
            TestRunner.Assert(credited.OfType<ActorEliminated>().Single().CreditedTo == 1, "a fall 6 s after the throw must be credited");
            TestRunner.Assert(late.OfType<ActorEliminated>().Single().CreditedTo == 0, "a fall past the window must not be credited");
            TestRunner.Assert(self.OfType<ActorEliminated>().Single().CreditedTo == 0, "nobody is credited for their own fall");
            TestRunner.Assert(replaced.OfType<ActorEliminated>().Single().CreditedTo == 0, "the last throw decides the credit");
            TestRunner.Assert(rules.Credits(1) == 1 && rules.Over && rules.Place(1) == 1, "credit count and the winner");
            var feed = new RingOutHud().Present(new ModeHudInput(2, ModePhase.Round, 0, new Dictionary<int, int>(), [], false, rules, At(108))).Feed!;
            TestRunner.Assert(feed.Count == RingOutHud.FeedLength && feed[0] == "P1 knocked P2 off" && feed[^1] == "P5 fell", $"kill feed: {string.Join(" | ", feed)}");
        });

        runner.Run("prototypes/ring out places simultaneous eliminations together and survivors at the limit", () =>
        {
            var rules = Ring(4);
            var both = Sample(rules, 101, Standing(1, 0, 0, -3), Standing(2, 0, 0, -3), Standing(3), Standing(4));
            TestRunner.Assert(both.OfType<ActorEliminated>().All(e => e.Place == 3) && both.Count == 2 && !rules.Over, "two out together share third");
            var limit = Sample(rules, 280, Standing(3), Standing(4));
            TestRunner.Assert(limit.SequenceEqual(new ModeCommand[] { new RoundOver() }) && rules.Place(3) == 1 && rules.Place(4) == 1 &&
                rules.PlacementsComplete, "survivors at the round limit share first");

            var last = Ring(2);
            var together = Sample(last, 101, Standing(1, 0, 0, -3), Standing(2, 0, 0, -3));
            TestRunner.Assert(together.OfType<ActorEliminated>().All(e => e.Place == 1) && together.OfType<RoundOver>().Any(),
                "the last two out together share the win");
        });

        runner.Run("prototypes/ring out treats a departure as an elimination without credit, and ignores input outside the round", () =>
        {
            var rules = Ring(3);
            rules.Handle(new ActorThrown(2, 1), At(101));
            var left = rules.Handle(new ActorLeft(2), At(102));
            TestRunner.Assert(left.SequenceEqual(new ModeCommand[] { new ActorEliminated(2, 0, 3, EliminationCause.Left) }) && rules.Credits(1) == 0,
                "a departure is placed but never credited");
            TestRunner.Assert(rules.Snapshot().Standings.Single(s => s.ActorId == 2) is { Present: false }, "a departed actor keeps its row");
            var closed = rules.Handle(new ActorsSampled(new[] { Standing(1, 0, 0, -3) }), new ModeClock(ModePhase.RoundEnd, 1, 103, 103));
            TestRunner.Assert(closed.Count == 0 && rules.IsAlive(1), "samples outside play must not eliminate");
            var end = rules.Handle(new PhaseEntered(ModePhase.RoundEnd, RoundInfo.First), At(104));
            TestRunner.Assert(end.OfType<EndMatch>().Any() && rules.Place(1) == 1 && rules.Place(3) == 1 && rules.PlacementsComplete,
                "a round ended from outside places its survivors first");
            var fresh = new RingOutRules();
            fresh.Initialize(new ModeSetup(new[] { new ModeActor(1, "P1") }, new ModeWorld(ModeWorldSource.Void), RingTuners, 1));
            TestRunner.Assert(fresh.Handle(new ActorsSampled(new[] { Standing(1, 0, 0, -9) }), At(1)).Count == 0, "no eliminations before the round");
            var spill = fresh.Handle(new SpillRequested(new SpillTicket(1, 1, 1), SpillReason.Hit, 1), At(1));
            TestRunner.Assert(spill.Single() is SpillCanceled { Reason: SpillCancel.Closed }, "Ring Out has no bank to spill");
        });

        runner.Run("prototypes/king mints for a lone upright occupant and nothing while contested", () =>
        {
            var rules = King(3);
            var first = KingSample(rules, 0, InZone(rules, 1));
            TestRunner.Assert(first.OfType<ZoneMoved>().Single().Zone == 0 && rules.Holder == 1, "the first sample places zone 0 and its holder");
            TestRunner.Assert(KingSample(rules, 1.9, InZone(rules, 1)).Count == 0, "no mint before the interval");
            var mint = KingSample(rules, 2, InZone(rules, 1));
            TestRunner.Assert(mint.SequenceEqual(new ModeCommand[] { new ScoreChanged(1, 1, 1) }), $"one mint at 2 s: {string.Join("; ", mint)}");
            TestRunner.Assert(KingSample(rules, 6.5, InZone(rules, 1)).OfType<ScoreChanged>().Count() == 2, "a coarse sample mints every interval it covers");
            TestRunner.Assert(KingSample(rules, 7, InZone(rules, 1), InZone(rules, 2, 3)).Count == 0 && rules.Contested && rules.Holder == 0, "contest");
            TestRunner.Assert(KingSample(rules, 12, InZone(rules, 1), InZone(rules, 2, -3)).Count == 0, "a contested zone mints nothing");
            TestRunner.Assert(KingSample(rules, 13, InZone(rules, 1), InZone(rules, 2, 9)).Count == 0 && rules.Holder == 1, "the rival left");
            TestRunner.Assert(KingSample(rules, 14.9, InZone(rules, 1)).Count == 0 && KingSample(rules, 15, InZone(rules, 1)).Count == 1,
                "the holder's count restarts after a contest");
            TestRunner.Assert(KingSample(rules, 16, InZone(rules, 1), InZone(rules, 2, 1, down: true)).Count == 0 && rules.Holder == 1,
                "a body lying in the zone neither holds nor contests");
            TestRunner.Assert(KingSample(rules, 17, InZone(rules, 1, 0, down: true), InZone(rules, 2)).Count == 0 && rules.Holder == 2,
                "a thrown holder loses the zone");
            var spot = rules.SpotOf(0);
            TestRunner.Assert(KingSample(rules, 20, new ActorSample(2, spot.X, spot.Y + 4, spot.Z)).Count == 0 && rules.Holder == 0,
                "the zone has a height");
            TestRunner.Assert(rules.MintedBy(1) == 4 && rules.Bank.Bank(1) == 4 && rules.Bank.Conserved, "mint totals");
        });

        runner.Run("prototypes/king drops a departing holder mid-hold and keeps the bank conserved", () =>
        {
            var rules = King(2);
            KingSample(rules, 0, InZone(rules, 1));
            KingSample(rules, 2, InZone(rules, 1));
            rules.Handle(new ActorLeft(1), KingAt(3));
            TestRunner.Assert(rules.Holder == 0, "a departed holder must release the zone");
            TestRunner.Assert(KingSample(rules, 5, InZone(rules, 1)).Count == 0 && rules.Holder == 0, "a stale sample of a departed actor holds nothing");
            KingSample(rules, 6, InZone(rules, 2));
            rules.Handle(new TankDetonated(2, TankOrigin.Map), KingAt(6.5));
            var minted = KingSample(rules, 8, InZone(rules, 2));
            TestRunner.Assert(minted.SequenceEqual(new ModeCommand[] { new ScoreChanged(2, 2, 1) }), "mints add to detonation banks");
            var ticket = new SpillTicket(2, 1, 1);
            rules.Handle(new SpillRequested(ticket, SpillReason.Thrown, 2), KingAt(9));
            var commit = rules.Handle(new SpillRegistered(ticket), KingAt(9));
            TestRunner.Assert(commit.OfType<SpillCommitted>().Any() && rules.Bank.Bank(2) == 0, "spills work as in Tank Bank");
            TestRunner.Assert(rules.Handle(new TankMinted(2), KingAt(9)).Count == 0, "only the zone mints");
            var audit = rules.Snapshot().Report.Single(r => r.Kind == ModeReportKind.Audit).Text;
            TestRunner.Assert(audit == "king audit initial=0 map=1 drops=0 minted=2 spills=2 banked=1 expected=1 ok=True", audit);
            TestRunner.Assert(rules.Snapshot().Standings.Single(s => s.ActorId == 1) is { Present: false, Score: 1 }, "the departed keep their bank");
        });

        runner.Run("prototypes/king relocates on schedule to a pile spot in range, deterministically", () =>
        {
            var rules = King(1);
            KingSample(rules, 0, InZone(rules, 1));
            TestRunner.Assert(!rules.Warning(41.9) && rules.Warning(42) && rules.ZoneAt(44.99) == 0 && rules.ZoneAt(45) == 1 && rules.UntilMove(40) == 5,
                "the warning precedes each move by its tuner");
            var moved = KingSample(rules, 45, InZone(rules, 1));
            TestRunner.Assert(moved.OfType<ZoneMoved>().Single() is { Zone: 1 } && rules.Holder == 0, "the move releases the holder");
            for (var zone = 0; zone < 12; zone++)
            {
                var (a, b) = (rules.SpotOf(zone), rules.SpotOf(zone + 1));
                var d = System.Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
                TestRunner.Assert(d >= 15 && d <= 25, $"zone {zone + 1} moved {d:0.0} m");
            }
            var twin = King(1);
            TestRunner.Assert(Enumerable.Range(0, 12).All(z => twin.SpotOf(z) == rules.SpotOf(z)), "the same seed must give the same schedule");
            var far = King(1, 3, new ModeSpot[] { new(0, 0, 0), new(100, 0, 0), new(5, 0, 0) });
            var next = Enumerable.Range(0, 4).Select(far.SpotOf).ToArray();
            TestRunner.Assert(next.Zip(next.Skip(1)).All(p => p.First != p.Second), "with nothing in range it still moves to the nearest miss");
            var lone = King(1, 3, new ModeSpot[] { new(4, 0, 4) });
            TestRunner.Assert(lone.SpotOf(3) == new ModeSpot(4, 0, 4), "a single spot keeps the zone in place");
            var hud = new KingHud().Present(new ModeHudInput(1, ModePhase.Round, 100, new Dictionary<int, int> { [1] = 0 }, [], true, rules, KingAt(88)));
            TestRunner.Assert(hud.Objectives.Count == 2 && hud.Banner == "THE ZONE MOVES IN 2", $"warning HUD: {hud.Banner}, {hud.Objectives.Count}");
        });
    }
}
