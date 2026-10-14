using System;
using System.Linq;
using Godot;
using Propane.Bots;
using Propane.Core;
using Propane.Navigation;
using Propane.Tank;

namespace Propane.Dev.UnitTests;

[UnitSuite]
internal static class NavigationTests
{
    private sealed class CourseWorld : INavigationWorld
    {
        public bool Fence { get; init; }
        public bool Gate { get; init; }
        public bool Impossible { get; init; }
        public long Queries { get; private set; }
        public int FlightQueries => 1;
        public bool Fits(Vector3 feet) { Queries++; return Ground(feet); }
        public bool Supported(Vector3 feet) { Queries++; return Ground(feet); }
        private bool Ground(Vector3 feet) => !Impossible && Math.Abs(feet.X) < 30 && Math.Abs(feet.Z) < 30;
        public bool Sweep(Vector3 from, Vector3 to)
        {
            Queries++;
            if (Impossible) { return false; }
            if (!Fence || from.Z * to.Z > 0) { return true; }
            var t = from.Z / (from.Z - to.Z);
            var crossing = from.Lerp(to, t);
            return Gate && Math.Abs(crossing.X) < 1;
        }
        public bool Flight(Vector3 from, Vector3 direction) { Queries++; return !Impossible && Fence && from.Z * (from.Z + direction.Z * 6.25f) < 0; }
    }

    private sealed class CountedWorld : INavigationWorld
    {
        private readonly CourseWorld course = new() { Fence = true };
        public int StandQueries { get; private set; }
        public long Queries => course.Queries;
        public int FlightQueries => course.FlightQueries;
        public bool Fits(Vector3 feet) { StandQueries++; return course.Fits(feet); }
        public bool Supported(Vector3 feet) => course.Supported(feet);
        public bool Sweep(Vector3 from, Vector3 to) => course.Sweep(from, to);
        public bool Flight(Vector3 from, Vector3 direction) => course.Flight(from, direction);
    }

    private static float FlatDistance(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Z - b.Z).Length();

    private static NavigationGrid Grid(INavigationWorld world, NavigationCapabilities caps, Tuning tuning) =>
        new(world, caps, tuning.BotRouteSpacing, tuning.BotJumpMinimumRange);

    public static void Register(TestRunner runner)
    {
        runner.Run("navigation ignores a freed tank when abandoning its route", () =>
        {
            using var bot = new BankingBot();
            var gone = PropaneTank.Create(runner.World);
            gone.Free();
            bot.Blacklist(gone, 10);
            TestRunner.Assert(bot.BlacklistedTargets == 0, "freed target was blacklisted");
            var live = new Node3D();
            try
            {
                bot.Blacklist(live, 10);
                TestRunner.Assert(bot.BlacklistedTargets == 1, "live target could not be blacklisted");
            }
            finally { live.Free(); }
        });
        using var shape = new CapsuleShape3D { Radius = 0.32f, Height = 1.8f };
        using var tuning = Tuning.LoadBaked();
        var caps = new NavigationCapabilities(shape.Radius, shape.Height, tuning.RunSpeed,
            tuning.SprintSpeed, tuning.JumpVelocity, tuning.PlayerGravity, tuning.Acceleration, tuning.AirControl);
        runner.Run("navigation uses actor jump and gait capabilities", () =>
        {
            TestRunner.Assert(caps.JumpHeight > 1.7f, "baked player jump clears privacy fences");
            var low = caps with { JumpVelocity = 1 };
            TestRunner.Assert(low.JumpHeight < 0.1f && low.JumpRange < 1, "no hardcoded jump height or reach");
            var state = new NavigationState(Vector3.Zero, Vector3.Zero, true);
            var goal = new Vector3(0, 0, -12);
            TestRunner.Assert(new BotNavigator().Navigate(Grid(new CourseWorld(), caps, tuning), new NavigationHazards(), state, goal, tuning, 1f / 60).Sprint,
                "long open routes use a faster sprint gait");
            TestRunner.Assert(!new BotNavigator().Navigate(Grid(new CourseWorld(), caps with { SprintSpeed = caps.Speed }, tuning), new NavigationHazards(), state, goal, tuning, 1f / 60).Sprint,
                "actors without a faster gait cannot be told to sprint");
        });
        runner.Run("navigation chooses jumps and equal-cost gates", () =>
        {
            var start = new Vector3(0, 0, 7);
            var goal = new Vector3(0, 0, -7);
            var route = BotNavigator.Plan(Grid(new CourseWorld { Fence = true }, caps, tuning), new NavigationHazards(), start, goal, tuning);
            TestRunner.Assert(route.Count > 0 && route.Any(s => s.Jump), "closed fence needs a planned jump");
            var gate = BotNavigator.Plan(Grid(new CourseWorld { Fence = true, Gate = true }, caps, tuning), new NavigationHazards(), start, goal, tuning);
            TestRunner.Assert(gate.Count == 1 && !gate[0].Jump, "walk straight through the gate");
            var low = BotNavigator.Plan(Grid(new CourseWorld { Fence = true }, caps with { JumpVelocity = 1 }, tuning), new NavigationHazards(), start, goal, tuning);
            TestRunner.Assert(low.Count == 0, "infinite fence is unreachable without sufficient jump range");
        });
        runner.Run("navigation replans alternate routes before changing goals", () =>
        {
            var nav = new BotNavigator();
            var world = Grid(new CourseWorld(), caps, tuning);
            var state = new NavigationState(Vector3.Zero, Vector3.Zero, true);
            var goal = new Vector3(0, 0, -12);
            var failed = false;
            var alternate = false;
            for (var tick = 0; tick < 600 && !failed; tick++)
            {
                var intent = nav.Navigate(world, new NavigationHazards(), state, goal, tuning, 1f / 60);
                alternate |= Math.Abs(intent.Direction.X) > 0.1f;
                failed = intent.Failed;
                if (nav.StuckEvents < tuning.BotRouteRetries) { TestRunner.Assert(!failed, "do not abandon on first obstruction"); }
            }
            TestRunner.Assert(alternate && failed && nav.Replans >= tuning.BotRouteRetries, "try alternate directions, then report failure in bounded time");
        });
        runner.Run("navigation bounds unreachable searches and stabilizes nearby goals", () =>
        {
            var nav = new BotNavigator();
            var state = new NavigationState(Vector3.Zero, Vector3.Zero, true);
            for (var tick = 0; tick < 60; tick++)
            {
                var goal = new Vector3(0.1f * Mathf.Sin(tick), 0, -12);
                nav.Navigate(Grid(new CourseWorld(), caps, tuning), new NavigationHazards(), state with { Position = new Vector3(0, 0, -tick * 0.1f) }, goal, tuning, 1f / 60);
            }
            TestRunner.Assert(nav.Replans == 1 && nav.StuckEvents == 0, "small target changes do not thrash the route");
            nav.Reset();
            var failed = false;
            for (var tick = 0; tick < 120 && !failed; tick++)
            {
                failed = nav.Navigate(Grid(new CourseWorld { Impossible = true }, caps, tuning), new NavigationHazards(), state, new Vector3(0, 0, -12), tuning, 1f / 60).Failed;
            }
            TestRunner.Assert(failed, "unreachable goals fail after bounded retries");
        });
        runner.Run("navigation finishes a committed jump when its goal changes", () =>
        {
            var nav = new BotNavigator();
            var world = Grid(new CourseWorld { Fence = true }, caps, tuning);
            var position = new Vector3(0, 0, 7);
            var velocity = Vector3.Zero;
            var tookOff = false;
            for (var tick = 0; tick < 600 && !tookOff; tick++)
            {
                var move = nav.Navigate(world, new NavigationHazards(), new NavigationState(position, velocity, true),
                    new Vector3(0, 0, -7), tuning, 1f / 60);
                velocity = move.Direction * caps.Speed;
                position += velocity / 60;
                if (move.Jump)
                {
                    var air = nav.Navigate(world, new NavigationHazards(), new NavigationState(position, velocity, false),
                        new Vector3(20, 0, 7), tuning, 1f / 60);
                    TestRunner.Assert(air.Direction.DistanceTo(move.Direction) < 0.001f && !air.Sprint && !air.Jump,
                        "air intent preserves the safe arc and emits only one jump press");
                    tookOff = true;
                }
            }
            TestRunner.Assert(tookOff && nav.Jumps == 1, "planned runway actually produces a jump");
        });
        runner.Run("navigation spreads search work across ticks without false recovery", () =>
        {
            using var slow = Tuning.LoadBaked();
            slow.BotSearchPerTick = 1;
            slow.BotProgressWindow = 0.1f;
            var nav = new BotNavigator();
            var counted = new CountedWorld();
            var world = Grid(counted, caps, slow);
            world.QueryBudget = 16;
            world.ExpansionBudget = 1;
            var state = new NavigationState(new Vector3(0, 0, 7), Vector3.Zero, true);
            var moved = false;
            var frames = 0;
            for (; frames < 1000 && !moved; frames++)
            {
                world.BeginTick(frames / 60d);
                var before = counted.Queries;
                var move = nav.Navigate(world, new NavigationHazards(), state, new Vector3(0, 0, -7), slow, 1f / 60);
                TestRunner.Assert(counted.Queries - before <= 16 && world.ExpansionsThisTick <= 1, "shared query and expansion budgets hold");
                TestRunner.Assert(!move.Failed && nav.StuckEvents == 0, "pending search is not a stalled movement");
                moved = move.Direction.LengthSquared() > 0;
            }
            TestRunner.Assert(moved && frames > 1 && nav.Replans == 1, "search resumes and completes instead of restarting each tick");
            slow.BotSearchPerTick = 0;
            TestRunner.Throws<ArgumentOutOfRangeException>(() => BotNavigator.Plan(world, new NavigationHazards(), state.Position, Vector3.Zero, slow));
        });
        runner.Run("normal aimed creep is progress at both physics rates", () =>
        {
            foreach (var rate in new[] { 60, 120 })
            {
                var nav = new BotNavigator();
                var position = Vector3.Zero;
                var velocity = Vector3.Zero;
                var legacyStuck = 0f;
                var dt = 1f / rate;
                for (var tick = 0; tick < rate * 6; tick++)
                {
                    var move = nav.Navigate(Grid(new CourseWorld(), caps, tuning), new NavigationHazards(), new NavigationState(position, velocity, true),
                        new Vector3(0, 0, -24), tuning, dt);
                    TestRunner.Assert(!move.Failed && nav.StuckEvents == 0, "valid creep must not trigger recovery or target exclusion");
                    // The player resolver's ordinary aimed gait. The old per-tick detector mistakes this
                    // legitimate speed for an obstacle at the project's 120 Hz physics rate.
                    velocity = move.Direction * tuning.CreepSpeed;
                    position += velocity * dt;
                    legacyStuck = velocity.Length() * dt < 0.02f ? legacyStuck + dt : 0;
                }
                if (rate == 120) { TestRunner.Assert(legacyStuck > 0.4f, "fixture reproduces the old creep misclassification"); }
            }
        });
        runner.Run("navigation shares cached answers and refills one process budget", () =>
        {
            var physics = new CourseWorld();
            var grid = Grid(physics, caps, tuning);
            grid.Reserve(new Rect2(-20, -20, 40, 40));
            TestRunner.Assert(grid.LayerAt(0.133f).Y == 0.133f, "storage reservation must not choose the road height");
            var layer = grid.LayerAt(0);
            grid.QueryBudget = 4;
            grid.BeginTick(0);
            TestRunner.Assert(grid.Standable(layer, Vector2I.Zero) == NavigationAnswer.Yes, "first point fits");
            var queries = physics.Queries;
            for (var bot = 0; bot < 16; bot++)
                TestRunner.Assert(grid.Standable(layer, Vector2I.Zero) == NavigationAnswer.Yes, "bots share the answer");
            TestRunner.Assert(physics.Queries == queries, "cached points cast no additional probes");
            TestRunner.Assert(grid.Standable(layer, Vector2I.One) == NavigationAnswer.Yes, "second point spends remaining budget");
            TestRunner.Assert(grid.Standable(layer, Vector2I.One * 2) == NavigationAnswer.Pending, "later bots wait");
            grid.BeginTick(1);
            TestRunner.Assert(grid.Standable(layer, Vector2I.One * 2) == NavigationAnswer.Yes, "next tick resumes");
            grid.Invalidate(Vector3.Zero, 1);
            TestRunner.Assert(grid.Standable(layer, Vector2I.Zero) == NavigationAnswer.Yes && physics.Queries > queries,
                "moving props forget local answers");
        });
        runner.Run("navigation disturbances expire temporary answers and takeoffs reserve complete arcs", () =>
        {
            var physics = new CourseWorld { Fence = true };
            var grid = Grid(physics, caps, tuning);
            var layer = grid.LayerAt(0);
            grid.BeginTick(0);
            grid.Disturb(Vector3.Zero, 2, 3);
            grid.Standable(layer, Vector2I.Zero);
            var queries = physics.Queries;
            grid.BeginTick(2);
            grid.Standable(layer, Vector2I.Zero);
            TestRunner.Assert(physics.Queries == queries, "settling answer is cached briefly");
            grid.BeginTick(3);
            grid.Standable(layer, Vector2I.Zero);
            TestRunner.Assert(physics.Queries > queries, "settled geometry is surveyed again");
            grid.QueryBudget = 2;
            grid.BeginTick(4);
            TestRunner.Assert(grid.Takeoff(new Vector3(0, 0, 3), Vector3.Forward) == NavigationAnswer.Pending && grid.QueriesThisTick == 0,
                "an incomplete arc spends no queries");
            grid.QueryBudget = 3;
            TestRunner.Assert(grid.Takeoff(new Vector3(0, 0, 3), Vector3.Forward) == NavigationAnswer.Yes && grid.QueriesThisTick == 3,
                "a complete live takeoff stays within budget");
        });
        runner.Run("hazard buckets agree with exhaustive circles including negative and distant positions", () =>
        {
            var hazards = new NavigationHazards();
            for (var i = 0; i < 70; i++) hazards.Add(new Vector3(i % 9 * 3 - 12, 0, i / 9 * 3 - 9), 0.5f + i % 5);
            hazards.Add(new Vector3(10000, 0, -10000), 6);
            hazards.Build();
            for (var z = -15; z <= 20; z++)
            {
                for (var x = -20; x <= 20; x++)
                {
                    var at = new Vector3(x * 0.8f, 0, z * 0.7f);
                    var expected = true;
                    for (var i = 0; i < hazards.Count; i++)
                        expected &= FlatDistance(at, hazards[i].Center) >= hazards[i].Radius;
                    TestRunner.Assert(hazards.Safe(at) == expected, "bucket boundary lost a circle");
                }
            }
            hazards.Clear();
            hazards.Add(Vector3.Zero, 4);
            TestRunner.Assert(hazards.SafeAlong(Vector3.Right, Vector3.Right * 6, 0.3f, Vector3.Right), "walk out of danger");
            TestRunner.Assert(!hazards.Safe(Vector3.Zero, Vector3.Right), "never walk farther into danger");
        });
        runner.Run("hazard indexing stays local when one tank flies far along an axis", () =>
        {
            var hazards = new NavigationHazards();
            hazards.Add(Vector3.Zero, 4);
            hazards.Add(new Vector3(100000000, 0, 0), 4);
            hazards.Build();
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 100; i++)
            {
                hazards.Clear();
                hazards.Add(Vector3.Zero, 4);
                var far = new Vector3(10000000000f + i * 100000, 0, 0);
                hazards.Add(far, 4);
                hazards.Build();
                TestRunner.Assert(!hazards.Safe(Vector3.Zero) && !hazards.Safe(far) && hazards.Safe(new Vector3(0, 0, 10)),
                    "far hazards must preserve local answers");
            }
            TestRunner.Assert(GC.GetAllocatedBytesForCurrentThread() == before, "moving hazards allocate by world span");
        });
        runner.Run("warm cached navigation allocates no managed bytes", () =>
        {
            var grid = Grid(new CourseWorld(), caps, tuning);
            var nav = new BotNavigator();
            var hazards = new NavigationHazards();
            var state = new NavigationState(Vector3.Zero, Vector3.Zero, true);
            var goal = new Vector3(0, 0, -12);
            for (var i = 0; i < 100; i++)
            {
                nav.Reset(); grid.BeginTick(i);
                nav.Navigate(grid, hazards, state, goal, tuning, 1f / 120);
            }
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 100; i++)
            {
                nav.Reset(); grid.BeginTick(i + 100);
                nav.Navigate(grid, hazards, state, goal, tuning, 1f / 120);
            }
            TestRunner.Assert(GC.GetAllocatedBytesForCurrentThread() == before, "steady route and hazard hot paths allocate");
        });
        runner.Run("bot tuners preserve the match snapshot schema", () =>
        {
            var before = tuning.Snapshot();
            tuning.BotJumpMargin *= 2;
            tuning.BotAimErrorHard *= 2;
            var after = tuning.Snapshot();
            TestRunner.Assert(before.Count == after.Count && !Tuning.ValueNames(tuning).Any(n => n.StartsWith("Bot", StringComparison.Ordinal)), "bot dials stay local");
            foreach (var (key, value) in before) { TestRunner.Assert(after[key].Equals(value), "no shared tuning value changed"); }
            Tuning.ValidateSnapshot(after, complete: true);
        });
    }
}
