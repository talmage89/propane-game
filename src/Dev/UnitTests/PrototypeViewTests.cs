using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Propane.Core;
using Propane.Dev.UnitTests.Fixtures;
using Propane.Modes;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev.UnitTests;

/// <summary>Independent pre-extraction presenter oracles, including clocks read between accepted facts.</summary>
[UnitSuite]
public static class PrototypeViewTests
{
    public static void Register(TestRunner host)
    {
        foreach (var mode in new[] { ModeRegistry.RingOut, ModeRegistry.ShortFuse, ModeRegistry.DissolveRun })
            host.Run($"prototype views/{mode.Id}: complete HUD values match the parent oracle", () => Compare(mode));
    }

    private static void Compare(ModeDefinition mode)
    {
        using var tuning = Tuning.LoadBaked();
        var rules = mode.CreateRules();
        var roster = Enumerable.Range(1, 3).Select(i => new ModeActor(i, $"P{i}")).ToArray();
        rules.Initialize(new ModeSetup(roster, new ModeWorld(mode.World, Spots: new[] { new ModeSpot(0, 0, 0) }), mode.CopyTuners(tuning), 23));
        var clock = new ModeClock(ModePhase.Round, 1, 100, 280);
        var presenter = mode.CreateHud();
        void Check(string label)
        {
            foreach (var actor in new[] { 1, 2, 3, 99 })
            {
                var input = new LegacyPrototypeInput(actor, clock.Phase, 60, rules, clock);
                var expected = rules switch
                {
                    RingOutRules => new LegacyRingOutHud().Present(input),
                    ShortFuseRules => new LegacyShortFuseHud().Present(input),
                    DissolveRunRules => new LegacyDissolveRunHud().Present(input),
                    _ => throw new InvalidOperationException(),
                };
                var actual = presenter.Present(PrototypePresentation.Input(actor, clock.Phase, 60,
                    new Dictionary<int, int>(), Array.Empty<Godot.Vector3>(), false, rules, clock));
                var old = JsonSerializer.Serialize(expected);
                var current = JsonSerializer.Serialize(actual);
                Assert(current == old, $"{mode.Id}/{label}/{actor}: {old} -> {current}");
                Godot.GD.Print($"[prototype view] {mode.Id}/{label}/{actor} {current}");
            }
        }
        void Accept(AcceptedEvent fact, double now)
        {
            clock = clock with { Now = now };
            rules.Handle(fact, clock);
        }
        Check("before-round");
        Accept(new PhaseEntered(ModePhase.Round, new RoundInfo(1, 1, mode.Timer, 100, 180)), 100);
        Check("start");
        if (rules is RingOutRules ring)
        {
            clock = clock with { Now = 100 + ring.Plan.WarnsAt(0) };
            Check("warning");
            Accept(new ActorThrown(2, 1), clock.Now);
            Accept(new ActorsSampled(new[] { new ActorSample(2, 0, -10, 0) }), clock.Now + .1);
            Check("credited-fall");
            Accept(new ActorLeft(3), clock.Now + .1);
        }
        else if (rules is ShortFuseRules)
        {
            Accept(new BlastAccepted(1, 1, 101), 101);
            Accept(new BlastHitReported(2, 1, 1, 0), 101);
            Check("hurt");
            clock = clock with { Now = 110 };
            Check("read-time-regen");
            Accept(new BulletHitReported(1, 1, 1, 2, 1, 101.1), 101.1);
            Accept(new BulletHitReported(1, 1, 2, 2, 1, 101.2), 101.2);
            Accept(new ActorsSampled(Array.Empty<ActorSample>()), 101.5);
            Check("spectating");
            Accept(new ActorLeft(3), 102);
        }
        else
        {
            Accept(new ActorsSampled(new[] { new ActorSample(1, 30, 0, 0) }), 102);
            Accept(new RunTankDetonated(1, 1), 102);
            Accept(new RunTankDetonated(1, 1), 102);
            Check("open-chain");
            Accept(new ActorsSampled(new[] { new ActorSample(1, 40, 0, 0) }), 105);
            Check("closed-chain");
            Accept(new ActorsSampled(new[] { new ActorSample(1, -100, 0, 0) }), 106);
            Check("caught");
            Accept(new ActorsSampled(new[] { new ActorSample(1, -100, 0, 0) }), 110);
        }
        Check("decided");
        clock = clock with { Phase = ModePhase.RoundEnd };
        Accept(new PhaseEntered(ModePhase.RoundEnd, new RoundInfo(1, 1, mode.Timer, 100, 180)), clock.Now);
        Check("round-end");
        Accept(new PhaseEntered(ModePhase.Round, new RoundInfo(2, 2, mode.Timer, 200, 180)), 200);
        Check("replacement");
    }
}
