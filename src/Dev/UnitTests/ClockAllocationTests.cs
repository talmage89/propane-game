using System;
using Propane.Core;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev.UnitTests;

[UnitSuite]
public static class ClockAllocationTests
{
    public static void Register(TestRunner runner)
    {
        runner.Run("clock/warmed dense and nested dispatch and pending scans allocate nothing", () =>
        {
            using var clock = new WorldClock();
            const int count = 1024;
            var fired = 0;
            var nested = false;
            Action callback = () =>
            {
                fired++;
                if (!nested) { nested = true; clock.Advance(0); }
            };
            Action dispatch = () =>
            {
                Assert(clock.PendingCount == count + 1, "live pending scan changed");
                clock.Advance(1);
                Assert(clock.PendingCount == 1, "nested burst retained work");
                clock.Advance(0); // Pending future work, empty eligibility snapshot.
            };
            clock.Schedule(1000, () => { });
            for (var round = 0; round < 4; round++)
            {
                nested = false;
                for (var i = 0; i < count; i++) { clock.Schedule(0, callback); }
                clock.Schedule(1000, callback).Dispose();
                // Schedule outside the measured window. Every measured window has a full due burst.
                if (round == 0) { dispatch(); }
                else { AssertNoAllocation(dispatch, "warmed clock dispatch allocated", windows: 1); }
            }
            Assert(fired == count * 4, "nested dispatch duplicated or lost callbacks");
        });
    }
}
