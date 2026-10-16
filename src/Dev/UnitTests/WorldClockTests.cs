using System;
using Godot;
using Propane.Core;
using Propane.Player;
using Propane.Tank;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev.UnitTests;

[UnitSuite]
public static class WorldClockTests
{
    public static void Register(TestRunner runner)
    {
        runner.Run("world/physical events use their own world's random factory", () =>
        {
            using var world = new WorldContext(runner, Tuning.SavedTemplate);
            var state = world.Random.State;
            using (var ordinary = world.CreatePhysicsRandom())
                Assert(world.Random.State == state, "ordinary physics entropy consumed the world's shared random stream");
            var calls = 0;
            world.CreatePhysicsRandom = () =>
            {
                calls++;
                return new RandomNumberGenerator { Seed = 23 };
            };
            Blast.Detonate(world, Vector3.One * 1000, null, chain: false);
            Assert(calls == 1, "blast spin bypassed the world's factory");
            DebrisQueue.Enqueue(world, world.Spawn, Transform3D.Identity, Vector3.Zero, Vector3.Zero);
            Assert(calls == 2, "offline debris scatter bypassed the world's factory");
            var player = new PlayerCharacter { World = world, ActorId = 1, Ammo = AmmoState.Starting(world.Tuning) };
            world.Root.AddChild(player);
            try
            {
                player.Collapse();
                Assert(player.IsRagdolled && calls == 3, "ragdoll flail bypassed the world's factory");
                using var other = new WorldContext(runner, Tuning.SavedTemplate);
                using var random = other.CreatePhysicsRandom();
                Assert(calls == 3, "a world's replay factory leaked into another world");
            }
            finally { player.Free(); }
        });
        foreach (var speed in new[] { 0.1, 8.0 })
        {
            runner.Run($"world/clock timer at simulation speed {speed}", () =>
            {
                using var world = new WorldContext(runner, Tuning.SavedTemplate);
                var tank = PropaneTank.Create(world);
                world.Root.AddChild(tank);
                try
                {
                    tank.MakeInvulnerable(2, Colors.White);
                    var clock = world.Clock;
                    var fired = 0;
                    clock.Schedule(2, () => fired++);
                    var wall = 0.0;
                    while (clock.Now + speed / 60 < 2)
                    {
                        clock.Advance(speed / 60);
                        tank._PhysicsProcess(speed / 60);
                        Assert(tank.Invulnerable, "gameplay shield expired before two simulation seconds");
                        wall += 1.0 / 60;
                        Assert(fired == 0, "timer fired before two simulation seconds");
                    }
                    clock.Advance(speed / 60);
                    tank._PhysicsProcess(speed / 60);
                    Assert(!tank.Invulnerable, "gameplay shield did not expire on simulation time");
                    Assert(fired == 1 && Math.Abs(clock.Now - 2) <= speed / 60, "timer did not use simulation delta");
                    Assert(speed < 1 ? wall > 19 : wall < 0.3, "fixture did not diverge from wall time");
                    clock.Advance(10);
                    Assert(fired == 1 && clock.PendingCount == 0, "timer fired twice or retained work");
                }
                finally { tank.Free(); }
            });
        }
        runner.Run("world/clock cancels callbacks and closes reentrant work", () =>
        {
            using var clock = new WorldClock();
            var fired = 0;
            clock.Schedule(1, () => clock.Dispose());
            clock.Schedule(1, () => fired++);
            clock.Advance(1);
            Assert(fired == 0 && clock.PendingCount == 0, "callback survived world invalidation");
            Throws<ObjectDisposedException>(() => clock.Schedule(0, () => fired++));
            using var other = new WorldClock();
            var canceled = other.Schedule(1, () => fired++);
            canceled.Dispose();
            other.Schedule(1, () => other.Schedule(0, () => fired++));
            other.Advance(1);
            Assert(fired == 0, "canceled or newly scheduled callback fired");
            other.Advance(0);
            Assert(fired == 1 && other.PendingCount == 0, "reentrant work lost");
        });
    }
}
