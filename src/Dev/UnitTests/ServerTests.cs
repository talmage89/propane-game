using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Dev.UnitTests.Fixtures;
using Propane.Modes;
using Propane.Net;
using Propane.Tank;

namespace Propane.Dev.UnitTests;

/// <summary>Exercise the production receive handler and clock with an in-memory packet sink, no sockets.</summary>
[UnitSuite]
public static class ServerTests
{
    public static void Register(TestRunner runner)
    {
        runner.Run("server/moving checkpoints retain reliable delivery on the second leg", () =>
        {
            using var f = new ServerFixture();
            var traffic = new List<TrafficClass>();
            f.Server.SendOverride = (_, _, delivery) => traffic.Add(delivery);
            byte[] Packet(uint sequence) => MessageCodec.Encode(new NetMessage(Msg.BodyStates, f.Id, 0,
                new BodyStatesMessage(0, new[] { new BodyState(BodyIds.MapTankId(0), Transform3D.Identity,
                    Vector3.Right, Vector3.Zero, 1, sequence) })), false);
            f.Server.OnReceived(2, Packet(1), TrafficClass.ReliableOrdered);
            TestRunner.Assert(traffic.Single() == TrafficClass.ReliableOrdered, "server discarded moving checkpoint reliability");
            traffic.Clear();
            f.Server.OnReceived(2, Packet(2), TrafficClass.ReliableBodyStates);
            TestRunner.Assert(traffic.Single() == TrafficClass.ReliableBodyStates, "server merged checkpoint and control lanes");
            foreach (var lane in new[] { TrafficClass.ReliableBodyStates2, TrafficClass.ReliableBodyStates3 })
            {
                traffic.Clear();
                f.Server.OnReceived(2, Packet(lane == TrafficClass.ReliableBodyStates2 ? 3u : 4u), lane);
                TestRunner.Assert(traffic.Single() == lane, "server merged an alternate checkpoint lane");
            }
            traffic.Clear();
            f.Server.OnReceived(2, Packet(5), TrafficClass.UnreliableBodyStates);
            TestRunner.Assert(traffic.Single() == TrafficClass.UnreliableBodyStates, "server made ordinary snapshots reliable");
        });
        runner.Run("server/loading relays the owner's final rest to clients still building", () =>
        {
            using var f = new ServerFixture(playing: false);
            var id = BodyIds.MapTankId(0);
            var pose = new Transform3D(Basis.Identity, new Vector3(1, .3f, 2));
            f.Receive(new NetWriter(Msg.BodyRest, f.Id).UInt(id).Transform(pose).Double(1).UInt(1).UInt(1));
            TestRunner.Assert(f.Packets.Single() is { Peer: 3, Message.Payload: BodyRestMessage rest } && rest.Transform == pose,
                "loading discarded the owner's only final rest");
            f.Packets.Clear();
            f.Receive(MessageCodec.Encode(new NetMessage(Msg.BodyStates, f.Id, 0,
                new BodyStatesMessage(0, new[] { new BodyState(id, pose, Vector3.Right, Vector3.Zero, 1, 2) })), false));
            TestRunner.Assert(f.Packets.Single().Message.Payload is BodyStatesMessage, "loading discarded owner movement");
            f.Reject(new NetWriter(Msg.BodyRest, f.Id).UInt(id).Transform(Transform3D.Identity).Double(2).UInt(1).UInt(3).ToArray(), peer: 3);
            f.Reject(new NetWriter(Msg.BodyRest, f.Id).UInt(id).Transform(Transform3D.Identity).Double(2).UInt(1).UInt(1).ToArray());
        });
        runner.Run("server/truncated messages never relay or mutate", () =>
        {
            using var f = new ServerFixture();
            foreach (var fixture in ProtocolTests.Fixtures().Where(p => p.Type is <= Msg.RemoveActor || Protocol.IsRelay(p.Type)))
            {
                var bytes = MessageCodec.Encode(fixture with { MatchId = Protocol.IsMatch(fixture.Type) ? f.Id : 0 }, false);
                f.Reject(bytes[..^1]);
            }
            // A truncated explosion must not have consumed the id or changed the bank.
            f.Receive(Explosion(f, BodyIds.MapTankId(0)));
            f.AssertScore(2, 5, 1);
        });
        runner.Run("server/unknown and duplicate explosions cannot score", () =>
        {
            using var f = new ServerFixture();
            f.Reject(Explosion(f, 0x12345678).ToArray());
            f.Reject(f.ActorWriter(Msg.Explode).UInt(BodyIds.MapTankId(0)).Vec3(new Vector3(float.NaN, 0, 0)).ToArray());
            f.Receive(Explosion(f, BodyIds.MapTankId(0)));
            f.AssertScore(2, 5, 1);
            f.Reject(Explosion(f, BodyIds.MapTankId(0), peer: 3).ToArray(), peer: 3);
        });
        runner.Run("server/drop batches are atomic and registered before scoring", () =>
        {
            using var f = new ServerFixture();
            var first = DropBody(0, 0);
            var second = DropBody(0, 1);
            f.Reject(f.Drop(new[] { first, second with { Id = BodyIds.DropTankId(1, 0) } }));
            f.Reject(Explosion(f, first.Id).ToArray());
            f.Reject(f.Drop(new[] { first, first }));
            f.Receive(f.Drop(new[] { first, second }));
            f.AssertScore(2, 2, -2);
            TestRunner.Assert(f.Packets.Any(p => ModeWireFixtures.AcceptedSpill(p.Message) != null), "drop was not relayed");
            f.Receive(Explosion(f, first.Id));
            f.AssertScore(2, 3, 1);
            f.Reject(f.Drop(new[] { first }));
            f.Reject(Explosion(f, first.Id).ToArray());
        });
        runner.Run("server/drop counts cannot raise scores or exceed bank and tuner", () =>
        {
            using var f = new ServerFixture();
            f.Reject(f.RawSpill(0, 0));
            f.Reject(f.RawSpill(0, ModeSchemas.MaxSpill + 1));
            f.Reject(f.Drop(Enumerable.Range(0, 5).Select(i => DropBody(0, i)).ToArray()));
            f.Reject(f.Drop(new[] { DropBody(0, 0), DropBody(0, 1) }, reason: 0));
            f.Receive(f.Drop(new[] { DropBody(0, 0) }));
            f.AssertScore(2, 3, -1);
        });
        runner.Run("server/expired playing clock refuses drops before the phase tick", () =>
        {
            using var f = new ServerFixture(playing: false);
            f.Receive(new NetWriter(Msg.Loaded, f.Id));
            f.Receive(new NetWriter(Msg.Loaded, f.Id), peer: 3);
            f.Advance(); // Loading -> Countdown.
            f.Advance(); // Countdown -> Playing.
            f.Clock.Seconds += 20; // Expire the deadline without running the phase tick.
            f.Packets.Clear();
            // Production polls incoming packets before advancing phases on this process frame.
            f.Receive(f.Drop(new[] { DropBody(0, 0) }));
            TestRunner.Assert(f.Packets.Single() is { Peer: 2 } only && ModeWireFixtures.Refused(only.Message) is { } reply &&
                reply.SequenceEqual(new[] { DropBody(0, 0).Id }), "expired clock accepted a predicted drop");
            f.Packets.Clear();
            f.Advance();
            TestRunner.Assert(f.Packets.Select(p => p.Message.Payload).OfType<IReadOnlyList<Standing>>().First().All(s => s.Score == 4),
                "late drop changed final banks before the phase tick");
        });
        runner.Run("server/gather uses the injected clock at the play deadline", () =>
        {
            using var f = new ServerFixture();
            var body = DropBody(0, 0) with { Id = BodyIds.MapTankId(0), Sequence = 1 };
            byte[] Packet(uint sequence) => MessageCodec.Encode(new NetMessage(Msg.Gather, f.Id, 0,
                new GatherMessage(GatherPhase.Glide, new[] { body with { Sequence = sequence } })), false);
            f.Clock.Seconds += 9;
            f.Receive(Packet(1));
            TestRunner.Assert(f.Packets.Single().Message.Payload is GatherMessage, "gather refused before the deadline");
            f.Clock.Seconds += 1;
            f.Reject(Packet(2)); // Exactly at the deadline, before the phase tick.
        });
        runner.Run("server/body and shot ids must exist", () =>
        {
            using var f = new ServerFixture();
            f.Reject(MessageCodec.Encode(new NetMessage(Msg.Claim, f.Id, 0, new ClaimMessage(1,
                new[] { DropBody(0, 0) with { Id = BodyIds.MapTankId(0) }, DropBody(0, 1) with { Id = 999 } })), false));
            f.Reject(f.ActorWriter(Msg.Puncture).UInt(999).Transform(Transform3D.Identity).Vec3(Vector3.Zero)
                .Vec3(Vector3.Zero).Vec3(Vector3.One).Vec3(Vector3.Up).Float(1).UInt(1).UInt(1).UInt(0).ToArray());
            f.Reject(new NetWriter(Msg.BodyRest, f.Id).UInt(999).Transform(Transform3D.Identity).Double(1).UInt(1).UInt(1).ToArray());
            f.Reject(MessageCodec.Encode(new NetMessage(Msg.BodyStates, f.Id, 0, new BodyStatesMessage(1,
                new[] { DropBody(0, 0) with { Id = BodyIds.MapTankId(0), Sequence = 1 }, DropBody(0, 1) with { Id = 999, Sequence = 1 } })), false));
            f.Reject(f.ActorWriter(Msg.Shot).Vec3(Vector3.One).Vec3(Vector3.Up).Vec3(Vector3.Forward).Byte(1).Int(999).Int(-1).UInt(1).ToArray());
            f.Reject(f.ActorWriter(Msg.Shot).Vec3(Vector3.One).Vec3(Vector3.Up).Vec3(Vector3.Forward).Byte(1).Int(0).Int(int.MaxValue).UInt(1).ToArray());
        });
        runner.Run("server/motion bounds future sample timestamps to the relay clock", () =>
        {
            using var f = new ServerFixture();
            f.Receive(MessageCodec.Encode(new NetMessage(Msg.BodyStates, f.Id, 0,
                new BodyStatesMessage(100000, new[] { DropBody(0, 0) with { Id = BodyIds.MapTankId(0), Sequence = 1 } })), false));
            TestRunner.Assert(f.Packets.Single().Message.Payload is BodyStatesMessage relayed &&
                relayed.Time == f.Clock.Seconds, "relay accepted a future sample time");
        });
        runner.Run("server/motion preserves bounded sample age across both transport legs", () =>
        {
            using var f = new ServerFixture();
            var sampled = f.Clock.Seconds - 0.12;
            var body = DropBody(0, 0) with { Id = BodyIds.MapTankId(0), Sequence = 1, Linear = Vector3.Right * 200 };
            f.Receive(MessageCodec.Encode(new NetMessage(Msg.BodyStates, f.Id, 0,
                new BodyStatesMessage(sampled, new[] { body })), false));
            var relayed = (BodyStatesMessage)f.Packets.Single().Message.Payload;
            TestRunner.Assert(relayed.Time == sampled, "relay erased the owner's sampling age");
            var moving = new RigidBody3D();
            runner.AddChild(moving);
            try
            {
                var sync = new BodySync(Tuning.SavedTemplate, 4, 2);
                sync.Register(body.Id, moving, true, 2);
                var received = sampled + 0.24;
                sync.OnState(2, relayed.Bodies.Single(), false, received, received - relayed.Time);
                sync.Correct(received);
                TestRunner.Assert(moving.GlobalPosition.IsEqualApprox(body.Transform.Origin + body.Linear * 0.24f),
                    "fast vent stayed behind by the sender-to-server leg");
            }
            finally { moving.Free(); }
            f.Packets.Clear();
            f.Receive(MessageCodec.Encode(new NetMessage(Msg.BodyStates, f.Id, 0,
                new BodyStatesMessage(0, new[] { body with { Sequence = 2 } })), false));
            TestRunner.Assert(f.Packets.Single().Message.Payload is BodyStatesMessage unsynced && unsynced.Time == f.Clock.Seconds,
                "missing clock synchronization used a stale sampling age");
            f.Packets.Clear();
            f.Receive(MessageCodec.Encode(new NetMessage(Msg.BodyStates, f.Id, 0,
                new BodyStatesMessage(1, new[] { body with { Sequence = 3 } })), false));
            TestRunner.Assert(f.Packets.Single().Message.Payload is BodyStatesMessage ancient &&
                ancient.Time == f.Clock.Seconds - 0.3, "owner supplied an unbounded sample age");
        });
        runner.Run("server/drop registration recovers an early snapshot and preserves the final rest", () =>
        {
            using var f = new ServerFixture();
            var dropped = DropBody(0, 0);
            var state = MessageCodec.Encode(new NetMessage(Msg.BodyStates, f.Id, 0,
                new BodyStatesMessage(1, new[] { DropBody(0, 1) with { Id = BodyIds.MapTankId(0), Sequence = 1 }, dropped with { Sequence = 1 } })), false);
            f.Reject(state); // Unreliable can overtake Drop: refuse the whole batch, without changing the ledger.
            f.Receive(f.Drop(new[] { dropped }));
            f.Packets.Clear();
            f.Receive(state);
            TestRunner.Assert(f.Packets.Single().Message.Payload is BodyStatesMessage, "later snapshot was permanently refused");
            f.Packets.Clear();
            f.Receive(new NetWriter(Msg.BodyRest, f.Id).UInt(dropped.Id).Transform(dropped.Transform).Double(2).UInt(1).UInt(2));
            TestRunner.Assert(f.Packets.Single() is { Peer: 3, Message.Payload: BodyRestMessage }, "reliable final rest was lost");
            f.Receive(Explosion(f, dropped.Id));
            f.Packets.Clear();
            state = MessageCodec.Encode(new NetMessage(Msg.BodyStates, f.Id, 0,
                new BodyStatesMessage(2, new[] { DropBody(0, 1) with { Id = BodyIds.MapTankId(0), Sequence = 2 }, dropped with { Sequence = 3 } })), false);
            f.Receive(state); // A late tombstone in a mixed batch must not starve the remaining moving bodies.
            TestRunner.Assert(f.Packets.Single().Message.Payload is BodyStatesMessage, "exploded id poisoned a later batch");
        });
        runner.Run("server/creator departure preserves slots, banks and the new host", () =>
        {
            using var f = new ServerFixture();
            f.Receive(f.ActorPacket(Msg.Puncture, new PunctureMessage(BodyIds.MapTankId(0),
                Transform3D.Identity, Vector3.Right, Vector3.Up, Vector3.One, Vector3.Up, -1)));
            f.Packets.Clear();
            f.Server.OnDisconnected(2);
            TestRunner.Assert(f.Packets.Any(p => p.Peer == 3 && p.Message.Payload is AuthorityMessage { Request: 0 } transfer &&
                transfer.Bodies.Any(b => b.Body.Id == BodyIds.MapTankId(0) && b.Owner == 3 && b.Body.Epoch == 3 && b.Body.Sequence == 0 &&
                    b.State == BodyState.State.Venting && b.Vent == new VentParameters(Vector3.One, Vector3.Up, -1))), "mid-vent departure did not grant canonical authority with a fresh epoch");
            TestRunner.Assert(f.Packets.Any(p => p.Peer == 3 && p.Message.Payload is PlayerLeftMessage { Player: 2, Host: 3 }),
                "creator departure did not identify the successor");
            f.Packets.Clear();
            f.Receive(f.Drop(new[] { DropBody(1, 0) }, peer: 3), peer: 3);
            f.AssertScore(3, 3, -1);
            f.Advance(20);
            var standings = f.Packets.Select(p => p.Message.Payload).OfType<IReadOnlyList<Standing>>().Single();
            TestRunner.Assert(standings.Any(s => s is { Id: 1, Score: 4, Present: false }) &&
                standings.Any(s => s is { Id: 2, Score: 3, Present: true }), "departure erased a bank or renumbered a spawn slot");
        });
        runner.Run("server/a drop refused after a tuning change gets an explicit reply", () =>
        {
            using var f = new ServerFixture();
            f.Receive(new NetWriter(Msg.TuningSet, f.Id).String(nameof(Tuning.RagdollDropCount)).Scalar(1));
            f.Packets.Clear();
            // This batch fit the former count of five when the victim sent it. The creator's tuning arrived first.
            f.Receive(f.Drop(new[] { DropBody(0, 0), DropBody(0, 1) }));
            TestRunner.Assert(f.Packets.Count == 1 && f.Packets[0] is { Peer: 2 } only && ModeWireFixtures.Refused(only.Message) is { } reply &&
                reply.SequenceEqual(new[] { DropBody(0, 0).Id, DropBody(0, 1).Id }),
                "refused prediction had no reply and could leave phantom tanks");
            f.Packets.Clear();
            f.Receive(f.Drop(new[] { DropBody(0, 2) }));
            f.AssertScore(2, 3, -1);
            TestRunner.Assert(f.Packets.Any(p => p.Peer == 2 && ModeWireFixtures.AcceptedSpill(p.Message) != null),
                "accepted prediction did not confirm its ids to the sender");
        });
        runner.Run("server/tuning requires original debug creator", () =>
        {
            using var f = new ServerFixture();
            var tune = new NetWriter(Msg.TuningSet, f.Id).String(nameof(Tuning.BlastRadius)).Scalar(5f);
            f.Reject(tune.ToArray(), peer: 3);
            f.Reject(new NetWriter(Msg.TuningSet, f.Id).String(nameof(Tuning.BlastRadius)).Scalar(float.NaN).ToArray());
            f.Receive(tune);
            TestRunner.Assert(f.Packets.Any(p => p.Message.Payload is TuningMessage { Name: nameof(Tuning.BlastRadius) }), "creator's valid tuning was refused");
            f.Server.OnDisconnected(2);
            f.Reject(tune.ToArray(), peer: 3);
            using var release = new ServerFixture(debug: false);
            release.Reject(new NetWriter(Msg.TuningSet, release.Id).String(nameof(Tuning.BlastRadius)).Scalar(5f).ToArray());
        });
        runner.Run("server/stale lifecycle and gameplay messages are ignored", () =>
        {
            using var f = new ServerFixture();
            f.Reject(Explosion(f, BodyIds.MapTankId(0), matchId: f.Id + 1).ToArray());
            f.Advance(20); // End this ten-second match.
            f.Advance(10); // Results -> lobby.
            f.Receive(new NetWriter(Msg.StartMatch).Snapshot(f.Tuning.Snapshot()));
            var next = f.Packets.Select(p => p.Message.Payload).OfType<MatchSetup>().First();
            TestRunner.Assert(next.Id > f.Id, "match id was reused");
            f.Reject(new NetWriter(Msg.Loaded, f.Id).ToArray());
            f.Reject(Explosion(f, BodyIds.MapTankId(0)).ToArray());
            f.Packets.Clear();
            f.Advance();
            TestRunner.Assert(!f.Packets.Any(p => p.Message.Type == Msg.Countdown), "stale Loaded started the next match");
            f.Receive(new NetWriter(Msg.Loaded, next.Id));
            f.Receive(new NetWriter(Msg.Loaded, next.Id), peer: 3);
            f.Advance();
            TestRunner.Assert(f.Packets.Any(p => p.Message.Type == Msg.Countdown), "current Loaded failed to start the next match");
        });
        runner.Run("server/legacy Hello is rejected without changing its prefix", () =>
        {
            var server = Propane.Modes.ModeRegistry.CreateServer();
            try
            {
                var sent = new List<NetMessage>();
                server.SendOverride = (_, bytes, _) => sent.Add(MessageCodec.Decode(bytes, true));
                server.OnConnected(2);
                server.OnReceived(2, new NetWriter(Msg.Hello).Int(1).String("0.2.0").String("old").String("Test").Int(0).ToArray());
                TestRunner.Assert(sent.Single().Payload is RejectedMessage { Version: not null }, "old client was not told why it was rejected");
            }
            finally { server.Free(); }
        });
    }

    /// <summary>An explosion request for one body from a peer's actor, with the header it would really send.</summary>
    private static NetWriter Explosion(ServerFixture f, uint body, int peer = 2, int? matchId = null) =>
        f.ActorWriter(Msg.Explode, peer, matchId).UInt(body).Vec3(Vector3.One).UInt(1).UInt(1).UInt(0);

    private static BodyState DropBody(int slot, int counter) => new(BodyIds.DropTankId(slot, counter), Transform3D.Identity, Vector3.Up, Vector3.Zero);
}
