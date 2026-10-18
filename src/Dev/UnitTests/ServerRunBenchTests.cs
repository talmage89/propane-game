using System;
using System.Diagnostics;
using System.Linq;
using Godot;
using Propane.Dev.UnitTests.Fixtures;
using Propane.Modes;
using Propane.Net;
using Propane.Player;

namespace Propane.Dev.UnitTests;

/// <summary>Real receive and whole run-tick workload; allocations include packet decoding and encoding.</summary>
[UnitSuite]
public static class ServerRunBenchTests
{
    public static void Register(TestRunner runner)
    {
        runner.Run("server run/benchmark is opt-in", () =>
        {
            if (DevArgs.Get("server-bench") == null) return;
            foreach (var mode in new[] { ModeRegistry.TankBank, ModeRegistry.King })
            {
                using var f = new ServerFixture(mode: mode, length: 3600, bots: 2);
                Measure(mode.Name, f.Server, f.Clock, f.Id, f.ActorId(2));
            }
            using var world = new LobbyWorldFixture(3);
            Measure("roaming", world.Server, world.Clock, world.World().Id, world.Actor(2).Id);
        });
    }

    private static void Measure(string label, LobbyServer server, ManualClock clock, int run, int actor)
    {
        const int warmup = 2000, ticks = 10000;
        var poses = new byte[warmup + ticks][];
        var bodies = new byte[poses.Length][];
        for (var i = 0; i < poses.Length; i++)
        {
            poses[i] = ActorMessages.Encode(Msg.PlayerState, run, actor, 1, (uint)i + 1,
                new PlayerStateMessage(0, new PlayerNetState { Position = new Vector3(2, 0, 3) }));
            bodies[i] = MessageCodec.Encode(new NetMessage(Msg.BodyStates, run, 0,
                new BodyStatesMessage(0, Enumerable.Range(0, 8).Select(n => new BodyState(BodySync.MapTankId(n),
                    new Transform3D(Basis.Identity, new Vector3(n, 1, 2)), Vector3.Right, Vector3.Zero, 1, (uint)i + 1)).ToArray())), false);
        }
        var packets = 0;
        long payload = 0;
        server.SendOverride = (_, bytes, _) => { packets++; payload += bytes.Length; };
        void Tick(int i)
        {
            clock.Seconds = i / 120.0;
            server.OnReceived(2, poses[i], TrafficClass.UnreliablePlayerPoses);
            server.OnReceived(2, bodies[i], TrafficClass.ReliableBodyStates2);
            server.AdvanceMatches(clock.Seconds);
        }
        for (var i = 0; i < warmup; i++) Tick(i);
        packets = 0;
        payload = 0;
        var times = new double[ticks];
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < ticks; i++)
        {
            var start = Stopwatch.GetTimestamp();
            Tick(warmup + i);
            times[i] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
        }
        var bytesPerTick = (GC.GetAllocatedBytesForCurrentThread() - allocated) / (double)ticks;
        TestRunner.Assert(packets >= ticks * 2 && payload > 0, "server workload did no relay work");
        Array.Sort(times);
        GD.Print(FormattableString.Invariant($"[server-bench] mode={label} ticks={ticks} bodies=8 packets={packets} payload={payload} bytes_per_tick={bytesPerTick:F2} mean_us={times.Average():F3} p95_us={times[(int)((ticks - 1) * .95)]:F3} p99_us={times[(int)((ticks - 1) * .99)]:F3}"));
    }
}
