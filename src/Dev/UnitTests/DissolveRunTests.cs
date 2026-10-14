using System;
using System.Collections.Generic;
using System.Linq;
using Propane.Modes;

namespace Propane.Dev.UnitTests;

/// <summary>Dissolve Run's pure rules and presenter, headless: the front, the catch, pushes, chains and the score.</summary>
[UnitSuite]
public static class DissolveRunTests
{
    private static readonly ModeTuners Tuners = new(new Dictionary<string, double>
    {
        [DissolveRunRules.DissolveFrontStartGap] = 25,
        [DissolveRunRules.DissolveFrontSpeed] = 1.5,
        [DissolveRunRules.DissolveFrontMaxSpeed] = 5,
        [DissolveRunRules.DissolveFrontRampSeconds] = 180,
        [DissolveRunRules.DissolveFrontPush] = 1.5,
        [DissolveRunRules.DissolveFrontMaxGap] = 60,
        [DissolveRunRules.DissolveFallSeconds] = 2.5,
        [DissolveRunRules.DissolveTankPoints] = 10,
        [DissolveRunRules.DissolveChainMin] = 2,
        [DissolveRunRules.DissolveChainWindow] = 1,
    });

    private static ModeClock At(double now) => new(ModePhase.Round, 1, now, double.PositiveInfinity);

    private static DissolveRunRules Run(double startX = 0, ModeTuners? tuners = null)
    {
        var rules = new DissolveRunRules();
        rules.Initialize(new ModeSetup(new[] { new ModeActor(1, "Runner") }, new ModeWorld(ModeWorldSource.SuburbPlan, 1, 0, new[] { new ModeSpot(startX, 0, 0) }),
            tuners ?? Tuners, 1));
        rules.Handle(new PhaseEntered(ModePhase.Round, new RoundInfo(1, 1, RoundTimer.CountUp, 0, 0)), At(0));
        return rules;
    }

    private static IReadOnlyList<ModeCommand> At(DissolveRunRules rules, double now, double x) =>
        rules.Handle(new ActorsSampled(new[] { new ActorSample(1, x, 0, 0) }), At(now));

    /// <summary>The front's position by the closed form of its ramped speed, without pushes.</summary>
    private static double Front(double t, double start = -25) => start + 5 * t + (1.5 - 5) * 180 * (1 - Math.Exp(-t / 180));

    public static void Register(TestRunner runner)
    {
        runner.Run("dissolve run/the front starts behind the runner and ramps toward its top speed, however it is stepped", () =>
        {
            var unbounded = Tuners.With(DissolveRunRules.DissolveFrontMaxGap, 1e6);
            var coarse = Run(tuners: unbounded);
            var fine = Run(tuners: unbounded);
            TestRunner.Assert(coarse.Front == -25 && Math.Abs(coarse.Speed(0) - 1.5) < 1e-9, "starts 25 m behind at 1.5 m/s");
            At(coarse, 120, 1000);
            for (var t = 1 / 120.0; t <= 120 + 1e-9; t += 1 / 120.0) At(fine, t, 1000);
            TestRunner.Assert(Math.Abs(coarse.Front - Front(120)) < 1e-6 && Math.Abs(fine.Front - Front(120)) < 1e-6, $"front {coarse.Front} / {fine.Front} vs {Front(120)}");
            TestRunner.Assert(coarse.Speed(1e5) < 5 + 1e-9 && coarse.Speed(1e5) > 4.999 && coarse.Speed(60) > coarse.Speed(30), "speed rises toward 5 m/s");
        });

        runner.Run("dissolve run/the front never trails the furthest point by more than the max gap", () =>
        {
            var rules = Run();
            At(rules, 1, 200);
            TestRunner.Assert(Math.Abs(rules.Front - 140) < 1e-9 && rules.Best == 200, $"front {rules.Front}");
            At(rules, 2, 150); // Back down the street: the furthest point holds the front.
            TestRunner.Assert(rules.Front >= 140 && rules.Best == 200 && rules.Meters == 200, "the furthest point stays");
        });

        runner.Run("dissolve run/a runner who stops is caught, falls, and the run ends after the fall", () =>
        {
            var rules = Run();
            double t = 0;
            IReadOnlyList<ModeCommand> commands = Array.Empty<ModeCommand>();
            while (!rules.Caught && t < 60) commands = At(rules, t += 0.01, 0);
            TestRunner.Assert(rules.Caught && Front(t) >= 0 && Front(t - 0.01) < 0, $"caught at {t:F2}s, when the front reached the runner");
            TestRunner.Assert(commands.OfType<RunCaught>().Single() is { ActorId: 1, Meters: 0 }, "the catch is announced once");
            TestRunner.Assert(!rules.Over && !At(rules, rules.CaughtAt!.Value + 2.4, 0).OfType<RoundOver>().Any(), "falling");
            TestRunner.Assert(At(rules, rules.CaughtAt!.Value + 2.5, 0).OfType<RoundOver>().Any() && rules.Over, "the round ends after the fall");
            TestRunner.Assert(!At(rules, 100, 0).Any() && !rules.Permits(ModeGate.World, At(100)), "nothing after the end");
        });

        runner.Run("dissolve run/each tank pushes the front back once and scores; chains score n squared once closed", () =>
        {
            var rules = Run();
            At(rules, 1, 10);
            var front = rules.Front;
            foreach (var chain in new uint[] { 1, 1, 1, 2, 3, 3 }) rules.Handle(new RunTankDetonated(1, chain), At(1));
            TestRunner.Assert(Math.Abs(rules.Front - (front - 6 * 1.5)) < 1e-9 && rules.Tanks == 6 && rules.Pushes == 6, "six pushes of 1.5 m");
            TestRunner.Assert(rules.OpenChain == 3 && rules.Bonus == 0 && rules.Score == 10 + 60, $"open chain {rules.OpenChain}, score {rules.Score}");
            var closed = At(rules, 1.9, 10).OfType<ChainClosed>().ToArray();
            TestRunner.Assert(closed.Length == 0, "chains stay open inside the window");
            closed = At(rules, 2.01, 10).OfType<ChainClosed>().ToArray();
            TestRunner.Assert(closed.Select(c => (c.Chain, c.Tanks, c.Bonus)).SequenceEqual(new[] { (1u, 3, 9), (2u, 1, 0), (3u, 2, 4) }),
                string.Join(" ", closed.Select(c => $"{c.Chain}:{c.Tanks}:{c.Bonus}")));
            TestRunner.Assert(rules.Bonus == 13 && rules.BestChain == 3 && rules.Score == 10 + 60 + 13 && rules.OpenChain == 0, $"score {rules.Score}");
            rules.Handle(new RunTankDetonated(1, 1), At(2.5)); // A reused id after its chain closed starts a new count.
            At(rules, 4, 10);
            TestRunner.Assert(rules.Chains.Count == 4 && rules.Chains[^1].Tanks == 1, "a closed chain is never reopened");
        });

        runner.Run("dissolve run/a catch closes open chains, and nothing scores after it", () =>
        {
            var rules = Run();
            rules.Handle(new RunTankDetonated(1, 7), At(0.5));
            rules.Handle(new RunTankDetonated(1, 7), At(0.6));
            At(rules, 0.7, -100);
            TestRunner.Assert(rules.Caught && rules.Bonus == 4 && rules.Chains.Single().Tanks == 2, "the open chain counts at the catch");
            TestRunner.Assert(!rules.Handle(new RunTankDetonated(1, 8), At(0.8)).Any() && rules.Tanks == 2, "a caught runner scores nothing more");
            TestRunner.Assert(rules.Score == rules.Meters + 20 + 4, $"score {rules.Score}");
        });

        runner.Run("dissolve run/the chain minimum and tank points come from the tuners", () =>
        {
            var rules = Run(tuners: Tuners.With(DissolveRunRules.DissolveChainMin, 4).With(DissolveRunRules.DissolveTankPoints, 3));
            foreach (var _ in Enumerable.Range(0, 3)) rules.Handle(new RunTankDetonated(1, 1), At(1));
            At(rules, 3, 0);
            TestRunner.Assert(rules.Bonus == 0 && rules.Score == 9, $"three under a minimum of four score only their points: {rules.Score}");
        });

        runner.Run("dissolve run/meters count from the start spot, and the HUD shows the score, the gap and the open chain", () =>
        {
            var rules = Run(startX: -30);
            At(rules, 1, 12.6);
            TestRunner.Assert(rules.Meters == 42 && rules.StartX == -30, $"meters {rules.Meters}");
            rules.Handle(new RunTankDetonated(1, 1), At(1));
            rules.Handle(new RunTankDetonated(1, 1), At(1));
            var hud = new DissolveRunHud().Present(new ModeHudInput(1, ModePhase.Round, 0, new Dictionary<int, int>(), Array.Empty<Godot.Vector3>(), false, rules, At(1)));
            TestRunner.Assert(hud.Counters[0] == new HudCounter(rules.Score, "SCORE") && hud.Counters[1].Value == 42 && hud.Counters[2] == new HudCounter(2, "TANKS"),
                "score, meters, tanks");
            TestRunner.Assert(hud.Banner == "CHAIN ×2" && hud.Bar is { Caption: var caption } && caption.StartsWith("FRONT ") && hud.Timer?.Kind == RoundTimer.CountUp,
                $"banner {hud.Banner}, bar {hud.Bar}");
            At(rules, 3, 12.6);
            hud = new DissolveRunHud().Present(new ModeHudInput(1, ModePhase.Round, 0, new Dictionary<int, int>(), Array.Empty<Godot.Vector3>(), false, rules, At(3)));
            TestRunner.Assert(hud.Banner == "" && hud.Feed!.Single() == "Chain of 2 +4", "the closed chain enters the feed");
            var report = rules.Snapshot().Report[0].Text;
            TestRunner.Assert(report.StartsWith("dissolve run meters=42 tanks=2 chains=1 bonus=4"), report);
        });
    }
}
