using System;
using System.Diagnostics;
using System.Linq;
using Godot;
using Propane.Dev.UnitTests.Fixtures;
using Propane.Modes;
using Propane.Net;
using Propane.Player;

namespace Propane.Dev.UnitTests;

/// <summary>Whole client receive/dispatch plus both existing poll callbacks; preparation excluded, receiver counted.</summary>
[UnitSuite]
public static class ClientReceiveBenchTests
{
    public static void Register(TestRunner runner)
    {
        runner.Run("client receive/benchmark is opt-in", () =>
        {
            if (DevArgs.Get("client-receive-bench") == null) return;
            var clock = new ManualClock();
            var client = new NetClient(new LoopbackNetwork().Create(), ModeRegistry.Codec, clock);
            try
            {
                var setup = (MatchSetup)ProtocolTests.Fixtures().Single(m => m.Type == Msg.MatchStart).Payload;
                Handshake.Welcome(client, 2);
                var lobby = new LobbyInfo(1, 2, 10, LobbyPhase.Waiting, setup.Players.Select(p => p.Player).ToArray()) { Peers = new[] { 2 } };
                client.Handle(MessageCodec.Encode(new NetMessage(Msg.LobbyState, 0, 0, lobby), true));
                client.Handle(MessageCodec.Encode(new NetMessage(Msg.MatchStart, setup.Id, 0, setup), true));
                var received = 0;
                var lists = 0;
                client.LobbiesChanged += () => lists++;
                TestRunner.Assert(client.AttachMatch(setup.Id, _ => received++), "benchmark receiver did not attach");
                var pose = MessageCodec.Encode(new NetMessage(Msg.PlayerState, setup.Id, 2, new PlayerStateMessage(0,
                    new PlayerNetState { Position = Vector3.One }), 2, 1, 1), true);
                var bodies = MessageCodec.Encode(new NetMessage(Msg.BodyStates, setup.Id, 2, new BodyStatesMessage(0,
                    Enumerable.Range(0, 8).Select(n => new Propane.Core.BodyState(Propane.Core.BodyIds.MapTankId(n),
                        Transform3D.Identity, Vector3.Zero, Vector3.Zero, 1, 1)).ToArray())), true);
                var list = MessageCodec.Encode(new NetMessage(Msg.LobbyList, 0, 0,
                    new LobbyListMessage(5, new[] { new LobbySummary(1, "bench", 1, LobbyPhase.Waiting) })), true);
                void Tick()
                {
                    clock.Seconds += 1.0 / 120;
                    client.Handle(pose); client.Handle(bodies); client.Handle(list);
                    client._PhysicsProcess(1.0 / 120); client._Process(1.0 / 60);
                }
                const int warmup = 20000;
                var ticks = DevArgs.Get("network-bench-long") != null ? 300000 : 30000;
                for (var i = 0; i < warmup; i++) Tick();
                received = lists = 0;
                var times = new double[ticks];
                using var process = Process.GetCurrentProcess();
                var cpuStart = process.TotalProcessorTime;
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < ticks; i++)
                {
                    var start = Stopwatch.GetTimestamp(); Tick();
                    times[i] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
                }
                var bytes = (GC.GetAllocatedBytesForCurrentThread() - allocated) / (double)ticks;
                var cpuUs = (process.TotalProcessorTime - cpuStart).TotalMicroseconds / ticks;
                TestRunner.Assert(received == ticks * 2 && lists == ticks, "benchmark lost dispatch work");
                Array.Sort(times);
                GD.Print(FormattableString.Invariant($"[client-receive-bench] ticks={ticks} packets={ticks * 3} dispatches={received} bodies=8 lobbies=1 payload={ticks * (pose.Length + bodies.Length + list.Length)} bytes_per_tick={bytes:F2} cpu_us={cpuUs:F3} mean_us={times.Average():F3} p95_us={times[(int)((ticks - 1) * .95)]:F3} p99_us={times[(int)((ticks - 1) * .99)]:F3}"));
            }
            finally { client.Free(); }
        });
    }
}
