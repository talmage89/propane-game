using System;
using Propane.Player;

namespace Propane.Dev.UnitTests;

public static class RecoilTests
{
    public static void Register(TestRunner runner)
    {
        runner.Run("recoil recovery is independent of render rate", () =>
        {
            float Sample(int hz)
            {
                var spring = new RecoilSpring();
                spring.Kick(5, 2);
                for (var i = 0; i < hz / 5; i++) spring.Update(1f / hz, 5, 0.65f);
                return spring.Position;
            }
            TestRunner.Assert(Math.Abs(Sample(30) - Sample(60)) < 0.00001f && Math.Abs(Sample(60) - Sample(120)) < 0.00001f,
                "spring recovery depends on frame rate");
        });
        runner.Run("recoil bursts accumulate and settle without drift", () =>
        {
            var spring = new RecoilSpring();
            spring.Kick(5, 2);
            spring.Update(1f / 9, 5, 0.65f);
            var remaining = spring.Position;
            spring.Kick(5, 2);
            TestRunner.Assert(remaining > 0 && spring.Position == remaining, "new shot discards displacement");
            for (var frame = 0; frame < 240; frame++)
            {
                if (frame < 120 && frame % 7 == 0) spring.Kick(5, 2);
                spring.Update(1f / 60, 5, 0.65f);
                TestRunner.Assert(float.IsFinite(spring.Position) && Math.Abs(spring.Position) < 2, "burst is unbounded");
            }
            TestRunner.Assert(Math.Abs(spring.Position) < 0.00001f && Math.Abs(spring.Velocity) < 0.00001f, "burst retains drift");
            spring.Kick(5, 2);
            spring.Reset();
            TestRunner.Assert(spring.Position == 0 && spring.Velocity == 0, "lifecycle retains recoil momentum");
        });
    }
}
