using System;
using System.IO;
using System.Reflection;
using Godot;
using Propane.Bots;
using Propane.Dev.UnitTests.Fixtures;

namespace Propane.Dev.UnitTests;

/// <summary>Parent-compatible departure differential through the complete service/brain/read callback.</summary>
[UnitSuite]
public static class ActorTacticsTests
{
    public static void Register(TestRunner runner)
    {
        using var output = DevArgs.Get("actor-tactics-transcript") is { } path ? new StreamWriter(path) : null;
        foreach (var scenario in new[] { "combat", "cover", "points" })
            runner.Run($"actor tactics/{scenario} retains departure decisions and RNG", () =>
            {
                using var world = new TacticalWorld(runner.World, scenario);
                using IBotGoalPolicy brain = scenario == "points" ? new KingBot(23) : new BankingBot(23);
                brain.PointsDriven = scenario == "points";
                brain.Skill = 1;
                brain.Prepare(world);
                var format = typeof(BotTacticsTests).GetMethod("Intent", BindingFlags.Static | BindingFlags.NonPublic)!;
                var rng = (RandomNumberGenerator)ActorLifetimeFixture.Field(brain, "rng");
                for (var tick = 0; tick < 180; tick++)
                {
                    world.Step(tick);
                    if (tick == 90) world.RemoveEnemy(brain);
                    brain.Think(world, 1f / 120);
                    var intent = brain.Read();
                    output?.WriteLine($"{scenario} {tick} {format.Invoke(null, new object[] { intent })} {brain.Status} {brain.NavigationDiagnostics} {rng.State} {world.Services.RayLines} {world.Services.NavigationQueries}");
                }
                TestRunner.Assert(world.RemoteBodies.Count == 0 && !GodotObject.IsInstanceValid(world.Enemy), "departure differential did not remove its native rival");
            });
    }
}
