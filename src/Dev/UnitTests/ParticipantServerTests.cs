using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Dev.UnitTests.Fixtures;
using Propane.Modes;
using Propane.Net;
using Propane.Player;
using Propane.Tank;

namespace Propane.Dev.UnitTests;

[UnitSuite]
public static class ParticipantServerTests
{
    private static byte[] Request(Msg type, object payload) => MessageCodec.Encode(new NetMessage(type, 0, 0, payload), false);
    private static void Assert(bool condition, string reason) => TestRunner.Assert(condition, reason);
    private static void AssertFreshLobby(ServerFixture f)
    {
        f.Server.RosterFactory = () => new ParticipantRoster();
        f.Server.OnConnected(4);
        f.Receive(new NetWriter(Msg.Hello).Int(Protocol.Version).String(BuildInfo.GameVersion).String("test")
            .String("Fresh peer").Int(0).Bool(true), 4);
        Assert(f.Packets.Any(p => p.Peer == 4 && p.Message.Payload is LobbyListMessage list && list.Lobbies.Count == 0),
            "exhausted lobby remains advertised");
        f.Packets.Clear();
        f.Receive(new NetWriter(Msg.CreateLobby), 4);
        Assert(f.Packets.Any(p => p.Message.Payload is LobbyInfo lobby &&
            lobby.Members.Single().LifeId == 1 && lobby.Id != 1), "exhaustion prevented a fresh lobby");
    }
    public static void Register(TestRunner runner)
    {
        foreach (var counter in new[] { "life", "revision" })
        {
            runner.Run($"participants/{counter} exhaustion retires the lobby without aborting server ticks", () =>
            {
                using var f = new ServerFixture(rosterFactory: () => counter == "life"
                    ? new ParticipantRoster(firstLife: uint.MaxValue)
                    : new ParticipantRoster(firstRevision: uint.MaxValue - 2));
                Assert(counter == "life" ? f.Setup.Players.All(p => p.Player.LifeId == uint.MaxValue)
                    : f.Setup.RosterRevision == uint.MaxValue, "fixture did not reach the exhausted counter");
                f.Advance(11);
                f.Packets.Clear();
                var capture = f.Server.SendOverride!;
                f.Server.SendOverride = (peer, bytes, traffic) =>
                {
                    capture(peer, bytes, traffic);
                    if (MessageCodec.Decode(bytes, true).Payload is RejectedMessage) f.Server.OnDisconnected(peer);
                };
                f.Advance(20);
                f.Advance(20); // No repeated overflow on subsequent server frames.
                Assert(f.Packets.Count(p => p.Message.Payload is RejectedMessage r && r.Reason.Contains("Reconnect")) == 2,
                    "exhausted participants were not told to reconnect");
                AssertFreshLobby(f);
            });
        }
        runner.Run("participants/revision exhaustion during departure retires the lobby", () =>
        {
            using var f = new ServerFixture(rosterFactory: () => new ParticipantRoster(firstRevision: uint.MaxValue - 2));
            Assert(f.Setup.RosterRevision == uint.MaxValue, "fixture did not exhaust revision space");
            var capture = f.Server.SendOverride!;
            f.Server.SendOverride = (peer, bytes, traffic) =>
            {
                capture(peer, bytes, traffic);
                if (MessageCodec.Decode(bytes, true).Payload is RejectedMessage) f.Server.OnDisconnected(peer);
            };
            f.Packets.Clear();
            f.Server.OnDisconnected(2);
            f.Advance(100);
            Assert(f.Packets.Count(p => p.Peer == 3 && p.Message.Payload is RejectedMessage) == 1,
                "exhausted departure did not reject its survivor exactly once");
            AssertFreshLobby(f);
        });
        runner.Run("participants/rounds retain actor and allocation identities but advance lives", () =>
        {
            using var f = new ServerFixture();
            var first = f.Setup;
            f.Advance(11); f.Advance(20);
            f.Packets.Clear();
            f.Receive(new NetWriter(Msg.StartMatch).Snapshot(f.Tuning.Snapshot()));
            var next = f.Packets.Select(p => p.Message.Payload).OfType<MatchSetup>().First();
            Assert(next.Id != first.Id && next.RosterRevision > first.RosterRevision, "round/revision did not advance");
            Assert(next.Players.All(p => first.Players.Any(old => old.Player.Id == p.Player.Id &&
                old.Player.AllocationSlot == p.Player.AllocationSlot && p.Player.LifeId == old.Player.LifeId + 1)),
                "round respawn reused actor identity or retained the old life");
            foreach (var peer in next.Peers) f.Receive(new NetWriter(Msg.Loaded, next.Id), peer.PeerId);
            f.Advance(); f.Advance();
            var actor = next.Players.First(p => p.Player.OwnerPeerId == 2).Player;
            f.Reject(MessageCodec.Encode(new NetMessage(Msg.Shot, next.Id, 0,
                new ShotMessage(Vector3.Zero, Vector3.Up, Vector3.Forward, 0, 0, -1), actor.Id, actor.LifeId - 1, 1), false));
        });
        runner.Run("participants/creator can start with an owned bot actor", () =>
        {
            using var f = new ServerFixture(playing: false);
            f.Advance(50); f.Advance(); f.Advance(20); f.Advance(2);
            var human = f.Setup.Players.Single(p => p.Player.OwnerPeerId == 2).Player;
            f.Receive(Request(Msg.RemoveActor, new RemoveActorMessage(1, human.Id, f.Revision())));
            f.Packets.Clear();
            f.Receive(new NetWriter(Msg.StartMatch).Snapshot(f.Tuning.Snapshot()));
            Assert(f.Packets.Count == 1 && f.Packets[0].Message.Type == Msg.Notice, "creator without an actor started a match");
            f.Receive(Request(Msg.AddActor, new AddActorMessage(2, ActorKind.Bot, "Creator bot", human.Appearance, ControllerConfig.Bot, f.Revision())));
            f.Packets.Clear();
            f.Receive(new NetWriter(Msg.StartMatch).Snapshot(f.Tuning.Snapshot()));
            var setup = f.Packets.Select(p => p.Message.Payload).OfType<MatchSetup>().First();
            Assert(setup.Id == f.Id + 1 && setup.Players.Count == 2 && setup.Players.Single(p => p.Player.OwnerPeerId == 2).Player.Kind == ActorKind.Bot,
                "actor kind incorrectly restricted creator ownership");
            var decoded = (MatchSetup)MessageCodec.Decode(MessageCodec.Encode(new NetMessage(Msg.MatchStart, setup.Id, 0, setup), true), true).Payload;
            Assert(decoded.HostId == 2, "wire decoder conflated authority peer with human actor kind");
        });
        runner.Run("participants/server owns revisions and idempotent roster requests", () =>
        {
            using var f = new ServerFixture(playing: false);
            f.Advance(50); f.Advance(); f.Advance(20); f.Advance(2); // Return to Waiting.
            var revision = f.Packets.Select(p => p.Message.Payload).OfType<LobbyInfo>().Last().RosterRevision;
            f.Packets.Clear();
            var add = Request(Msg.AddActor, new AddActorMessage(1, ActorKind.Bot, "Embedded", AppearanceRecord.FromPalette(0), ControllerConfig.Bot, f.Revision()));
            f.Receive(add);
            var roster = f.Packets.Select(p => p.Message.Payload).OfType<RosterStateMessage>().First();
            var bot = roster.Actors.Single(a => a.Kind == ActorKind.Bot);
            Assert(roster.Revision == revision + 1 && bot.OwnerPeerId == 2 && bot.Id != bot.OwnerPeerId, "actor ownership or revision incorrect");
            f.Packets.Clear(); f.Receive(add);
            Assert(f.Packets.Count == 1 && f.Packets[0].Message.Payload is ActorRequestResultMessage { Accepted: true } duplicate && duplicate.Revision == revision + 1, "duplicate request mutated roster");
            f.Packets.Clear();
            f.Receive(Request(Msg.UpdateActor, new UpdateActorMessage(1, bot.Id, "Stolen", bot.Appearance, ControllerConfig.Bot, f.Revision())), 3);
            Assert(f.Packets.Single().Message.Payload is ActorRequestResultMessage { Accepted: false } spoof && spoof.Revision == revision + 1, "spoofed update accepted");
            f.Packets.Clear();
            f.Receive(Request(Msg.UpdateActor, new UpdateActorMessage(2, bot.Id, "Renamed", bot.Appearance, ControllerConfig.Bot, f.Revision())));
            Assert(f.Packets.Any(p => p.Message.Payload is RosterStateMessage r && r.Revision == revision + 2 && r.Actors.Any(a => a.Name == "Renamed")), "update not broadcast");
            f.Packets.Clear(); f.Receive(Request(Msg.RemoveActor, new RemoveActorMessage(3, bot.Id, f.Revision())));
            Assert(f.Packets.Any(p => p.Message.Payload is RosterStateMessage r && r.Revision == revision + 3 && r.Actors.Count == 2), "remove not broadcast");
            f.Packets.Clear(); f.Receive(Request(Msg.AddActor, new AddActorMessage(4, ActorKind.Bot, "Next", bot.Appearance, ControllerConfig.Bot, f.Revision())));
            var next = f.Packets.Select(p => p.Message.Payload).OfType<RosterStateMessage>().First().Actors.Single(a => a.Kind == ActorKind.Bot);
            Assert(next.Id > bot.Id && next.AllocationSlot > bot.AllocationSlot, "removed actor or allocation reused");
            f.Packets.Clear();
            f.Receive(new NetWriter(Msg.StartMatch).Snapshot(f.Tuning.Snapshot()));
            var setup = f.Packets.Select(p => p.Message.Payload).OfType<MatchSetup>().First();
            var mapping = setup.Players.Single(p => p.Player.Id == next.Id);
            Assert(mapping.Spawn != mapping.Player.AllocationSlot, "spawn order remained allocation identity");
            foreach (var peer in new[] { 2, 3 }) f.Receive(new NetWriter(Msg.Loaded, setup.Id), peer);
            f.Advance(); f.Advance(); f.Packets.Clear();
            var drop = new BodyState(BodyIds.DropTankId(next.AllocationSlot, 0), Transform3D.Identity, Vector3.Zero, Vector3.Zero);
            f.Receive(MessageCodec.Encode(ModeWireFixtures.Spill(setup.Id, next.Id, 1, 1, Modes.SpillReason.Hit, new[] { drop }), false));
            Assert(f.Packets.Any(p => ModeWireFixtures.IsScore(p.Message, next.Id, 3)),
                "ledger validated the spawn slot instead of the allocation slot");
        });
        runner.Run("participants/frozen roster counts actors and validates ownership and lives", () =>
        {
            using var f = new ServerFixture(bots: 1);
            Assert(f.Setup.Players.Count == 3 && f.Setup.Peers.Count == 2, "actor count conflated with readiness peers");
            var bot = f.Setup.Players.Single(p => p.Player.Kind == ActorKind.Bot).Player;
            var human = f.Setup.Players.Single(p => p.Player.OwnerPeerId == 2 && p.Player.Kind == ActorKind.Human).Player;
            var explosion = new ExplodeMessage(BodyIds.MapTankId(0), Vector3.Zero);
            byte[] Packet(int actor, uint life, uint action) => MessageCodec.Encode(new NetMessage(Msg.Explode, f.Id, 0, explosion, actor, life, action), false);
            f.Reject(Packet(human.Id, 1, 1), 3);
            f.Reject(Packet(bot.Id, 2, 1));
            f.Receive(Packet(bot.Id, 1, 1));
            Assert(f.Packets.Any(p => ModeWireFixtures.IsScore(p.Message, bot.Id, 5)), "bot gain credited to human");
            Assert(!f.Packets.Any(p => ModeWireFixtures.IsScore(p.Message, human.Id)), "last local actor stole score");
            f.Reject(Packet(bot.Id, 1, 1));
            f.Packets.Clear();
            f.Receive(Request(Msg.AddActor, new AddActorMessage(2, ActorKind.Bot, "Late", bot.Appearance, ControllerConfig.Bot, f.Revision())));
            Assert(f.Packets.Single().Message.Payload is ActorRequestResultMessage { Accepted: false }, "match roster was mutable");
            f.Packets.Clear();
            f.Receive(new NetWriter(Msg.SetProfile).String("Frozen profile").Appearance(bot.Appearance));
            Assert(f.Packets.Count == 0, "legacy profile request bypassed the frozen roster");
        });
        runner.Run("participants/departure removes every actor and retains departed banks", () =>
        {
            using var f = new ServerFixture(bots: 1);
            var departed = f.Setup.Players.Where(a => a.Player.OwnerPeerId == 2).Select(a => a.Player.Id).ToArray();
            f.Server.OnDisconnected(2);
            Assert(f.Packets.Any(p => p.Message.Payload is AuthorityMessage a && a.Bodies.All(b => b.Owner == 3 && b.Body.Epoch == 2)), "body authority was not transferred with a fresh epoch");
            Assert(f.Packets.Any(p => p.Message.Payload is RosterStateMessage r && r.Actors.All(a => a.OwnerPeerId != 2)), "embedded actor survived departure");
            f.Packets.Clear(); f.Advance(11);
            var standings = f.Packets.Select(p => p.Message.Payload).OfType<IReadOnlyList<Standing>>().Single();
            Assert(standings.Count == 3 && departed.All(id => standings.Any(s => s.Id == id && !s.Present && s.Score == 4)), "departed standings erased bank");
            f.Reject(new NetWriter(Msg.TuningSet, f.Id).String("BlastRadius").Scalar(5f).ToArray(), 3);
        });
        foreach (var failSend in new[] { false, true })
        {
            runner.Run($"participants/readiness {(failSend ? "stops after send failure" : "waits for the first complete frame")}", () =>
            {
                using var f = new ServerFixture();
                var network = new LoopbackNetwork();
                var server = network.Create();
                var transport = network.Create();
                var endpoint = new PacketEndpoint("", 1);
                server.Listen(endpoint, 1);
                var client = new NetClient(transport, ModeRegistry.Codec, f.Clock);
                Match? match = null;
                try
                {
                    client._Ready();
                    client.Connect(endpoint.Address, endpoint.Port, "Builder", 0);
                    for (var i = 0; i < 4; i++) { client._Process(0); server.Poll(); }
                    client.Handle(MessageCodec.Encode(new NetMessage(Msg.Welcome, 0, 0, new WelcomeMessage(2, null)), true));
                    var loaded = 0;
                    server.Packet += (_, _, bytes) => { if ((Msg)bytes.Span[0] == Msg.Loaded) loaded++; };
                    match = new Match { Net = client, Setup = f.Setup, CreateLocalView = false };
                    runner.AddChild(match);
                    server.Poll();
                    Assert(loaded == 0, "construction prematurely ended native loading grace");
                    match._Process(0); server.Poll();
                    Assert(loaded == 0, "readiness preceded initial physics and shader work");
                    if (failSend)
                    {
                        transport.BlockSends = true;
                        client.StatusChanged += (status, _) => { if (status == NetClient.Status.Offline) match.Session.Dispose(); };
                        match._Process(0);
                        Assert(match.World.IsAlive && client.State == NetClient.Status.Offline,
                            "failed readiness retired inside its render callback");
                        client._Process(0);
                        Assert(!match.World.IsAlive && client.State == NetClient.Status.Offline && loaded == 0,
                            "failed readiness did not retire the match");
                        return;
                    }
                    match._Process(0); server.Poll();
                    Assert(loaded == 1, "completed first frame never reported readiness");
                    match._Process(0); server.Poll();
                    Assert(loaded == 1, "readiness was reported more than once");
                }
                finally { match?.Free(); client._ExitTree(); client.Free(); server.Close(); }
            });
        }

        runner.Run("participants/same-peer shot and melee use one event path and dedupe echoes", () =>
        {
            using var f = new ServerFixture(bots: 1);
            var client = new NetClient(PacketTransports.CreateClient(), ModeRegistry.Codec);
            client.Handle(MessageCodec.Encode(new NetMessage(Msg.Welcome, 0, 0, new WelcomeMessage(2, null)), true));
            var match = new Match { Net = client, Setup = f.Setup, CreateLocalView = false };
            runner.AddChild(match);
            try
            {
                var human = match.Player;
                var bot = match.Session.OwnedActors.Single(a => a.Character != human).Character;
                match.OnMessage(new NetMessage(Msg.MatchGo, f.Id, 0, new FloatMessage(10)));
                match.OnMessage(ModeWireFixtures.Score(f.Id, bot.ActorId, 4, 0));
                var shot = new NetMessage(Msg.Shot, f.Id, 2, new ShotMessage(Vector3.Zero, Vector3.Up, Vector3.Forward, 0, bot.ActorId, -1), human.ActorId, 1, 1);
                match.OnMessage(shot);
                Assert(ClientActorAccess.For(bot).DropPredictions.UnconfirmedCount == 1 && ClientActorAccess.For(human).DropPredictions.UnconfirmedCount == 0, "same-peer shot did not spill the victim's bank");
                var time = bot.Runtime.LastHitTime;
                match.OnMessage(shot);
                Assert(ClientActorAccess.For(bot).DropPredictions.UnconfirmedCount == 1 && bot.Runtime.LastHitTime == time, "echo repeated prediction");
                var stale = shot with { ActionId = 2, Payload = ((ShotMessage)shot.Payload) with { VictimLifeId = 2 } };
                match.OnMessage(stale);
                Assert(ClientActorAccess.For(bot).DropPredictions.UnconfirmedCount == 1, "old-life hit spilled a new life");
                var swing = new NetMessage(Msg.MeleeStart, f.Id, 2, new MeleeStartMessage(1, Vector3.Forward), human.ActorId, 1, 3);
                match.OnMessage(swing);
                var contact = new NetMessage(Msg.MeleeContact, f.Id, 2, new MeleeContactMessage(1, bot.ActorId, -1), human.ActorId, 1, 4);
                match.OnMessage(contact); match.OnMessage(contact);
                Assert(bot.IsRagdolled && ClientActorAccess.For(bot).DropPredictions.UnconfirmedCount == 4, "same-peer melee failed or spilled twice");
                var id = BodyIds.DropTankId(ClientActorAccess.For(human).AllocationSlot, 899999);
                ClientActorAccess.For(human).DropPredictions.Add(id);
                match.OnMessage(ModeWireFixtures.Score(f.Id, human.ActorId, 3, -1));
                var dropped = new BodyState(id, Transform3D.Identity, Vector3.Zero, Vector3.Zero);
                match.OnMessage(ModeWireFixtures.Accepted(f.Id, human.ActorId, 1, Modes.SpillReason.Hit, new[] { dropped }));
                Assert(ClientActorAccess.For(human).DropPredictions.Refused(new[] { id }).Count == 0,
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
            var root = ledger.Detonate(2, new ExplodeMessage(BodyIds.MapTankId(0), Vector3.Zero, Request: 10), 101, true);
            Assert(root.Bodies.Single().CauseActorId == 101, "canonical blast lost attribution");
            var child = new PunctureMessage(BodyIds.MapTankId(1), Transform3D.Identity, Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Up, 1, Cause: 10);
            Assert(ledger.Puncture(2, child, 102, true).Reason == ClaimRefusal.Cause, "same peer borrowed another actor's chain");
            var accepted = ledger.Puncture(2, child, 101, true);
            Assert(accepted.Reason == ClaimRefusal.None && accepted.Bodies.Single().CauseActorId == 101,
                "initiator could not continue chain or vent attribution was lost");
            Assert(ledger.Transfer(2, 3).Single(b => b.Body.Id == child.Id).CauseActorId == 101,
                "body transfer changed immutable initiating actor");
        });
        runner.Run("participants/delayed view stages its actor before buffered blast delivery", () =>
        {
            using var fixture = new ServerFixture();
            var client = new NetClient(PacketTransports.CreateClient(), ModeRegistry.Codec);
            client.Handle(MessageCodec.Encode(new NetMessage(Msg.Welcome, 0, 0, new WelcomeMessage(2, null)), true));
            client.Handle(MessageCodec.Encode(new NetMessage(Msg.LobbyState, 0, 0, new LobbyInfo(1, 2, 10,
                LobbyPhase.Loading, fixture.Setup.Players.Select(p => p.Player).ToArray())), true));
            client.Handle(MessageCodec.Encode(new NetMessage(Msg.MatchStart, fixture.Id, 0, fixture.Setup), true));
            var own = fixture.Setup.Players.Single(p => p.Player.OwnerPeerId == 2).Player;
            foreach (var message in new[]
            {
                new NetMessage(Msg.MatchGo, fixture.Id, 0, new FloatMessage(10)),
                ModeWireFixtures.Score(fixture.Id, own.Id, 8, 0),
                new NetMessage(Msg.Authority, fixture.Id, 0, new AuthorityMessage(3, 1, ClaimRefusal.None,
                    new[] { new BodyAuthority(new BodyState(BodyIds.MapTankId(0), Transform3D.Identity, Vector3.Zero, Vector3.Zero, 2),
                        3, BodyState.State.Exploded, true, null, new Vector3(400, .3f, 0), 2) })),
            })
            {
                client.Handle(MessageCodec.Encode(message, true));
            }

            Match? delayed = null;
            var throws = 0;
            delayed = new Match
            {
                Net = client,
                Setup = fixture.Setup,
                CreateLocalView = false,
                BeforeInboxAttach = () =>
            {
                delayed!.Player.Teleport(new Vector3(404, .05f, 0), 0);
                delayed.Player.Ragdolled += (_, _, _) => throws++;
            }
            };
            try
            {
                runner.AddChild(delayed);
                Assert(throws == 1 && delayed.Player.IsRagdolled && client.InboxPressure.Packets == 3,
                    $"buffered blast proof: throws={throws} ragdoll={delayed.Player.IsRagdolled} inbox={client.InboxPressure.Packets} position={delayed.Player.GlobalPosition} playing={delayed.IsPlaying} receivers={delayed.World.BlastReceivers.Count}");
            }
            finally { delayed.Free(); client.Free(); }
        });
        runner.Run("participants/capacity counts actors and only the waiting creator can select it", () =>
        {
            using var f = new ServerFixture(playing: false);
            f.Advance(50); f.Advance(); f.Advance(20); f.Advance(2);
            f.Packets.Clear();
            f.Receive(Request(Msg.SetCapacity, new IntMessage(16)), 3);
            Assert(f.Packets.Count == 0, "noncreator changed capacity");
            f.Receive(Request(Msg.SetCapacity, new IntMessage(16)));
            Assert(f.Packets.Any(p => p.Message.Payload is LobbyInfo { Capacity: 16 }), "creator cannot select tested ceiling");
            for (uint request = 1; request <= 14; request++)
                f.Receive(Request(Msg.AddActor, new AddActorMessage(request, ActorKind.Bot, $"Bot {request}", AppearanceRecord.FromPalette(0), ControllerConfig.Bot, f.Revision())));
            var full = f.Packets.Select(p => p.Message.Payload).OfType<RosterStateMessage>().Last();
            Assert(full.Actors.Count == 16, "capacity remained peer- or palette-derived");
            f.Packets.Clear();
            f.Receive(Request(Msg.AddActor, new AddActorMessage(15, ActorKind.Bot, "Excess", AppearanceRecord.FromPalette(0), ControllerConfig.Bot, f.Revision())));
            Assert(f.Packets.Single().Message.Payload is ActorRequestResultMessage { Accepted: false }, "actor exceeded selected capacity");
            f.Packets.Clear(); f.Receive(Request(Msg.SetCapacity, new IntMessage(2)));
            Assert(f.Packets.Count == 0, "capacity shrank below actual roster");
            foreach (var invalid in new[] { 1, 17 }) f.Reject(Request(Msg.SetCapacity, new IntMessage(invalid)));
        });
        runner.Run("participants/client ignores stale roster revisions and foreign lobbies", () =>
        {
            var client = new NetClient(PacketTransports.CreateClient(), ModeRegistry.Codec);
            try
            {
                foreach (var type in new[] { Msg.Welcome, Msg.LobbyState })
                    client.Handle(MessageCodec.Encode(ProtocolTests.Fixtures().Single(f => f.Type == type), true));
                var lobby = client.Lobby!;
                var updated = lobby.Members.Select(p => p with { Name = "Updated" }).ToArray();
                void Feed(uint revision, int lobbyId, IReadOnlyList<PlayerProfile> actors) =>
                    client.Handle(MessageCodec.Encode(new NetMessage(Msg.RosterState, 0, 0, new RosterStateMessage(lobbyId, revision, actors)), true));
                Feed(3, lobby.Id, updated);
                Feed(2, lobby.Id, lobby.Members);
                Feed(4, lobby.Id + 1, lobby.Members);
                client.Handle(MessageCodec.Encode(new NetMessage(Msg.LobbyState, 0, 0, lobby with { RosterRevision = 2 }), true));
                Assert(client.RosterRevision == 3 && client.Roster.All(a => a.Name == "Updated"), "stale state overwrote the current roster");
            }
            finally { client.Free(); }
        });
        runner.Run("participants/delayed chain keeps the initiating actor after other actor actions", () =>
        {
            var tank = PropaneTank.Create(runner.World);
            runner.World.Root.AddChild(tank);
            try
            {
                tank.TakeShot(tank.ToGlobal(PropaneTank.LocalCenter), Vector3.Up, Vector3.Forward, 101);
                Action? finish = null;
                tank.FuseScheduleOverride = (_, callback) => finish = callback;
                tank.ChainHit(Vector3.Zero, 0, 44, 102);
                var other = PropaneTank.Create(runner.World);
                runner.World.Root.AddChild(other);
                try { other.TakeShot(other.ToGlobal(PropaneTank.LocalCenter), Vector3.Up, Vector3.Forward, 103); }
                finally { other.Free(); }
                Assert(finish != null && tank.CauseActorId == 102 && tank.ChainCause == 44, "fuse attribution was inferred from the last actor");
                var scorer = 0; tank.Detonated += source => scorer = source.CauseActorId;
                finish!();
                Assert(scorer == 102, "delayed fuse changed its initiating actor");
            }
            finally { if (GodotObject.IsInstanceValid(tank)) tank.Free(); }
        });
        runner.Run("participants/wire rejects zero identities and malformed controller configuration", () =>
        {
            var shot = new NetMessage(Msg.Shot, 1, 2, new ShotMessage(Vector3.Zero, Vector3.Up, Vector3.Forward, 0, 0, -1), 101, 1, 1);
            foreach (var malformed in new[] { shot with { ActorId = 0 }, shot with { LifeId = 0 }, shot with { ActionId = 0 } })
                TestRunner.Throws<InvalidDataException>(() => MessageCodec.Decode(MessageCodec.Encode(malformed, false), false));
            foreach (var config in new[] { new ControllerConfig(2, ControllerKind.Bot, 1), new ControllerConfig(1, ControllerKind.Bot, 3), ControllerConfig.Human })
                TestRunner.Throws<InvalidDataException>(() => MessageCodec.Decode(Request(Msg.AddActor, new AddActorMessage(1, ActorKind.Bot, "Bot", AppearanceRecord.FromPalette(0), config, 1)), false));
        });
    }
}
