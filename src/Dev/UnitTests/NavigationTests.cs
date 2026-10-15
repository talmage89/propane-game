using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Bots;
using Propane.Core;
using Propane.Lab;
using Propane.Modes;
using Propane.Navigation;
using Propane.Net;
using Propane.Player;
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

    private sealed class ChangingArea : INavigationArea
    {
        public bool Removed { get; set; }
        public bool Allows(Vector3 feet) => !Removed || Math.Abs(feet.X) >= 0.75f || Math.Abs(feet.Z) >= 0.75f;
    }

    private sealed class BankingWorld(WorldContext world, PlayerCharacter player, PlayerCharacter enemy) : IBotWorld, IDisposable
    {
        public WorldContext World => world;
        public PlayerCharacter Player => player;
        public bool IsPlaying => true;
        public float TimeLeft => 180;
        public int MyScore => 0;
        public IReadOnlyDictionary<int, int> Scores { get; } = new Dictionary<int, int> { [1] = 0, [2] = 8 };
        public double Now => 0;
        public double LastHitTime => double.NegativeInfinity;
        public int LastHitBy => 0;
        public IReadOnlyList<PlayerCharacter> RemoteBodies { get; init; } = new[] { enemy };
        public IModeView? View { get; init; }
        public IReadOnlyList<AmmoPickup> Pickups => Array.Empty<AmmoPickup>();
        public BotServices Services { get; } = new(world);
        public void Dispose() => Services.Dispose();
    }

    private static float FlatDistance(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Z - b.Z).Length();

    private static NavigationGrid Grid(INavigationWorld world, NavigationCapabilities caps, Tuning tuning) =>
        new(world, caps, tuning.BotRouteSpacing, tuning.BotJumpMinimumRange, tuning.BotLayerTolerance);

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
        runner.Run("navigation/ray collider IDs preserve capsule and ragdoll ownership without warm allocation", () =>
        {
            var player = new PlayerCharacter { World = runner.World, ActorId = 1 };
            var enemy = new PlayerCharacter { World = runner.World, ActorId = 2, Position = Vector3.Right * 10 };
            runner.World.Root.AddChild(player);
            runner.World.Root.AddChild(enemy);
            using var services = new BotServices(runner.World);
            try
            {
                services.PrepareActor(player);
                services.PrepareActor(enemy);
                TestRunner.Assert(services.ActorOwns(enemy, enemy.GetRid()) && !services.ActorOwns(player, enemy.GetRid()),
                    "standing capsules disagree with actor ownership");
                enemy.RemoteRagdoll(enemy.GlobalPosition, Vector3.Zero, Vector3.Zero);
                var bodies = enemy.FindChildren("*", nameof(CollisionObject3D), true, false).OfType<CollisionObject3D>().ToArray();
                TestRunner.Assert(bodies.Length > 1, "ragdoll collider fixture was not built");
                foreach (var body in bodies)
                {
                    TestRunner.Assert(PlayerCharacter.Owning(body) == enemy && services.ActorOwns(enemy, body.GetRid()) &&
                        !services.ActorOwns(player, body.GetRid()), "bone RID ownership differs from walking parents");
                }
                var collider = bodies[0].GetRid();
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < 100; i++) TestRunner.Assert(services.ActorOwns(enemy, collider), "warmed ownership changed");
                var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                TestRunner.Assert(bytes == 0, $"warmed collider lookup allocated {bytes} bytes");
            }
            finally { player.Free(); enemy.Free(); }
        });
        runner.Run("navigation/ground plane sharing follows the local accuracy tuner", () =>
        {
            var caps = new NavigationCapabilities(shape.Radius, shape.Height, tuning.RunSpeed, tuning.SprintSpeed,
                tuning.JumpVelocity, tuning.PlayerGravity, tuning.Acceleration, tuning.AirControl);
            var shared = Grid(new CourseWorld(), caps, tuning);
            TestRunner.Assert(ReferenceEquals(shared.LayerAt(0), shared.LayerAt(0.1f)), "near ground planes should share by default");
            using var precise = Tuning.LoadBaked();
            precise.BotLayerTolerance = 0.05f;
            var separate = Grid(new CourseWorld(), caps, precise);
            TestRunner.Assert(!ReferenceEquals(separate.LayerAt(0), separate.LayerAt(0.1f)), "accuracy override was ignored");
            precise.BotLayerTolerance = -1;
            TestRunner.Throws<ArgumentOutOfRangeException>(() => Grid(new CourseWorld(), caps, precise));
        });
        runner.Run("navigation/prototype walking recovery respects live hazards and dissolved ground without allocation", () =>
        {
            var hazards = new NavigationHazards();
            hazards.Add(Vector3.Zero, 2);
            hazards.Build();
            TestRunner.Assert(!BotMotor.RecoveryAllowed(Vector3.Right * 2.1f, Vector3.Right * 1.9f, hazards, 0.1f, null),
                "walking recovery entered tank clearance");
            TestRunner.Assert(BotMotor.RecoveryAllowed(Vector3.Right, Vector3.Right * 1.1f, hazards, 0.1f, null),
                "walking recovery prevented escape from existing danger");
            hazards.Clear();
            var area = new ChangingArea { Removed = true };
            TestRunner.Assert(!BotMotor.RecoveryAllowed(Vector3.Left, Vector3.Right, hazards, 0.1f, area),
                "walking recovery crossed a dissolved lot with supported endpoints");
            TestRunner.Assert(!BotMotor.RecoveryAllowed(Vector3.Right * 0.8f, Vector3.Right * 0.7f, hazards, 0.1f, area),
                "walking recovery stepped off the remaining ground");
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 100; i++)
            {
                TestRunner.Assert(BotMotor.RecoveryAllowed(Vector3.Right, Vector3.Right * 1.1f, hazards, 0.1f, area),
                    "safe recovery changed after warm-up");
            }
            TestRunner.Assert(GC.GetAllocatedBytesForCurrentThread() == before, "walking recovery allocated");
        });
        runner.Run("navigation/crowded King recovery regroups forward then returns without warm allocation", () =>
        {
            var player = new PlayerCharacter { World = runner.World, ActorId = 1, Position = Vector3.Right * 2 };
            var enemy = new PlayerCharacter { World = runner.World, ActorId = 2, Position = Vector3.Right * 100 };
            runner.World.Root.AddChild(player);
            runner.World.Root.AddChild(enemy);
            var view = new KingState(0, new ModeSpot(0, 0, 0), new ModeSpot(20, 0, 0), 6, 1, new[] { 1 },
                2, 2, 45, 3, Array.Empty<KingMinted>(), 0);
            using var crowd = new BankingWorld(runner.World, player, enemy)
            {
                View = view,
                RemoteBodies = Enumerable.Repeat(enemy, 15).ToArray(),
            };
            using var brain = new KingBot(23);
            try
            {
                brain.Prepare(crowd);
                player.RemoteRagdoll(player.GlobalPosition, Vector3.Zero, Vector3.Zero);
                brain.Think(crowd, 1f / 120);
                TestRunner.Assert(brain.Status == "down", "knockdown must suspend intent");
                player.Teleport(Vector3.Right * 2, 0);
                brain.Think(crowd, 1f / 120);
                TestRunner.Assert(brain.Status == "regrouping outside the zone", "crowded recovery must not immediately charge back");
                for (var i = 0; i < 100; i++) brain.Think(crowd, 1f / 120);
                var yaw = Mathf.DegToRad(player.Aim.Yaw);
                TestRunner.Assert(new Vector3(-Mathf.Sin(yaw), 0, -Mathf.Cos(yaw)).Dot(Vector3.Right) > 0.9f,
                    "retreat must face outward so the controller permits sprint");
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < 100; i++) brain.Think(crowd, 1f / 120);
                TestRunner.Assert(GC.GetAllocatedBytesForCurrentThread() == before, "warmed regroup thoughts allocate");
                brain.Think(crowd, runner.World.Tuning.BotKingRegroupMax + 1);
                brain.Think(crowd, 1f / 120);
                TestRunner.Assert(brain.Status != "regrouping outside the zone", "regroup must end and resume the zone policy");
                using var small = new BankingWorld(runner.World, player, enemy) { View = view };
                using var original = new KingBot(23);
                original.Prepare(small);
                player.RemoteRagdoll(player.GlobalPosition, Vector3.Zero, Vector3.Zero);
                original.Think(small, 1f / 120);
                player.Teleport(Vector3.Right * 2, 0);
                original.Think(small, 1f / 120);
                TestRunner.Assert(original.Status != "regrouping outside the zone", "small matches must retain immediate reentry");
            }
            finally { player.Free(); enemy.Free(); }
        });
        runner.Run("navigation/King bot views match snapshots without steady allocation", () =>
        {
            var rules = new KingRules();
            rules.Initialize(new ModeSetup(new[] { new ModeActor(1, "Bot") },
                new ModeWorld(ModeWorldSource.Void, Spots: new[] { new ModeSpot(0, 0, 0), new ModeSpot(20, 0, 0) }),
                ModeRegistry.King.CopyTuners(tuning), 23));
            var clock = new ModeClock(ModePhase.Round, 1, 100, 280);
            TestRunner.Assert(KingBotView.Read(rules, clock) == null, "no zone may be guessed before it is sampled");
            rules.Handle(new PhaseEntered(ModePhase.Round, new RoundInfo(1, 1, RoundTimer.Countdown, 100, 180)), clock);
            foreach (var elapsed in new[] { 0d, 42d, 44d, 45d, 46d, 90d })
            {
                clock = clock with { Now = 100 + elapsed };
                rules.Handle(new ActorsSampled(new[] { new ActorSample(1, 0, 0, 0) }), clock);
                var snapshot = rules.State(clock)!;
                var expected = new KingBotView(snapshot.Zone, snapshot.Spot, snapshot.Next, snapshot.Radius,
                    snapshot.Holder, snapshot.Warning, snapshot.UntilMove);
                TestRunner.Assert(KingBotView.Read(rules, clock) == expected &&
                    KingBotView.Read(snapshot, elapsed) == expected, "rule and network clocks disagree at a warning or move");
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < 1000; i++)
                {
                    TestRunner.Assert(KingBotView.Read(rules, clock) == expected && KingBotView.Read(snapshot, elapsed) == expected,
                        "a warmed King view changed");
                }

                TestRunner.Assert(GC.GetAllocatedBytesForCurrentThread() == before, "warmed King observations allocate");
            }
        });
        runner.Run("navigation/large Short Fuse rosters use separated road starts across resets", () =>
        {
            var settings = ModeRegistry.ShortFuse.Supply.Suburb(tuning, 16);
            for (var seed = 11; seed <= 13; seed++)
            {
                for (var round = 1; round <= 4; round++)
                {
                    var planSeed = 100 + seed * 37 + 7919 * (round - 1);
                    var plan = Propane.World.SuburbPlans.Generate(settings, planSeed);
                    var starts = ShortFuseArena.RimSpawns(plan, 16, (ulong)planSeed, tuning.MpStartSpacing);
                    TestRunner.Assert(starts.Count == 16 && starts.Distinct().Count() == 16, "sixteen starts must be unique");
                    TestRunner.Assert(starts.SequenceEqual(ShortFuseArena.RimSpawns(plan, 16, (ulong)planSeed, tuning.MpStartSpacing)), "starts must be seeded");
                    for (var i = 0; i < starts.Count; i++)
                    {
                        for (var j = 0; j < i; j++)
                            TestRunner.Assert(starts[i].DistanceTo(starts[j]) > 10, $"seed {planSeed}: starts {i}/{j} are crowded");
                    }
                }
            }
        });
        var caps = new NavigationCapabilities(shape.Radius, shape.Height, tuning.RunSpeed,
            tuning.SprintSpeed, tuning.JumpVelocity, tuning.PlayerGravity, tuning.Acceleration, tuning.AirControl);
        runner.Run("navigation/reserved standing heights choose actual planes without hot allocation", () =>
        {
            var grid = Grid(new CourseWorld(), caps, tuning);
            grid.ReserveLayers(tuning.BotLayerReserve);
            grid.Reserve(new Rect2(-20, -20, 40, 40));
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < tuning.BotLayerReserve; i++)
            {
                var height = 0.133f + i * 0.8f;
                var layer = grid.LayerAt(height);
                TestRunner.Assert(layer.Y == height, "reserved metadata guessed a ground height");
                TestRunner.Assert(ReferenceEquals(layer, grid.LayerAt(height)), "a reused height consumed another reserve");
                TestRunner.Assert(grid.Standable(layer, Vector2I.Zero) == NavigationAnswer.Yes,
                    "reserved height could not store its first collision answers");
            }
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            TestRunner.Assert(bytes == 0, $"reserved height discovery allocated {bytes} bytes");
            TestRunner.Throws<ArgumentOutOfRangeException>(() => grid.ReserveLayers(-1));
        });
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
            var world = Grid(new CourseWorld(), caps, tuning);
            var state = new NavigationState(Vector3.Zero, Vector3.Zero, true);
            for (var tick = 0; tick < 60; tick++)
            {
                var goal = new Vector3(0.1f * Mathf.Sin(tick), 0, -12);
                nav.Navigate(world, new NavigationHazards(), state with { Position = new Vector3(0, 0, -tick * 0.1f) }, goal, tuning, 1f / 60);
            }
            TestRunner.Assert(nav.Replans == 1 && nav.StuckEvents == 0, "small target changes do not thrash the route");
            nav.Reset();
            world = Grid(new CourseWorld { Impossible = true }, caps, tuning);
            var failed = false;
            var (gain, island) = (tuning.BotPartialRouteGain, tuning.BotIslandExpansions);
            tuning.BotPartialRouteGain = 0;
            tuning.BotIslandExpansions = -1;
            for (var tick = 0; tick < 120 && !failed; tick++)
            {
                failed = nav.Navigate(world, new NavigationHazards(), state, new Vector3(0, 0, -12), tuning, 1f / 60).Failed;
            }
            (tuning.BotPartialRouteGain, tuning.BotIslandExpansions) = (gain, island);
            TestRunner.Assert(failed, "unreachable goals fail after bounded retries");
            // Off ground it has no lattice for, it first steps toward the goal, and still gives up in bounded time.
            nav.Reset();
            world = Grid(new CourseWorld { Impossible = true }, caps, tuning);
            failed = false;
            var walked = false;
            for (var tick = 0; tick < 900 && !failed; tick++)
            {
                var intent = nav.Navigate(world, new NavigationHazards(), state, new Vector3(0, 0, -12), tuning, 1f / 60);
                walked |= intent.Direction.Z < -0.5f;
                failed = intent.Failed;
            }
            TestRunner.Assert(walked && failed, "an unreachable goal is walked toward, then abandoned in bounded time");
        });
        runner.Run("navigation abandons cached routes when its shared grid is replaced", () =>
        {
            var nav = new BotNavigator();
            var state = new NavigationState(Vector3.Zero, Vector3.Zero, true);
            var goal = Vector3.Forward * 12;
            var open = Grid(new CourseWorld(), caps, tuning);
            TestRunner.Assert(nav.Navigate(open, new NavigationHazards(), state, goal, tuning, 1f / 60).Direction.LengthSquared() > 0,
                "fixture did not plan an open route");
            var replaced = Grid(new CourseWorld { Impossible = true }, caps, tuning);
            var move = nav.Navigate(replaced, new NavigationHazards(), state, goal, tuning, 1f / 60);
            TestRunner.Assert(move.Direction == Vector3.Zero && nav.Route.Count == 0 && nav.Replans == 2,
                "route or search from the retired grid survived replacement");
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
                    var air = nav.Navigate(Grid(new CourseWorld { Impossible = true }, caps, tuning), new NavigationHazards(), new NavigationState(position, velocity, false),
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
                var world = Grid(new CourseWorld(), caps, tuning);
                var dt = 1f / rate;
                for (var tick = 0; tick < rate * 6; tick++)
                {
                    var move = nav.Navigate(world, new NavigationHazards(), new NavigationState(position, velocity, true),
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
            // Between three overlapping circles every way out passes nearer one center: leaving them in all is enough.
            hazards.Clear();
            for (var i = 0; i < 3; i++) hazards.Add(new Vector3(1, 0, 0).Rotated(Vector3.Up, i * Mathf.Tau / 3), 2);
            var outward = new Vector3(1, 0, 0).Rotated(Vector3.Up, Mathf.Tau / 6);
            TestRunner.Assert(hazards.SafeAlong(Vector3.Zero, outward * 4, 0.3f, Vector3.Zero), "walk out from between overlapping circles");
            TestRunner.Assert(!hazards.Safe(Vector3.Zero, outward), "never walk deeper between overlapping circles");
            TestRunner.Assert(!hazards.Safe(outward * 2.5f + new Vector3(3, 0, 0), Vector3.Zero) || hazards.Safe(outward * 2.5f + new Vector3(3, 0, 0)),
                "escaping never enters a circle the start is outside");
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
        runner.Run("mode boundaries reject cached walks across removed ground", () =>
        {
            var grid = Grid(new CourseWorld(), caps, tuning);
            var hazards = new NavigationHazards();
            var area = new ChangingArea();
            var start = new Vector3(0, 0, -4);
            var goal = new Vector3(0, 0, 4);
            var nav = new BotNavigator();
            nav.Navigate(grid, hazards, new NavigationState(start, Vector3.Zero, true), goal, tuning, 1f / 120, area);
            TestRunner.Assert(nav.Route.Count == 1, "fixture must cache the direct walk before the ground disappears");
            area.Removed = true;
            nav.Navigate(grid, hazards, new NavigationState(start, Vector3.Zero, true), goal, tuning, tuning.BotReplanInterval + 0.01f, area);
            TestRunner.Assert(nav.Route.Count == 0, "live boundary must invalidate the cached shortcut");
            var route = BotNavigator.Plan(grid, hazards, start, goal, tuning, area: area);
            TestRunner.Assert(route.Count > 1, "route must detour around the removed lot");
            foreach (var leg in route)
            {
                for (var i = 0; i <= 100; i++)
                    TestRunner.Assert(area.Allows(leg.From.Lerp(leg.To, i / 100f)), "a route with safe endpoints crossed removed ground");
            }
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
        runner.Run("banking bots pursue a richer opponent after the suburb is cleared", () =>
        {
            var player = new PlayerCharacter { World = runner.World, ActorId = 1, Ammo = AmmoState.Starting(tuning) };
            var enemy = new PlayerCharacter { World = runner.World, ActorId = 2, Position = Vector3.Right * 100 };
            runner.World.Root.AddChild(player);
            runner.World.Root.AddChild(enemy);
            using var observations = new BankingWorld(runner.World, player, enemy);
            using var brain = new BankingBot(23);
            var old = runner.World.Tuning.BotHuntWhenEmpty;
            try
            {
                brain.Prepare(observations);
                for (var i = 0; i < 100; i++) brain.Think(observations, 1f / 120);
                TestRunner.Assert(brain.Status.StartsWith("closing on 2", StringComparison.Ordinal), "empty suburb must not leave a trailing bot idle");
                TestRunner.Assert(!brain.Read().FireHeld, "do not shoot a target beyond rifle range");
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < 100; i++) brain.Think(observations, 1f / 120);
                var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                TestRunner.Assert(bytes == 0, $"steady banking thoughts allocate {bytes} bytes");
                runner.World.Tuning.BotHuntWhenEmpty = false;
                for (var i = 0; i < 100; i++) brain.Think(observations, 1f / 120);
                TestRunner.Assert(brain.Status == "no target", "pursuit is controlled by its local tuner");
            }
            finally
            {
                runner.World.Tuning.BotHuntWhenEmpty = old;
                player.Free(); enemy.Free();
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
