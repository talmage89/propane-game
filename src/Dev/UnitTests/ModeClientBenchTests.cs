using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Godot;
using Propane.Dev.UnitTests.Fixtures;
using Propane.Modes;
using Propane.Net;

namespace Propane.Dev.UnitTests;

/// <summary>Identical opt-in whole client physics/render callbacks with native actors, bodies and HUD feeds.</summary>
[UnitSuite]
public static class ModeClientBenchTests
{
    public static void Register(TestRunner runner)
    {
        runner.Run("mode client/opt-in whole client tick cost", () =>
        {
            if (DevArgs.Get("mode-client-bench") == null) return;
            using var bank = new ServerFixture(length: 3600);
            using var king = new ServerFixture(length: 3600, mode: ModeRegistry.King);
            using var lobby = new LobbyWorldFixture(3);
            foreach (var setup in new[] { bank.Setup, king.Setup, lobby.World() })
            {
                using var client = new MatchClient(runner, setup, bank.Clock, 2);
                var match = client.Match;
                match.OnMessage(new NetMessage(Msg.MatchGo, setup.Id, 0, new FloatMessage(3600)));
                if (setup.ModeId == ModeRegistry.KingId)
                {
                    match.OnMessage(new NetMessage(Msg.ModeSnapshot, setup.Id, 0, new ModeEnvelope(1, 1, setup.ModeId, 1, 0, 0, 1,
                        ModeSchemas.KingKinds.Zone, new KingState(1, new ModeSpot(0, 0, 0), new ModeSpot(8, 0, 0), 4, match.Player.ActorId,
                            new[] { match.Player.ActorId }, 2, 2, 40, 5, Array.Empty<KingMinted>(), 0))));
                }

                var owned = match.Session.OwnedActors.ToArray();
                var ticks = int.Parse(DevArgs.Get("mode-client-bench-ticks") ?? "12000", CultureInfo.InvariantCulture);
                TestRunner.Assert(ticks > 0 && ticks < 300000, "benchmark tick count exceeds the playing window");
                var physics = new double[ticks];
                var actors = new double[ticks];
                var render = new double[ticks];
                var allocations = new long[ticks];
                void Tick(int sample = -1)
                {
                    var start = Stopwatch.GetTimestamp();
                    bank.Clock.Seconds += 1d / 120;
                    match._PhysicsProcess(1d / 120);
                    var afterPhysics = Stopwatch.GetTimestamp();
                    foreach (var actor in owned) actor.Character._PhysicsProcess(1d / 120);
                    var afterActors = Stopwatch.GetTimestamp();
                    match._Process(1d / 120);
                    var end = Stopwatch.GetTimestamp();
                    if (sample >= 0)
                    {
                        physics[sample] = Stopwatch.GetElapsedTime(start, afterPhysics).TotalMicroseconds;
                        actors[sample] = Stopwatch.GetElapsedTime(afterPhysics, afterActors).TotalMicroseconds;
                        render[sample] = Stopwatch.GetElapsedTime(afterActors, end).TotalMicroseconds;
                    }
                }
                for (var i = 0; i < 20000; i++) Tick();
                var samples = new double[ticks];
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < ticks; i++)
                {
                    var beforeTick = GC.GetAllocatedBytesForCurrentThread();
                    var start = Stopwatch.GetTimestamp();
                    Tick(i);
                    samples[i] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
                    allocations[i] = GC.GetAllocatedBytesForCurrentThread() - beforeTick;
                }
                var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                TestRunner.Assert(bytes == allocations.Sum(), "client tick allocation accounting is incomplete");
                var mean = samples.Average();
                Array.Sort(samples);
                TestRunner.Assert(match.Session.IsAlive && match.IsPlaying && owned.Length == 1, "client tick workload changed");
                GD.Print(FormattableString.Invariant($"[mode-client-cost] mode={setup.ModeId} actors={match.Session.Actors.Count} owned={owned.Length} bodies={match.Bodies.Entries.Count()} ticks={ticks} bytes={bytes} mean_us={mean:F3} p95_us={samples[(int)(ticks * .95) - 1]:F3} p99_us={samples[(int)(ticks * .99) - 1]:F3}"));
                var sizes = string.Join(",", allocations.GroupBy(size => size).OrderBy(group => group.Key).Select(group => $"{group.Key}:{group.Count()}"));
                GD.Print(FormattableString.Invariant($"[mode-client-stages] mode={setup.ModeId} physics_us={physics.Average():F3} actors_us={actors.Average():F3} render_us={render.Average():F3} allocation_sizes={sizes}"));
            }
        });
    }
}
