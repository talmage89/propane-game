using System;
using System.Collections.Generic;
using System.Linq;
using Propane.Net;

namespace Propane.Dev.UnitTests;

/// <summary>Contract assertions use manual time and poisoned receive buffers; never sleeps or opens a socket.</summary>
[UnitSuite]
public static class TransportTests
{
    private sealed class Clock : IMonotonicClock { public double Seconds { get; set; } = 100; }
    private static readonly PacketEndpoint Endpoint = new("", 1234);
    private static void Assert(bool condition, string message) => TestRunner.Assert(condition, message);

    private sealed class Pair : IDisposable
    {
        public readonly Clock Clock = new();
        public readonly LoopbackNetwork Network = new();
        public readonly LoopbackTransport Server;
        public readonly LoopbackTransport RawClient;
        public readonly IPacketTransport Client;
        public readonly ConnectionHandle ClientHandle;
        public readonly ConnectionHandle ServerHandle;
        public readonly List<(TrafficClass Traffic, byte[] Bytes)> AtServer = new();
        public readonly List<byte[]> AtClient = new();

        public Pair(PacketFaults? faults = null, int seed = 1, int maxPayload = 512 * 1024,
            int queuePackets = 4096, int queueBytes = 4 << 20, int faultPackets = 4096, int faultBytes = 4 << 20)
        {
            Server = Network.Create(maxPayload, queuePackets, queueBytes);
            RawClient = Network.Create(maxPayload, queuePackets, queueBytes);
            Client = faults == null ? RawClient : new FaultPacketTransport(RawClient, Clock, seed, faults, faultPackets, faultBytes);
            Assert(Server.Listen(Endpoint, 5).Success, "listen failed");
            Assert(Client.Connect(Endpoint, out var connection).Success && connection != null, "connect failed");
            ClientHandle = connection!;
            Client.Poll();
            Server.Poll();
            ServerHandle = Server.PeerIds.Connection(2)!;
            Server.Packet += (_, traffic, bytes) => AtServer.Add((traffic, bytes.ToArray()));
            Client.Packet += (_, _, bytes) => AtClient.Add(bytes.ToArray());
        }
        public SendResult Send(byte value, TrafficClass traffic = TrafficClass.ReliableOrdered) => Client.Send(ClientHandle, traffic, new byte[] { value });
        public void Pump(double advance = 0) { Clock.Seconds += advance; Client.Poll(); Server.Poll(); }
        public void Dispose() { Client.Close(); Server.Close(); }
    }

    public static void Register(TestRunner runner)
    {
        runner.Run("transport/new checkpoint lanes pass a blocked older snapshot", () =>
        {
            using var p = new Pair(new PacketFaults(BodyStall: 2, Loss: 1));
            Assert(TrafficClass.ReliableBodyStates2.IsReliable() && TrafficClass.ReliableBodyStates3.IsReliable(),
                "replacement checkpoints are still disposable");
            p.Send(1, TrafficClass.ReliableBodyStates);
            p.Send(2, TrafficClass.ReliableBodyStates2);
            p.Send(3, TrafficClass.ReliableBodyStates3);
            p.Pump(.1);
            Assert(p.AtServer.Select(n => n.Bytes[0]).SequenceEqual(new byte[] { 2, 3 }),
                "older snapshot blocked its replacements");
            p.Pump(2);
            Assert(p.AtServer.Select(n => n.Bytes[0]).SequenceEqual(new byte[] { 2, 3, 1 }),
                "late reliable snapshot disappeared");
        });
        runner.Run("transport/stalled motion checkpoints cannot delay reliable control", () =>
        {
            Assert(NetTransport.Channel(TrafficClass.ReliableBodyStates) != NetTransport.Channel(TrafficClass.ReliableOrdered),
                "native checkpoints still share control retransmission ordering");
            using var p = new Pair(new PacketFaults(BodyStall: 2, Loss: 1));
            p.Send(1, TrafficClass.ReliableBodyStates);
            p.Send(2);
            p.Pump(.1);
            Assert(p.AtServer.Select(n => n.Bytes[0]).SequenceEqual(new byte[] { 2 }), "body stall held a control message behind its checkpoint");
            p.Pump(2);
            Assert(p.AtServer.Select(n => n.Bytes[0]).SequenceEqual(new byte[] { 2, 1 }) &&
                p.AtServer.Last().Traffic == TrafficClass.ReliableBodyStates, "independent reliable checkpoint disappeared under disposable loss");
        });
        runner.Run("transport/checkpoint backend pressure preserves each lane without blocking control", () =>
        {
            using var p = new Pair(new PacketFaults());
            p.RawClient.BlockTraffic = TrafficClass.ReliableBodyStates;
            p.Send(1, TrafficClass.ReliableBodyStates);
            p.Send(2);
            p.Send(3, TrafficClass.ReliableBodyStates);
            p.Send(4);
            p.Pump();
            Assert(p.AtServer.Select(n => n.Bytes[0]).SequenceEqual(new byte[] { 2, 4 }), "checkpoint pressure blocked ordered control");
            p.RawClient.BlockTraffic = null;
            p.Pump();
            Assert(p.AtServer.Select(n => n.Bytes[0]).SequenceEqual(new byte[] { 2, 4, 1, 3 }), "checkpoint retry reordered or lost its lane");
        });
        runner.Run("transport/both reliable lanes retain independent order through jitter", () =>
        {
            using var p = new Pair(new PacketFaults(.05, .3, Loss: 1));
            for (byte i = 0; i < 40; i++)
            {
                p.Send(i);
                p.Send((byte)(i + 100), TrafficClass.ReliableBodyStates);
                p.Server.Send(p.ServerHandle, TrafficClass.ReliableOrdered, new byte[] { i });
                p.Server.Send(p.ServerHandle, TrafficClass.ReliableBodyStates, new byte[] { (byte)(i + 100) });
            }
            p.Pump(.4);
            var ordered = Enumerable.Range(0, 40).Select(i => (byte)i);
            Assert(p.AtServer.Where(n => n.Traffic == TrafficClass.ReliableOrdered).Select(n => n.Bytes[0]).SequenceEqual(ordered), "control lane reordered");
            Assert(p.AtServer.Where(n => n.Traffic == TrafficClass.ReliableBodyStates).Select(n => (byte)(n.Bytes[0] - 100)).SequenceEqual(ordered), "checkpoint lane reordered");
            p.Pump(.4);
            Assert(p.AtClient.Where(n => n[0] < 100).Select(n => n[0]).SequenceEqual(ordered) &&
                p.AtClient.Where(n => n[0] >= 100).Select(n => (byte)(n[0] - 100)).SequenceEqual(ordered), "incoming lane order failed");
        });
        runner.Run("transport/reliable order survives seeded jitter in both directions", () =>
        {
            using var p = new Pair(new PacketFaults(.05, .3));
            for (byte i = 0; i < 100; i++)
            {
                Assert(p.Send(i) == SendResult.Success, "reliable send failed");
                Assert(p.Server.Send(p.ServerHandle, TrafficClass.ReliableOrdered, new byte[] { i }) == SendResult.Success, "reply failed");
            }
            p.Pump();
            Assert(p.AtServer.Count == 0 && p.AtClient.Count == 0, "lag bypassed");
            p.Pump(.4);
            Assert(p.AtServer.Select(n => n.Bytes[0]).SequenceEqual(Enumerable.Range(0, 100).Select(i => (byte)i)), "outgoing reordered");
            Assert(p.AtClient.Select(n => n[0]).SequenceEqual(Enumerable.Range(0, 100).Select(i => (byte)i)), "incoming reordered");
        });
        runner.Run("transport/reliable deadlines are independent per connection", () =>
        {
            var network = new LoopbackNetwork();
            var clock = new Clock();
            var raw = network.Create();
            var server = new FaultPacketTransport(raw, clock, 1, new PacketFaults(Jitter: 1));
            var a = network.Create();
            var b = network.Create();
            try
            {
                server.Listen(Endpoint, 5);
                a.Connect(Endpoint, out _); b.Connect(Endpoint, out _);
                a.Poll(); b.Poll(); server.Poll();
                var values = new List<byte>();
                a.Packet += (_, _, bytes) => values.Add(bytes.Span[0]);
                b.Packet += (_, _, bytes) => values.Add(bytes.Span[0]);
                server.Send(raw.PeerIds.Connection(2)!, TrafficClass.ReliableOrdered, new byte[] { 1 });
                server.Send(raw.PeerIds.Connection(3)!, TrafficClass.ReliableOrdered, new byte[] { 2 });
                var random = new Random(1);
                var first = random.NextDouble(); var second = random.NextDouble();
                Assert(first > second, "seed no longer exercises independent deadlines");
                clock.Seconds += (first + second) / 2;
                server.Poll(); a.Poll(); b.Poll();
                Assert(values.SequenceEqual(new byte[] { 2 }), "one connection stalled another");
                clock.Seconds += 1;
                server.Poll(); a.Poll(); b.Poll();
                Assert(values.SequenceEqual(new byte[] { 2, 1 }), "first connection never drained");
            }
            finally { server.Close(); a.Close(); b.Close(); }
        });
        runner.Run("transport/loss drops only outgoing disposable traffic", () =>
        {
            using var p = new Pair(new PacketFaults(Loss: 1));
            for (byte i = 0; i < 20; i++)
            {
                p.Send(i);
                p.Send(i, TrafficClass.UnreliablePlayerPoses);
                p.Send(i, TrafficClass.UnreliableBodyStates);
            }
            p.Pump();
            Assert(p.AtServer.Count == 20 && p.AtServer.All(n => n.Traffic == TrafficClass.ReliableOrdered), "loss affected reliable traffic");
            var d = p.Client.Diagnostics(p.ClientHandle);
            Assert(d.PacketsOut == 60 && d.BytesOut == 60 && d.Drops == 40 && d.QueuedPackets == 0, "fault counters wrong");
        });
        runner.Run("transport/fault schedule repeats with its seed", () =>
        {
            static string Replay(int seed)
            {
                using var p = new Pair(new PacketFaults(.1, .4, .3), seed);
                for (byte i = 0; i < 40; i++) { p.Send(i, TrafficClass.UnreliableBodyStates); }
                var trace = new List<string>();
                for (var tick = 0; tick < 30; tick++)
                {
                    p.Pump(.025);
                    trace.Add(string.Join(',', p.AtServer.Select(n => n.Bytes[0])));
                }
                return string.Join(';', trace);
            }
            Assert(Replay(123) == Replay(123), "same seed changed loss/delay");
            Assert(Replay(123) != Replay(456), "different seeds did not change schedule");
        });
        runner.Run("transport/reliable stall permits unreliable traffic and then drains in order", () =>
        {
            using var p = new Pair(new PacketFaults(ReliableStall: .5));
            p.Send(1); p.Send(2); p.Send(3, TrafficClass.UnreliablePlayerPoses);
            p.Pump(.25);
            Assert(p.AtServer.Select(n => n.Bytes[0]).SequenceEqual(new byte[] { 3 }), "stall failed or blocked poses");
            p.Pump(.26);
            Assert(p.AtServer.Select(n => n.Bytes[0]).SequenceEqual(new byte[] { 3, 1, 2 }), "stalled reliable packets reordered/lost");
            p.Server.Send(p.ServerHandle, TrafficClass.ReliableOrdered, new byte[] { 4 });
            p.Client.Poll();
            Assert(p.AtClient.Count == 0, "incoming reliable stall bypassed");
            p.Pump(.51);
            Assert(p.AtClient.Single()[0] == 4, "incoming reliable stall never drained");
        });
        runner.Run("transport/disconnect preserves kinds, detail and backend codes during polling", () =>
        {
            foreach (var kind in Enum.GetValues<DisconnectKind>())
            {
                using var p = new Pair(new PacketFaults(Lag: 1));
                var reason = new DisconnectReason(kind, "test reason", 4321);
                var seen = new List<DisconnectReason>();
                p.Client.Disconnected += (_, r) => seen.Add(r);
                p.Server.Disconnected += (_, r) => seen.Add(r);
                p.Send(7);
                p.Client.Disconnect(p.ClientHandle, reason);
                Assert(seen.Count == 0, "disconnect callback outside Poll");
                Assert(p.Send(8) == SendResult.Closed, "disconnected handle accepted send");
                p.Pump(2);
                Assert(seen.Count == 2 && seen.All(r => r == reason), "reason/code lost");
                Assert(p.AtServer.Count == 0 && p.Client.Diagnostics(p.ClientHandle).QueuedPackets == 0, "queued data survived disconnect");
            }
        });
        runner.Run("transport/closing in a packet callback cancels the rest and retires diagnostics", () =>
        {
            using var p = new Pair(new PacketFaults(Lag: .1));
            p.Server.Send(p.ServerHandle, TrafficClass.ReliableOrdered, new byte[] { 1 });
            p.Server.Send(p.ServerHandle, TrafficClass.ReliableOrdered, new byte[] { 2 });
            p.Client.Poll();
            p.Client.Packet += (_, _, _) => p.Client.Close();
            TransportDiagnostics? final = null;
            p.Client.Disconnected += (connection, _) => final = p.Client.Diagnostics(connection);
            p.Pump(.2);
            Assert(p.AtClient.Count == 1, "close in callback delivered a later packet");
            p.Pump();
            Assert(final?.PacketsIn == 2 && final?.QueuedPackets == 0, "final diagnostics were missing during callback");
            Assert(p.Client.Diagnostics(p.ClientHandle) == default, "retired diagnostics retained connection state");
        });
        runner.Run("transport/reconnect discards old delayed packets and retires old handles", () =>
        {
            using var p = new Pair(new PacketFaults(Lag: .1));
            p.Send(1);
            TransportDiagnostics? retired = null;
            p.Client.Disconnected += (connection, _) =>
            {
                if (connection == p.ClientHandle) { retired = p.Client.Diagnostics(connection); }
            };
            p.Client.Close();
            Assert(p.Client.Connect(Endpoint, out var fresh).Success && fresh != p.ClientHandle, "reconnect failed/reused handle");
            Assert(p.Client.Send(fresh!, TrafficClass.ReliableOrdered, new byte[] { 2 }) == SendResult.Closed, "connecting send accepted");
            p.Client.Poll(); p.Server.Poll();
            Assert(p.Send(3) == SendResult.Closed, "retired handle revived");
            Assert(retired?.PacketsOut == 1, "reconnect erased diagnostics before the old disconnect callback");
            Assert(p.Client.Send(fresh!, TrafficClass.ReliableOrdered, new byte[] { 4 }) == SendResult.Success, "fresh handle rejected");
            p.Pump(.2);
            Assert(p.AtServer.Select(n => n.Bytes[0]).SequenceEqual(new byte[] { 4 }), "old delayed packet survived reconnect");
        });
        runner.Run("transport/close notification is local and deferred", () =>
        {
            using var p = new Pair();
            DisconnectReason? reason = null;
            p.Client.Disconnected += (_, r) => reason = r;
            p.Client.Close();
            Assert(reason == null && p.Send(1) == SendResult.Closed, "close lifetime wrong");
            p.Client.Poll();
            Assert(reason?.Kind == DisconnectKind.LocalClose, "local close cause missing");
        });
        runner.Run("transport/oversize and backpressure accept no bytes", () =>
        {
            using var p = new Pair(maxPayload: 4, queuePackets: 2, queueBytes: 4);
            Assert(p.Client.Send(p.ClientHandle, TrafficClass.ReliableOrdered, new byte[5]) == SendResult.Oversized, "oversize accepted");
            Assert(p.Client.Send(p.ClientHandle, TrafficClass.ReliableOrdered, new byte[3]) == SendResult.Success, "first send refused");
            Assert(p.Client.Send(p.ClientHandle, TrafficClass.ReliableOrdered, new byte[2]) == SendResult.Backpressure, "byte bound ignored");
            Assert(p.Send(1) == SendResult.Success, "exact capacity refused");
            Assert(p.Send(2) == SendResult.Backpressure, "packet bound ignored");
            Assert(p.Client.Diagnostics(p.ClientHandle).PacketsOut == 2 && p.Client.Diagnostics(p.ClientHandle).BytesOut == 4, "failed sends counted");
            p.Pump();
            Assert(p.Send(2) == SendResult.Success, "pressure did not clear");
        });
        runner.Run("transport/decorator has packet and byte bounds", () =>
        {
            using var p = new Pair(new PacketFaults(Lag: 1), maxPayload: 4, faultPackets: 2, faultBytes: 4);
            Assert(p.Client.Send(p.ClientHandle, TrafficClass.ReliableOrdered, new byte[5]) == SendResult.Oversized, "decorator oversize accepted");
            Assert(p.Client.Send(p.ClientHandle, TrafficClass.ReliableOrdered, new byte[3]) == SendResult.Success, "decorator first send refused");
            Assert(p.Client.Send(p.ClientHandle, TrafficClass.ReliableOrdered, new byte[2]) == SendResult.Backpressure, "decorator byte bound ignored");
            Assert(p.Send(2) == SendResult.Success, "decorator exact capacity refused");
            Assert(p.Send(2) == SendResult.Backpressure, "decorator packet bound ignored");
            p.Pump(1.01);
            Assert(p.Send(3) == SendResult.Success, "decorator pressure never cleared");
            using var small = new Pair(new PacketFaults(), maxPayload: 4, faultBytes: 2);
            Assert(small.Client.MaxPayloadSize == 2 && small.Client.Send(small.ClientHandle, TrafficClass.ReliableOrdered, new byte[3]) == SendResult.Oversized,
                "decorator advertised a packet that can never fit its buffer");
        });
        runner.Run("transport/decorator retries reliable backend pressure without reordering", () =>
        {
            using var p = new Pair(new PacketFaults());
            p.RawClient.BlockSends = true;
            p.Send(1); p.Send(2); p.Send(3, TrafficClass.UnreliableBodyStates);
            p.Pump();
            Assert(p.AtServer.Count == 0 && p.Client.Diagnostics(p.ClientHandle).QueuedPackets == 2 && p.Client.Diagnostics(p.ClientHandle).Drops == 1, "blocked queue policy wrong");
            p.RawClient.BlockSends = false;
            p.Pump();
            Assert(p.AtServer.Select(n => n.Bytes[0]).SequenceEqual(new byte[] { 1, 2 }), "retry reordered or lost reliable traffic");
        });
        runner.Run("transport/receive overflow fails reliable connections, drops unreliable", () =>
        {
            using var p = new Pair(new PacketFaults(Lag: 1), faultPackets: 1);
            p.Server.Send(p.ServerHandle, TrafficClass.UnreliablePlayerPoses, new byte[] { 1 });
            p.Server.Send(p.ServerHandle, TrafficClass.UnreliableBodyStates, new byte[] { 2 });
            p.Client.Poll();
            Assert(p.Client.Diagnostics(p.ClientHandle).Drops == 1, "incoming unreliable overflow not counted");
            DisconnectReason? reason = null;
            p.Client.Disconnected += (_, r) => reason = r;
            p.Server.Send(p.ServerHandle, TrafficClass.ReliableOrdered, new byte[] { 3 });
            p.Client.Poll(); p.Client.Poll();
            Assert(reason?.Kind == DisconnectKind.TransportFailure, "reliable overflow silently dropped data");
        });
        runner.Run("transport/receive memory is borrowed and delayed packets own copies", () =>
        {
            using var p = new Pair(new PacketFaults(Lag: .2));
            var bytes = new byte[] { 7 };
            p.Client.Send(p.ClientHandle, TrafficClass.ReliableOrdered, bytes);
            bytes[0] = 9;
            p.Pump(.21);
            Assert(p.AtServer.Single().Bytes[0] == 7, "send did not copy caller bytes");
            ReadOnlyMemory<byte> borrowed = default;
            p.RawClient.Packet += (_, _, data) => borrowed = data;
            p.Server.Send(p.ServerHandle, TrafficClass.ReliableOrdered, new byte[] { 11 });
            p.Client.Poll();
            Assert(borrowed.Span[0] == 0xdd && p.AtClient.Count == 0, "test did not invalidate borrowed memory");
            p.Pump(.21);
            Assert(p.AtClient.Single()[0] == 11, "decorator retained borrowed memory");
        });
        RegisterClientTests(runner);
    }

    private static void RegisterClientTests(TestRunner runner)
    {
        runner.Run("transport/client services catch-up physics without a process frame", () =>
        {
            var network = new LoopbackNetwork();
            var clock = new Clock();
            var rawServer = network.Create();
            var rawClient = network.Create();
            var server = new LobbyServer(new ServerTransport(rawServer, rawServer.PeerIds), clock) { Port = Endpoint.Port, Worlds = false };
            var client = new NetClient(rawClient, clock);
            try
            {
                server.Start(); client._Ready(); client.Connect("", Endpoint.Port, "Physics", 0);
                for (var step = 0; step < 6; step++) { client._PhysicsProcess(1.0 / 120); server._Process(0); }
                Assert(client.State == NetClient.Status.Connected, "physics steps did not service the handshake");
                client.CreateLobby();
                for (var step = 0; step < 4; step++) { client._PhysicsProcess(1.0 / 120); server._Process(0); }
                Assert(client.Lobby != null, "physics steps did not deliver queued reliable control");
                var before = client.Diagnostics.PacketsOut;
                clock.Seconds += 1.1;
                for (var step = 0; step < 8; step++) { client._PhysicsProcess(1.0 / 120); client._Process(0); }
                Assert(client.Diagnostics.PacketsOut == before + 1, "catch-up and process polls multiplied the ping rate");
            }
            finally { client._ExitTree(); server._ExitTree(); client.Free(); server.Free(); }
        });
        runner.Run("transport/loading grace ends on readiness even after the loading deadline", () =>
        {
            var network = new LoopbackNetwork();
            var clock = new Clock();
            var rawServer = network.Create();
            var rawClient = network.Create();
            var serverTransport = new FaultPacketTransport(rawServer, clock, 1, new PacketFaults());
            var clientTransport = new FaultPacketTransport(rawClient, clock, 2, new PacketFaults());
            var server = new LobbyServer(new ServerTransport(serverTransport, rawServer.PeerIds), clock) { Port = Endpoint.Port, Worlds = false };
            var client = new NetClient(clientTransport, clock);
            try
            {
                server.Start(); client._Ready(); client.Connect("", Endpoint.Port, "Builder", 0);
                void Pump() { client._Process(0); server._Process(0); }
                for (var i = 0; i < 6; i++) { Pump(); }
                client.CreateLobby();
                for (var i = 0; i < 4; i++) { Pump(); }
                MatchSetup? setup = null;
                client.MatchStarting += value => setup = value;
                client.StartMatch();
                for (var i = 0; i < 4; i++) { Pump(); }
                Assert(setup != null && rawServer.Loading.Values.Single() && rawClient.Loading.Values.Single(),
                    "construction did not extend both native endpoints through the decorator");
                var events = new List<Msg>();
                client.AttachMatch(setup!.Id, message => events.Add(message.Type));
                clock.Seconds += 25;
                for (var i = 0; i < 4; i++) { Pump(); }
                clock.Seconds += 5;
                for (var i = 0; i < 4; i++) { Pump(); }
                Assert(events.Contains(Msg.MatchGo) && rawServer.Loading.Values.Single(),
                    "countdown revoked a late builder's grace before readiness");
                client.Send(new NetWriter(Msg.Loaded, setup!.Id));
                for (var i = 0; i < 4; i++) { Pump(); }
                Assert(!rawServer.Loading.Values.Single() && !rawClient.Loading.Values.Single(),
                    "a late Loaded left the live match with construction disconnect latency");
                // Leaving releases an unfinished loading grace without Loaded.
                clientTransport.SetLoading(rawClient.Loading.Keys.Single(), true);
                serverTransport.SetLoading(rawServer.Loading.Keys.Single(), true);
                client.LeaveLobby();
                for (var i = 0; i < 4; i++) { Pump(); }
                Assert(!rawServer.Loading.Values.Single() && !rawClient.Loading.Values.Single(),
                    "leaving construction retained a long timeout");
            }
            finally { client._ExitTree(); server._ExitTree(); client.Free(); server.Free(); }
        });
        runner.Run("transport/native loading grace outlasts readiness by the time spent loading", () =>
        {
            var grace = new LoadingGrace();
            var slow = new ConnectionHandle();
            var fast = new ConnectionHandle();
            grace.Begin(slow, 100);
            grace.Begin(fast, 100);
            grace.Begin(slow, 104); // A repeated start keeps the first: retries back off from it.
            Assert(!grace.End(fast, 102) && !grace.End(slow, 111), "readiness released a grace before ENet could retry");
            Assert(grace.Expire(104.9).Count == 0 && grace.Expire(105).Single() == fast,
                "a 2 s load did not keep its grace for 2 s plus the retry margin");
            Assert(!grace.End(slow, 112) && grace.Expire(122.9).Count == 0, "a repeated Loaded moved or released the hold");
            Assert(grace.Expire(123).Single() == slow && grace.Expire(200).Count == 0,
                "an 11 s load did not release exactly once after 11 s plus the retry margin");
            Assert(grace.End(slow, 200), "a connection that is not loading kept a long timeout");
            // A new MatchStart during the hold cancels the release; disconnects forget the connection.
            grace.Begin(fast, 300);
            Assert(!grace.End(fast, 301), "readiness after a short load released at once");
            grace.Begin(fast, 301.5);
            Assert(grace.Expire(400).Count == 0, "a new load kept the previous load's release");
            grace.Forget(fast);
            Assert(grace.End(fast, 400) && grace.Expire(1000).Count == 0, "a retired connection kept a grace");
        });
        runner.Run("transport/client and server handshake, lobby, disconnect and reconnect", () =>
        {
            var network = new LoopbackNetwork();
            var clock = new Clock();
            var rawServer = network.Create();
            var rawClient = network.Create();
            var server = new LobbyServer(new ServerTransport(rawServer, rawServer.PeerIds), clock) { Port = Endpoint.Port, Worlds = false };
            var client = new NetClient(rawClient, clock);
            try
            {
                Assert(server.Start().Success, "server could not start");
                client._Ready();
                client.Connect("", Endpoint.Port, "Tester", 0);
                void Pump() { client._Process(0); server._Process(0); }
                for (var i = 0; i < 5; i++) { Pump(); }
                Assert(client.State == NetClient.Status.Connected && client.MyId == 2, "Hello/Welcome failed");
                client.CreateLobby();
                for (var i = 0; i < 3; i++) { Pump(); }
                Assert(client.Lobby?.Members.Single().Name == "Tester" && client.IsCreator, "lobby flow failed");
                var old = rawServer.PeerIds.Connection(2)!;
                string? ui = null;
                client.StatusChanged += (_, detail) => ui = detail;
                var reason = new DisconnectReason(DisconnectKind.Timeout, "Timed out during test.", 9001);
                rawServer.Disconnect(old, reason);
                Pump();
                Assert(client.State == NetClient.Status.Offline && client.LastDisconnect == reason && ui == reason.Detail, "disconnect reason did not reach UI");
                client.Connect("", Endpoint.Port, "Again", 1);
                // Reconnect before the old local-close notification is polled.
                for (var i = 0; i < 5; i++) { Pump(); }
                Assert(client.State == NetClient.Status.Connected && client.MyId == 3 && rawServer.PeerIds.Connection(3) != old, "reconnect reused handle or stale close dropped new connection");
                Assert(client.Lobbies.Count == 0 && client.Lobby == null, "old lobby survived reconnect");
                MatchSetup? setup = null;
                client.MatchStarting += value => setup = value;
                client.CreateLobby();
                for (var i = 0; i < 3; i++) { Pump(); }
                client.StartMatch();
                for (var i = 0; i < 3; i++) { Pump(); }
                Assert(setup != null && PlanCodec.Decode(setup.Plan).Tanks.Count > 0, "retained setup lost its borrowed plan memory");
                client.Send(new NetWriter(Msg.Loaded, setup!.Id));
                for (var i = 0; i < 3; i++) { Pump(); }
                clock.Seconds += 10;
                for (var i = 0; i < 3; i++) { Pump(); }
                var events = new List<Msg>();
                Assert(client.AttachMatch(setup.Id, message => events.Add(message.Type)) && events.Contains(Msg.MatchGo), "loopback match inbox lost queued Go");
                client.Disconnect(); Pump();
                Assert(client.LastDisconnect?.Kind == DisconnectKind.LocalClose, "client local close cause missing");
            }
            finally { client._ExitTree(); server._ExitTree(); client.Free(); server.Free(); }
        });
        runner.Run("transport/client shows application rejection and retains server version", () =>
        {
            using var p = new Pair();
            var client = new NetClient(p.Client, p.Clock);
            try
            {
                client._Ready();
                client.Connect("", Endpoint.Port, "Tester", 0);
                p.Server.Poll(); client._Process(0); p.Server.Poll();
                var handle = p.Server.PeerIds.Connection(3)!;
                p.Server.Send(handle, TrafficClass.ReliableOrdered, new NetWriter(Msg.Rejected).String("That lobby is full.").String(BuildInfo.GameVersion).ToArray());
                string? ui = null;
                client.StatusChanged += (_, detail) => ui = detail;
                client._Process(0);
                Assert(client.LastDisconnect?.Kind == DisconnectKind.ApplicationRejected && ui == "That lobby is full." && client.RejectedBy == BuildInfo.GameVersion, "application rejection lost");
            }
            finally { client._ExitTree(); client.Free(); }
        });
        runner.Run("transport/client teardown detaches polling callbacks", () =>
        {
            using var p = new Pair();
            var client = new NetClient(p.Client, p.Clock);
            try
            {
                client._Ready(); client.Connect("", Endpoint.Port, "Tester", 0);
                client._Process(0); p.Server.Poll();
                var statuses = 0;
                client.StatusChanged += (_, _) => statuses++;
                client._ExitTree();
                p.Client.Poll(); p.Server.Poll();
                Assert(statuses == 0, "teardown retained callbacks into the retired node");
            }
            finally { client._ExitTree(); client.Free(); }
        });
        runner.Run("transport/reliable handshake stall hits deterministic client timeout", () =>
        {
            var network = new LoopbackNetwork();
            var clock = new Clock();
            var rawServer = network.Create();
            var rawClient = network.Create();
            var transport = new FaultPacketTransport(rawClient, clock, 123, new PacketFaults(ReliableStall: 10));
            var server = new LobbyServer(new ServerTransport(rawServer, rawServer.PeerIds), clock) { Port = Endpoint.Port, Worlds = false };
            var client = new NetClient(transport, clock);
            try
            {
                server.Start(); client._Ready(); client.Connect("", Endpoint.Port, "Tester", 0);
                client._Process(0); server._Process(0);
                clock.Seconds += 8.1;
                client._Process(0); server._Process(0);
                Assert(client.State == NetClient.Status.Offline && client.LastDisconnect?.Kind == DisconnectKind.Timeout, "reliable stall failed to exercise timeout");
                clock.Seconds += 30; client._Process(0); server._Process(0);
                Assert(client.State == NetClient.Status.Offline, "stale Hello/Welcome resurrected timed-out connection");
            }
            finally { client._ExitTree(); server._ExitTree(); client.Free(); server.Free(); }
        });
    }
}
