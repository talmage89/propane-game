using System;
using Propane.Core;
using Propane.Player;

namespace Propane.Dev.UnitTests;

[UnitSuite]
public static class ActorPoseTests
{
    public static void Register(TestRunner runner)
    {
        runner.Run("actor pose distance bands preserve near and owning views", Bands);
        runner.Run("actor pose clock retains elapsed time and reacts to live rate changes", Clock);
        runner.Run("actor pose clocks stagger work without managed allocations", Stagger);
    }

    private static void Bands()
    {
        using var t = new Tuning();
        var clock = new ActorPoseCadence(1);
        TestRunner.Assert(clock.Interval(t, 0, false, false) == 0, "near hidden actor throttled");
        TestRunner.Assert(clock.Interval(t, t.ActorPoseNearDistance + t.ActorPoseHysteresis / 2, true, false) == 0, "near boundary chatters");
        TestRunner.Assert(clock.Interval(t, t.ActorPoseNearDistance + t.ActorPoseHysteresis + 1, true, false) == 1 / t.ActorPoseRate, "mid-distance rate");
        TestRunner.Assert(clock.Interval(t, t.ActorPoseNearDistance, false, false) == 0, "near actor did not restore immediately");
        TestRunner.Assert(clock.Interval(t, t.ActorPoseFarDistance + t.ActorPoseHysteresis + 1, true, false) == 1 / t.ActorPoseFarRate, "far rate");
        TestRunner.Assert(clock.Interval(t, t.ActorPoseFarDistance - 1, false, false) == 1 / t.ActorPoseHiddenRate, "hidden rate");
        TestRunner.Assert(clock.Interval(t, 1000, false, true) == 0, "owning view/recovery throttled");
        t.ActorPoseLod = false;
        TestRunner.Assert(clock.Interval(t, 1000, false, false) == 0, "disable switch ignored");
        t.ActorPoseLod = true;
        t.ActorPoseNearDistance = 60;
        t.ActorPoseFarDistance = 20;
        TestRunner.Assert(clock.Interval(t, 40, false, false) == 0, "inverted tuners sacrifice nearby pose");
    }

    private static void Clock()
    {
        var clock = new ActorPoseCadence(8);
        const float dt = 1f / 60;
        var total = 0f;
        var samples = 0;
        for (var frame = 0; frame < 600; frame++)
        {
            if (!clock.Advance(dt, 1f / 15, out var delta)) continue;
            total += delta;
            samples++;
        }
        TestRunner.Assert(samples >= 149 && samples <= 152, $"15 Hz drifted: {samples}");
        TestRunner.Assert(clock.Advance(dt, 0, out var restored), "near restore skipped a frame");
        total += restored;
        TestRunner.Assert(Math.Abs(total - 601 * dt) < 0.0001f, "clock lost or invented animation time");
        TestRunner.Assert(clock.Advance(dt, 1f / 30, out _), "live rate change did not sample immediately");
        clock.Reset();
        TestRunner.Assert(clock.Advance(dt, 1, out var reset) && reset == dt, "teleport retains old interval/time");
        TestRunner.Assert(clock.Advance(2, 1, out var hitch) && hitch == 2, "hitch did not sample current pose once");
    }

    private static void Stagger()
    {
        var clocks = new ActorPoseCadence[16];
        for (var i = 0; i < clocks.Length; i++) clocks[i] = new ActorPoseCadence((ulong)i + 1);
        foreach (var clock in clocks) clock.Advance(1f / 60, .1f, out _);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var minimum = clocks.Length;
        var maximum = 0;
        for (var frame = 0; frame < 120; frame++)
        {
            var calls = 0;
            foreach (var clock in clocks) if (clock.Advance(1f / 60, .1f, out _)) calls++;
            minimum = Math.Min(minimum, calls);
            maximum = Math.Max(maximum, calls);
        }
        var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        TestRunner.Assert(bytes == 0, $"clock allocated {bytes} bytes");
        TestRunner.Assert(minimum > 0 && maximum < clocks.Length / 2, $"pose work bunched: {minimum}..{maximum}");
    }
}
