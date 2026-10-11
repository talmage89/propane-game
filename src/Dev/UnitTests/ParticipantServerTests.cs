using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Propane.Net;
using Propane.Player;
using Propane.Tank;

namespace Propane.Dev.UnitTests;

[UnitSuite]
public static class ParticipantServerTests
{
    private static byte[] Request(Msg type, object payload) => MessageCodec.Encode(new NetMessage(type, 0, 0, payload), false);
    private static void Assert(bool condition, string reason) => TestRunner.Assert(condition, reason);
    public static void Register(TestRunner runner)
    {
        runner.Run("participants/server owns revisions and idempotent roster requests", () =>
        {
            using var f = new ServerTests.Fixture(playing: false);
            f.Advance(50); f.Advance(); f.Advance(20); f.Advance(2); // Return to Waiting.
            f.Packets.Clear();
            var add = Request(Msg.AddActor, new AddActorMessage(1, ActorKind.Bot, "Embedded", AppearanceRecord.FromPalette(0), ControllerConfig.Bot));
            f.Receive(add);
            var roster = f.Packets.Select(p => p.Message.Payload).OfType<RosterStateMessage>().First();
            var bot = roster.Actors.Single(a => a.Kind == ActorKind.Bot);
            Assert(roster.Revision == 3 && bot.OwnerPeerId == 2 && bot.Id != bot.OwnerPeerId, "actor ownership or revision incorrect");
            f.Packets.Clear(); f.Receive(add);
            Assert(f.Packets.Count == 1 && f.Packets[0].Message.Payload is ActorRequestResultMessage { Accepted: true, Revision: 3 }, "duplicate request mutated roster");
            f.Packets.Clear();
            f.Receive(Request(Msg.UpdateActor, new UpdateActorMessage(1, bot.Id, "Stolen", bot.Appearance, ControllerConfig.Bot)), 3);
            Assert(f.Packets.Single().Message.Payload is ActorRequestResultMessage { Accepted: false, Revision: 3 }, "spoofed update accepted");
            f.Packets.Clear();
            f.Receive(Request(Msg.UpdateActor, new UpdateActorMessage(2, bot.Id, "Renamed", bot.Appearance, ControllerConfig.Bot)));
            Assert(f.Packets.Any(p => p.Message.Payload is RosterStateMessage { Revision: 4 } r && r.Actors.Any(a => a.Name == "Renamed")), "update not broadcast");
            f.Packets.Clear(); f.Receive(Request(Msg.RemoveActor, new RemoveActorMessage(3, bot.Id)));
            Assert(f.Packets.Any(p => p.Message.Payload is RosterStateMessage { Revision: 5 } r && r.Actors.Count == 2), "remove not broadcast");
            f.Packets.Clear(); f.Receive(Request(Msg.AddActor, new AddActorMessage(4, ActorKind.Bot, "Next", bot.Appearance, ControllerConfig.Bot)));
            var next = f.Packets.Select(p => p.Message.Payload).OfType<RosterStateMessage>().First().Actors.Single(a => a.Kind == ActorKind.Bot);
            Assert(next.Id > bot.Id && next.AllocationSlot > bot.AllocationSlot, "removed actor or allocation reused");
            f.Packets.Clear();
            f.Receive(new NetWriter(Msg.StartMatch).Snapshot(f.Tuning.Snapshot()));
            var setup = f.Packets.Select(p => p.Message.Payload).OfType<MatchSetup>().First();
            var mapping = setup.Players.Single(p => p.Player.Id == next.Id);
            Assert(mapping.Spawn != mapping.Player.AllocationSlot, "spawn order remained allocation identity");
            foreach (var peer in new[] { 2, 3 }) f.Receive(new NetWriter(Msg.Loaded, setup.Id), peer);
            f.Advance(); f.Advance(); f.Packets.Clear();
            var drop = new BodyState(BodySync.DropTankId(next.AllocationSlot, 0), Transform3D.Identity, Vector3.Zero, Vector3.Zero);
            f.Receive(MessageCodec.Encode(new NetMessage(Msg.Drop, setup.Id, 0, new DropMessage(0, new[] { drop }), next.Id, 1, 1), false));
            Assert(f.Packets.Any(p => p.Message.Payload is ScoreMessage s && s.Player == next.Id && s.Score == 3),
                "ledger validated the spawn slot instead of the allocation slot");
        });
        runner.Run("participants/frozen roster counts actors and validates ownership and lives", () =>
        {
            using var f = new ServerTests.Fixture(bots: 1);
            Assert(f.Setup.Players.Count == 3 && f.Setup.Peers.Count == 2, "actor count conflated with readiness peers");
            var bot = f.Setup.Players.Single(p => p.Player.Kind == ActorKind.Bot).Player;
            var human = f.Setup.Players.Single(p => p.Player.OwnerPeerId == 2 && p.Player.Kind == ActorKind.Human).Player;
            var explosion = new ExplodeMessage(BodySync.MapTankId(0), Vector3.Zero);
            byte[] Packet(int actor, uint life, uint action) => MessageCodec.Encode(new NetMessage(Msg.Explode, f.Id, 0, explosion, actor, life, action), false);
            f.Reject(Packet(human.Id, 1, 1), 3);
            f.Reject(Packet(bot.Id, 2, 1));
            f.Receive(Packet(bot.Id, 1, 1));
            Assert(f.Packets.Any(p => p.Message.Payload is ScoreMessage s && s.Player == bot.Id && s.Score == 5), "bot gain credited to human");
            Assert(!f.Packets.Any(p => p.Message.Payload is ScoreMessage s && s.Player == human.Id), "last local actor stole score");
            f.Reject(Packet(bot.Id, 1, 1));
            f.Packets.Clear();
            f.Receive(Request(Msg.AddActor, new AddActorMessage(2, ActorKind.Bot, "Late", bot.Appearance, ControllerConfig.Bot)));
            Assert(f.Packets.Single().Message.Payload is ActorRequestResultMessage { Accepted: false }, "match roster was mutable");
        });
        runner.Run("participants/departure removes every actor and retains departed banks", () =>
        {
            using var f = new ServerTests.Fixture(bots: 1);
            var departed = f.Setup.Players.Where(a => a.Player.OwnerPeerId == 2).Select(a => a.Player.Id).ToArray();
            f.Server.OnDisconnected(2);
            Assert(f.Packets.Any(p => p.Message.Payload is AuthorityMessage a && a.Bodies.All(b => b.Owner == 3 && b.Body.Epoch == 2)), "body authority was not transferred with a fresh epoch");
            Assert(f.Packets.Any(p => p.Message.Payload is RosterStateMessage r && r.Actors.All(a => a.OwnerPeerId != 2)), "embedded actor survived departure");
            f.Packets.Clear(); f.Advance(11);
            var standings = f.Packets.Select(p => p.Message.Payload).OfType<IReadOnlyList<Standing>>().Single();
            Assert(standings.Count == 3 && departed.All(id => standings.Any(s => s.Id == id && !s.Present && s.Score == 4)), "departed standings erased bank");
            f.Reject(new NetWriter(Msg.TuningSet, f.Id).String("BlastRadius").Scalar(5f).ToArray(), 3);
        });
        runner.Run("participants/same-peer shot and melee use one event path and dedupe echoes", () =>
        {
            using var f = new ServerTests.Fixture(bots: 1);
            var client = new NetClient();
            client.Handle(MessageCodec.Encode(new NetMessage(Msg.Welcome, 0, 0, new WelcomeMessage(2, null)), true));
            var match = new Match { Net = client, Setup = f.Setup, CreateLocalView = false };
            runner.AddChild(match);
            try
            {
                var human = match.Player;
                var bot = match.Session.OwnedActors.Single(a => a.Character != human).Character;
                match.OnMessage(new NetMessage(Msg.MatchGo, f.Id, 0, new FloatMessage(10)));
                match.OnMessage(new NetMessage(Msg.Score, f.Id, 0, new ScoreMessage(bot.ActorId, 4, 0)));
                var shot = new NetMessage(Msg.Shot, f.Id, 2, new ShotMessage(Vector3.Zero, Vector3.Up, Vector3.Forward, 0, bot.ActorId, -1), human.ActorId, 1, 1);
                match.OnMessage(shot);
                Assert(bot.Runtime.DropPredictions.UnconfirmedCount == 1 && human.Runtime.DropPredictions.UnconfirmedCount == 0, "same-peer shot did not spill the victim's bank");
                var time = bot.Runtime.LastHitTime;
                match.OnMessage(shot);
                Assert(bot.Runtime.DropPredictions.UnconfirmedCount == 1 && bot.Runtime.LastHitTime == time, "echo repeated prediction");
                var stale = shot with { ActionId = 2, Payload = ((ShotMessage)shot.Payload) with { VictimLifeId = 2 } };
                match.OnMessage(stale);
                Assert(bot.Runtime.DropPredictions.UnconfirmedCount == 1, "old-life hit spilled a new life");
                var swing = new NetMessage(Msg.MeleeStart, f.Id, 2, new MeleeStartMessage(1, Vector3.Forward), human.ActorId, 1, 3);
                match.OnMessage(swing);
                var contact = new NetMessage(Msg.MeleeContact, f.Id, 2, new MeleeContactMessage(1, bot.ActorId, -1), human.ActorId, 1, 4);
                match.OnMessage(contact); match.OnMessage(contact);
                Assert(bot.IsRagdolled && bot.Runtime.DropPredictions.UnconfirmedCount == 4, "same-peer melee failed or spilled twice");
                var id = BodySync.DropTankId(human.Runtime.AllocationSlot, 899999);
                human.Runtime.DropPredictions.Add(id);
                match.OnMessage(new NetMessage(Msg.Score, f.Id, 0, new ScoreMessage(human.ActorId, 3, -1)));
                var dropped = new BodyState(id, Transform3D.Identity, Vector3.Zero, Vector3.Zero);
                match.OnMessage(new NetMessage(Msg.Drop, f.Id, 2, new DropMessage(0, new[] { dropped }), human.ActorId, 1, 1));
                Assert(human.Runtime.DropPredictions.Refused(new[] { id }).Count == 0,
                    "newer contact prediction suppressed an earlier drop confirmation");
                var reserve = human.Ammo!.Reserve;
                bot.Ammo!.Reserve = 0;
                match.OnMessage(new NetMessage(Msg.PickupTaken, f.Id, 0, new PickupMessage(0, bot.ActorId)));
                Assert(bot.Ammo.Reserve == match.World.Tuning.PickupAmount && human.Ammo.Reserve == reserve,
                    "pickup replenished the wrong actor's ammo");
                Assert(match.Session.LocalViews.Count == 0 && match.Session.OwnedActors.Count() == 2, "owned-actor adapter remained singular");
            }
            finally { match.Free(); client.Free(); }
        });
        runner.Run("participants/chain attribution cannot switch to another actor of the same peer", () =>
        {
            var plan = PlanTests.Fixture();
            var ledger = new TankLedger(plan, 2);
            var root = ledger.Detonate(2, new ExplodeMessage(BodySync.MapTankId(0), Vector3.Zero, Request: 10), 101, true);
            Assert(root.Bodies.Single().CauseActorId == 101, "canonical blast lost attribution");
            var child = new PunctureMessage(BodySync.MapTankId(1), Transform3D.Identity, Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Up, 1, Cause: 10);
            Assert(ledger.Puncture(2, child, 102, true).Reason == ClaimRefusal.Cause, "same peer borrowed another actor's chain");
            Assert(ledger.Puncture(2, child, 101, true).Reason == ClaimRefusal.None, "initiator could not continue chain");
        });
        runner.Run("participants/wire rejects zero identities and malformed controller configuration", () =>
        {
            var shot = new NetMessage(Msg.Shot, 1, 2, new ShotMessage(Vector3.Zero, Vector3.Up, Vector3.Forward, 0, 0, -1), 101, 1, 1);
            foreach (var malformed in new[] { shot with { ActorId = 0 }, shot with { LifeId = 0 }, shot with { ActionId = 0 } })
                TestRunner.Throws<InvalidDataException>(() => MessageCodec.Decode(MessageCodec.Encode(malformed, false), false));
            foreach (var config in new[] { new ControllerConfig(2, ControllerKind.Bot, 1), new ControllerConfig(1, ControllerKind.Bot, 3), ControllerConfig.Human })
                TestRunner.Throws<InvalidDataException>(() => MessageCodec.Decode(Request(Msg.AddActor, new AddActorMessage(1, ActorKind.Bot, "Bot", AppearanceRecord.FromPalette(0), config)), false));
        });
    }
}
