using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Godot;
using Propane.Net;
using Propane.World;

namespace Propane.Dev.UnitTests;

/// <summary>Supplement the frozen release transcript with synchronous send boundaries and a real-path benchmark.</summary>
[UnitSuite]
public static class BodyPublicationTests
{
    public static void Register(TestRunner runner)
    {
        runner.Run("publication/later batches sample after synchronous sends, and retirement still advances sequences", () =>
        {
            using var f = new Fixture(runner, Protocol.MaxBodyStates * 2 + 1);
            var packets = new List<object>();
            var calls = 0;
            var changed = new Transform3D(Basis.Identity, new Vector3(17, 23, 31));
            // Put every entry on the same initial lane, crossing two full batches and a partial one.
            foreach (var entry in f.Entries) entry.CheckpointTraffic = TrafficClass.ReliableBodyStates3;
            f.Wire.OnSend = (traffic, bytes) =>
            {
                packets.Add(new { traffic, bytes = Convert.ToBase64String(bytes.Span) });
                var states = (BodyStatesMessage)MessageCodec.Decode(bytes.ToArray(), false).Payload;
                calls++;
                if (calls == 1) f.Entries[Protocol.MaxBodyStates].Body.GlobalTransform = changed;
                if (calls == 2)
                {
                    TestRunner.Assert(states.Bodies[0].Transform == changed, "later batch sampled before the preceding send");
                    // Match.Send checks world lifetime at every send, but serialization already advances sequences.
                    f.Match.World.Dispose();
                }
            };
            f.Publish(100);
            TestRunner.Assert(calls == 2 && f.Entries.All(e => e.Sequence == 1), "retirement changed send/sequence boundaries");
            // Optional parent/candidate artifact, separate from the immutable release fixtures.
            if (DevArgs.Get("publication-transcript") is { } path)
                File.WriteAllText(path, JsonSerializer.Serialize(new { packets, sequences = f.Entries.Select(e => e.Sequence) }));
        });
        runner.Run("publication/empty collections do not replay earlier groups or retain their encounter order", () =>
        {
            using var f = new Fixture(runner, 4);
            var traffic = new List<TrafficClass>();
            f.Wire.OnSend = (lane, _) => traffic.Add(lane);
            f.Publish(100);
            TestRunner.Assert(traffic.SequenceEqual(new[] { TrafficClass.ReliableBodyStates, TrafficClass.ReliableBodyStates2,
                TrafficClass.ReliableBodyStates3 }), "first encounter order changed");
            traffic.Clear();
            f.Publish(100);
            TestRunner.Assert(traffic.Count == 0, "empty collection replayed a prior group");
            f.Entries[0].ClaimPending = true;
            f.Publish(100.3);
            TestRunner.Assert(traffic.SequenceEqual(new[] { TrafficClass.ReliableBodyStates3, TrafficClass.ReliableBodyStates,
                TrafficClass.ReliableBodyStates2 }), "group order leaked from an earlier collection");
        });
        if (DevArgs.Get("publication-bench") != null)
        {
            runner.Run("publication/controlled mixed-body collector plus publisher benchmark", () => Benchmark(runner, wholeCallback: false));
            runner.Run("publication/controlled mixed-body whole Match physics callback benchmark", () => Benchmark(runner, wholeCallback: true));
        }
    }

    private static void Benchmark(TestRunner runner, bool wholeCallback)
    {
        const int count = 256;
        const int warmup = 2000;
        const int ticks = 10000;
        using var f = new Fixture(runner, count);
        var packets = 0;
        long payloadBytes = 0;
        f.Wire.OnSend = (_, bytes) => { packets++; payloadBytes += bytes.Length; };
        Action<double> tick = wholeCallback ? f.PhysicsTick : f.Publish;
        for (var i = 0; i < warmup; i++) tick(100 + i / 120.0);
        packets = 0;
        payloadBytes = 0;
        var times = new double[ticks];
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < ticks; i++)
        {
            var start = Stopwatch.GetTimestamp();
            tick(100 + (warmup + i) / 120.0);
            times[i] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
        }
        var bytesPerTick = (GC.GetAllocatedBytesForCurrentThread() - allocated) / (double)ticks;
        TestRunner.Assert(packets > 0 && payloadBytes > 0 && f.Entries.All(e => e.Sequence > 1), "benchmark did no publication work");
        Array.Sort(times);
        GD.Print(FormattableString.Invariant($"[publication-bench] path={(wholeCallback ? "match-physics" : "publication")} bodies={count} tanks={count / 2} cars={count / 2} ticks={ticks} packets={packets} payload_bytes={payloadBytes} bytes_per_tick={bytesPerTick:F2} mean_us={times.Average():F3} p95_us={times[(int)((ticks - 1) * .95)]:F3} p99_us={times[(int)((ticks - 1) * .99)]:F3}"));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ServerTests.Fixture server = new();
        private readonly LoopbackTransport listener;
        private readonly NetClient client;
        public readonly SinkTransport Wire;
        public readonly Match Match;
        public readonly BodySync.Entry[] Entries;
        public readonly Action<double> Publish;

        public Fixture(TestRunner runner, int count)
        {
            var network = new LoopbackNetwork();
            listener = network.Create();
            Wire = new SinkTransport(network.Create());
            client = new NetClient(Wire, server.Clock) { Template = server.Tuning };
            TestRunner.Assert(listener.Listen(new PacketEndpoint("", 1234), 4).Success, "publication fixture listen");
            client._Ready();
            client.Connect("", 1234, "Publication", 0);
            client._Process(0);
            listener.Poll();
            client.Handle(MessageCodec.Encode(new NetMessage(Msg.Welcome, 0, 0, new WelcomeMessage(2, null)), true));
            var setup = server.Setup with { Plan = PlanCodec.Encode(SuburbPlans.Generate(SuburbPlans.Match(server.Tuning, 2), 23)) };
            Match = new Match { Net = client, Setup = setup, Clock = server.Clock, CreateLocalView = false };
            runner.AddChild(Match);
            Match.Bodies.Clear();
            for (var i = 0; i < count; i++)
            {
                var body = new RigidBody3D { Sleeping = false };
                Match.AddChild(body);
                body.GlobalTransform = new Transform3D(Basis.Identity, new Vector3(i, 10, -i));
                body.LinearVelocity = new Vector3(i + 1, 2, 3);
                body.AngularVelocity = new Vector3(0, i % 4, 0);
                var isTank = i % 2 == 0;
                var id = isTank ? BodySync.MapTankId(i) : BodySync.CarId(i);
                Match.Bodies.Register(id, body, isTank, 2);
                Match.Bodies.Get(id)!.CheckpointTraffic = i % 3 == 0 ? TrafficClass.ReliableBodyStates3 :
                    i % 3 == 1 ? TrafficClass.ReliableBodyStates : TrafficClass.ReliableBodyStates2;
            }
            Entries = Match.Bodies.Entries.ToArray();
            Publish = typeof(Match).GetMethod("SendBodies", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<Action<double>>(Match);
        }

        public void PhysicsTick(double now)
        {
            server.Clock.Seconds = now;
            Match._PhysicsProcess(1.0 / 120);
        }

        public void Dispose()
        {
            Match.World.Dispose();
            Match.Free();
            client._ExitTree();
            client.Free();
            listener.Close();
            server.Dispose();
        }
    }

    // Consume outgoing bytes synchronously, without recording/decoding/queueing allocations in benchmark windows.
    private sealed class SinkTransport(LoopbackTransport inner) : IPacketTransport
    {
        public Action<TrafficClass, ReadOnlyMemory<byte>>? OnSend;
        public int MaxPayloadSize => inner.MaxPayloadSize;
        public event Action<ConnectionHandle>? Connected { add => inner.Connected += value; remove => inner.Connected -= value; }
        public event Action<ConnectionHandle, DisconnectReason>? Disconnected { add => inner.Disconnected += value; remove => inner.Disconnected -= value; }
        public event Action<ConnectionHandle, TrafficClass, ReadOnlyMemory<byte>>? Packet { add => inner.Packet += value; remove => inner.Packet -= value; }
        public OpenResult Listen(PacketEndpoint endpoint, int maxConnections) => inner.Listen(endpoint, maxConnections);
        public OpenResult Connect(PacketEndpoint endpoint, out ConnectionHandle? connection) => inner.Connect(endpoint, out connection);
        public void Poll() => inner.Poll();
        public SendResult Send(ConnectionHandle connection, TrafficClass traffic, ReadOnlyMemory<byte> bytes)
        {
            OnSend?.Invoke(traffic, bytes);
            return SendResult.Success;
        }
        public void Disconnect(ConnectionHandle connection, DisconnectReason reason) => inner.Disconnect(connection, reason);
        public void Close() => inner.Close();
        public TransportDiagnostics Diagnostics(ConnectionHandle connection) => inner.Diagnostics(connection);
    }
}
