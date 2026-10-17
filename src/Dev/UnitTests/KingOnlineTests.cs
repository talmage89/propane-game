using System;
using System.IO;
using System.Linq;
using Godot;
using Propane.Bots;
using Propane.Core;
using Propane.Dev.UnitTests.Fixtures;
using Propane.Fx;
using Propane.Modes;
using Propane.Net;
using Propane.Player;

namespace Propane.Dev.UnitTests;

/// <summary>King's real server path: typed wire, lobby authority, sampled actors and registered world piles.</summary>
[UnitSuite]
public static class KingOnlineTests
{
    private static readonly KingState Zone = new(3, new ModeSpot(1, 0, 2), new ModeSpot(12, 0, 25), 6, 2, new[] { 2 },
        4, 2, 180, 3, new[] { new KingMinted(2, 7), new KingMinted(3, 1) }, 3);

    private static byte[] Packet(Msg type, ushort kind, object payload, uint sequence = 100, int actor = 2, uint round = 1) =>
        MessageCodec.Encode(new NetMessage(type, 7, 0, new ModeEnvelope(round, 1, ModeRegistry.KingId, 1,
            type == Msg.ModeEvent ? actor : 0, type == Msg.ModeEvent ? 1u : 0, sequence, kind, payload)), type != Msg.ModeEvent);

    private static object Decode(byte[] bytes, bool server = true) => ((ModeEnvelope)MessageCodec.Decode(bytes, server).Payload).Payload;
    private static KingState Latest(ServerFixture f) => f.Packets.Select(p => p.Message.Payload).OfType<ModeEnvelope>()
        .Select(e => e.Payload).OfType<KingState>().Last();

    private static void Pose(ServerFixture f, int peer, Vector3 at, uint sequence, PlayerNetState.Mode mode = PlayerNetState.Mode.Active)
    {
        var state = new PlayerNetState { Position = at, PelvisPosition = at, State = mode };
        f.Receive(MessageCodec.Encode(new NetMessage(Msg.PlayerState, f.Id, 0, new PlayerStateMessage(0, state), f.ActorId(peer), 1, sequence), false), peer);
    }

    private static void Place(ServerFixture f, KingPileWanted wanted, int peer = 2, uint sequence = 100, uint round = 1, int count = -1)
    {
        var poses = Enumerable.Range(0, count < 0 ? wanted.Count : count)
            .Select(i => new Transform3D(Basis.Identity, wanted.Spot + Vector3.Right * (i * 0.1f))).ToArray();
        var envelope = new ModeEnvelope(round, 1, ModeRegistry.KingId, 1, f.ActorId(peer), 1, sequence, ModeSchemas.KingKinds.PilePlaced,
            new KingPilePlaced(wanted.Zone, poses));
        f.Receive(MessageCodec.Encode(new NetMessage(Msg.ModeEvent, f.Id, 0, envelope), false), peer);
    }

    public static void Register(TestRunner runner)
    {
        runner.Run("king online/every kind round-trips, including empty placement and maximum snapshot", () =>
        {
            var body = new BodyState(BodySync.MapTankId(25), Transform3D.Identity, Vector3.Zero, Vector3.Zero);
            var spill = new TankBankSpill(SpillReason.Hit, new[] { body with { Id = BodySync.DropTankId(0, 0) } });
            var packets = new[]
            {
                Packet(Msg.ModeSnapshot, ModeSchemas.KingKinds.Zone, Zone),
                Packet(Msg.ModeSnapshot, ModeSchemas.KingKinds.Zone, Zone with { Holder = 0, Occupants = new[] { 2, 3 } }),
                Packet(Msg.ModeSnapshot, ModeSchemas.KingKinds.Zone, Zone with { Holder = 0, Occupants = Array.Empty<int>(), Minted = Array.Empty<KingMinted>() }),
                Packet(Msg.ModeSnapshot, ModeSchemas.KingKinds.Zone, Zone with { Holder = 0,
                    Occupants = Enumerable.Range(1, Protocol.MaxWireActors).ToArray(),
                    Minted = Enumerable.Range(1, Protocol.MaxWireActors).Select(i => new KingMinted(i, i)).ToArray() }),
                Packet(Msg.ModeCommand, ModeSchemas.KingKinds.PileWanted, new KingPileWanted(1, Vector3.One, 6, 9, 1)),
                Packet(Msg.ModeCommand, ModeSchemas.KingKinds.PileSpawned, new KingPileSpawned(1, 2, new[] { body })),
                Packet(Msg.ModeEvent, ModeSchemas.KingKinds.PilePlaced, new KingPilePlaced(1, new[] { Transform3D.Identity })),
                Packet(Msg.ModeEvent, ModeSchemas.KingKinds.PilePlaced, new KingPilePlaced(1, Array.Empty<Transform3D>())),
                Packet(Msg.ModeEvent, ModeSchemas.KingKinds.Spill, spill),
                Packet(Msg.ModeCommand, ModeSchemas.KingKinds.SpillAccepted, spill),
                Packet(Msg.ModeCommand, ModeSchemas.KingKinds.SpillRefused, new TankBankRefused(new[] { spill.Tanks[0].Id })),
                Packet(Msg.ModeCommand, ModeSchemas.KingKinds.Score, new TankBankScore(5, 1)),
                Packet(Msg.ModeSnapshot, ModeSchemas.KingKinds.Banks, new TankBankBanks(new[] { (2, 5, true) })),
            };
            foreach (var bytes in packets)
            {
                var server = bytes[0] != (byte)Msg.ModeEvent;
                TestRunner.Assert(MessageCodec.Encode(MessageCodec.Decode(bytes, server), server).SequenceEqual(bytes), "King kind changed on the wire");
                for (var length = 1; length < bytes.Length; length++)
                    TestRunner.Throws<Exception>(() => MessageCodec.Decode(bytes[..length], server));
                TestRunner.Throws<InvalidDataException>(() => MessageCodec.Decode(bytes.Append((byte)0).ToArray(), server));
            }
            TestRunner.Assert(((KingState)Decode(packets[0])).Matches(Zone), "snapshot lost zone, timing or mint state");
        });
        runner.Run("king online/malformed snapshot and pile facts never reach a handler", () =>
        {
            foreach (var bad in new[]
            {
                Zone with { Holder = 0 }, Zone with { Occupants = new[] { 2, 2 } }, Zone with { Radius = double.NaN },
                Zone with { Radius = 0 }, Zone with { NextMint = -1 }, Zone with { MintSeconds = 0 }, Zone with { MoveAt = double.PositiveInfinity },
                Zone with { WarningSeconds = -1 }, Zone with { Elapsed = -1 }, Zone with { Spot = new ModeSpot(double.NaN, 0, 0) },
                Zone with { Minted = new[] { new KingMinted(2, -1) } }, Zone with { Minted = new[] { new KingMinted(2, 1), new KingMinted(2, 2) } },
            })
            {
                TestRunner.Throws<InvalidDataException>(() => Decode(Packet(Msg.ModeSnapshot, ModeSchemas.KingKinds.Zone, bad)));
            }

            var body = new BodyState(BodySync.MapTankId(3), Transform3D.Identity, Vector3.Zero, Vector3.Zero);
            foreach (var bodies in new[] { new[] { body, body }, new[] { body with { Id = BodySync.DropTankId(0, 0) } }, Array.Empty<BodyState>() })
                TestRunner.Throws<InvalidDataException>(() => Decode(Packet(Msg.ModeCommand, ModeSchemas.KingKinds.PileSpawned, new KingPileSpawned(1, 2, bodies))));
            TestRunner.Throws<InvalidDataException>(() => Decode(Packet(Msg.ModeCommand, ModeSchemas.KingKinds.PileWanted, new KingPileWanted(1, Vector3.Zero, 6, float.NaN, 1))));
        });
        runner.Run("king online/lobby settings validate steps and only the waiting creator changes them", () =>
        {
            foreach (var pace in new[] { 0.5f, 2f, 10f })
            {
                var settings = new ModeSettings(ModeRegistry.KingId, pace);
                var bytes = MessageCodec.Encode(new NetMessage(Msg.SetMode, 0, 0, settings), false);
                TestRunner.Assert(MessageCodec.Decode(bytes, false).Payload.Equals(settings), "valid lobby pace changed");
                using var f = new ServerFixture(modeSettings: settings);
                using var tuning = Tuning.FromSnapshot(f.Setup.Tuning);
                TestRunner.Assert(f.Setup.ModeId == ModeRegistry.KingId && tuning.KingMintSeconds == pace &&
                    PlanCodec.Decode(f.Setup.Plan).AmmoSpots.Count == 0, "lobby settings did not reach shared match setup");
                f.Packets.Clear(); f.Receive(bytes);
                TestRunner.Assert(f.Packets.Count == 0, "playing creator changed lobby mode");
            }
            foreach (var pace in new[] { 0f, 0.75f, 10.5f, float.NaN, float.PositiveInfinity })
                TestRunner.Throws<InvalidDataException>(() => MessageCodec.Decode(new NetWriter(Msg.SetMode).Byte(ModeRegistry.KingId).Float(pace).Int(1).ToArray(), false));
            foreach (var mode in new[] { ModeRegistry.SandboxId, ModeRegistry.RingOutId, ModeRegistry.ShortFuseId, ModeRegistry.DissolveRunId, (byte)99 })
                TestRunner.Throws<InvalidDataException>(() => MessageCodec.Decode(new NetWriter(Msg.SetMode).Byte(mode).Float(2).Int(1).ToArray(), false));
            using var server = new LobbyServer();
            var sent = new System.Collections.Generic.List<(int Peer, NetMessage Message)>();
            server.SendOverride = (peer, bytes, _) => sent.Add((peer, MessageCodec.Decode(bytes, true)));
            foreach (var peer in new[] { 2, 3 })
            {
                server.OnConnected(peer);
                server.OnReceived(peer, new NetWriter(Msg.Hello).Int(Protocol.Version).String(BuildInfo.GameVersion).String("test").String($"P{peer}").Int(0).Bool(true).ToArray());
            }
            server.OnReceived(2, new NetWriter(Msg.CreateLobby).ToArray());
            server.OnReceived(3, new NetWriter(Msg.JoinLobby).Int(1).ToArray());
            TestRunner.Assert(sent.Select(p => p.Message.Payload).OfType<LobbyInfo>().Last().Mode == ModeSettings.Default, "Tank Bank no longer default");
            sent.Clear();
            var choose = new NetWriter(Msg.SetMode).Byte(ModeRegistry.KingId).Float(1.5f).Int(4).ToArray();
            server.OnReceived(3, choose);
            TestRunner.Assert(sent.Count == 0, "guest changed mode");
            server.OnReceived(2, choose);
            TestRunner.Assert(sent.Where(p => p.Message.Type == Msg.LobbyState).Count() == 2 &&
                sent.All(p => p.Message.Payload is not LobbyInfo lobby || lobby.Mode == new ModeSettings(ModeRegistry.KingId, 1.5f, 4)), "peers disagree on creator choice");
            server.Free();
        });
        runner.Run("king online/server samples holds, contests, down bodies, stale poses and departures", () =>
        {
            using var f = new ServerFixture(mode: ModeRegistry.King, length: 100);
            f.Advance(); var state = Latest(f); var spot = KingZone.Point(state.Spot);
            Pose(f, 2, spot, 1); f.Advance(.1); state = Latest(f);
            TestRunner.Assert(state.Holder == f.ActorId(2), "reported feet did not hold the zone");
            f.Advance(2); f.AssertScore(2, 5, 1);
            Pose(f, 3, spot, 1); f.Advance(.1);
            TestRunner.Assert(Latest(f).Contested && Latest(f).Holder == 0, "two occupants minted during contest");
            f.Packets.Clear(); f.Advance(3);
            TestRunner.Assert(!f.Packets.Any(p => p.Message.Payload is ModeEnvelope { Payload: TankBankScore }), "contest minted tanks");
            Pose(f, 3, spot, 2, PlayerNetState.Mode.Ragdoll); f.Advance(.1);
            TestRunner.Assert(Latest(f).Holder == f.ActorId(2), "down body occupied zone");
            Pose(f, 3, spot, 1); f.Advance(.1); // Older pose cannot resurrect an occupant.
            TestRunner.Assert(Latest(f).Holder == f.ActorId(2), "stale pose replaced a down sample");
            f.Advance(2); f.AssertScore(2, 6, 1);
            f.Receive(new NetWriter(Msg.LeaveLobby));
            TestRunner.Assert(Latest(f).Holder == 0 && Latest(f).MintedBy(f.ActorId(2)) == 2, "departing holder was retained or lost its minted audit");
            f.Packets.Clear(); f.Advance(2);
            TestRunner.Assert(!f.Packets.Any(p => p.Message.Payload is ModeEnvelope { Payload: TankBankScore }), "departed actor still minted");
            f.Advance(100);
            var standings = f.Packets.Select(p => p.Message.Payload).OfType<System.Collections.Generic.IReadOnlyList<Standing>>().Last();
            TestRunner.Assert(standings.Single(s => s.Id == f.ActorId(2)) is { Score: 6, Present: false } && standings.Sum(s => s.Score) == 10,
                "final bank lost a departed actor's minted tanks");
        });
        runner.Run("king online/moves reset holding and spawn one registered world pile through its authority", () =>
        {
            using var f = new ServerFixture(mode: ModeRegistry.King, length: 100);
            f.Advance(); var before = Latest(f);
            var away = (KingZone.Point(before.Spot) - KingZone.Point(before.Next)).Normalized();
            Pose(f, 2, KingZone.Point(before.Spot) + away * (float)before.Radius * .7f, 1); f.Advance(.1);
            f.Advance(42); TestRunner.Assert(Latest(f).At(42.1).Warning, "move warning absent");
            f.Packets.Clear(); f.Advance(3);
            var after = Latest(f);
            TestRunner.Assert(after.Zone == 1 && after.Spot == before.Next && after.Holder == 0, "seeded move failed to reset old holder");
            var wanted = f.Packets.Where(p => p.Peer == 2).Select(p => p.Message.Payload).OfType<ModeEnvelope>().Select(e => e.Payload).OfType<KingPileWanted>().Single();
            Place(f, wanted, peer: 3); Place(f, wanted, round: 2, sequence: 101); Place(f, wanted, count: 1, sequence: 102);
            TestRunner.Assert(!f.Packets.Any(p => p.Message.Payload is ModeEnvelope { Payload: KingPileSpawned }), "wrong authority, round or count created a pile");
            Place(f, wanted, sequence: 103);
            var spawned = f.Packets.Select(p => p.Message.Payload).OfType<ModeEnvelope>().Select(e => e.Payload).OfType<KingPileSpawned>().First();
            var plan = PlanCodec.Decode(f.Setup.Plan);
            TestRunner.Assert(spawned.Tanks.Count == wanted.Count && spawned.Tanks[0].Id == BodySync.MapTankId(plan.Tanks.Count) &&
                spawned.Owner == 2 && spawned.Tanks.All(t => t.Epoch == 1 && t.Linear == Vector3.Zero), "pile ids or authority disagree");
            f.Packets.Clear(); Place(f, wanted, sequence: 104);
            TestRunner.Assert(!f.Packets.Any(p => p.Message.Payload is ModeEnvelope { Payload: KingPileSpawned }), "pile duplicated");
            var body = spawned.Tanks[0];
            f.Receive(MessageCodec.Encode(new NetMessage(Msg.Puncture, f.Id, 0, new PunctureMessage(body.Id, body.Transform, Vector3.Zero, Vector3.Zero,
                Vector3.Zero, Vector3.Up, 1, Epoch: 1, Request: 1), f.ActorId(3), 1, 101), false), peer: 3);
            f.Receive(MessageCodec.Encode(new NetMessage(Msg.Explode, f.Id, 0, new ExplodeMessage(body.Id, body.Transform.Origin, Epoch: 2, Request: 2),
                f.ActorId(3), 1, 102), false), peer: 3);
            f.AssertScore(3, 5, 1);
        });
        runner.Run("king online/minted banks spill and re-detonate without losing conservation", () =>
        {
            using var f = new ServerFixture(mode: ModeRegistry.King, length: 100);
            f.Advance(); Pose(f, 2, KingZone.Point(Latest(f).Spot), 1); f.Advance(.1); f.Advance(4.01);
            TestRunner.Assert(Latest(f).MintedBy(f.ActorId(2)) == 2, "expected two minted tanks");
            var drops = Enumerable.Range(0, 2).Select(i => new BodyState(BodySync.DropTankId(0, i), Transform3D.Identity, Vector3.Zero, Vector3.Zero)).ToArray();
            var envelope = new ModeEnvelope(1, 1, ModeRegistry.KingId, 1, f.ActorId(2), 1, 1, ModeSchemas.KingKinds.Spill,
                new TankBankSpill(SpillReason.Thrown, drops));
            var bytes = MessageCodec.Encode(new NetMessage(Msg.ModeEvent, f.Id, 0, envelope), false);
            f.Receive(bytes); f.AssertScore(2, 4, -2);
            f.Packets.Clear(); f.Receive(bytes);
            TestRunner.Assert(f.Packets.Count == 0, "repeated King spill debited twice");
            for (var i = 0; i < drops.Length; i++)
            {
                f.Receive(MessageCodec.Encode(new NetMessage(Msg.Explode, f.Id, 0, new ExplodeMessage(drops[i].Id, Vector3.Zero, Request: (uint)i + 1),
                    f.ActorId(3), 1, (uint)i + 1), false), peer: 3);
            }

            f.AssertScore(3, 6, 1);
            f.Receive(new NetWriter(Msg.LeaveLobby)); f.Advance(100);
            var standings = f.Packets.Select(p => p.Message.Payload).OfType<System.Collections.Generic.IReadOnlyList<Standing>>().Last();
            TestRunner.Assert(standings.Sum(s => s.Score) == 8 + 2 + 2 - 2 && standings.Single(s => s.Id == f.ActorId(2)).Score == 4,
                "initial + minted + drops - spills did not conserve banks after departure");
        });
        runner.Run("king online/peer HUD and lobby bot consume snapshots and ignore stale zone state", () =>
        {
            using var f = new ServerFixture(mode: ModeRegistry.King, bots: 1);
            var client = new NetClient();
            client.Handle(MessageCodec.Encode(new NetMessage(Msg.Welcome, 0, 0, new WelcomeMessage(2, null)), true));
            var match = new Match { Net = client, Setup = f.Setup, CreateLocalView = false };
            runner.AddChild(match);
            try
            {
                var bot = match.Session.OwnedActors.Single(a => a.Character != match.Player).Character;
                var brain = match.BotBrain(bot.ActorId);
                TestRunner.Assert(brain is KingBot && ReferenceEquals(bot.InputOverride, brain) && bot.Ammo == null && bot.View == null,
                    "King bot did not receive its unlimited-ammo camera-free policy");
                match.OnMessage(new NetMessage(Msg.MatchGo, f.Id, 0, new FloatMessage(10)));
                match._PhysicsProcess(1 / 60.0);
                TestRunner.Assert(brain!.Status == "waiting" && match.ModeView == null, "peer guessed the zone before a snapshot");
                var state = Zone with { Holder = bot.ActorId, Occupants = new[] { bot.ActorId } };
                NetMessage Snapshot(uint id, KingState value, uint revision = 1) => new(Msg.ModeSnapshot, f.Id, 0,
                    new ModeEnvelope(1, revision, ModeRegistry.KingId, 1, 0, 0, id, ModeSchemas.KingKinds.Zone, value));
                match.OnMessage(Snapshot(10, state));
                match._PhysicsProcess(1 / 60.0);
                TestRunner.Assert(brain.Status != "waiting" && match.ModeView is KingState received && received.Matches(state),
                    "King bot or peer did not consume the authoritative view");
                match.OnMessage(Snapshot(9, state with { Zone = 4 }));
                match.OnMessage(Snapshot(11, state with { Zone = 5 }, revision: 2));
                TestRunner.Assert((match.ModeView as KingState)?.Zone == 3, "overtaken or foreign-world zone changed the view");
                var hud = new KingHud().Present(new ModeHudInput(bot.ActorId, ModePhase.Round, 10, match.Scores,
                    Array.Empty<Vector3>(), true, View: match.ModeView));
                TestRunner.Assert(hud.Banner == "HOLDING THE ZONE" && hud.Bar != null && hud.Objectives.Count == 1,
                    "HUD did not use the same snapshot as the bot");
            }
            finally { match.Free(); client.Free(); }
        });
        runner.Run("king online/closed-round pile placement cannot create world state", () =>
        {
            using var f = new ServerFixture(mode: ModeRegistry.King, length: 60);
            f.Advance(45);
            var wanted = f.Packets.Select(p => p.Message.Payload).OfType<ModeEnvelope>().Select(e => e.Payload).OfType<KingPileWanted>().Last();
            f.Advance(15); f.Packets.Clear(); Place(f, wanted);
            TestRunner.Assert(!f.Packets.Any(p => p.Message.Payload is ModeEnvelope { Payload: KingPileSpawned }), "late pile built after deadline");
        });
        runner.Run("king online/pending pile passes to successor and empty placement is final", () =>
        {
            using var f = new ServerFixture(mode: ModeRegistry.King, length: 100);
            f.Advance(45); // First sample at zone 1 asks for its fresh pile.
            var wanted = f.Packets.Select(p => p.Message.Payload).OfType<ModeEnvelope>().Select(e => e.Payload).OfType<KingPileWanted>().Last();
            f.Packets.Clear(); f.Receive(new NetWriter(Msg.LeaveLobby));
            TestRunner.Assert(f.Packets.Any(p => p.Peer == 3 && p.Message.Payload is ModeEnvelope { Payload: KingPileWanted }), "successor not asked to place pending pile");
            Place(f, wanted, peer: 3, count: 0);
            f.Packets.Clear(); Place(f, wanted, peer: 3, sequence: 101);
            TestRunner.Assert(!f.Packets.Any(p => p.Message.Payload is ModeEnvelope { Payload: KingPileSpawned }), "completed empty placement retried");
        });
    }
}
