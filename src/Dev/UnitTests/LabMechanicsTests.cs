using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using Propane.Bots;
using Propane.Core;
using Propane.Lab;
using Propane.Modes;
using Propane.Tank;
using static Propane.Dev.UnitTests.Fixtures.LabRunnerInspection;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev.UnitTests;

/// <summary>Runner-level preservation and collection assertions; identical source runs before/after B4b.</summary>
[UnitSuite(1)]
public static class LabMechanicsTests
{
    public static void Register(TestRunner host)
    {
        foreach (var id in new[] { "duel", "ringout", "king", "shortfuse", "dissolve" })
        {
            host.Run($"lab mechanics/{id}: live difficulty uses attachment slots", () => Difficulty(host, id));
            host.Run($"lab mechanics/{id}: subscriptions and state retire with the runner", () =>
            {
                var (runner, state, wrapper) = Retire(host, id);
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                Assert(!runner.IsAlive && !state.IsAlive && !wrapper.IsAlive, "a retired runner or its bookkeeping owner stayed reachable");
            });
        }
        foreach (var id in new[] { "duel", "king" })
            host.Run($"lab mechanics/{id}: tuner sampling keeps its original boundary", () => Sampling(host, id));
    }

    private static LabSession Enter(TestRunner host, string id)
    {
        using var tuning = Tuning.LoadBaked();
        tuning.FixedSeed = 23;
        tuning.LabDuelPiles = 3;
        tuning.LabDuelPileCount = 1;
        tuning.LabPrototypeBots = 3;
        tuning.HitDropCount = 1;
        tuning.HitstopDuration = 0;
        var lab = new LabSession
        {
            Scenario = LabScenarios.Get(id),
            Template = tuning,
            EnvironmentKind = id is "shortfuse" or "dissolve" ? LabEnvironment.Suburb : LabEnvironment.Void,
            CreateLocalView = false,
            TestBankBots = 3,
            TestRandomSeed = 23,
            TestSeedPhysicsRandom = true,
        };
        host.AddChild(lab);
        return lab;
    }

    private static Node Runner(LabSession lab) => (Node?)lab.Bank ?? lab.Prototype!;

    private static void Tick(LabSession lab)
    {
        if (lab.Bank is { } bank) bank._PhysicsProcess(1d / 120);
        else lab.Prototype!._PhysicsProcess(1d / 120);
    }

    private static void Difficulty(TestRunner host, string id)
    {
        var lab = Enter(host, id);
        try
        {
            if (id == "dissolve") lab.Prototype!.AttachBot(lab.Player);
            var brains = ((IEnumerable)Field(Runner(lab), "brains")).Cast<ITuple>().Select(t => (IBotGoalPolicy)t[0]!).ToArray();
            Assert(brains.Length == (id == "dissolve" ? 1 : 3), "difficulty fixture lost attached brains");
            var tuning = lab.World.Tuning;
            foreach (var offset in new[] { 0, 1, 2, 3 })
            {
                tuning.LabHardOffset = offset;
                tuning.LabHardBots = 1;
                tuning.LabBotSkill = tuning.BotEasySkill;
                tuning.BotHardChasesPoints = true;
                Tick(lab);
                for (var i = 0; i < brains.Length; i++)
                {
                    Assert(brains[i].Skill == (i == offset ? tuning.BotHardSkill : tuning.BotEasySkill), "difficulty followed rotating turn instead of attachment slot");
                    if (id is "duel" or "king") Assert(brains[i].PointsDriven == (i == offset), "points policy changed selected slots");
                }
            }
            tuning.LabBotSkill = tuning.BotHardSkill;
            foreach (var points in new[] { false, true })
            {
                tuning.BotHardChasesPoints = points;
                Tick(lab);
                Assert(brains.All(b => b.Skill == tuning.BotHardSkill), "live global Hard skill did not apply");
                if (id is "duel" or "king") Assert(brains.All(b => b.PointsDriven == points), "live global points toggle did not apply");
            }
            GD.Print($"[lab mechanics] {id}: difficulty slots=0,1,2,3 ticks=6 brains={brains.Length} rng={lab.World.Random.State}");
        }
        finally { lab.Session.Dispose(); lab.Free(); }
    }

    private static void Sampling(TestRunner host, string id)
    {
        var lab = Enter(host, id);
        try
        {
            var runner = Runner(lab);
            var bank = lab.Bank?.Rules ?? ((KingRules)lab.Prototype!.Rules).Bank;
            lab.World.Tuning.HitDropCount = 2;
            Tick(lab);
            Assert(((ModeTuners)Field(bank, "tuners")).Int(TankBankRules.HitDropCount) == (id == "duel" ? 1 : 2),
                "Bank must sample on spill; prototypes must sample each tick");
            var clock = lab.Bank != null ? new ModeClock(ModePhase.Round, 1, 0, 60) : lab.Prototype!.Clock;
            foreach (var command in bank.Handle(new TankDetonated(1, TankOrigin.Map), clock)) Invoke(runner, "Apply", (object)new ModeCommand[] { command });
            Invoke(runner, "Spill", lab.Player, SpillReason.Hit, 1, new Vector3(200, .1f, -200), Vector3.Zero);
            Assert(bank.Spilled == 1 && bank.Conserved && ((ModeTuners)Field(bank, "tuners")).Int(TankBankRules.HitDropCount) == 2,
                "successful spill did not keep live tuners and conservation");
            lab.World.Tuning.HitDropCount = 3;
            Invoke(runner, "Spill", lab.Player, SpillReason.Hit, 1, Vector3.Zero, Vector3.Zero);
            Assert(((ModeTuners)Field(bank, "tuners")).Int(TankBankRules.HitDropCount) == 2,
                "empty spill sampled tuners before its count guard");
            GD.Print($"[lab mechanics] {id}: sampling tick/spill/empty preserved spills={bank.Spilled} rng={lab.World.Random.State}");
        }
        finally { lab.Session.Dispose(); lab.Free(); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Runner, WeakReference State, WeakReference Wrapper) Retire(TestRunner host, string id)
    {
        var lab = Enter(host, id);
        var runner = Runner(lab);
        var state = TankState(runner);
        var tank = lab.LiveTanks.First();
        Shots(runner)[tank] = 1;
        Drops(runner).Add(tank);
        lab.Session.Dispose();
        Assert(Shots(runner).Count == 0 && Drops(runner).Count == 0 &&
            (lab.Bank?.TrackingCount ?? lab.Prototype!.TrackingCount) == 0, "shutdown retained tank facts or subscriptions");
        lab.Session.Dispose();
        lab.Free();
        return (new WeakReference(runner), new WeakReference(state), new WeakReference(tank));
    }

    private static void Invoke(Node runner, string method, params object[] arguments) =>
        runner.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(runner, arguments);
}
