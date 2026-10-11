using System;
using Godot;
using Propane.Core;
using Propane.Tank;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev.UnitTests;

public static class WorldClockTests
{
    public static void Register(TestRunner runner)
    {
        foreach (var speed in new[] { 0.1, 8.0 })
        {
            runner.Run($"world/clock timer at simulation speed {speed}", () =>
            {
                var tank = PropaneTank.Create();
                runner.AddChild(tank);
                try
                {
                    tank.MakeInvulnerable(2, Colors.White);
                    using var clock = new WorldClock();
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
