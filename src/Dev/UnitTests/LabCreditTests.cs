using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Godot;
using Propane.Core;
using Propane.Lab;
using Propane.Modes;
using Propane.Player;
using Propane.Tank;
using static Propane.Dev.UnitTests.Fixtures.LabRunnerInspection;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev.UnitTests;

/// <summary>Runner attribution beyond the frozen Bank trace, also runnable unchanged against the parent.</summary>
// Keep new native fixtures after the existing synchronous suites and their warmed allocation checks.
[UnitSuite(1)]
public static class LabCreditTests
{
    public static void Register(TestRunner runner)
    {
        foreach (var scenario in new[] { "duel", "king" })
            runner.Run($"lab credit/{scenario}: direct, chain, drop and uncredited puncture", () => Credit(runner, scenario));
    }

    private static void Credit(TestRunner host, string scenario)
    {
        using var tuning = Tuning.LoadBaked();
        tuning.FixedSeed = 23;
        tuning.LabDuelPiles = 3;
        tuning.LabDuelPileCount = 1;
        tuning.LabPrototypeBots = 1;
        var commands = new List<ModeCommand>();
        var lab = new LabSession
        {
            Scenario = LabScenarios.Get(scenario),
            Template = tuning,
            EnvironmentKind = LabEnvironment.Void,
            CreateLocalView = false,
            TestBankBots = 1,
            TestRandomSeed = 23,
            TestSeedPhysicsRandom = true,
            TestObserveCommand = commands.Add,
        };
        host.AddChild(lab);
        try
        {
            var adapter = (Node?)lab.Bank ?? lab.Prototype!;
            var rules = lab.Bank?.Rules ?? ((KingRules)lab.Prototype!.Rules).Bank;
            var scores = lab.Bank?.Scores ?? lab.Prototype!.Scores;
            var actors = lab.Session.Actors.Select(a => a.Character).ToArray();
            for (var i = 0; i < actors.Length; i++) actors[i].Teleport(new Vector3(-500, 0.05f, 500 + 20 * i), 0);
            var tanks = lab.LiveTanks.Take(3).ToArray();
            Assert(tanks.Length == 3, "credit fixture needs three map tanks");
            for (var i = 0; i < tanks.Length; i++)
            {
                tanks[i].GlobalPosition = new Vector3(i * 30, 0, -200);
                tanks[i].Freeze = true;
            }

            void Shot(PlayerCharacter actor, PropaneTank tank)
            {
                Impact(actor, tank);
                Assert(tank.TakeShot(tank.GlobalTransform * PropaneTank.LocalCenter, Vector3.Up, Vector3.Forward), "fixture shot ignored");
            }
            Shot(actors[0], tanks[0]);
            Assert(scores[1] == 0 && scores[2] == 0 && Shots(adapter).Count == 0, "a puncture kept its shooter or scored");
            Shot(actors[1], tanks[0]);
            Assert(tanks[0].BlastRequest == 2 && scores[2] == 1, "the last direct shooter did not get credit");

            Shot(actors[1], tanks[1]);
            Impact(actors[1], tanks[1]); // A pending direct contact must lose to the initiating chain.
            tanks[1].ChainHit(tanks[1].GlobalPosition + Vector3.Right, 1, cause: 1);
            lab.World.Clock.Advance(2);
            Assert(tanks[1].State == PropaneTank.TankState.Exploded && tanks[1].BlastRequest == 1 && scores[1] == 1,
                "the chain did not override the last direct shooter");

            adapter.GetType().GetMethod("Spill", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(adapter, new object[] { actors[1], SpillReason.Hit, 1, new Vector3(200, 0.1f, -200), Vector3.Zero });
            Assert(scores[2] == 0 && rules.Spilled == 1, "the spill was not committed");
            var drop = lab.LiveTanks.Single(t => !tanks.Contains(t) && Drops(adapter).Contains(t));
            GD.Print($"[lab spill] {scenario}: " + JsonSerializer.Serialize(new
            {
                Position = Bits(drop.GlobalPosition),
                BasisX = Bits(drop.GlobalBasis.X),
                BasisY = Bits(drop.GlobalBasis.Y),
                BasisZ = Bits(drop.GlobalBasis.Z),
                Velocity = Bits(drop.LinearVelocity),
                Spin = Bits(drop.AngularVelocity),
                RandomState = lab.World.Random.State,
            }));
            drop.Freeze = true;
            lab.World.Clock.Advance(lab.World.Tuning.DropInvulnerableTime + 0.1);
            Shot(actors[0], drop);
            Shot(actors[0], drop);
            Assert(drop.BlastRequest == 1 && scores[1] == 2 && rules.MapDetonations == 2 && rules.DropDetonations == 1 && rules.Conserved,
                "a re-banked drop changed its origin or credit");

            Shot(actors[1], tanks[2]);
            tanks[2].DetonateRemote(tanks[2].GlobalTransform * PropaneTank.LocalCenter);
            Assert(tanks[2].BlastRequest == 0 && scores[1] == 2 && scores[2] == 0 && Shots(adapter).Count == 0 && Drops(adapter).Count == 0,
                "an uncredited detonation reused puncture attribution or retained a drop");
            GD.Print($"[lab credit] {scenario}: " + string.Join(" | ", commands) +
                $" | map={rules.MapDetonations} drops={rules.DropDetonations} spills={rules.Spilled} banked={rules.Banked} shots={Shots(adapter).Count} drop_refs={Drops(adapter).Count}");
        }
        finally { lab.Session.Dispose(); lab.Free(); }
    }

    private static int[] Bits(Vector3 value) => new[]
        { BitConverter.SingleToInt32Bits(value.X), BitConverter.SingleToInt32Bits(value.Y), BitConverter.SingleToInt32Bits(value.Z) };
    private static void Impact(PlayerCharacter actor, PropaneTank tank) =>
        ((Action<ShotReport>?)typeof(PlayerCharacter).GetField("ShotImpacting", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(actor))!
            (new ShotReport(Vector3.Zero, Vector3.Zero, Vector3.Up, Vector3.Forward, tank, true));
}
