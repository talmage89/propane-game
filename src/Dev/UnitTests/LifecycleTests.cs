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
        runner.Run("lifecycle/sleeping bodies publish a final state after their owner leaves", () =>
        {
            var body = new RigidBody3D { Sleeping = true };
            try
            {
                var sync = new BodySync(me: 3, host: 2);
                var id = BodySync.MapTankId(0);
                sync.Register(id, body, isTank: true, owner: 2);
                // The old owner's final correction need not have finished before PlayerLeft arrives.
                sync.OnRest(2, 10, id, new Transform3D(Basis.Identity, Vector3.One), 1);
                sync.OwnerLeft(2, 3);
                var (moving, rested) = sync.Collect(1);
                TestRunner.Assert(moving.Count == 0 && rested.SingleOrDefault()?.Id == id,
                    "new owner went quiet while the other copies could still be correcting");
                TestRunner.Assert(sync.Get(id) is { Owner: 3, HasTarget: false }, "old owner still corrects the successor");
                TestRunner.Assert(sync.Collect(1).Rested.Count == 0, "handoff repeatedly sends rest");
            }
            finally { body.Free(); }
        });
        runner.Run("lifecycle/claims start a fresh owner's correction stream", () =>
        {
            var body = new RigidBody3D { Sleeping = true };
            try
            {
                var sync = new BodySync(me: 2, host: 3);
                var id = BodySync.MapTankId(0);
                sync.Register(id, body, isTank: true, owner: 3);
                sync.OnState(3, 100, id, Transform3D.Identity, Vector3.Zero, Vector3.Zero, 1);
                sync.OnClaim(4, new[] { id });
                // Sender times use each process's uptime. A new owner can have a lower timestamp.
                sync.OnState(4, 2, id, new Transform3D(Basis.Identity, Vector3.One), Vector3.Up, Vector3.Zero, 1.1);
                TestRunner.Assert(sync.Get(id) is { Owner: 4, LastSenderTime: 2, Target.Origin: var position } && position == Vector3.One,
                    "previous owner's clock starved the new correction stream");
                sync.OnClaim(2, new[] { id });
                TestRunner.Assert(sync.Collect(1).Rested.SingleOrDefault()?.Id == id, "sleeping claimant omitted its final state");
            }
            finally { body.Free(); }
        });
        runner.Run("lifecycle/a delayed moving snapshot cannot supersede the final rest", () =>
        {
            var body = new RigidBody3D { Sleeping = true };
            try
            {
                var sync = new BodySync(me: 3, host: 2);
                var id = BodySync.MapTankId(0);
                sync.Register(id, body, isTank: true, owner: 2);
                sync.OnState(2, 10, id, Transform3D.Identity, Vector3.Up, Vector3.Zero, 1);
                var rest = new Transform3D(Basis.Identity, Vector3.One);
                sync.OnRest(2, 11, id, rest, 1.1);
                // Sent after the last received motion, but before the rest: different channels can reorder it.
                sync.OnState(2, 10.5, id, Transform3D.Identity, Vector3.Up, Vector3.Zero, 1.2);
                TestRunner.Assert(sync.Get(id) is { TargetRest: true, Target.Origin: var position } && position == Vector3.One,
                    "late unreliable motion replaced the reliable final position");
                sync.Get(id)!.HasTarget = false; // The rest correction finished; keep its timestamp boundary.
                sync.OnState(2, 10.75, id, Transform3D.Identity, Vector3.Up, Vector3.Zero, 2);
                TestRunner.Assert(!sync.Get(id)!.HasTarget, "late motion resumed after the rest correction finished");
                sync.OnState(2, 12, id, Transform3D.Identity, Vector3.Up, Vector3.Zero, 2.1);
                TestRunner.Assert(sync.Get(id) is { HasTarget: true, TargetRest: false, LastSenderTime: 12 }, "genuine later motion was refused");
                sync.OnRest(2, 11, id, rest, 2.2);
                TestRunner.Assert(!sync.Get(id)!.TargetRest, "old rest froze genuinely later motion");
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
                Feed(client, Msg.Claim, new ClaimMessage(new[] { BodySync.MapTankId(0) }));
                Feed(client, Msg.MatchEnd, new[] { new Standing(2, "Test", 1, 1, true) });
                TestRunner.Assert(delivered.Count == 0, "events dispatched before attachment");
                Action<NetMessage> receiver = m => delivered.Add(m.Type);
                TestRunner.Assert(client.AttachMatch(7, receiver), "current scene could not attach");
                TestRunner.Assert(delivered.SequenceEqual(new[] { Msg.Countdown, Msg.MatchGo, Msg.Score, Msg.Claim, Msg.MatchEnd }), "queued events lost or reordered");
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
