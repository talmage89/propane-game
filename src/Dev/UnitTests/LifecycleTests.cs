using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Net;

namespace Propane.Dev.UnitTests;

public static class LifecycleTests
{
    public static void Register(TestRunner runner)
    {
        runner.Run("lifecycle/body sampling converts to server time only after a ping", () =>
        {
            var client = new NetClient();
            try
            {
                TestRunner.Assert(client.BodySampleTime(4) == 0, "unsynchronized sample invented a server clock");
                var before = NetTransport.Now;
                var serverTime = before + 100;
                client.Handle(MessageCodec.Encode(new NetMessage(Msg.Pong, 0, 0, new PongMessage(before - 0.2, serverTime)), true));
                var after = NetTransport.Now;
                var stamp = client.BodySampleTime(after + 3);
                TestRunner.Assert(stamp >= serverTime + 3.1 - 0.00001 && stamp <= serverTime + 3.1 + after - before + 0.00001,
                    "sampling used the client's unrelated clock instead of its ping estimate");
                client.Disconnect();
                TestRunner.Assert(client.BodySampleTime(4) == 0 && client.ServerClockOffset == 0,
                    "a reconnect could reuse the previous server's clock");
            }
            finally { client.Free(); }
        });
        runner.Run("lifecycle/refused drop predictions release only their own tanks and bank reservation", () =>
        {
            foreach (var refuseFirst in new[] { false, true })
            {
                var pending = new DropPredictions();
                var accepted = BodySync.DropTankId(0, 0);
                var refused = BodySync.DropTankId(0, 1);
                pending.Add(accepted);
                pending.Add(refused);
                if (!refuseFirst) { pending.ScoreConfirmed(-1); pending.Accepted(new[] { accepted }); }
                var removed = pending.Refused(new[] { BodySync.MapTankId(0), refused, refused });
                TestRunner.Assert(removed.SequenceEqual(new[] { refused }), "refusal removed foreign or duplicate ids");
                if (refuseFirst) { pending.ScoreConfirmed(-1); pending.Accepted(new[] { accepted }); }
                TestRunner.Assert(pending.UnconfirmedCount == 0, "refusal permanently consumed the local bank reservation");
                TestRunner.Assert(pending.Refused(new[] { accepted, refused }).Count == 0, "late refusal removed a confirmed tank");
                pending.Add(BodySync.DropTankId(0, 2));
                TestRunner.Assert(pending.UnconfirmedCount == 1, "subsequent spill could not reserve its bank");
            }
        });
        runner.Run("lifecycle/sleeping successor publishes once and clears the previous target", () =>
        {
            var body = new RigidBody3D { Sleeping = true };
            try
            {
                var sync = new BodySync(me: 3, host: 2);
                var id = BodySync.MapTankId(0);
                sync.Register(id, body, isTank: true, owner: 2);
                var state = new BodyState(id, Transform3D.Identity, Vector3.Zero, Vector3.Zero, 1, 10);
                sync.OnState(2, state, rest: true, 1);
                sync.OnAuthority(new BodyAuthority(state with { Epoch = 2, Sequence = 0 }, 3, TankLedger.State.Intact, true, null, Vector3.Zero));
                TestRunner.Assert(sync.Get(id) is { Owner: 3, HasTarget: false, Epoch: 2 }, "old owner still corrects the successor");
                TestRunner.Assert(sync.Collect(1).Rested.SingleOrDefault()?.Id == id, "sleeping successor omitted its final state");
                TestRunner.Assert(sync.Collect(1).Rested.Count == 0, "handoff repeatedly sends rest");
            }
            finally { body.Free(); }
        });
        runner.Run("lifecycle/buffer through Go and End until the match exists", () =>
        {
            var client = Client();
            try
            {
                var delivered = new List<Msg>();
                Start(client, 7);
                Feed(client, Msg.Countdown, new FloatMessage(4));
                Feed(client, Msg.MatchGo, new FloatMessage(90));
                Feed(client, Msg.Score, new ScoreMessage(2, 1, 1));
                Feed(client, Msg.Authority, ProtocolTests.Fixtures().Single(f => f.Type == Msg.Authority).Payload);
                Feed(client, Msg.MatchEnd, new[] { new Standing(2, "Test", 1, 1, true) });
                TestRunner.Assert(delivered.Count == 0, "events dispatched before attachment");
                Action<NetMessage> receiver = m => delivered.Add(m.Type);
                TestRunner.Assert(client.AttachMatch(7, receiver), "current scene could not attach");
                TestRunner.Assert(delivered.SequenceEqual(new[] { Msg.Countdown, Msg.MatchGo, Msg.Score, Msg.Authority, Msg.MatchEnd }), "queued events lost or reordered");
                client.AttachMatch(7, receiver);
                TestRunner.Assert(delivered.Count == 5, "queue replayed twice");
                Feed(client, Msg.Clock, new FloatMessage(0));
                TestRunner.Assert(delivered.Count == 6, "attached receiver did not get new events");
            }
            finally { client.Free(); }
        });
        runner.Run("lifecycle/stale packets and old cleanup cannot affect the next match", () =>
        {
            var client = Client();
            try
            {
                Start(client, 7);
                var oldCount = 0;
                Action<NetMessage> old = _ => oldCount++;
                client.AttachMatch(7, old);
                Feed(client, Msg.MatchGo, new FloatMessage(90));
                Start(client, 8);
                Feed(client, Msg.MatchGo, new FloatMessage(90), id: 7);
                Feed(client, Msg.MatchGo, new FloatMessage(90), id: 8);
                client.DetachMatch(7, old);
                TestRunner.Assert(!client.AttachMatch(7, old), "old scene attached to new match");
                var nextCount = 0;
                client.AttachMatch(8, _ => nextCount++);
                TestRunner.Assert(oldCount == 1 && nextCount == 1, "stale scene or packet affected the current match");
                Start(client, 7); // A replayed MatchStart may not replace the current match.
                TestRunner.Assert(client.ActiveMatchId == 8, "stale MatchStart reset the inbox");
            }
            finally { client.Free(); }
        });
        runner.Run("lifecycle/leaving and disconnecting clear pending events", () =>
        {
            var client = Client();
            try
            {
                Start(client, 7);
                Feed(client, Msg.MatchGo, new FloatMessage(90));
                client.LeaveLobby();
                TestRunner.Assert(client.ActiveMatchId == 0 && !client.AttachMatch(7, _ => throw new InvalidOperationException("old message")), "leave kept a pending match");
                client.Handle(MessageCodec.Encode(ProtocolTests.Fixtures().Single(f => f.Type == Msg.LobbyState), true));
                Start(client, 8);
                Feed(client, Msg.MatchGo, new FloatMessage(90), id: 8);
                client.Disconnect();
                TestRunner.Assert(client.ActiveMatchId == 0 && !client.AttachMatch(8, _ => throw new InvalidOperationException("old message")), "disconnect kept a pending match");
            }
            finally { client.Free(); }
        });
        runner.Run("lifecycle/malformed packets never reach the handoff", () =>
        {
            var client = Client();
            try
            {
                Start(client, 7);
                var score = MessageCodec.Encode(new NetMessage(Msg.Score, 7, 0, new ScoreMessage(2, 1, 1)), true);
                client.Handle(score[..^1]);
                var count = 0;
                client.AttachMatch(7, _ => count++);
                TestRunner.Assert(count == 0, "partial score was queued or dispatched");
            }
            finally { client.Free(); }
        });
        runner.Run("lifecycle/queue byte and packet limits fail before mutation", () =>
        {
            var inbox = new MatchInbox();
            inbox.Begin(7);
            var go = new NetMessage(Msg.MatchGo, 7, 0, new FloatMessage(90));
            inbox.Receive(go, MatchInbox.MaxBytes);
            TestRunner.Throws<MatchInbox.OverflowException>(() => inbox.Receive(go, 1));
            var count = 0;
            inbox.Attach(7, _ => count++);
            TestRunner.Assert(count == 1, "overflow mutated the queue");
            inbox.Begin(8);
            for (var i = 0; i < MatchInbox.MaxPackets; i++) { inbox.Receive(go with { MatchId = 8 }, 1); }
            TestRunner.Throws<MatchInbox.OverflowException>(() => inbox.Receive(go with { MatchId = 8 }, 1));
            inbox.Clear();
            TestRunner.Assert(!inbox.Attach(8, _ => throw new InvalidOperationException("uncleared")), "cleared queue can still attach");
        });
        runner.Run("lifecycle/overflow disconnects with a reason and clears the handoff", () =>
        {
            var client = Client();
            try
            {
                Start(client, 7);
                string? reason = null;
                client.StatusChanged += (_, text) => reason = text;
                for (var i = 0; i <= MatchInbox.MaxPackets; i++) { Feed(client, Msg.Clock, new FloatMessage(0)); }
                TestRunner.Assert(client.State == NetClient.Status.Offline && client.ActiveMatchId == 0, "overflow left the connection or queue alive");
                TestRunner.Assert(reason != null && reason.Contains("message buffer"), "overflow did not explain the disconnect");
                TestRunner.Assert(!client.AttachMatch(7, _ => throw new InvalidOperationException("overflowed packet")), "overflowed queue can still attach");
            }
            finally { client.Free(); }
        });
        runner.Run("lifecycle/a new match during replay stops the old queue", () =>
        {
            var inbox = new MatchInbox();
            inbox.Begin(7);
            var go = new NetMessage(Msg.MatchGo, 7, 0, new FloatMessage(90));
            inbox.Receive(go, 10);
            inbox.Receive(go, 10);
            var oldCount = 0;
            inbox.Attach(7, _ =>
            {
                oldCount++;
                inbox.Begin(8);
                inbox.Receive(go with { MatchId = 8 }, 10);
            });
            var nextCount = 0;
            inbox.Attach(8, _ => nextCount++);
            TestRunner.Assert(oldCount == 1 && nextCount == 1, "replay crossed match generations");
        });
    }

    private static NetClient Client()
    {
        var client = new NetClient();
        foreach (var type in new[] { Msg.Welcome, Msg.LobbyState })
        {
            client.Handle(MessageCodec.Encode(ProtocolTests.Fixtures().Single(f => f.Type == type), true));
        }
        return client;
    }
    private static void Start(NetClient client, int id)
    {
        var fixture = ProtocolTests.Fixtures().Single(f => f.Type == Msg.MatchStart);
        client.Handle(MessageCodec.Encode(fixture with { MatchId = id, Payload = ((MatchSetup)fixture.Payload) with { Id = id } }, true));
    }
    private static void Feed(NetClient client, Msg type, object payload, int id = 7) => client.Handle(MessageCodec.Encode(new NetMessage(type, id, 2, payload), true));
}
