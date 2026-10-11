using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Net;
using Propane.Tank;

namespace Propane.Dev.UnitTests;

/// <summary>Exercise the production receive handler and clock with an in-memory packet sink, no sockets.</summary>
[UnitSuite]
public static class ServerTests
{
    public static void Register(TestRunner runner)
    {
        runner.Run("server/truncated messages never relay or mutate", () =>
        {
            using var f = new Fixture();
            foreach (var fixture in ProtocolTests.Fixtures().Where(p => p.Type is <= Msg.Ping || Protocol.IsRelay(p.Type)))
            {
                var bytes = MessageCodec.Encode(fixture with { MatchId = Protocol.IsMatch(fixture.Type) ? f.Id : 0 }, false);
                f.Reject(bytes[..^1]);
            }
            // A truncated explosion must not have consumed the id or changed the bank.
            f.Receive(new NetWriter(Msg.Explode, f.Id).UInt(BodySync.MapTankId(0)).Vec3(Vector3.One).UInt(1).UInt(1).UInt(0));
            f.AssertScore(2, 5, 1);
        });
        runner.Run("server/unknown and duplicate explosions cannot score", () =>
        {
            using var f = new Fixture();
            f.Reject(new NetWriter(Msg.Explode, f.Id).UInt(0x12345678).Vec3(Vector3.One).UInt(1).UInt(1).UInt(0).ToArray());
            f.Reject(new NetWriter(Msg.Explode, f.Id).UInt(BodySync.MapTankId(0)).Vec3(new Vector3(float.NaN, 0, 0)).ToArray());
            var explosion = new NetWriter(Msg.Explode, f.Id).UInt(BodySync.MapTankId(0)).Vec3(Vector3.One).UInt(1).UInt(1).UInt(0);
            f.Receive(explosion);
            f.AssertScore(2, 5, 1);
            f.Reject(explosion.ToArray(), peer: 3);
        });
        runner.Run("server/drop batches are atomic and registered before scoring", () =>
        {
            using var f = new Fixture();
            var first = DropBody(0, 0);
            var second = DropBody(0, 1);
            f.Reject(f.Drop(new[] { first, second with { Id = BodySync.DropTankId(1, 0) } }));
            f.Reject(new NetWriter(Msg.Explode, f.Id).UInt(first.Id).Vec3(Vector3.One).UInt(1).UInt(1).UInt(0).ToArray());
            f.Reject(f.Drop(new[] { first, first }));
            f.Receive(f.Drop(new[] { first, second }));
            f.AssertScore(2, 2, -2);
            TestRunner.Assert(f.Packets.Any(p => p.Message.Payload is DropMessage), "drop was not relayed");
            f.Receive(new NetWriter(Msg.Explode, f.Id).UInt(first.Id).Vec3(Vector3.One).UInt(1).UInt(1).UInt(0));
            f.AssertScore(2, 3, 1);
            f.Reject(f.Drop(new[] { first }));
            f.Reject(new NetWriter(Msg.Explode, f.Id).UInt(first.Id).Vec3(Vector3.One).UInt(1).UInt(1).UInt(0).ToArray());
        });
        runner.Run("server/drop counts cannot raise scores or exceed bank and tuner", () =>
        {
            using var f = new Fixture();
            f.Reject(new NetWriter(Msg.Drop, f.Id).Byte(0).Int(-1).ToArray());
            f.Reject(new NetWriter(Msg.Drop, f.Id).Byte(0).Int(0).ToArray());
            f.Reject(f.Drop(Enumerable.Range(0, 5).Select(i => DropBody(0, i)).ToArray()));
            f.Reject(f.Drop(new[] { DropBody(0, 0), DropBody(0, 1) }, reason: 0));
            f.Receive(f.Drop(new[] { DropBody(0, 0) }));
            f.AssertScore(2, 3, -1);
        });
        runner.Run("server/expired playing clock refuses drops before the phase tick", () =>
        {
            using var f = new Fixture(playing: false);
            f.Receive(new NetWriter(Msg.Loaded, f.Id));
            f.Receive(new NetWriter(Msg.Loaded, f.Id), peer: 3);
            f.Advance(); // Loading -> Countdown.
            f.Advance(); // Countdown -> Playing.
            f.Clock.Seconds += 20; // Expire the deadline without running the phase tick.
            f.Packets.Clear();
            // Production polls incoming packets before advancing phases on this process frame.
            f.Receive(f.Drop(new[] { DropBody(0, 0) }));
            TestRunner.Assert(f.Packets.Single() is { Peer: 2, Message.Payload: DropRefusedMessage reply } &&
                reply.Ids.SequenceEqual(new[] { DropBody(0, 0).Id }), "expired clock accepted a predicted drop");
            f.Packets.Clear();
            f.Advance();
            TestRunner.Assert(f.Packets.Select(p => p.Message.Payload).OfType<IReadOnlyList<Standing>>().First().All(s => s.Score == 4),
                "late drop changed final banks before the phase tick");
        });
        runner.Run("server/gather uses the injected clock at the play deadline", () =>
        {
            using var f = new Fixture();
            var body = DropBody(0, 0) with { Id = BodySync.MapTankId(0), Sequence = 1 };
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
            using var f = new Fixture();
            f.Reject(MessageCodec.Encode(new NetMessage(Msg.Claim, f.Id, 0, new ClaimMessage(1,
                new[] { DropBody(0, 0) with { Id = BodySync.MapTankId(0) }, DropBody(0, 1) with { Id = 999 } })), false));
            f.Reject(new NetWriter(Msg.Puncture, f.Id).UInt(999).Transform(Transform3D.Identity).Vec3(Vector3.Zero)
                .Vec3(Vector3.Zero).Vec3(Vector3.One).Vec3(Vector3.Up).Float(1).UInt(1).UInt(1).UInt(0).ToArray());
            f.Reject(new NetWriter(Msg.BodyRest, f.Id).UInt(999).Transform(Transform3D.Identity).Double(1).UInt(1).UInt(1).ToArray());
            f.Reject(MessageCodec.Encode(new NetMessage(Msg.BodyStates, f.Id, 0, new BodyStatesMessage(1,
                new[] { DropBody(0, 0) with { Id = BodySync.MapTankId(0), Sequence = 1 }, DropBody(0, 1) with { Id = 999, Sequence = 1 } })), false));
            f.Reject(new NetWriter(Msg.Shot, f.Id).Vec3(Vector3.One).Vec3(Vector3.Up).Vec3(Vector3.Forward).Byte(1).Int(999).Int(-1).ToArray());
            f.Reject(new NetWriter(Msg.Shot, f.Id).Vec3(Vector3.One).Vec3(Vector3.Up).Vec3(Vector3.Forward).Byte(1).Int(0).Int(int.MaxValue).ToArray());
        });
        runner.Run("server/motion bounds future sample timestamps to the relay clock", () =>
        {
            using var f = new Fixture();
            f.Receive(MessageCodec.Encode(new NetMessage(Msg.BodyStates, f.Id, 0,
                new BodyStatesMessage(100000, new[] { DropBody(0, 0) with { Id = BodySync.MapTankId(0), Sequence = 1 } })), false));
            TestRunner.Assert(f.Packets.Single().Message.Payload is BodyStatesMessage relayed &&
                relayed.Time == f.Clock.Seconds, "relay accepted a future sample time");
        });
        runner.Run("server/motion preserves bounded sample age across both transport legs", () =>
        {
            using var f = new Fixture();
            var sampled = f.Clock.Seconds - 0.12;
            var body = DropBody(0, 0) with { Id = BodySync.MapTankId(0), Sequence = 1, Linear = Vector3.Right * 200 };
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
            using var f = new Fixture();
            var dropped = DropBody(0, 0);
            var state = MessageCodec.Encode(new NetMessage(Msg.BodyStates, f.Id, 0,
                new BodyStatesMessage(1, new[] { DropBody(0, 1) with { Id = BodySync.MapTankId(0), Sequence = 1 }, dropped with { Sequence = 1 } })), false);
            f.Reject(state); // Unreliable can overtake Drop: refuse the whole batch, without changing the ledger.
            f.Receive(f.Drop(new[] { dropped }));
            f.Packets.Clear();
            f.Receive(state);
            TestRunner.Assert(f.Packets.Single().Message.Payload is BodyStatesMessage, "later snapshot was permanently refused");
            f.Packets.Clear();
            f.Receive(new NetWriter(Msg.BodyRest, f.Id).UInt(dropped.Id).Transform(dropped.Transform).Double(2).UInt(1).UInt(2));
            TestRunner.Assert(f.Packets.Single() is { Peer: 3, Message.Payload: BodyRestMessage }, "reliable final rest was lost");
            f.Receive(new NetWriter(Msg.Explode, f.Id).UInt(dropped.Id).Vec3(Vector3.One).UInt(1).UInt(1).UInt(0));
            f.Packets.Clear();
            state = MessageCodec.Encode(new NetMessage(Msg.BodyStates, f.Id, 0,
                new BodyStatesMessage(2, new[] { DropBody(0, 1) with { Id = BodySync.MapTankId(0), Sequence = 2 }, dropped with { Sequence = 3 } })), false);
            f.Receive(state); // A late tombstone in a mixed batch must not starve the remaining moving bodies.
            TestRunner.Assert(f.Packets.Single().Message.Payload is BodyStatesMessage, "exploded id poisoned a later batch");
        });
        runner.Run("server/creator departure preserves slots, banks and the new host", () =>
        {
            using var f = new Fixture();
            f.Receive(MessageCodec.Encode(new NetMessage(Msg.Puncture, f.Id, 0, new PunctureMessage(BodySync.MapTankId(0),
                Transform3D.Identity, Vector3.Right, Vector3.Up, Vector3.One, Vector3.Up, -1)), false));
            f.Packets.Clear();
            f.Server.OnDisconnected(2);
            TestRunner.Assert(f.Packets.Any(p => p.Peer == 3 && p.Message.Payload is AuthorityMessage { Request: 0 } transfer &&
                transfer.Bodies.Any(b => b.Body.Id == BodySync.MapTankId(0) && b.Owner == 3 && b.Body.Epoch == 3 && b.Body.Sequence == 0 &&
                    b.State == TankLedger.State.Venting && b.Vent == new VentParameters(Vector3.One, Vector3.Up, -1))), "mid-vent departure did not grant canonical authority with a fresh epoch");
            TestRunner.Assert(f.Packets.Any(p => p.Peer == 3 && p.Message.Payload is PlayerLeftMessage { Player: 2, Host: 3 }),
                "creator departure did not identify the successor");
            f.Packets.Clear();
            f.Receive(f.Drop(new[] { DropBody(1, 0) }), peer: 3);
            f.AssertScore(3, 3, -1);
            f.Advance(20);
            var standings = f.Packets.Select(p => p.Message.Payload).OfType<IReadOnlyList<Standing>>().Single();
            TestRunner.Assert(standings.Any(s => s is { Id: 2, Score: 4, Present: false }) &&
                standings.Any(s => s is { Id: 3, Score: 3, Present: true }), "departure erased a bank or renumbered a spawn slot");
        });
        runner.Run("server/a drop refused after a tuning change gets an explicit reply", () =>
        {
            using var f = new Fixture();
            f.Receive(new NetWriter(Msg.TuningSet, f.Id).String(nameof(Tuning.RagdollDropCount)).Scalar(1));
            f.Packets.Clear();
            // This batch fit the former count of five when the victim sent it. The creator's tuning arrived first.
            f.Receive(f.Drop(new[] { DropBody(0, 0), DropBody(0, 1) }));
            TestRunner.Assert(f.Packets.Count == 1 && f.Packets[0] is { Peer: 2, Message.Payload: DropRefusedMessage reply } &&
                reply.Ids.SequenceEqual(new[] { DropBody(0, 0).Id, DropBody(0, 1).Id }),
                "refused prediction had no reply and could leave phantom tanks");
            f.Packets.Clear();
            f.Receive(f.Drop(new[] { DropBody(0, 2) }));
            f.AssertScore(2, 3, -1);
            TestRunner.Assert(f.Packets.Any(p => p is { Peer: 2, Message.Sender: 2, Message.Payload: DropMessage }),
                "accepted prediction did not confirm its ids to the sender");
        });
        runner.Run("server/tuning requires original debug creator", () =>
        {
            using var f = new Fixture();
            var tune = new NetWriter(Msg.TuningSet, f.Id).String(nameof(Tuning.BlastRadius)).Scalar(5f);
            f.Reject(tune.ToArray(), peer: 3);
            f.Reject(new NetWriter(Msg.TuningSet, f.Id).String(nameof(Tuning.BlastRadius)).Scalar(float.NaN).ToArray());
            f.Receive(tune);
            TestRunner.Assert(f.Packets.Any(p => p.Message.Payload is TuningMessage { Name: nameof(Tuning.BlastRadius) }), "creator's valid tuning was refused");
            f.Server.OnDisconnected(2);
            f.Reject(tune.ToArray(), peer: 3);
            using var release = new Fixture(debug: false);
            release.Reject(new NetWriter(Msg.TuningSet, release.Id).String(nameof(Tuning.BlastRadius)).Scalar(5f).ToArray());
        });
        runner.Run("server/stale lifecycle and gameplay messages are ignored", () =>
        {
            using var f = new Fixture();
            f.Reject(new NetWriter(Msg.Explode, f.Id + 1).UInt(BodySync.MapTankId(0)).Vec3(Vector3.One).UInt(1).UInt(1).UInt(0).ToArray());
            f.Advance(20); // End this ten-second match.
            f.Advance(10); // Results -> lobby.
            f.Receive(new NetWriter(Msg.StartMatch).Snapshot(f.Tuning.Snapshot()));
            var next = f.Packets.Select(p => p.Message.Payload).OfType<MatchSetup>().First();
            TestRunner.Assert(next.Id > f.Id, "match id was reused");
            f.Reject(new NetWriter(Msg.Loaded, f.Id).ToArray());
            f.Reject(new NetWriter(Msg.Explode, f.Id).UInt(BodySync.MapTankId(0)).Vec3(Vector3.One).UInt(1).UInt(1).UInt(0).ToArray());
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
            var server = new LobbyServer();
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

    private static BodyState DropBody(int slot, int counter) => new(BodySync.DropTankId(slot, counter), Transform3D.Identity, Vector3.Up, Vector3.Zero);

    internal sealed class Fixture : IDisposable
    {
        public readonly ManualClock Clock = new();
        public readonly LobbyServer Server;
        public readonly Tuning Tuning = new() { CountdownTime = 0, ResultsTime = 1 };
        public readonly List<(int Peer, NetMessage Message)> Packets = new();
        public readonly int Id;
        public readonly MatchSetup Setup;

        public Fixture(bool debug = true, bool playing = true)
        {
            Server = new LobbyServer(PacketTransports.CreateServer(), Clock) { StartScore = 4 };
            Server.SendOverride = (peer, bytes, _) => Packets.Add((peer, MessageCodec.Decode(bytes, true)));
            for (var peer = 2; peer <= 3; peer++)
            {
                Server.OnConnected(peer);
                Receive(new NetWriter(Msg.Hello).Int(Protocol.Version).String(BuildInfo.GameVersion).String("test")
                    .String($"Player{peer}").Int(peer - 2).Bool(peer == 2 ? debug : true), peer);
            }
            Receive(new NetWriter(Msg.CreateLobby));
            Receive(new NetWriter(Msg.JoinLobby).Int(1), peer: 3);
            Receive(new NetWriter(Msg.SetMatchLength).Float(10));
            Receive(new NetWriter(Msg.StartMatch).Snapshot(Tuning.Snapshot()));
            Setup = Packets.Select(p => p.Message.Payload).OfType<MatchSetup>().First();
            Id = Setup.Id;
            if (playing)
            {
                Receive(new NetWriter(Msg.Loaded, Id));
                Receive(new NetWriter(Msg.Loaded, Id), peer: 3);
                Advance();
                Advance();
                TestRunner.Assert(Packets.Any(p => p.Message.Type == Msg.MatchGo), "fixture never reached play");
            }
            Packets.Clear();
        }

        public void Advance(double seconds = 0)
        {
            Clock.Seconds += seconds;
            Server.AdvanceMatches(Clock.Seconds);
        }

        public void Receive(NetWriter packet, int peer = 2) => Receive(packet.ToArray(), peer);
        public void Receive(byte[] packet, int peer = 2) => Server.OnReceived(peer, packet);
        public void Reject(byte[] bytes, int peer = 2)
        {
            Packets.Clear();
            Receive(bytes, peer);
            if (Packets.Count == 1 && Packets[0] is { Message.Payload: AuthorityMessage { Reason: not ClaimRefusal.None } authority } && Packets[0].Peer == peer)
            {
                var request = MessageCodec.Decode(bytes, false).Payload switch
                {
                    ClaimMessage claim => claim.Request,
                    PunctureMessage puncture => puncture.Request,
                    ExplodeMessage explosion => explosion.Request,
                    _ => throw new InvalidOperationException("non-claim received an authority refusal"),
                };
                TestRunner.Assert(authority.Request == request && authority.Peer == peer, "refusal changed its request identity");
                return;
            }
            if (Packets.Count == 1 && Packets[0] is { Message.Payload: DropRefusedMessage reply } && Packets[0].Peer == peer)
            {
                // Only a fully decoded Drop may receive a refusal. Malformed packets must emit nothing.
                var drop = MessageCodec.Decode(bytes, false).Payload as DropMessage;
                TestRunner.Assert(drop != null && reply.Ids.SequenceEqual(drop.Tanks.Select(t => t.Id)), "refusal changed the predicted ids");
            }
            else
            {
                TestRunner.Assert(Packets.Count == 0, $"invalid {(bytes.Length > 0 ? ((Msg)bytes[0]).ToString() : "empty")} mutated or relayed {Packets.Count} packets");
            }
        }
        public byte[] Drop(IReadOnlyList<BodyState> tanks, byte reason = 1) => MessageCodec.Encode(new NetMessage(Msg.Drop, Id, 0, new DropMessage(reason, tanks)), false);
        public void AssertScore(int player, int score, int delta) => TestRunner.Assert(Packets.Any(p => p.Message.Payload is ScoreMessage s &&
            s.Player == player && s.Score == score && s.Delta == delta), $"expected score {score}, delta {delta}");
        public void Dispose() { Server.Free(); Tuning.Dispose(); }
    }
}
