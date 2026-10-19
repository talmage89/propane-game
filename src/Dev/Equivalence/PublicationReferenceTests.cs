using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using Propane.Core;
using Propane.Dev.UnitTests;
using Propane.Dev.UnitTests.Fixtures;
using Propane.Modes;
using Propane.Net;
using Propane.Tank;
using Propane.World;

namespace Propane.Dev.Equivalence;

/// <summary>Calls the real Match collector/serializer/send path without advancing native physics or network time.</summary>
[UnitSuite]
public static class PublicationReferenceTests
{
    private sealed class RecordingTransport(LoopbackTransport inner) : IPacketTransport
    {
        public readonly List<object> Sent = new();
        public readonly List<int> BatchSizes = new();
        public string Step = "setup";
        public int MaxPayloadSize => inner.MaxPayloadSize;
        public event Action<ConnectionHandle>? Connected { add => inner.Connected += value; remove => inner.Connected -= value; }
        public event Action<ConnectionHandle, DisconnectReason>? Disconnected { add => inner.Disconnected += value; remove => inner.Disconnected -= value; }
        public event Action<ConnectionHandle, TrafficClass, ReadOnlyMemory<byte>>? Packet { add => inner.Packet += value; remove => inner.Packet -= value; }
        public OpenResult Listen(PacketEndpoint endpoint, int maxConnections) => inner.Listen(endpoint, maxConnections);
        public OpenResult Connect(PacketEndpoint endpoint, out ConnectionHandle? connection) => inner.Connect(endpoint, out connection);
        public void Poll() => inner.Poll();
        public SendResult Send(ConnectionHandle connection, TrafficClass traffic, ReadOnlyMemory<byte> bytes)
        {
            var result = inner.Send(connection, traffic, bytes);
            if (bytes.Length > 0 && (Msg)bytes.Span[0] == Msg.BodyStates)
                BatchSizes.Add(((BodyStatesMessage)MessageCodec.Decode(bytes.ToArray(), false).Payload).Bodies.Count);
            if (bytes.Length > 0 && (Msg)bytes.Span[0] is Msg.BodyStates or Msg.BodyRest)
                Sent.Add(new { Step, traffic = traffic.ToString(), result = result.ToString(), bytes = Convert.ToBase64String(bytes.Span) });
            return result;
        }
        public void Disconnect(ConnectionHandle connection, DisconnectReason reason) => inner.Disconnect(connection, reason);
        public void Close() => inner.Close();
        public TransportDiagnostics Diagnostics(ConnectionHandle connection) => inner.Diagnostics(connection);
    }

    public static void Register(TestRunner runner)
    {
        runner.Run("reference/publication mixed batches, gathers, sleep/wake, pending drops, epochs, checkpoints and failed sends", () =>
        {
            using var fixture = new ServerFixture(bots: 1);
            var network = new LoopbackNetwork();
            var rawServer = network.Create(); var rawClient = network.Create();
            var wire = new RecordingTransport(rawClient);
            var client = new NetClient(wire, ModeRegistry.Codec, fixture.Clock) { Template = fixture.Tuning };
            var endpoint = new PacketEndpoint("", 1234);
            TestRunner.Assert(rawServer.Listen(endpoint, 4).Success, "publication listen");
            client._Ready(); client.Connect("", endpoint.Port, "Reference", 0);
            client._Process(0); rawServer.Poll();
            client.Handle(MessageCodec.Encode(new NetMessage(Msg.Welcome, 0, 0, new WelcomeMessage(2, "0.4.0")), true));
            var match = new Match { Net = client, Setup = fixture.Setup with { Plan = PlanCodec.Encode(SuburbPlans.Generate(SuburbPlans.Match(fixture.Tuning, 3), 23)) }, Clock = fixture.Clock };
            runner.AddChild(match);
            try
            {
                var tank = match.LiveTanks.First();
                var sync = match.Bodies;
                sync.Clear();
                var count = Protocol.MaxBodyStates * 3 + 5;
                for (var i = 0; i < count; i++)
                {
                    RigidBody3D body;
                    if (i == 0)
                    {
                        body = tank;
                    }
                    else { body = new RigidBody3D(); match.AddChild(body); }
                    body.GlobalTransform = new Transform3D(Basis.Identity, new Vector3(i, i % 3, -i));
                    body.LinearVelocity = new Vector3(i + 1, 2, 3);
                    body.AngularVelocity = new Vector3(0, i % 4, 0);
                    body.Sleeping = false;
                    var isTank = i % 2 == 0 || i >= count - 2;
                    var id = i >= count - 2 ? BodyIds.DropTankId(i == count - 1 ? 0 : 1, 0) : isTank ? BodyIds.MapTankId(i) : BodyIds.CarId(i);
                    sync.Register(id, body, isTank, 2);
                    // Three groups, interleaved in encounter order; each must preserve its own member order.
                    sync.Get(id)!.CheckpointTraffic = i % 3 == 0 ? TrafficClass.ReliableBodyStates3 :
                        i % 3 == 1 ? TrafficClass.ReliableBodyStates : TrafficClass.ReliableBodyStates2;
                }
                var entries = sync.Entries.ToArray();
                var drop = entries[^1];
                ClientActorAccess.For(match.Player).DropPredictions.Add(drop.Id);
                var bot = match.Session.OwnedActors.Single(a => a.Character != match.Player).Character;
                var botDrop = entries[^2];
                ClientActorAccess.For(bot).DropPredictions.Add(botDrop.Id);
                var publish = typeof(Match).GetMethod("SendBodies", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var steps = new List<object>();
                void Step(string name, double time)
                {
                    wire.Step = name; fixture.Clock.Seconds = time;
                    publish.Invoke(match, new object[] { time });
                    rawServer.Poll();
                    steps.Add(new
                    {
                        name,
                        time = ReferenceFile.Value(time),
                        client = client.State.ToString(),
                        failure = client.LastDisconnect?.Kind.ToString(),
                        bodies = entries.Select(e => new
                        {
                            e.Id,
                            e.Owner,
                            e.Epoch,
                            e.Sequence,
                            e.PublishRequired,
                            e.PublishReliably,
                            e.WasAwake,
                            e.ClaimPending,
                            lane = e.CheckpointTraffic.ToString(),
                            lastReliable = ReferenceFile.Value(e.LastReliablePublication)
                        }).ToArray()
                    });
                }
                Step("first-checkpoints-pending-drop", 100);
                Step("same-time-no-publication", 100);
                client.Handle(MessageCodec.Encode(new NetMessage(Msg.Pong, 0, 0, new PongMessage(99.75, 100.5)), true));
                Step("rate-motion", 100.1);
                foreach (var entry in entries.Where((_, i) => i % 3 == 0)) entry.Body.Sleeping = true;
                tank.BeginGather(); tank.Sleeping = true;
                Step("gather-while-sleeping-and-final-rests", 100.2);
                tank.EndGather(); tank.Sleeping = true;
                Step("gather-final-rest", 100.21);
                ClientActorAccess.For(match.Player).DropPredictions.Accepted(new[] { drop.Id });
                drop.Body.Sleeping = true;
                Step("confirmed-drop-first-rest", 100.22);
                TestRunner.Assert(botDrop.Sequence == 0 && drop.Sequence == 1, "confirmation released another actor's pending drop");
                ClientActorAccess.For(bot).DropPredictions.Accepted(new[] { botDrop.Id });
                Step("confirmed-bot-drop-first-motion", 100.23);
                foreach (var entry in entries) entry.Body.Sleeping = false;
                Step("wake-and-reliable-checkpoints", 100.6);
                var transferred = entries[1];
                sync.OnAuthority(new BodyAuthority(BodySync.Capture(transferred) with { Epoch = 2, Sequence = 7 }, 3,
                    BodyState.State.Intact, false, null, Vector3.Zero), 100.61);
                sync.Claim(new[] { transferred.Body });
                Step("foreign-owner-pending-claim", 100.7);
                sync.OnAuthority(new BodyAuthority(BodySync.Capture(transferred) with { Epoch = 3, Sequence = 0 }, 2,
                    BodyState.State.Intact, false, null, Vector3.Zero), 100.71);
                Step("epoch-granted-sequence-reset", 100.71);
                rawClient.BlockTraffic = TrafficClass.UnreliableBodyStates;
                Step("disposable-send-failure", 100.8);
                rawClient.BlockSends = true;
                Step("required-send-failure", 101.2);
                TestRunner.Assert(wire.BatchSizes.Contains(Protocol.MaxBodyStates) && wire.BatchSizes.Any(n => n < Protocol.MaxBodyStates),
                    "publication fixture did not cross full and partial batch boundaries");
                TestRunner.Assert(wire.Sent.Count > 10 && steps.Count == 12, "publication transcript did not exercise its steps");
                ReferenceFile.Check("publication", new { packets = wire.Sent, steps });
            }
            finally { match.World.Dispose(); match.Free(); client._ExitTree(); client.Free(); rawServer.Close(); }
        });
    }
}
