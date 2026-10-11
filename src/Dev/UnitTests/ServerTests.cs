using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Net;

namespace Propane.Dev.UnitTests;

/// <summary>Exercise the production receive handler and clock with an in-memory packet sink, no sockets.</summary>
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
            f.Receive(new NetWriter(Msg.Explode, f.Id).UInt(BodySync.MapTankId(0)).Vec3(Vector3.One));
            f.AssertScore(2, 5, 1);
        });
        runner.Run("server/unknown and duplicate explosions cannot score", () =>
        {
            using var f = new Fixture();
            f.Reject(new NetWriter(Msg.Explode, f.Id).UInt(0x12345678).Vec3(Vector3.One).ToArray());
            f.Reject(new NetWriter(Msg.Explode, f.Id).UInt(BodySync.MapTankId(0)).Vec3(new Vector3(float.NaN, 0, 0)).ToArray());
            var explosion = new NetWriter(Msg.Explode, f.Id).UInt(BodySync.MapTankId(0)).Vec3(Vector3.One);
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
            f.Reject(new NetWriter(Msg.Explode, f.Id).UInt(first.Id).Vec3(Vector3.One).ToArray());
            f.Reject(f.Drop(new[] { first, first }));
            f.Receive(f.Drop(new[] { first, second }));
            f.AssertScore(2, 2, -2);
            TestRunner.Assert(f.Packets.Any(p => p.Message.Payload is DropMessage), "drop was not relayed");
            f.Receive(new NetWriter(Msg.Explode, f.Id).UInt(first.Id).Vec3(Vector3.One));
            f.AssertScore(2, 3, 1);
            f.Reject(f.Drop(new[] { first }));
            f.Reject(new NetWriter(Msg.Explode, f.Id).UInt(first.Id).Vec3(Vector3.One).ToArray());
        });
        runner.Run("server/drop counts cannot raise scores or exceed bank and tuner", () =>
        {
            using var f = new Fixture();
            f.Reject(new NetWriter(Msg.Drop, f.Id).Byte(0).Int(-1).Int(0).ToArray());
            f.Reject(new NetWriter(Msg.Drop, f.Id).Byte(0).Int(0).Int(0).ToArray());
            f.Reject(f.Drop(Enumerable.Range(0, 5).Select(i => DropBody(0, i)).ToArray()));
            f.Reject(f.Drop(new[] { DropBody(0, 0), DropBody(0, 1) }, reason: 0));
            f.Reject(f.Drop(new[] { DropBody(0, 0) }, color: 1));
            f.Receive(f.Drop(new[] { DropBody(0, 0) }));
            f.AssertScore(2, 3, -1);
        });
        runner.Run("server/body and shot ids must exist", () =>
        {
            using var f = new Fixture();
            f.Reject(new NetWriter(Msg.Claim, f.Id).UShort(2).UInt(BodySync.MapTankId(0)).UInt(999).ToArray());
            f.Reject(new NetWriter(Msg.Puncture, f.Id).UInt(999).Transform(Transform3D.Identity).Vec3(Vector3.Zero)
                .Vec3(Vector3.Zero).Vec3(Vector3.One).Vec3(Vector3.Up).Float(1).ToArray());
            f.Reject(new NetWriter(Msg.BodyRest, f.Id).UInt(999).Transform(Transform3D.Identity).Double(1).ToArray());
            f.Reject(MessageCodec.Encode(new NetMessage(Msg.BodyStates, f.Id, 0, new BodyStatesMessage(1,
                new[] { DropBody(0, 0) with { Id = BodySync.MapTankId(0) }, DropBody(0, 1) with { Id = 999 } })), false));
            f.Reject(new NetWriter(Msg.Shot, f.Id).Vec3(Vector3.One).Vec3(Vector3.Up).Vec3(Vector3.Forward).Byte(1).Int(999).Int(-1).ToArray());
            f.Reject(new NetWriter(Msg.Shot, f.Id).Vec3(Vector3.One).Vec3(Vector3.Up).Vec3(Vector3.Forward).Byte(1).Int(0).Int(int.MaxValue).ToArray());
        });
        runner.Run("server/drop registration recovers an early snapshot and preserves the final rest", () =>
        {
            using var f = new Fixture();
            var dropped = DropBody(0, 0);
            var state = MessageCodec.Encode(new NetMessage(Msg.BodyStates, f.Id, 0,
                new BodyStatesMessage(1, new[] { DropBody(0, 1) with { Id = BodySync.MapTankId(0) }, dropped })), false);
            f.Reject(state); // Unreliable can overtake Drop: refuse the whole batch, without changing the ledger.
            f.Receive(f.Drop(new[] { dropped }));
            f.Packets.Clear();
            f.Receive(state);
            TestRunner.Assert(f.Packets.Single().Message.Payload is BodyStatesMessage, "later snapshot was permanently refused");
            f.Packets.Clear();
            f.Receive(new NetWriter(Msg.BodyRest, f.Id).UInt(dropped.Id).Transform(dropped.Transform).Double(2));
            TestRunner.Assert(f.Packets.Single() is { Peer: 3, Message.Payload: BodyRestMessage }, "reliable final rest was lost");
            f.Receive(new NetWriter(Msg.Explode, f.Id).UInt(dropped.Id).Vec3(Vector3.One));
            f.Packets.Clear();
            f.Receive(state); // A late tombstone in a mixed batch must not starve the remaining moving bodies.
            TestRunner.Assert(f.Packets.Single().Message.Payload is BodyStatesMessage, "exploded id poisoned a later batch");
        });
        runner.Run("server/creator departure preserves slots, banks and the new host", () =>
        {
            using var f = new Fixture();
            f.Server.OnDisconnected(2);
            TestRunner.Assert(f.Packets.Any(p => p.Peer == 3 && p.Message.Payload is PlayerLeftMessage { Player: 2, Host: 3 }),
                "creator departure did not identify the successor");
            f.Packets.Clear();
            f.Receive(f.Drop(new[] { DropBody(1, 0) }, color: 1), peer: 3);
            f.AssertScore(3, 3, -1);
            f.Server.AdvanceMatches(NetTransport.Now + 20);
            var standings = f.Packets.Select(p => p.Message.Payload).OfType<IReadOnlyList<Standing>>().Single();
            TestRunner.Assert(standings.Any(s => s is { Id: 2, Score: 4, Present: false }) &&
                standings.Any(s => s is { Id: 3, Score: 3, Present: true }), "departure erased a bank or renumbered a spawn slot");
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
            f.Reject(new NetWriter(Msg.Explode, f.Id + 1).UInt(BodySync.MapTankId(0)).Vec3(Vector3.One).ToArray());
            f.Server.AdvanceMatches(NetTransport.Now + 20); // End this ten-second match.
            f.Server.AdvanceMatches(NetTransport.Now + 30); // Results -> lobby.
            f.Receive(new NetWriter(Msg.StartMatch).Snapshot(f.Tuning.Snapshot()));
            var next = f.Packets.Select(p => p.Message.Payload).OfType<MatchSetup>().First();
            TestRunner.Assert(next.Id > f.Id, "match id was reused");
            f.Reject(new NetWriter(Msg.Loaded, f.Id).ToArray());
            f.Reject(new NetWriter(Msg.Explode, f.Id).UInt(BodySync.MapTankId(0)).Vec3(Vector3.One).ToArray());
            f.Packets.Clear();
            f.Server.AdvanceMatches(NetTransport.Now);
            TestRunner.Assert(!f.Packets.Any(p => p.Message.Type == Msg.Countdown), "stale Loaded started the next match");
            f.Receive(new NetWriter(Msg.Loaded, next.Id));
            f.Receive(new NetWriter(Msg.Loaded, next.Id), peer: 3);
            f.Server.AdvanceMatches(NetTransport.Now);
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

    private sealed class Fixture : IDisposable
    {
        public readonly LobbyServer Server = new() { StartScore = 4 };
        public readonly Tuning Tuning = new() { CountdownTime = 0, ResultsTime = 1 };
        public readonly List<(int Peer, NetMessage Message)> Packets = new();
        public readonly int Id;

        public Fixture(bool debug = true)
        {
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
            Id = Packets.Select(p => p.Message.Payload).OfType<MatchSetup>().First().Id;
            Receive(new NetWriter(Msg.Loaded, Id));
            Receive(new NetWriter(Msg.Loaded, Id), peer: 3);
            Server.AdvanceMatches(NetTransport.Now);
            Server.AdvanceMatches(NetTransport.Now);
            TestRunner.Assert(Packets.Any(p => p.Message.Type == Msg.MatchGo), "fixture never reached play");
            Packets.Clear();
        }

        public void Receive(NetWriter packet, int peer = 2) => Receive(packet.ToArray(), peer);
        public void Receive(byte[] packet, int peer = 2) => Server.OnReceived(peer, packet);
        public void Reject(byte[] bytes, int peer = 2)
        {
            Packets.Clear();
            Receive(bytes, peer);
            TestRunner.Assert(Packets.Count == 0, $"invalid {(bytes.Length > 0 ? ((Msg)bytes[0]).ToString() : "empty")} emitted {Packets.Count} packets");
        }
        public byte[] Drop(IReadOnlyList<BodyState> tanks, byte reason = 1, int color = 0) => MessageCodec.Encode(new NetMessage(Msg.Drop, Id, 0, new DropMessage(reason, color, tanks)), false);
        public void AssertScore(int player, int score, int delta) => TestRunner.Assert(Packets.Any(p => p.Message.Payload is ScoreMessage s &&
            s.Player == player && s.Score == score && s.Delta == delta), $"expected score {score}, delta {delta}");
        public void Dispose() { Server.Free(); Tuning.Dispose(); }
    }
}
