using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Godot;
using Propane.Lab;
using Propane.Modes;
using Propane.Player;
using Propane.Session;
using static Propane.Dev.UnitTests.TestRunner;

using static Godot.GodotObject;
using static Propane.Dev.LabCases.LabScenarioTools;

namespace Propane.Dev.LabCases;

internal sealed partial class DissolveScenario(ILabScenarioHost host) : LabScenario(host)
{
    protected override Task RunCore() => DissolveRun();

    /// <summary>Stands still: what the player does once the scripted run stops.</summary>
    private sealed class Idle : IPlayerInputSource
    {
        public PlayerIntent Read() => new();
    }

    /// <summary>
    /// Brackets each physics tick's callbacks: the time from one tick's last callback to the next tick's first is the
    /// physics engine's own step (and anything else the engine does between ticks).
    /// </summary>
    private sealed partial class TickProbe : Node
    {
        public required TickProbe? Partner { get; init; }
        public double LastEnd;
        public double MaxGap;
        public double Gap;
        public bool Measure;
        public long Ticks, Bytes;
        public double Started, Seconds;
        public long Allocated;
        public readonly long[] Histogram = new long[20001];
        public readonly long[] WholeHistogram = new long[20001];
        public double WholeSeconds;
        public long WholeTicks;
        private bool tailPending;

        public override void _Ready()
        {
            if (!Measure || Partner != null) return;
            GetTree().PhysicsFrame += CloseTail;
            GetTree().ProcessFrame += CloseTail;
        }

        public override void _ExitTree()
        {
            if (!Measure || Partner != null) return;
            GetTree().PhysicsFrame -= CloseTail;
            GetTree().ProcessFrame -= CloseTail;
        }

        // Like FrameMetrics' native-tail probe: close before rendering or the next tick's callbacks. This includes
        // native physics, engine bookkeeping and query flush, but is not a timer for Jolt's internal solver jobs.
        private void CloseTail()
        {
            if (!tailPending) return;
            var milliseconds = Time.GetTicksUsec() / 1000.0 - Started;
            WholeSeconds += milliseconds / 1000;
            WholeHistogram[Math.Min(WholeHistogram.Length - 1, (int)(milliseconds * 100))]++;
            WholeTicks++;
            tailPending = false;
        }

        public double Percentile(double fraction, bool whole = false)
        {
            var histogram = whole ? WholeHistogram : Histogram;
            var wanted = (long)Math.Ceiling((whole ? WholeTicks : Ticks) * fraction);
            long count = 0;
            for (var i = 0; i < histogram.Length; i++)
            {
                count += histogram[i];
                if (count >= wanted) return i / 100d;
            }
            return 0;
        }

        public override void _PhysicsProcess(double delta)
        {
            var now = Time.GetTicksUsec() / 1000.0;
            if (Partner == null)
            {
                if (Measure && Started > 0)
                {
                    var milliseconds = now - Started;
                    Seconds += milliseconds / 1000;
                    Bytes += GC.GetAllocatedBytesForCurrentThread() - Allocated;
                    Histogram[Math.Min(Histogram.Length - 1, (int)(milliseconds * 100))]++;
                    Ticks++;
                    tailPending = true;
                }
                LastEnd = now;
                return;
            } // The last callback of the tick.
            if (Measure) { Partner.Started = now; Partner.Allocated = GC.GetAllocatedBytesForCurrentThread(); }
            if (Partner.LastEnd > 0) { Gap = now - Partner.LastEnd; MaxGap = Math.Max(MaxGap, Gap); }
        }
    }

    /// <summary>
    /// Runs down the street at a set pace without aim or navigation: along its crown, or a lane to either side when tanks
    /// stand on the crown ahead (the jackpot pyramid).
    /// </summary>
    private sealed class Pacer(PlayerCharacter player, Func<IEnumerable<Tank.PropaneTank>> tanks, bool sprint, float walk) : IPlayerInputSource
    {
        private static readonly float[] Lanes = { 0f, 1.4f, -1.4f };

        public PlayerIntent Read()
        {
            var at = player.GlobalPosition;
            var ahead = tanks().Select(t => t.GlobalPosition).Where(p => p.X > at.X - 1f && p.X < at.X + 12f).ToArray();
            var lane = Lanes.First(z => Array.TrueForAll(ahead, p => Mathf.Abs(p.Z - z) > 1.3f) || z == Lanes[^1]);
            return new PlayerIntent { Move = new Vector2(Mathf.Clamp((lane - at.Z) * 0.35f, -0.6f, 0.6f), walk), Sprint = sprint };
        }
    }

    /// <summary>
    /// A scripted Dissolve Run, headless: the bot drives the player down the street for --seconds (default 240), then
    /// stops (--stop-at) and must be caught by the front within the time the front needs to cover the gap. Throughout,
    /// segments must build ahead and retire behind, the live segments and their nodes and bodies must stay bounded, the
    /// whole tree's nodes must not grow, and the score must add up. It reports frame times (wall, per
    /// rendered frame), segment build and retire times and memory, per --report-every seconds, and with --metrics writes
    /// one CSV row per second. --driver=pace runs the player straight down the street (--pace=walk|run|sprint) without the
    /// bot, so frame times measure the stream and the world rather than bot navigation. --shots=4,12.5 adds --capture-dir screenshots at those run times; --camera=overview|ahead|behind|side watches
    /// from a camera that follows the runner, and --until=S stops a capture at that run time without the end checks.
    /// </summary>
    private async Task DissolveRun()
    {
        var scenario = LabScenarios.Get("dissolve");
        var seconds = DevArgs.GetFloat("seconds", 240);
        var stopAt = DevArgs.GetFloat("stop-at", seconds);
        var seeds = (int)DevArgs.GetFloat("seeds", 1);
        var firstSeed = (int)DevArgs.GetFloat("first-seed", 23);
        var reportEvery = DevArgs.GetFloat("report-every", 30);
        var shots = (DevArgs.Get("shots") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        DevArgs.ApplyTuningOverrides(template);
        for (var seed = firstSeed; seed < firstSeed + seeds; seed++)
        {
            template.FixedSeed = seed;
            var lab = new LabSession
            {
                Scenario = scenario,
                EnvironmentKind = LabEnvironment.Suburb,
                Template = template,
                PauseOnFocusLoss = false,
                TestRandomSeed = (ulong)seed,
                TestSeedPhysicsRandom = DevArgs.Get("replay-random") != null,
                DrivePlayer = DevArgs.Get("driver") != "pace",
                Name = "Lab"
            };
            var wrappers = new List<WeakReference<Tank.PropaneTank>>();
            if (DevArgs.Get("tracking-proof") != null)
                lab.TestObserveTank = tank => wrappers.Add(new WeakReference<Tank.PropaneTank>(tank));
            AddLab(lab);
            if (!lab.DrivePlayer)
            {
                var pace = DevArgs.Get("pace") ?? "run";
                lab.Player.InputOverride = new Pacer(lab.Player, () => lab.LiveTanks, pace == "sprint", pace == "walk" ? 0.5f : 1f);
            }
            var runner = lab.Prototype!;
            var arena = lab.Dissolve!;
            var tuning = lab.World.Tuning;
            Assert(lab.Session.Actors.Count == 1 && lab.RunSeed == seed && lab.LiveTanks.Any(), "dissolve run: solo, seeded, stocked");
            Camera3D? overview = null;
            var view = DevArgs.Get("camera") ?? (DevArgs.Get("overview") != null ? "overview" : null);
            if (view != null)
            {
                overview = new Camera3D { Name = "Overview", Fov = 55, Far = 2000, Current = true };
                lab.AddChild(overview);
            }
            var tickEnd = new TickProbe { Partner = null, ProcessPhysicsPriority = int.MaxValue, Name = "TickEnd" };
            var tickStart = new TickProbe { Partner = tickEnd, ProcessPhysicsPriority = int.MinValue, Name = "TickStart" };
            tickStart.Measure = tickEnd.Measure = DevArgs.Get("tracking-metrics") != null;
            var allocatedAtStart = GC.GetTotalAllocatedBytes();
            lab.AddChild(tickEnd);
            lab.AddChild(tickStart);
            var caught = -1.0;
            var chains = new List<ChainClosed>();
            var observe = Guard<ModeCommand>(command =>
            {
                if (command is RunCaught) caught = runner.Elapsed;
                if (command is ChainClosed done) chains.Add(done);
            });
            runner.Decided += observe;
            using var observation = DetachOnDisposal(() => runner.Decided -= observe);
            var frames = new List<(double Time, double Wall)>();
            var physicsSteps = new List<double>();
            var rows = new List<string> { "time,x,front,gap,segments,segment_nodes,segment_bodies,tree_nodes,objects,static_mb,frame_ms_max,built,retired,score" };
            var census = new List<(double Time, int Segments, int Nodes, int Bodies, int Tree, int Objects, double Memory)>();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var last = watch.Elapsed.TotalMilliseconds;
            var stopped = false;
            var shot = 0;
            var nextSample = 1.0;
            var nextReport = (double)reportEvery;
            var gapAtStop = 0.0;
            var secondMax = 0.0;
            while (runner.IsPlaying)
            {
                await Frames(1).ConfigureAwait(true);
                var now = watch.Elapsed.TotalMilliseconds;
                var wall = now - last;
                last = now;
                if (runner.Elapsed > 3) frames.Add((runner.Elapsed, wall));
                secondMax = Math.Max(secondMax, wall);
                var stepMax = tickStart.MaxGap;
                tickStart.MaxGap = 0;
                physicsSteps.Add(stepMax);
                if (DevArgs.Get("trace-frames") != null && wall > 12 && runner.Elapsed > 3)
                {
                    GD.Print($"[lab trace] {runner.Elapsed:F2}s frame {wall:F1} ms gc={GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)} " +
                        $"step_max={stepMax:F1} x={lab.Player.GlobalPosition.X:F1} y={lab.Player.GlobalPosition.Y:F2} front={((DissolveRunRules)runner.Rules).Front:F1}");
                }
                if (wall > 50 && runner.Elapsed > 3)
                {
                    // A slow frame names what ran: a segment build or retirement (arena), the physics engine's step, or neither.
                    GD.Print($"[lab dissolve] seed {seed}: slow frame {wall:F0} ms at {runner.Elapsed:F2}s, building={arena.Source.Pending?.Index}, " +
                        $"built={arena.Source.Built} retired={arena.Source.Retired} gc={GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)} " +
                        $"arena={arena.LastStepMilliseconds:F1} ms physics_step={stepMax:F1} ms");
                }
                var rules = (DissolveRunRules)runner.Rules;
                if (!stopped && runner.Elapsed >= stopAt)
                {
                    stopped = true;
                    lab.Player.InputOverride = new Idle();
                    gapAtStop = rules.Gap;
                    GD.Print($"[lab dissolve] seed {seed}: stopped at {runner.Elapsed:F1}s, x={lab.Player.GlobalPosition.X:F1}, front {gapAtStop:F1} m behind");
                }
                // The front needs at most gap / start speed to arrive (it only speeds up), plus the fall.
                Assert(!stopped || runner.Elapsed - stopAt < gapAtStop / Math.Max(0.1, tuning.DissolveFrontSpeed) + tuning.DissolveFallSeconds + 2,
                    $"dissolve seed {seed}: the front never caught a player who stopped");
                Assert(runner.Elapsed < seconds + 600, "dissolve run never ended");
                if (overview != null)
                {
                    var at = lab.Player.GlobalPosition;
                    var back = arena.Back;
                    switch (view)
                    {
                        case "ahead": // Over the runner's shoulder, high, onto the street materializing ahead.
                            overview.LookAtFromPosition(new Vector3(at.X - 14, 16, at.Z + 10), new Vector3(arena.Horizon - 12, 0, 0), Vector3.Up);
                            break;
                        case "behind": // Ahead of the runner, looking back at it and the front.
                            overview.LookAtFromPosition(new Vector3(at.X + 16, 9, at.Z + 14), new Vector3((at.X + back) / 2, 0, 0), Vector3.Up);
                            break;
                        case "side": // Broadside, the runner and the front both in frame.
                            var middle = (at.X + back) / 2 + 4;
                            overview.LookAtFromPosition(new Vector3(middle, 18, 30), new Vector3(middle, 0, 0), Vector3.Up);
                            break;
                        default:
                            overview.LookAtFromPosition(new Vector3(at.X - 34, 30, at.Z + 26), new Vector3(at.X + 6, 0, 0), Vector3.Up);
                            break;
                    }
                }
                if (runner.Elapsed >= DevArgs.GetFloat("until", float.MaxValue))
                {
                    GD.Print($"[lab dissolve] seed {seed}: capture stopped at {runner.Elapsed:F1}s");
                    break;
                }
                if (shot < shots.Length && runner.Elapsed >= shots[shot]) capture.Screenshot($"dissolve_{seed}_{shots[shot++]:000.0}s");
                if (runner.Elapsed >= nextSample)
                {
                    nextSample += DevArgs.GetFloat("census-every", 1);
                    var (segments, nodes, bodies) = arena.Census();
                    var tree = GetTree().GetNodeCount();
                    var objects = (int)Performance.GetMonitor(Performance.Monitor.ObjectCount);
                    var memory = Performance.GetMonitor(Performance.Monitor.MemoryStatic) / 1048576.0;
                    census.Add((runner.Elapsed, segments, nodes, bodies, tree, objects, memory));
                    rows.Add(string.Join(",", new object[] { runner.Elapsed.ToString("F1", CultureInfo.InvariantCulture),
                        lab.Player.GlobalPosition.X.ToString("F1", CultureInfo.InvariantCulture), rules.Front.ToString("F1", CultureInfo.InvariantCulture),
                        rules.Gap.ToString("F1", CultureInfo.InvariantCulture), segments, nodes, bodies, tree, objects,
                        memory.ToString("F1", CultureInfo.InvariantCulture), secondMax.ToString("F1", CultureInfo.InvariantCulture),
                        lab.Dissolve!.Source.Built, arena.Source.Retired, rules.Score }));
                    secondMax = 0;
                    if (DevArgs.Get("tracking-proof") != null)
                    {
                        var expectedTracking = arena.Source.Segments.Sum(s => s.Tanks.Count(t => IsInstanceValid(t) && t.State != Tank.PropaneTank.TankState.Exploded));
                        var actualTracking = TrackingCount(runner);
                        if (DevArgs.Get("assert-tracking") != null)
                            Assert(actualTracking == expectedTracking, $"tracking count {actualTracking} differs from active content {expectedTracking}");
                    }
                    Assert(segments <= 6, $"dissolve seed {seed}: {segments} segments alive");
                    if (DevArgs.Get("trace") != null)
                        GD.Print($"[lab trace] {runner.Elapsed:F1}s x={lab.Player.GlobalPosition.X:F1} front={rules.Front:F1} score={rules.Score} {runner.BotStatus(1)}");
                }
                if (runner.Elapsed >= nextReport)
                {
                    nextReport += reportEvery;
                    if (DevArgs.Get("tracking-proof") != null)
                        GD.Print($"[lab tracking] at={runner.Elapsed:F1}s active={TrackingCount(runner)} content={arena.Source.Segments.Sum(s => s.Tanks.Count(t => IsInstanceValid(t) && t.State != Tank.PropaneTank.TankState.Exploded))} historical={wrappers.Count}");
                    Report(seed, runner.Elapsed, frames.Where(f => f.Time > runner.Elapsed - reportEvery).Select(f => f.Wall).ToArray(), census[^1], arena);
                }
            }
            if (runner.IsPlaying)
            {
                lab.Session.Dispose(); lab.Free();
                continue;
            }
            var final = (DissolveRunRules)runner.Rules;
            foreach (var line in final.Snapshot().Report) GD.Print($"[lab dissolve] seed {seed}: {line.Text}");
            Assert(caught >= 0 && final.Caught && final.Over && runner.Finished, $"dissolve seed {seed}: the run must end by the front catching the runner");
            // A ragdolled body keeps the height it went down at; the pelvis is where the runner is.
            var fallen = lab.Player.IsRagdolled ? lab.Player.PelvisPosition : lab.Player.GlobalPosition;
            Assert(fallen.Y < -3f, $"dissolve seed {seed}: a caught runner falls (y={fallen.Y:F1}, ragdolled={lab.Player.IsRagdolled})");
            Assert(final.Score == final.Meters + tuning.DissolveTankPoints * final.Tanks + final.Bonus, "the score adds up");
            Assert(final.Bonus == chains.Sum(c => c.Bonus) && chains.Sum(c => c.Tanks) == final.Tanks && final.Pushes == final.Tanks,
                "every tank is in one chain, pushes the front once, and the chain bonuses add up");
            Assert(chains.All(c => c.Bonus == (c.Tanks >= tuning.DissolveChainMin ? c.Tanks * c.Tanks : 0)), "a chain scores n squared");
            var source = arena.Source;
            var expected = (int)((final.Best - SegmentPlansStart(lab)) / tuning.DissolveSegmentLength);
            Assert(source.Built >= expected && source.Retired >= Math.Max(0, expected - 2), $"segments built {source.Built}, retired {source.Retired}, run {final.Meters} m");
            if (seconds >= 120)
            {
                // Bounded, not growing: the last third of the run holds no more than the first third, give or take a segment.
                var third = census.Count / 3;
                var early = census.Skip(2).Take(Math.Max(1, third)).ToArray();
                var late = census.Skip(census.Count - third).ToArray();
                Assert(late.Max(c => c.Nodes) <= early.Max(c => c.Nodes) * 1.5 + 500 && late.Max(c => c.Bodies) <= early.Max(c => c.Bodies) * 1.5 + 50,
                    $"segment nodes/bodies grew: {early.Max(c => c.Nodes)}/{early.Max(c => c.Bodies)} -> {late.Max(c => c.Nodes)}/{late.Max(c => c.Bodies)}");
                // Godot's object count is reported, not asserted: it rises until the .NET GC releases wrappers, then drops.
                Assert(late.Max(c => c.Tree) <= early.Max(c => c.Tree) * 1.5 + 500,
                    $"the tree grew: {early.Max(c => c.Tree)} -> {late.Max(c => c.Tree)} nodes");
            }
            Report(seed, runner.Elapsed, frames.Select(f => f.Wall).ToArray(), census[^1], arena, final: true);
            var steps = physicsSteps.OrderBy(v => v).ToArray();
            GD.Print($"[lab dissolve] seed {seed} physics engine step (worst per frame): p50={steps[steps.Length / 2]:F1} p99={steps[(int)(steps.Length * 0.99)]:F1} max={steps[^1]:F1} ms");
            GD.Print($"[lab dissolve] seed={seed} run={runner.Elapsed:F1}s caught_at={caught:F1}s meters={final.Meters} tanks={final.Tanks} " +
                $"chains={chains.Count(c => c.Bonus > 0)} best_chain={final.BestChain} score={final.Score} built={source.Built} retired={source.Retired} " +
                $"dissolved_tanks={arena.DissolvedTanks} wall={watch.Elapsed.TotalSeconds:F1}s");
            if (DevArgs.Get("metrics") is { } path) File.WriteAllLines(path.Replace("{seed}", seed.ToString(CultureInfo.InvariantCulture)), rows);
            if (tickEnd.Measure)
                GD.Print(FormattableString.Invariant($"[lab whole-session] ticks={tickEnd.Ticks} whole_mean_ms={tickEnd.WholeSeconds * 1000 / Math.Max(1, tickEnd.WholeTicks):F4} whole_p95_ms={tickEnd.Percentile(0.95, whole: true):F2} whole_p99_ms={tickEnd.Percentile(0.99, whole: true):F2} callback_mean_ms={tickEnd.Seconds * 1000 / Math.Max(1, tickEnd.Ticks):F4} callback_p95_ms={tickEnd.Percentile(0.95):F2} callback_p99_ms={tickEnd.Percentile(0.99):F2} callback_bytes={tickEnd.Bytes} total_bytes={GC.GetTotalAllocatedBytes() - allocatedAtStart}"));
            await Frames(20).ConfigureAwait(true);
            if (DevArgs.Get("tracking-proof") != null)
            {
                var freed = RetiredWrappers(wrappers, arena.Source);
                await Collect().ConfigureAwait(true);
                var retained = freed.Count(w => w.TryGetTarget(out _));
                GD.Print($"[lab tracking collection] historical={wrappers.Count} freed={freed.Count} retained={retained} session_alive={lab.Session.IsAlive}");
                if (DevArgs.Get("assert-tracking") != null)
                    Assert(freed.Count > 0 && retained == 0 && lab.Session.IsAlive, "retired wrappers remained reachable in a live session");
            }
            capture.Screenshot($"dissolve_{seed}_end");
            Assert(lab.World.Paused && lab.Overlay.MenuVisible, "a finished run must pause and show its result");
            lab.Session.Dispose(); lab.Free();
            observation.Dispose();
            await Collect().ConfigureAwait(true);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<WeakReference<Tank.PropaneTank>> RetiredWrappers(List<WeakReference<Tank.PropaneTank>> wrappers, SegmentWorldSource source)
    {
        // Segment tank arrays legitimately retain detonated shells until their content has finished retiring.
        var owned = source.Segments.SelectMany(s => s.Tanks).ToHashSet();
        var retiring = (Queue<RunSegment>)typeof(SegmentWorldSource)
            .GetField("retiring", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(source)!;
        foreach (var segment in retiring) owned.UnionWith(segment.Tanks);
        return wrappers.Where(w => !w.TryGetTarget(out var tank) || (!IsInstanceValid(tank) && !owned.Contains(tank))).ToList();
    }

    private static float SegmentPlansStart(LabSession lab) => lab.Dissolve!.Start.X;

    private static void Report(int seed, double at, double[] walls, (double Time, int Segments, int Nodes, int Bodies, int Tree, int Objects, double Memory) census,
        DissolveArena arena, bool final = false)
    {
        if (walls.Length == 0) return;
        var sorted = walls.OrderBy(w => w).ToArray();
        double P(double q) => sorted[Math.Min(sorted.Length - 1, (int)(q * sorted.Length))];
        var builds = arena.BuildSlices.DefaultIfEmpty(0).ToArray();
        var retires = arena.RetireMilliseconds.DefaultIfEmpty(0).ToArray();
        GD.Print($"[lab dissolve] seed {seed} {(final ? "run" : $"at {at:F0}s")}: allocated {GC.GetTotalAllocatedBytes() / 1048576.0:F0} MB, gc {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)} paused {GC.GetTotalPauseDuration().TotalMilliseconds:F0} ms; frame wall p50={P(0.5):F1} p95={P(0.95):F1} p99={P(0.99):F1} max={sorted[^1]:F1} ms " +
            $"over {sorted.Length} frames; segment build steps={builds.Length} avg={builds.Average():F2} max={builds.Max():F1} ms, retire avg={retires.Average():F1} max={retires.Max():F1} ms; " +
            $"segments={census.Segments} nodes={census.Nodes} bodies={census.Bodies} tree={census.Tree} objects={census.Objects} static={census.Memory:F0} MB");
    }
}
