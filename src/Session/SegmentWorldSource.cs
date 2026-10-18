using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Propane.Core;
using Propane.World;
using Propane.World.Plan;

namespace Propane.Session;

/// <summary>
/// Where a segment stream's plans come from. Plans are pure data keyed by index, so the local feed generates them on a
/// worker thread, and a networked feed could instead receive each from a server (as <c>PlanCodec</c> bytes, the way a
/// match's suburb travels) under a world revision in the envelope header. Nothing else in the stream changes.
/// </summary>
public interface ISegmentFeed : IDisposable
{
    /// <summary>Asks for a segment's plan ahead of need; asking again is harmless.</summary>
    void Request(int index);

    /// <summary>The plan, once it has arrived.</summary>
    bool TryGet(int index, out SegmentPlan plan);

    /// <summary>The plan now, waiting for it if it has not arrived (only while a session is being built).</summary>
    SegmentPlan Wait(int index);

    /// <summary>The segment is gone for good: its plan need not be kept.</summary>
    void Forget(int index);
}

/// <summary>Generates a run's segment plans locally, each on a worker thread, from a catalog snapshot.</summary>
public sealed class LocalSegmentFeed(SegmentSettings settings, int runSeed) : ISegmentFeed
{
    private readonly PlanCatalogSnapshot catalog = new(new GameCatalog());
    private readonly Dictionary<int, Task<SegmentPlan>> plans = new();
    // Feed-lifetime tombstones: [0, forgottenBefore) is implicit. Only forgotten indices beyond a gap need entries.
    // Ordered streaming uses no set storage; capacity is bounded by the maximum outstanding out-of-order forgets,
    // not total retirements. Arbitrary callers can leave arbitrarily many gaps; no request/forget history is evicted.
    private readonly HashSet<int> forgotten = new();
    private long forgottenBefore; // long represents the exclusive boundary after forgetting int.MaxValue safely.

    public int RunSeed => runSeed;

    public void Request(int index)
    {
        if (index < forgottenBefore || plans.ContainsKey(index) || forgotten.Contains(index)) return;
        plans[index] = Task.Run(() => SegmentPlans.Generate(settings, catalog, runSeed, index));
    }

    public bool TryGet(int index, out SegmentPlan plan)
    {
        plan = null!;
        if (!plans.TryGetValue(index, out var task) || !task.IsCompleted) return false;
        plan = task.GetAwaiter().GetResult(); // A failed generation surfaces here, on the main thread.
        return true;
    }

    public SegmentPlan Wait(int index)
    {
        Request(index);
        return plans[index].GetAwaiter().GetResult();
    }

    public void Forget(int index)
    {
        // Negative indices were never requestable. Forgetting one has no observable effect beyond removing a plan.
        if (index == forgottenBefore)
        {
            forgottenBefore++;
            while (forgottenBefore <= int.MaxValue && forgotten.Remove((int)forgottenBefore)) forgottenBefore++;
        }
        else if (index > forgottenBefore)
        {
            forgotten.Add(index);
        }
        plans.Remove(index);
    }

    /// <summary>Plans still in flight finish on their own; their results are dropped with this feed.</summary>
    public void Dispose() => plans.Clear();
}

/// <summary>One built segment of the run: its plan, its suburb (one content root) and its floor.</summary>
public sealed class RunSegment
{
    public required SegmentPlan Plan { get; init; }
    public required Suburb Suburb { get; init; }
    public required CollisionShape3D Floor { get; init; }
    public required BoxShape3D FloorShape { get; init; }
    public List<AmmoPickup> Pickups { get; } = new();
    /// <summary>World X up to which this segment's floor has been taken away.</summary>
    public float CutTo { get; set; } = float.NegativeInfinity;
    public int Index => Plan.Index;
    /// <summary>The segment's tanks, in plan order (freed ones included).</summary>
    public required IReadOnlyList<Tank.PropaneTank> Tanks { get; init; }
}

/// <summary>
/// The world of a streamed street: the void, and a run root under which each segment is its own suburb (its own
/// content root, effects and physics bodies), placed end to end along world +X. The stream's owner decides when to
/// build and retire; this builds a segment from its plan and retires it whole through
/// <see cref="WorldContext.RetireContent"/>, taking its effects with it.
/// </summary>
public sealed class SegmentWorldSource(SegmentSettings settings, ISegmentFeed feed) : ISessionWorldSource
{
    private readonly VoidWorldSource voidSource = new();
    private readonly List<RunSegment> segments = new();
    private WorldContext world = null!;

    public SegmentSettings Settings => settings;
    public ISegmentFeed Feed => feed;
    public VoidEnvironment Environment => voidSource.Environment;
    public Node3D Root { get; private set; } = null!;
    /// <summary>The segments built and not yet retired, in run order.</summary>
    public IReadOnlyList<RunSegment> Segments => segments;
    public int Built { get; private set; }
    public int Retired { get; private set; }

    public void Build(SessionContext session)
    {
        world = session.World;
        voidSource.Build(session);
        Root = new Node3D { Name = "Run" };
        world.Root.AddChild(Root);
        world.SetContentRoot(Root);
        session.OnStopping(feed.Dispose);
        Suburb.Warm(PlanCatalogSnapshot.Keys);
    }

    /// <summary>The segment being built a few pieces at a time, if any.</summary>
    public SegmentPlan? Pending => pending?.Plan;

    private (SegmentPlan Plan, Suburb.Staged Build)? pending;

    /// <summary>Starts building a segment from its plan: its suburb centered on the segment, hidden until complete.</summary>
    public void Begin(SegmentPlan plan)
    {
        if (pending != null) throw new InvalidOperationException("a segment is already being built");
        var build = Suburb.BuildStaged(world, plan.Plan, settings.Suburb, Root, new Vector3(plan.Center, 0, 0), revealSide: 2);
        build.Suburb.Name = $"Segment_{plan.Index}";
        pending = (plan, build);
    }

    /// <summary>Builds the pending segment for about <paramref name="milliseconds"/>; returns it once complete.</summary>
    public RunSegment? Advance(double milliseconds)
    {
        if (pending is not var (plan, build) || !build.Advance(milliseconds)) return null;
        pending = null;
        // The floor is one box the front shrinks from behind, taking it away under whatever stands there.
        var floor = build.Suburb.StaticRoot.GetNode<StaticBody3D>("SlabBody").GetChildren().OfType<CollisionShape3D>().Single();
        var shape = (BoxShape3D)((BoxShape3D)floor.Shape).Duplicate();
        floor.Shape = shape;
        var segment = new RunSegment { Plan = plan, Suburb = build.Suburb, Floor = floor, FloorShape = shape, Tanks = build.Suburb.MapTanks.ToArray() };
        segments.Add(segment);
        Built++;
        return segment;
    }

    /// <summary>Builds a segment at once (while the session assembles).</summary>
    public RunSegment BuildSegment(SegmentPlan plan)
    {
        Begin(plan);
        return Advance(double.PositiveInfinity)!;
    }

    /// <summary>Takes the floor away behind world X <paramref name="x"/> (whole segments go once x passes them).</summary>
    public static void CutFloor(RunSegment segment, float x)
    {
        if (x <= segment.CutTo) return;
        segment.CutTo = x;
        var start = segment.Plan.Start;
        var end = segment.Plan.End;
        var from = Mathf.Clamp(x, start, end);
        if (from >= end - 0.01f)
        {
            segment.Floor.Disabled = true;
            return;
        }
        var size = segment.FloorShape.Size;
        segment.FloorShape.Size = new Vector3(end - from, size.Y, size.Z);
        segment.Floor.Position = new Vector3((from + end) / 2f - segment.Plan.Center, segment.Floor.Position.Y, segment.Floor.Position.Z);
    }

    private readonly Queue<RunSegment> retiring = new();

    /// <summary>Segments leaving the run, still being freed.</summary>
    public int Retiring => retiring.Count;

    /// <summary>
    /// Takes a segment out of the run: it vanishes and leaves physics at once, and is freed a few nodes at a time by
    /// <see cref="AdvanceRetire"/>; its last step frees the suburb and every effect it spawned.
    /// </summary>
    public void Retire(RunSegment segment)
    {
        if (!segments.Remove(segment)) return;
        feed.Forget(segment.Index);
        segment.Suburb.Visible = false;
        segment.Suburb.ProcessMode = Node.ProcessModeEnum.Disabled;
        retiring.Enqueue(segment);
    }

    /// <summary>Frees retiring segments' nodes for about <paramref name="milliseconds"/>.</summary>
    public void AdvanceRetire(double milliseconds)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (retiring.Count > 0 && timer.Elapsed.TotalMilliseconds < milliseconds)
        {
            var suburb = retiring.Peek().Suburb;
            if (!GodotObject.IsInstanceValid(suburb)) { retiring.Dequeue(); continue; }
            var root = suburb.DynamicRoot.GetChildCount() > 0 ? suburb.DynamicRoot : suburb.StaticRoot.GetChildCount() > 0 ? suburb.StaticRoot : null;
            if (root == null)
            {
                world.RetireContent(suburb);
                retiring.Dequeue();
                Retired++;
                continue;
            }
            for (var i = 0; i < 16 && root.GetChildCount() > 0; i++) root.GetChild(root.GetChildCount() - 1).Free();
        }
    }
}
