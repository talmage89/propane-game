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
        public bool Standable(Vector3 feet) => !Impossible && Math.Abs(feet.X) < 30 && Math.Abs(feet.Z) < 30;
        public bool Walk(Vector3 from, Vector3 to)
        {
            if (Impossible) { return false; }
            if (!Fence || from.Z * to.Z > 0) { return true; }
            var t = from.Z / (from.Z - to.Z);
            var crossing = from.Lerp(to, t);
            return Gate && Math.Abs(crossing.X) < 1;
        }
        public bool Jump(Vector3 from, Vector3 to) => !Impossible && Fence && from.Z * to.Z < 0;
    }

    private sealed class CountedWorld : INavigationWorld
    {
        private readonly CourseWorld course = new() { Fence = true };
        public int StandQueries { get; private set; }
        public bool Standable(Vector3 feet) { StandQueries++; return course.Standable(feet); }
        public bool Walk(Vector3 from, Vector3 to) => course.Walk(from, to);
        public bool Jump(Vector3 from, Vector3 to) => course.Jump(from, to);
    }

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
        var caps = new NavigationCapabilities(shape, Vector3.Up * 0.9f, shape.Radius, tuning.RunSpeed,
            tuning.SprintSpeed, tuning.JumpVelocity, tuning.PlayerGravity, tuning.Acceleration, tuning.AirControl);
        runner.Run("navigation uses actor jump and gait capabilities", () =>
        {
            TestRunner.Assert(caps.JumpHeight > 1.7f, "baked player jump clears privacy fences");
            var low = caps with { JumpVelocity = 1 };
            TestRunner.Assert(low.JumpHeight < 0.1f && low.JumpRange < 1, "no hardcoded jump height or reach");
            var state = new NavigationState(Vector3.Zero, Vector3.Zero, true);
            var goal = new Vector3(0, 0, -12);
            TestRunner.Assert(new BotNavigator().Navigate(new CourseWorld(), state, caps, goal, tuning, 1f / 60).Sprint,
                "long open routes use a faster sprint gait");
            TestRunner.Assert(!new BotNavigator().Navigate(new CourseWorld(), state, caps with { SprintSpeed = caps.Speed }, goal, tuning, 1f / 60).Sprint,
                "actors without a faster gait cannot be told to sprint");
        });
        runner.Run("navigation chooses jumps and equal-cost gates", () =>
        {
            var start = new Vector3(0, 0, 7);
            var goal = new Vector3(0, 0, -7);
            var route = BotNavigator.Plan(new CourseWorld { Fence = true }, start, goal, caps, tuning);
            TestRunner.Assert(route.Count > 0 && route.Any(s => s.Jump), "closed fence needs a planned jump");
            var gate = BotNavigator.Plan(new CourseWorld { Fence = true, Gate = true }, start, goal, caps, tuning);
            TestRunner.Assert(gate.Count == 1 && !gate[0].Jump, "walk straight through the gate");
            var low = BotNavigator.Plan(new CourseWorld { Fence = true }, start, goal, caps with { JumpVelocity = 1 }, tuning);
            TestRunner.Assert(low.Count == 0, "infinite fence is unreachable without sufficient jump range");
        });
        runner.Run("navigation replans alternate routes before changing goals", () =>
        {
            var nav = new BotNavigator();
            var world = new CourseWorld();
            var state = new NavigationState(Vector3.Zero, Vector3.Zero, true);
            var goal = new Vector3(0, 0, -12);
            var failed = false;
            var alternate = false;
            for (var tick = 0; tick < 600 && !failed; tick++)
            {
                var intent = nav.Navigate(world, state, caps, goal, tuning, 1f / 60);
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
                nav.Navigate(new CourseWorld(), state with { Position = new Vector3(0, 0, -tick * 0.1f) }, caps, goal, tuning, 1f / 60);
            }
            TestRunner.Assert(nav.Replans == 1 && nav.StuckEvents == 0, "small target changes do not thrash the route");
            nav.Reset();
            var failed = false;
            for (var tick = 0; tick < 120 && !failed; tick++)
            {
                failed = nav.Navigate(new CourseWorld { Impossible = true }, state, caps, new Vector3(0, 0, -12), tuning, 1f / 60).Failed;
            }
            TestRunner.Assert(failed, "unreachable goals fail after bounded retries");
        });
        runner.Run("navigation finishes a committed jump when its goal changes", () =>
        {
            var nav = new BotNavigator();
            var world = new CourseWorld { Fence = true };
            var position = new Vector3(0, 0, 7);
            var velocity = Vector3.Zero;
            var tookOff = false;
            for (var tick = 0; tick < 600 && !tookOff; tick++)
            {
                var move = nav.Navigate(world, new NavigationState(position, velocity, true), caps,
                    new Vector3(0, 0, -7), tuning, 1f / 60);
                velocity = move.Direction * caps.Speed;
                position += velocity / 60;
                if (move.Jump)
                {
                    var air = nav.Navigate(world, new NavigationState(position, velocity, false), caps,
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
            var world = new CountedWorld();
            var state = new NavigationState(new Vector3(0, 0, 7), Vector3.Zero, true);
            var moved = false;
            var frames = 0;
            for (; frames < 1000 && !moved; frames++)
            {
                var before = world.StandQueries;
                var move = nav.Navigate(world, state, caps, new Vector3(0, 0, -7), slow, 1f / 60);
                TestRunner.Assert(world.StandQueries - before <= 16, "one expansion tests at most sixteen neighbor cells");
                TestRunner.Assert(!move.Failed && nav.StuckEvents == 0, "pending search is not a stalled movement");
                moved = move.Direction.LengthSquared() > 0;
            }
            TestRunner.Assert(moved && frames > 1 && nav.Replans == 1, "search resumes and completes instead of restarting each tick");
            slow.BotSearchPerTick = 0;
            TestRunner.Throws<ArgumentOutOfRangeException>(() => BotNavigator.Plan(world, state.Position, Vector3.Zero, caps, slow));
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
                    var move = nav.Navigate(new CourseWorld(), new NavigationState(position, velocity, true), caps,
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
