using System;
using System.Collections.Generic;
using System.Diagnostics;
using Godot;
using Propane.Core;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev.UnitTests;

[UnitSuite]
public static class ClockDispatchTests
{
    public static void Register(TestRunner runner)
    {
        runner.Run("clock/entry eligibility and insertion order transcript", () =>
        {
            using var clock = new WorldClock();
            var trace = new List<string>();
            void Mark(string name) => trace.Add($"{name}:{clock.Now}:{clock.PendingCount}");
            clock.Schedule(2, () => { Mark("first"); clock.Schedule(0, () => Mark("new")); });
            clock.Schedule(1, () => Mark("second"));
            clock.Advance(0.5);
            Mark("early");
            clock.Advance(1.5);
            Mark("after");
            clock.Advance(0);
            Transcript(trace, "early:0.5:2|first:2:1|second:2:1|after:2:1|new:2:0");
        });
        runner.Run("clock/cancellation transcript", () =>
        {
            using var clock = new WorldClock();
            var trace = new List<string>();
            IDisposable later = null!;
            clock.Schedule(0, () =>
            {
                trace.Add($"before:{clock.PendingCount}");
                later.Dispose();
                trace.Add($"after:{clock.PendingCount}");
            });
            later = clock.Schedule(0, () => trace.Add("canceled"));
            clock.Schedule(10, () => trace.Add("future-canceled")).Dispose();
            clock.Advance(0);
            trace.Add($"end:{clock.PendingCount}");
            Transcript(trace, "before:1|after:0|end:0");
        });
        runner.Run("clock/nested advancement transcript", () =>
        {
            using var clock = new WorldClock();
            var trace = new List<string>();
            void Mark(string name) => trace.Add($"{name}:{clock.Now}:{clock.PendingCount}");
            clock.Schedule(1, () =>
            {
                Mark("outer");
                clock.Schedule(0, () => { Mark("new"); clock.Advance(1); Mark("deep-return"); });
                clock.Advance(1);
                Mark("return");
            });
            clock.Schedule(1, () => Mark("second"));
            clock.Schedule(3, () => Mark("future"));
            clock.Advance(1);
            Mark("end");
            Transcript(trace, "outer:1:2|second:2:2|new:2:1|future:3:0|deep-return:3:0|return:3:0|end:3:0");
        });
        runner.Run("clock/disposal transcript", () =>
        {
            using var clock = new WorldClock();
            var trace = new List<string>();
            clock.Schedule(1, () =>
            {
                trace.Add($"before:{clock.PendingCount}");
                clock.Dispose();
                clock.Advance(double.NaN); // Disposed clocks ignore even invalid advances.
                Throws<ObjectDisposedException>(() => clock.Schedule(0, () => { }));
                trace.Add($"disposed:{clock.Now}:{clock.PendingCount}");
            });
            clock.Schedule(1, () => trace.Add("survived"));
            clock.Schedule(10, () => trace.Add("future"));
            clock.Advance(1);
            Transcript(trace, "before:2|disposed:1:0");
        });
        runner.Run("clock/nested exception and recovery transcript", () =>
        {
            using var clock = new WorldClock();
            var trace = new List<string>();
            var failure = new InvalidOperationException("fixture");
            clock.Schedule(0, () =>
            {
                trace.Add($"outer:{clock.PendingCount}");
                clock.Advance(1);
                trace.Add("unreachable");
            });
            clock.Schedule(1, () =>
            {
                trace.Add($"throw:{clock.Now}:{clock.PendingCount}");
                clock.Schedule(0, () => trace.Add($"new:{clock.PendingCount}"));
                throw failure;
            });
            clock.Schedule(1, () => trace.Add($"remaining:{clock.PendingCount}"));
            try { clock.Advance(0); Assert(false, "exception swallowed"); }
            catch (InvalidOperationException caught) { Assert(ReferenceEquals(caught, failure), "exception changed"); }
            trace.Add($"caught:{clock.Now}:{clock.PendingCount}");
            clock.Advance(0);
            Transcript(trace, "outer:2|throw:1:1|caught:1:2|remaining:1|new:0");
        });
        runner.Run("clock/dense fuses preserve cancellation, order and next-tick work", () =>
        {
            using var clock = new WorldClock();
            const int count = 1024;
            var timers = new IDisposable[count];
            var fired = 0;
            var nextTick = 0;
            for (var i = 0; i < count; i++)
            {
                var index = i;
                timers[i] = clock.Schedule(1, () =>
                {
                    Assert(index == fired * 2, "dense fuse order changed");
                    timers[index + 1].Dispose();
                    fired++;
                    clock.Schedule(0, () => nextTick++);
                });
            }
            clock.Advance(0.5);
            Assert(fired == 0 && clock.PendingCount == count, "dense fuses fired early");
            clock.Advance(0.5);
            Assert(fired == count / 2 && nextTick == 0 && clock.PendingCount == count / 2,
                "dense burst changed eligibility or cancellation");
            clock.Advance(0);
            Assert(nextTick == count / 2 && clock.PendingCount == 0, "dense follow-up lost work");
        });
        if (DevArgs.Get("clock-bench") != null) { Benchmark(); }
    }

    private static void Transcript(List<string> trace, string expected)
    {
        var actual = string.Join("|", trace);
        Assert(actual == expected, $"transcript: {actual}; expected {expected}");
        GD.Print($"[clock-transcript] {actual}");
    }

    private static void Benchmark()
    {
        const int width = 256, warmup = 2000, samples = 100000;
        using var clock = new WorldClock();
        var fired = 0;
        Action callback = () => fired++;
        var dispatch = new double[samples];
        var tick = new double[samples];
        long dispatchBytes = 0, tickBytes = 0;
        for (var i = -warmup; i < samples; i++)
        {
            var tickBefore = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            for (var j = 0; j < width; j++) { clock.Schedule(0, callback); }
            var before = GC.GetAllocatedBytesForCurrentThread();
            var dispatchStart = Stopwatch.GetTimestamp();
            clock.Advance(1.0 / 120);
            var count = clock.PendingCount;
            var end = Stopwatch.GetTimestamp();
            var bytes = GC.GetAllocatedBytesForCurrentThread();
            Assert(count == 0, "burst retained timers");
            if (i < 0) { continue; }
            dispatch[i] = (end - dispatchStart) * 1e6 / Stopwatch.Frequency;
            tick[i] = (end - start) * 1e6 / Stopwatch.Frequency;
            dispatchBytes += bytes - before;
            tickBytes += bytes - tickBefore;
        }
        Assert(fired == (warmup + samples) * width, "burst callbacks lost");
        Report("dispatch+count", dispatch, dispatchBytes);
        Report("whole-clock-tick", tick, tickBytes);
    }

    private static void Report(string label, double[] times, long bytes)
    {
        double sum = 0;
        foreach (var time in times) { sum += time; }
        Array.Sort(times);
        GD.Print(FormattableString.Invariant($"[clock-bench] {label} ticks={times.Length} timers/tick=256 bytes/tick={bytes / (double)times.Length:F1} mean-us={sum / times.Length:F3} p95-us={times[(int)(times.Length * 0.95)]:F3} p99-us={times[(int)(times.Length * 0.99)]:F3}"));
    }
}
