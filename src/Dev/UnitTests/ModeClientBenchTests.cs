using System;
using System.Diagnostics;
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
                void Tick()
                {
                    bank.Clock.Seconds += 1d / 120;
                    match._PhysicsProcess(1d / 120);
                    foreach (var actor in owned) actor.Character._PhysicsProcess(1d / 120);
                    match._Process(1d / 120);
                }
                const int ticks = 12000;
                for (var i = 0; i < 3000; i++) Tick();
                var samples = new double[ticks];
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < ticks; i++)
                {
                    var start = Stopwatch.GetTimestamp();
                    Tick();
                    samples[i] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
                }
                var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                var mean = samples.Average();
                Array.Sort(samples);
                TestRunner.Assert(match.Session.IsAlive && match.IsPlaying && owned.Length == 1, "client tick workload changed");
                GD.Print(FormattableString.Invariant($"[mode-client-cost] mode={setup.ModeId} actors={match.Session.Actors.Count} owned={owned.Length} bodies={match.Bodies.Entries.Count()} ticks={ticks} bytes={bytes} mean_us={mean:F3} p95_us={samples[(int)(ticks * .95) - 1]:F3} p99_us={samples[(int)(ticks * .99) - 1]:F3}"));
            }
        });
    }
}
