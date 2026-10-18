using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Godot;
using Propane.Lab;
using Propane.Modes;
using Propane.Session;
using Propane.World.Plan;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev.LabCases;

/// <summary>Controlled real native streaming, without physics/worker scheduling changing the workload.</summary>
internal sealed class StreamingHistoryScenario(ILabScenarioHost host) : LabScenario(host)
{
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object Field(object owner, string name) => owner.GetType().GetField(name, Private)!.GetValue(owner)!;
    private static int Count(object owner, string name) => (int)Field(owner, name).GetType().GetProperty("Count")!.GetValue(Field(owner, name))!;
    private static void Raw(DissolveArena arena, bool enabled) => typeof(DissolveArena).GetProperty("CollectRawTimings")?.SetValue(arena, enabled);

    protected override async Task RunCore()
    {
        var lab = Enter(LabScenarios.Get("dissolve"), LabEnvironment.Suburb);
        lab.World.SetPaused(true);
        var arena = lab.Dissolve!;
        var source = arena.Source;
        var diagnostic = DevArgs.Get("raw") != null;
        Raw(arena, diagnostic);
        var iterations = (int)DevArgs.GetFloat("segments", 512);
        Assert(iterations >= 64 && iterations % 64 == 0, "streaming-history requires a positive multiple of 64 segments");
        var ticks = new List<double>(100000);
        long bytes = 0;
        var retired = new List<WeakReference>();
        var native = new List<ulong>();
        var transcript = DevArgs.Get("stream-transcript") is { } ? new List<string>() : null;
        void Census(int at)
        {
            var c = arena.Census();
            GD.Print($"[stream-history] at={at} built={source.Built} retired={source.Retired} live={c.Segments} nodes={c.Nodes} bodies={c.Bodies} retiring={source.Retiring} plans={Count(source.Feed, "plans")} forgotten={Count(source.Feed, "forgotten")} builds={arena.BuildSlices.Count} retires={arena.RetireMilliseconds.Count} dissolved={Count(arena, "dissolved")} pickups={arena.Pickups.Count} " +
                $"plan_capacity={((Dictionary<int, Task<Propane.World.Plan.SegmentPlan>>)Field(source.Feed, "plans")).EnsureCapacity(0)} forgotten_capacity={((HashSet<int>)Field(source.Feed, "forgotten")).EnsureCapacity(0)} build_capacity={((List<double>)arena.BuildSlices).Capacity} retire_capacity={((List<double>)arena.RetireMilliseconds).Capacity} " +
                $"tracked={LabScenarioTools.TrackingCount(lab.Prototype!)} lab_tanks={Count(lab, "tanks")} direct_shots={Count(lab.Prototype!, "directShots")} drops={Count(lab.Prototype!, "drops")} " +
                $"receivers={lab.World.BlastReceivers.Count} reveal={Count(lab.World.Reveal, "geometry")} effects={Count(lab.World, "effectOrigins")} timers={lab.World.Clock.PendingCount} resources={Performance.GetMonitor(Performance.Monitor.ObjectResourceCount)}");
            Assert(c.Segments <= 4 && source.Retiring == 0 && Count(source.Feed, "plans") <= 5, "controlled stream exceeded its live window");
        }
        void Tick()
        {
            var building = source.Pending != null;
            var retiring = source.Retiring > 0;
            var retiredBefore = source.Retired;
            var buildCount = arena.BuildSlices.Count;
            var retireCount = arena.RetireMilliseconds.Count;
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            arena._PhysicsProcess(1d / 120);
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            bytes += GC.GetAllocatedBytesForCurrentThread() - allocated;
            ticks.Add(elapsed);
            if (diagnostic)
            {
                Assert(arena.BuildSlices.Count == buildCount + (building ? 1 : 0), "raw build sample boundary changed");
                Assert(arena.RetireMilliseconds.Count == retireCount + (retiring || source.Retiring > 0 || source.Retired > retiredBefore ? 1 : 0), "raw retirement sample boundary changed");
                Assert(!building || double.IsFinite(arena.BuildSlices[^1]) && arena.BuildSlices[^1] >= 0, "invalid build sample");
                Assert(arena.RetireMilliseconds.Count == retireCount || double.IsFinite(arena.RetireMilliseconds[^1]) && arena.RetireMilliseconds[^1] >= 0, "invalid retirement sample");
            }
        }
        void Sample(double x) => lab.Prototype!.Rules.Handle(new ActorsSampled(new[] { new ActorSample(lab.Player.ActorId, x, 0, 0) }),
            new ModeClock(ModePhase.Round, 1, 0, double.PositiveInfinity));
        Census(0);
        PositionCamera(arena);
        await Frames(3).ConfigureAwait(true);
        capture.Screenshot("stream_before");
        for (var i = 1; i <= iterations; i++)
        {
            var next = source.Segments[^1].Index + 1;
            var plan = source.Feed.Wait(next); // Control worker readiness, never time worker scheduling as arena cost.
            transcript?.Add($"wait {next}: seed={plan.Seed} tanks={plan.Tanks} jackpot={plan.Jackpot}");
            Sample(source.Segments[^1].Plan.End - lab.World.Tuning.DissolveBuildLead - lab.World.Tuning.DissolveAhead + 0.25);
            Tick(); // The real Stream path begins construction and requests the next worker.
            Assert(source.Pending?.Index == next, "controlled stream did not begin the expected segment");
            while (source.Pending != null) Tick();
            var forgottenIndex = source.Segments[0].Index;
            ObserveOldest(arena, retired, native);
            Sample(source.Segments[0].Plan.End + lab.World.Tuning.DissolveRetireLag + lab.World.Tuning.DissolveFrontMaxGap + 1);
            Tick(); // The real Step path retires content, forgets its plan and advances native retirement.
            while (source.Retiring > 0) Tick();
            Assert(source.Retired == i && source.Segments.Count == 3, "controlled retirement changed its window");
            source.Feed.Request(forgottenIndex);
            Assert(!source.Feed.TryGet(forgottenIndex, out _), "retired plan became available again");
            transcript?.Add($"forget {forgottenIndex}; request ignored; try-get false; plans={string.Join(',', ((Dictionary<int, Task<Propane.World.Plan.SegmentPlan>>)Field(source.Feed, "plans")).Keys.Order())}");
            if (i == 32) { ticks.Clear(); bytes = 0; } // Native/JIT warm-up is identical in each paired run.
            if (i % 64 == 0)
            {
                await Collect().ConfigureAwait(true);
                Assert(retired.All(w => !w.IsAlive), "retired managed content retained in live world");
                Assert(native.All(id => !GodotObject.IsInstanceIdValid(id)), "retired native content survived");
                retired.Clear(); native.Clear();
                Census(i);
            }
        }
        if (transcript != null) System.IO.File.WriteAllLines(DevArgs.Get("stream-transcript")!, transcript);
        ticks.Sort();
        GD.Print(FormattableString.Invariant($"[stream-tick] ticks={ticks.Count} built={source.Built} retired={source.Retired} bytes={bytes} mean={ticks.Average():F6} p95={ticks[(int)(ticks.Count * .95)]:F6} p99={ticks[(int)(ticks.Count * .99)]:F6} raw={diagnostic}"));
        var beforeSteady = bytes;
        for (var i = 0; i < 1200; i++) Tick();
        GD.Print($"[stream-steady] ticks=1200 bytes={bytes - beforeSteady}");
        if (diagnostic) Assert(arena.BuildSlices.Count > 0 && arena.RetireMilliseconds.Count > 0, "raw timing evidence absent");
        if (DevArgs.Get("assert-bounded") != null)
            Assert(Count(source.Feed, "forgotten") == 0 && (diagnostic || arena.BuildSlices.Count + arena.RetireMilliseconds.Count == 0), "incidental history grew");
        PositionCamera(arena);
        await Frames(3).ConfigureAwait(true);
        capture.Screenshot("stream_after");
        lab.Session.Dispose(); lab.Free();
    }

    private static void PositionCamera(DissolveArena arena)
    {
        var segment = arena.Source.Segments[1];
        var back = segment.Plan.Start + 10;
        typeof(DissolveArena).GetField("<Back>k__BackingField", Private)!.SetValue(arena, back);
        typeof(DissolveArena).GetField("horizon", Private)!.SetValue(arena, segment.Plan.End - 10);
        typeof(DissolveArena).GetMethod("Window", Private)!.Invoke(arena, null);
        var camera = new Camera3D { Current = true, Far = 2000 };
        arena.Lab.AddChild(camera);
        camera.LookAtFromPosition(new Vector3(segment.Plan.Center - 25, 32, 35), new Vector3(segment.Plan.Center, 0, 0), Vector3.Up);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ObserveOldest(DissolveArena arena, List<WeakReference> retired, List<ulong> native)
    {
        var segment = arena.Source.Segments[0];
        retired.Add(new WeakReference(segment));
        retired.Add(new WeakReference(segment.Suburb));
        retired.Add(new WeakReference(segment.FloorShape));
        native.Add(segment.Suburb.GetInstanceId());
        native.Add(segment.FloorShape.GetInstanceId());
        foreach (var tank in segment.Tanks) { retired.Add(new WeakReference(tank)); native.Add(tank.GetInstanceId()); }
    }
}
