using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Propane.Core;
using Propane.Session;
using Propane.World;
using Propane.World.Plan;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev.UnitTests;

/// <summary>Controlled worker completion: no scheduling race can conceal a forgotten result.</summary>
[UnitSuite]
public static class SegmentFeedTests
{
    public static void Register(TestRunner runner)
    {
        runner.Run("segment feed/real worker completion, repeat request and retirement", () =>
        {
            using var tuning = Tuning.LoadBaked();
            using var feed = new LocalSegmentFeed(SuburbPlans.Segments(tuning), 23);
            feed.Request(0);
            var first = feed.Wait(0);
            feed.Request(0);
            Assert(feed.TryGet(0, out var again) && ReferenceEquals(first, again) && ReferenceEquals(first, feed.Wait(0)),
                "repeated request replaced the completed plan");
            feed.Forget(0);
            feed.Request(0);
            Assert(!feed.TryGet(0, out _), "retired plan regenerated");
            var rejected = false;
            try { feed.Wait(0); } catch (KeyNotFoundException) { rejected = true; }
            Assert(rejected, "Wait on a forgotten index changed its failure contract");
        });

        runner.Run("segment feed/requests in open gaps still generate", () =>
        {
            using var tuning = Tuning.LoadBaked();
            using var feed = new LocalSegmentFeed(SuburbPlans.Segments(tuning), 23);
            feed.Forget(4);
            feed.Forget(2);
            feed.Forget(0);
            // These must use the real Request path: planting a pending task would hide an over-eager watermark.
            foreach (var index in new[] { 1, 3, 5 })
            {
                feed.Request(index);
                Assert(feed.Wait(index).Index == index, $"unforgotten gap {index} was rejected");
                feed.Forget(index);
                Assert(!feed.TryGet(index, out _), "completed gap survived retirement");
            }
            // A synthetic prefix tests the exclusive int.MaxValue boundary without billions of generations.
            var boundary = typeof(LocalSegmentFeed).GetField("forgottenBefore", BindingFlags.Instance | BindingFlags.NonPublic);
            if (boundary != null)
            {
                boundary.SetValue(feed, (long)int.MaxValue);
                feed.Forget(int.MaxValue);
                Assert((long)boundary.GetValue(feed)! == (long)int.MaxValue + 1, "forgotten prefix overflowed");
                feed.Request(int.MaxValue);
                Assert(!feed.TryGet(int.MaxValue, out _), "maximum forgotten index was admitted again");
            }
        });

        runner.Run("segment feed/out-of-order forgetting and late completion", () =>
        {
            using var tuning = Tuning.LoadBaked();
            using var feed = new LocalSegmentFeed(SuburbPlans.Segments(tuning), 23);
            var plans = (Dictionary<int, Task<SegmentPlan>>)typeof(LocalSegmentFeed)
                .GetField("plans", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(feed)!;
            var forgotten = new HashSet<int>(); // Independent parent semantics, including negative/extreme indices.
            var transcript = new List<string>();
            void Check(int index)
            {
                // Plant an unfinished worker only where the old Request contract admits it.
                var admitted = index >= 0 && !forgotten.Contains(index);
                if (admitted && !plans.ContainsKey(index))
                    plans[index] = new TaskCompletionSource<SegmentPlan>().Task;
                feed.Request(index);
                Assert(plans.ContainsKey(index) == admitted, $"request admission changed at {index}");
                Assert(!feed.TryGet(index, out _), "unfinished/forgotten plan became visible");
                transcript.Add($"request {index}: {admitted}");
            }
            void Forget(int index)
            {
                forgotten.Add(index);
                feed.Forget(index);
                Assert(!plans.ContainsKey(index) && !feed.TryGet(index, out _), "forget retained a plan");
                transcript.Add($"forget {index}");
                Check(index);
            }
            Check(-1);
            foreach (var index in new[] { 4, 2, 0, 4, -1, int.MinValue, int.MaxValue, 1, 3 }) Forget(index);
            for (var i = 5; i < 10000; i += 2) { Forget(i + 1); Check(i); Forget(i); }
            // A completion arriving after Forget has no continuation that can repopulate the dictionary.
            var late = new TaskCompletionSource<SegmentPlan>();
            plans[10002] = late.Task;
            Forget(10002);
            late.SetResult(null!);
            Assert(!feed.TryGet(10002, out _) && !plans.ContainsKey(10002), "late result reappeared");
            Check(10001);
            plans[10001] = Task.FromResult<SegmentPlan>(null!);
            Assert(feed.TryGet(10001, out _) && feed.Wait(10001) == null, "completed result changed");
            Forget(10001);
            var failure = new TaskCompletionSource<SegmentPlan>();
            plans[10005] = failure.Task;
            failure.SetException(new InvalidOperationException("controlled generation failure"));
            var surfaced = false;
            try { feed.TryGet(10005, out _); } catch (InvalidOperationException) { surfaced = true; }
            Assert(surfaced, "worker failure stopped surfacing on the caller");
            Forget(10005);
            var canceled = new TaskCompletionSource<SegmentPlan>();
            plans[10006] = canceled.Task;
            Forget(10006);
            canceled.SetCanceled();
            Assert(!feed.TryGet(10006, out _), "discarded worker cancellation surfaced");
            feed.Dispose();
            Assert(plans.Count == 0, "dispose retained worker handles");
            if (DevArgs.Get("feed-transcript") is { } path) System.IO.File.WriteAllLines(path, transcript);
        });
    }
}
