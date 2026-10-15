using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Modes;
using Propane.Net;
using Propane.Player;

namespace Propane.Dev.UnitTests;

/// <summary>
/// The lobby world: its pure refill rules, its lobby-state fields on the wire, and the server's world runs (admission
/// while roaming, the live roster, bots, the creator's authority, the stage, stale traffic after the start, the
/// return after a match and departures at every step), and a client building, reconciling and locking its world.
/// </summary>
[UnitSuite]
public static class LobbyWorldTests
{
    /// <summary>A real server with lobby worlds and recorded packets, a lobby created by peer 2.</summary>
    internal sealed class Fixture : IDisposable
    {
        public readonly ManualClock Clock = new();
        public readonly LobbyServer Server;
        /// <summary>Packets since the last clear, and every packet sent (lobby states, starts and actors are looked up there).</summary>
        public readonly List<(int Peer, NetMessage Message)> Packets = new();
        public readonly List<(int Peer, NetMessage Message)> All = new();
        public readonly Tuning Start = new() { CountdownTime = 0, ResultsTime = 1, LobbyStageTime = 2 };
        private readonly Dictionary<int, uint> actions = new();

        public Fixture(params int[] guests)
        {
            Server = new LobbyServer(PacketTransports.CreateServer(), Clock) { Worlds = true };
            Server.SendOverride = (peer, bytes, _) =>
            {
                var message = MessageCodec.Decode(bytes, true);
                Packets.Add((peer, message));
                All.Add((peer, message));
            };
            Connect(2);
            Receive(new NetWriter(Msg.CreateLobby));
            foreach (var guest in guests) { Connect(guest); Join(guest); }
        }

        public void Connect(int peer)
        {
            Server.OnConnected(peer);
            Receive(new NetWriter(Msg.Hello).Int(Protocol.Version).String(BuildInfo.GameVersion).String("test")
                .String($"Player{peer}").Int(peer % Protocol.PlayerColors.Length).Bool(true), peer);
        }

        public void Join(int peer) => Receive(new NetWriter(Msg.JoinLobby).Int(1), peer);
        public void Receive(NetWriter packet, int peer = 2) => Server.OnReceived(peer, packet.ToArray());
        public void Receive(byte[] packet, int peer = 2) => Server.OnReceived(peer, packet);
        public void Advance(double seconds = 0)
        {
            Clock.Seconds += seconds;
            Server.AdvanceMatches(Clock.Seconds);
        }

        public IEnumerable<T> To<T>(int peer) => Packets.Where(p => p.Peer == peer).Select(p => p.Message.Payload).OfType<T>();
        private IEnumerable<T> Ever<T>(int peer) => All.Where(p => p.Peer == peer).Select(p => p.Message.Payload).OfType<T>();
        public LobbyInfo Lobby(int peer = 2) => Ever<LobbyInfo>(peer).Last();
        public MatchSetup World(int peer = 2) => Ever<MatchSetup>(peer).Last(s => s.ModeId == ModeRegistry.LobbyWorldId);
        public MatchSetup Match(int peer = 2) => Ever<MatchSetup>(peer).Last(s => s.ModeId != ModeRegistry.LobbyWorldId);
        public IReadOnlyList<BodyAuthority> Arrived(int peer = 2) => Ever<AuthorityMessage>(peer).Last(a => a.Request == 0 && a.Bodies.All(b => b.Rest)).Bodies;
        public PlayerProfile Actor(int peer, ActorKind kind = ActorKind.Human) =>
            Lobby(peer).Members.Last(m => m.OwnerPeerId == peer && m.Kind == kind);

        /// <summary>An actor message from a peer's actor in a run, with its next action id.</summary>
        public byte[] Act(int peer, int run, Msg type, object payload, PlayerProfile? actor = null)
        {
            actor ??= Actor(peer);
            var action = actions.GetValueOrDefault(actor.Id) + 1;
            actions[actor.Id] = action;
            return MessageCodec.Encode(new NetMessage(type, run, 0, payload, actor.Id, actor.LifeId, action), false);
        }

        public byte[] Pose(int peer, int run, PlayerProfile? actor = null) =>
            Act(peer, run, Msg.PlayerState, new PlayerStateMessage(0, new PlayerNetState { Position = Vector3.One }), actor);

        public void StartMatch(int peer = 2) => Receive(new NetWriter(Msg.StartMatch).Snapshot(Start.Snapshot()), peer);

        public void Dispose() { Server.Free(); Start.Dispose(); }
    }

    private static ModeSetup Setup(int spots, double delay = 5, int limit = 100, int pile = 10) => new(Array.Empty<ModeActor>(),
        new ModeWorld(ModeWorldSource.Void, 1, 0, LobbyLayout.Spots(spots, 10)),
        new ModeTuners(new Dictionary<string, double>
        {
            [LobbyRules.LobbyRefillDelay] = delay, [LobbyRules.LobbyTankLimit] = limit, [LobbyRules.LobbyPileCount] = pile,
        }), 1);

    private static ModeClock At(double now) => new(ModePhase.Round, 1, now, double.PositiveInfinity);

    public static void Register(TestRunner runner)
    {
        runner.Run("lobby world/rules stock every spot, refill an emptied one after its delay, within the tank limit", () =>
        {
            var rules = new LobbyRules();
            rules.Initialize(Setup(3));
            var stock = rules.Handle(new PhaseEntered(ModePhase.Round, RoundInfo.First), At(0));
            TestRunner.Assert(stock.OfType<StockPile>().Select(s => s.Spot).SequenceEqual(new[] { 0, 1, 2 }), "round start stocks every spot");
            foreach (var spot in new[] { 0, 1, 2 }) rules.Handle(new PileFilled(spot), At(0));
            TestRunner.Assert(rules.Handle(new PilesStocked(new[] { 0, 4, 4 }, 8), At(10)).Count == 0, "an emptied spot waits out its delay");
            TestRunner.Assert(rules.Handle(new PilesStocked(new[] { 0, 4, 4 }, 8), At(14.9)).Count == 0, "still waiting");
            var refill = rules.Handle(new PilesStocked(new[] { 0, 4, 4 }, 8), At(15));
            TestRunner.Assert(refill.OfType<RefillPile>().Single().Spot == 0 && rules.Pending(0), "the empty spot refills after its delay");
            TestRunner.Assert(rules.Handle(new PilesStocked(new[] { 0, 4, 4 }, 8), At(40)).Count == 0, "a pending refill is not asked twice");
            rules.Handle(new PileUnplaced(0), At(41));
            TestRunner.Assert(rules.Handle(new PilesStocked(new[] { 0, 4, 4 }, 8), At(45)).Count == 0 &&
                rules.Handle(new PilesStocked(new[] { 0, 4, 4 }, 8), At(46)).OfType<RefillPile>().Count() == 1, "no clear ground retries after another delay");
            rules.Handle(new PileFilled(0), At(47));
            TestRunner.Assert(rules.Handle(new PilesStocked(new[] { 1, 4, 4 }, 9), At(60)).Count == 0, "a standing tank keeps a spot stocked");

            var crowded = new LobbyRules();
            crowded.Initialize(Setup(2, delay: 0, limit: 40));
            TestRunner.Assert(crowded.Handle(new PilesStocked(new[] { 0, 0 }, 40), At(1)).Count == 0, "no refill while the world holds its limit");
            TestRunner.Assert(crowded.Handle(new PilesStocked(new[] { 0, 0 }, 21), At(2)).OfType<RefillPile>().Single().Spot == 0,
                "refills resume below it, as many whole piles as fit");
            TestRunner.Assert(crowded.Handle(new PilesStocked(new[] { 0, 0 }, 21), At(3)).Count == 0, "a pile on its way counts against the limit");
            crowded.Handle(new PileFilled(0), At(4));
            TestRunner.Assert(crowded.Handle(new PilesStocked(new[] { 10, 0 }, 30), At(5)).OfType<RefillPile>().Single().Spot == 1,
                "the next fits once the first stands");
            TestRunner.Assert(crowded.Handle(new SpillRequested(new SpillTicket(2, 1, 1), SpillReason.Hit, 1), At(3)).Single() is SpillCanceled,
                "nothing spills in the lobby");
            TestRunner.Assert(crowded.Permits(ModeGate.World, At(4)) && !crowded.Permits(ModeGate.Pickup, At(4)), "the world is open; there are no cans");
        });

        runner.Run("lobby world/layout spreads arrivals by slot and faces them in", () =>
        {
            var arrivals = Enumerable.Range(1, 16).Select(slot => LobbyLayout.Arrival(slot, 6)).ToArray();
            for (var i = 0; i < arrivals.Length; i++)
            {
                var (at, yaw) = arrivals[i];
                TestRunner.Assert(Math.Abs(Math.Sqrt(at.X * at.X + at.Z * at.Z) - 6) < 1e-9, "arrivals stand on their ring");
                // Forward is (-sin yaw, -cos yaw): toward the middle.
                TestRunner.Assert(-Math.Sin(yaw) * -at.X + -Math.Cos(yaw) * -at.Z > 5.99, "each arrival faces the middle");
                for (var j = 0; j < i; j++)
                    TestRunner.Assert(Math.Abs(at.X - arrivals[j].At.X) + Math.Abs(at.Z - arrivals[j].At.Z) > 0.9, "arrivals do not share a spot");
            }
            TestRunner.Assert(LobbyLayout.Spots(6, 17).All(s => Math.Abs(Math.Sqrt(s.X * s.X + s.Z * s.Z) - 17) < 1e-9), "piles stand on their ring");
        });

        runner.Run("lobby world/lobby state carries the stage and the world, and refuses them out of place", () =>
        {
            var lobby = (LobbyInfo)ProtocolTests.Fixtures().Single(m => m.Type == Msg.LobbyState).Payload;
            var staging = lobby with { Phase = LobbyServer.LobbyPhase.Staging, StartsIn = 3.5f, WorldId = 9 };
            var decoded = (LobbyInfo)MessageCodec.Decode(MessageCodec.Encode(new NetMessage(Msg.LobbyState, 0, 0, staging), true), true).Payload;
            TestRunner.Assert(decoded.StartsIn == 3.5f && decoded.WorldId == 9 && decoded.Phase == LobbyServer.LobbyPhase.Staging, "staging fields lost");
            foreach (var bad in new[]
                     {
                         lobby with { StartsIn = 2 }, staging with { StartsIn = LobbyServer.MaxStage + 1 }, staging with { StartsIn = -1 },
                         lobby with { Phase = LobbyServer.LobbyPhase.Loading, WorldId = 4 }, lobby with { WorldId = -1 },
                     })
            {
                TestRunner.Throws<InvalidDataException>(() => MessageCodec.Decode(MessageCodec.Encode(new NetMessage(Msg.LobbyState, 0, 0, bad), true), true));
            }
            var cancel = new NetWriter(Msg.CancelStart).ToArray();
            TestRunner.Assert(MessageCodec.Decode(cancel, false).Payload is EmptyMessage, "cancel start decodes");
            TestRunner.Throws<InvalidDataException>(() => MessageCodec.Decode(cancel.Append((byte)0).ToArray(), false));
            TestRunner.Throws<InvalidDataException>(() => MessageCodec.Decode(cancel, true));
        });

        runner.Run("lobby world/creating a lobby opens a stocked world and admits its creator with every body", () =>
        {
            using var f = new Fixture();
            var lobby = f.Lobby();
            var world = f.World();
            TestRunner.Assert(lobby.WorldId == world.Id && world.Mode == ModeRegistry.LobbyWorld && world.Players.Single().Player.OwnerPeerId == 2,
                "the creator's lobby state names its world, then its world start follows");
            var bodies = f.Arrived();
            using var tuning = Tuning.SavedTemplate.Copy();
            TestRunner.Assert(bodies.Count == tuning.LobbyPiles * tuning.LobbyPileCount &&
                bodies.All(b => b.State == TankLedger.State.Intact && b.Rest && b.Owner == 2), "every pile arrives standing, owned by the creator");
            var order = f.Packets.Where(p => p.Peer == 2).Select(p => p.Message.Type).ToList();
            TestRunner.Assert(order.IndexOf(Msg.LobbyState) < order.IndexOf(Msg.MatchStart) && order.IndexOf(Msg.MatchStart) < order.IndexOf(Msg.Authority) &&
                order.IndexOf(Msg.Authority) < order.IndexOf(Msg.MatchGo), "lobby state, world start, bodies, then go");
            var plan = PlanCodec.Decode(world.Plan);
            TestRunner.Assert(plan.Lots.Count == 0 && plan.Tanks.Count == 0 && plan.Spawns.Count == 1, "the world's plan is the void with one arrival");
        });

        runner.Run("lobby world/joining while roaming sees the world as it is; leaving and rejoining arrives anew", () =>
        {
            using var f = new Fixture();
            var world = f.World().Id;
            var tank = f.Arrived()[0];
            f.Receive(f.Act(2, world, Msg.Explode, new ExplodeMessage(tank.Body.Id, tank.Body.Transform.Origin, 1, 1)));
            TestRunner.Assert(f.To<AuthorityMessage>(2).Last().Bodies.Single().State == TankLedger.State.Exploded, "roamers blow tanks up");
            f.Packets.Clear();
            f.Connect(3);
            f.Join(3);
            var start = f.World(3);
            var arrived = f.Arrived(3);
            TestRunner.Assert(start.Id == world && start.Players.Count == 2 && arrived.All(b => b.Body.Id != tank.Body.Id) &&
                arrived.Count == Tuning.SavedTemplate.LobbyPiles * Tuning.SavedTemplate.LobbyPileCount - 1, "the newcomer arrives in the same world without the exploded tank");
            TestRunner.Assert(f.To<MatchSetup>(2).Count() == 0 && f.Lobby(2).Members.Count == 2, "the others are told by the lobby state alone");
            f.Receive(f.Pose(3, world), 3);
            TestRunner.Assert(f.Packets.Any(p => p.Peer == 2 && p.Message.Type == Msg.PlayerState && p.Message.ActorId == f.Actor(3).Id), "the newcomer's poses relay");
            var first = f.Actor(3);
            f.Packets.Clear();
            f.Receive(new NetWriter(Msg.LeaveLobby), 3);
            TestRunner.Assert(f.To<PlayerLeftMessage>(2).Single().Player == 3 && f.Packets.Single(p => p.Message.Type == Msg.PlayerLeft).Message.MatchId == world,
                "leaving the lobby leaves its world");
            f.Join(3);
            var again = f.Actor(3);
            TestRunner.Assert(again.Id != first.Id && f.World(3).Id == world && f.World(3).Players.Any(p => p.Player.Id == again.Id), "rejoining arrives as a new actor");
            f.Packets.Clear();
            f.Receive(f.Pose(3, world, first), 3);
            TestRunner.Assert(!f.Packets.Any(p => p.Message.Type == Msg.PlayerState), "the departed actor's poses do not relay");
        });

        runner.Run("lobby world/bots added while roaming roam it; removed bots leave it", () =>
        {
            using var f = new Fixture(3);
            var world = f.World().Id;
            f.Receive(MessageCodec.Encode(new NetMessage(Msg.AddActor, 0, 0, new AddActorMessage(1, ActorKind.Bot, "Roamer", AppearanceRecord.FromPalette(4),
                ControllerConfig.ForBot(2), f.Server.RosterRevision(2))), false));
            var bot = f.Actor(2, ActorKind.Bot);
            f.Packets.Clear();
            f.Receive(f.Pose(2, world, bot));
            TestRunner.Assert(f.Packets.Any(p => p.Peer == 3 && p.Message.ActorId == bot.Id), "the bot's poses relay in the world");
            f.Receive(MessageCodec.Encode(new NetMessage(Msg.RemoveActor, 0, 0, new RemoveActorMessage(2, bot.Id, f.Server.RosterRevision(2))), false));
            f.Packets.Clear();
            f.Receive(f.Pose(2, world, bot));
            TestRunner.Assert(f.Packets.Count == 0, "a removed bot no longer acts in the world");
        });

        runner.Run("lobby world/only the creator changes settings, starts or cancels; the stage locks the lobby", () =>
        {
            using var f = new Fixture(3);
            f.Packets.Clear();
            f.Receive(MessageCodec.Encode(new NetMessage(Msg.SetMode, 0, 0, ModeSettings.Default with { TankSupply = 6 }), false), 3);
            f.Receive(new NetWriter(Msg.SetMatchLength).Float(300), 3);
            f.Receive(new NetWriter(Msg.SetCapacity).Int(9), 3);
            f.StartMatch(3);
            TestRunner.Assert(f.Packets.Count == 0, "a guest changed or started the lobby");
            f.StartMatch();
            var staged = f.Lobby(3);
            TestRunner.Assert(staged.Phase == LobbyServer.LobbyPhase.Staging && Math.Abs(staged.StartsIn - 2) < 1e-6 && staged.WorldId == f.World().Id,
                "the creator's start stages a countdown in the world");
            f.Packets.Clear();
            f.Receive(new NetWriter(Msg.CancelStart), 3);
            f.Receive(MessageCodec.Encode(new NetMessage(Msg.SetMode, 0, 0, ModeSettings.Default with { TankSupply = 6 }), false));
            f.Receive(MessageCodec.Encode(new NetMessage(Msg.AddActor, 0, 0, new AddActorMessage(1, ActorKind.Bot, "Late", AppearanceRecord.FromPalette(4),
                ControllerConfig.Bot, f.Server.RosterRevision(2))), false));
            f.Receive(new NetWriter(Msg.SetProfile).String("Renamed").Appearance(AppearanceRecord.FromPalette(1)), 3);
            TestRunner.Assert(!f.To<LobbyInfo>(2).Any() && f.To<ActorRequestResultMessage>(2).Single().Accepted == false, "the stage locks settings and roster");
            f.Connect(4);
            f.Join(4);
            TestRunner.Assert(f.To<TextMessage>(4).Single().Value.Contains("starting", StringComparison.Ordinal), "nobody joins a starting lobby");
            f.Receive(new NetWriter(Msg.CancelStart));
            TestRunner.Assert(f.Lobby(3).Phase == LobbyServer.LobbyPhase.Waiting && f.Lobby(3).StartsIn == 0, "the creator takes the start back");
            f.Advance(5);
            TestRunner.Assert(!f.To<MatchSetup>(3).Any(s => s.ModeId != ModeRegistry.LobbyWorldId), "a canceled start never begins");
        });

        runner.Run("lobby world/the stage gives way to the match; the world's traffic is stale from then on", () =>
        {
            using var f = new Fixture(3);
            var world = f.World().Id;
            var tank = f.Arrived()[0];
            f.StartMatch();
            f.Advance(1);
            f.Receive(f.Pose(2, world));
            TestRunner.Assert(f.Packets.Any(p => p.Peer == 3 && p.Message.Type == Msg.PlayerState), "the world still relays while the stage counts down");
            f.Advance(1);
            var match = f.Match(3);
            TestRunner.Assert(match.ModeId == ModeRegistry.TankBankId && match.Id > world && f.Lobby(3) is { Phase: LobbyServer.LobbyPhase.Loading, WorldId: 0 },
                "the match starts and the lobby has no world");
            f.Packets.Clear();
            f.Receive(f.Pose(2, world));
            f.Receive(f.Act(2, world, Msg.Explode, new ExplodeMessage(tank.Body.Id, Vector3.Zero, 1, 7)));
            f.Receive(new NetWriter(Msg.Loaded, world), 3);
            TestRunner.Assert(f.Packets.Count == 0, "stale world traffic relayed or reached the ledger");
        });

        runner.Run("lobby world/after the results everyone returns to a new world, membership kept", () =>
        {
            using var f = new Fixture(3);
            f.Receive(MessageCodec.Encode(new NetMessage(Msg.AddActor, 0, 0, new AddActorMessage(1, ActorKind.Bot, "Stays", AppearanceRecord.FromPalette(4),
                ControllerConfig.Bot, f.Server.RosterRevision(2))), false));
            var first = f.World();
            var lives = f.Lobby().Members.ToDictionary(m => m.Id, m => m.LifeId);
            f.StartMatch();
            f.Advance(2);
            var match = f.Match(2);
            f.Receive(new NetWriter(Msg.Loaded, match.Id));
            f.Receive(new NetWriter(Msg.Loaded, match.Id), 3);
            f.Advance();
            f.Advance();
            f.Advance(match.Length + 1);
            f.Advance(2);
            var back = f.World(3);
            var lobby = f.Lobby(3);
            TestRunner.Assert(back.Id > match.Id && lobby.Phase == LobbyServer.LobbyPhase.Waiting && lobby.WorldId == back.Id, "a new world opens after the results");
            TestRunner.Assert(back.Players.Select(p => p.Player.Id).OrderBy(id => id).SequenceEqual(lives.Keys.OrderBy(id => id)) &&
                back.Players.All(p => p.Player.LifeId == lives[p.Player.Id] + 1), "the same actors return, each in its next life");
            TestRunner.Assert(f.World(2).Id == back.Id, "everyone is admitted to it");
            TestRunner.Assert(first.Id < match.Id, "ids keep increasing across worlds and matches");
        });

        runner.Run("lobby world/the creator leaving hands the world to a successor, and cancels a stage", () =>
        {
            using (var f = new Fixture(3))
            {
                var world = f.World().Id;
                f.Packets.Clear();
                f.Receive(new NetWriter(Msg.LeaveLobby));
                var left = f.To<PlayerLeftMessage>(3).Single();
                TestRunner.Assert(left is { Player: 2, Host: 3 } && f.Lobby(3).CreatorId == 3 && f.Lobby(3).WorldId == world, "the successor hosts the same world");
                TestRunner.Assert(f.To<AuthorityMessage>(3).Single().Bodies.All(b => b.Owner == 3) && f.To<AuthorityMessage>(3).Single().Bodies.Count > 0,
                    "the successor owns the departed creator's bodies");
            }
            using (var f = new Fixture(3))
            {
                f.StartMatch();
                f.Receive(new NetWriter(Msg.LeaveLobby));
                TestRunner.Assert(f.Lobby(3) is { Phase: LobbyServer.LobbyPhase.Waiting, CreatorId: 3 }, "the creator's stage ends with them");
                f.Advance(5);
                TestRunner.Assert(!f.To<MatchSetup>(3).Any(s => s.ModeId != ModeRegistry.LobbyWorldId), "nobody else's match starts");
            }
        });

        runner.Run("lobby world/disconnects during the countdown and during loading leave the others to play", () =>
        {
            using (var f = new Fixture(3, 4))
            {
                f.StartMatch();
                f.Advance(1);
                f.Server.OnDisconnected(4);
                f.Advance(1);
                var match = f.Match(2);
                TestRunner.Assert(match.ModeId == ModeRegistry.TankBankId && match.Peers.Select(p => p.PeerId).OrderBy(p => p).SequenceEqual(new[] { 2, 3 }),
                    "a guest lost during the countdown is left out of the match");
            }
            using (var f = new Fixture(3))
            {
                f.StartMatch();
                f.Advance(2);
                var match = f.Match(2);
                f.Receive(new NetWriter(Msg.Loaded, match.Id));
                f.Packets.Clear();
                f.Server.OnDisconnected(3);
                f.Advance();
                TestRunner.Assert(f.To<PlayerLeftMessage>(2).Single().Player == 3 && f.Packets.Any(p => p.Message.Type == Msg.Countdown),
                    "a guest lost while loading leaves, and the match goes on without waiting for them");
            }
        });

        runner.Run("lobby world/refills come from the world authority, checked, and materialize for everyone", () =>
        {
            using var f = new Fixture(3);
            var world = f.World().Id;
            var tuning = Tuning.SavedTemplate;
            var spot = LobbyLayout.Spots(tuning.LobbyPiles, tuning.LobbyPileRing)[0];
            var center = new Vector3((float)spot.X, 0, (float)spot.Z);
            var pile = f.Arrived()
                .Where(b => new Vector2(b.Body.Transform.Origin.X - center.X, b.Body.Transform.Origin.Z - center.Z).Length() < 2).ToArray();
            TestRunner.Assert(pile.Length == tuning.LobbyPileCount, "the first spot's pile stands on it");
            uint request = 10;
            foreach (var tank in pile) f.Receive(f.Act(2, world, Msg.Explode, new ExplodeMessage(tank.Body.Id, Vector3.Zero, 1, request++)));
            f.Packets.Clear();
            f.Advance(0.6);
            f.Advance(tuning.LobbyRefillDelay + 0.6);
            var wanted = f.To<ModeEnvelope>(2).Select(e => e.Payload).OfType<KingPileWanted>().Single();
            TestRunner.Assert(wanted.Count == tuning.LobbyPileCount && wanted.Spot.DistanceTo(center) < 1e-3 && !f.To<ModeEnvelope>(3).Any(),
                "the world authority alone is asked for the emptied spot");
            var poses = Enumerable.Range(0, wanted.Count).Select(i => new Transform3D(Basis.Identity, center + Vector3.Right * (i * 0.3f))).ToArray();
            byte[] Placed(int peer, IReadOnlyList<Transform3D> tanks) => MessageCodec.Encode(new NetMessage(Msg.ModeEvent, world, 0,
                new ModeEnvelope(1, 1, ModeRegistry.LobbyWorldId, 1, f.Actor(peer).Id, 1, (uint)(500 + tanks.Count + peer), ModeSchemas.LobbyKinds.PilePlaced,
                    new KingPilePlaced(wanted.Zone, tanks))), false);
            f.Packets.Clear();
            f.Receive(Placed(3, poses), 3);
            TestRunner.Assert(f.Packets.Count == 0, "another peer cannot place the pile");
            f.Receive(Placed(2, poses));
            var spawned = f.To<ModeEnvelope>(3).Select(e => e.Payload).OfType<KingPileSpawned>().Single();
            TestRunner.Assert(spawned.Owner == 2 && spawned.Tanks.Count == wanted.Count && spawned.Tanks.All(t => t.Id < BodySync.DropTankBase) &&
                f.To<ModeEnvelope>(2).Select(e => e.Payload).OfType<KingPileSpawned>().Any(), "the placed pile goes to everyone as new world tanks");
            f.Packets.Clear();
            f.Advance(0.6);
            f.Advance(tuning.LobbyRefillDelay + 1);
            TestRunner.Assert(!f.To<ModeEnvelope>(2).Any(), "a standing refill is not asked for again");
        });

        runner.Run("lobby world/a client builds its world, follows the roster live and stands still on the stage", () =>
        {
            using var f = new Fixture(3);
            var start = f.World(2);
            using var template = new Tuning();
            var client = new NetClient { Template = template, LobbyWorlds = true };
            Match? world = null;
            try
            {
                client.Handle(MessageCodec.Encode(new NetMessage(Msg.Welcome, 0, 0, new WelcomeMessage(2, null)), true));
                var lobby = f.Lobby(2);
                client.Handle(MessageCodec.Encode(new NetMessage(Msg.LobbyState, 0, 0, lobby), true));
                MatchSetup? setup = null;
                client.MatchStarting += incoming => setup = incoming;
                client.Handle(MessageCodec.Encode(new NetMessage(Msg.MatchStart, start.Id, 0, start), true));
                foreach (var authority in f.To<AuthorityMessage>(2).Where(a => a.Request == 0))
                    client.Handle(MessageCodec.Encode(new NetMessage(Msg.Authority, start.Id, 0, authority), true));
                client.Handle(MessageCodec.Encode(new NetMessage(Msg.MatchGo, start.Id, 0, new FloatMessage(0)), true));
                TestRunner.Assert(setup?.Id == start.Id, "the client enters its lobby's world");
                world = new Match { Net = client, Setup = setup!, CreateLocalView = false };
                runner.AddChild(world);
                TestRunner.Assert(world.IsLobby && world.IsPlaying && world.LiveTanks.Count() == Tuning.SavedTemplate.LobbyPiles * Tuning.SavedTemplate.LobbyPileCount,
                    "the arrival builds every standing pile and plays at once");
                TestRunner.Assert(world.Suburb.StaticRoot.FindChild("SlabBody", true, false) == null, "the lobby world stands on the void, not a slab");
                TestRunner.Assert(world.RemoteBodies.Count() == 1 && world.Player.Ammo == null, "the guest is there, and rifles never run dry");

                // A bot of this peer arrives with its brain; another peer's new actor arrives as a remote copy.
                var bot = new PlayerProfile(40, "Arrival", AppearanceRecord.FromPalette(3))
                    { OwnerPeerId = 2, Kind = ActorKind.Bot, ControllerConfig = ControllerConfig.ForBot(2), AllocationSlot = 30 };
                var guestBot = bot with { Id = 41, Name = "Visitor", OwnerPeerId = 3, AllocationSlot = 31 };
                client.Handle(MessageCodec.Encode(new NetMessage(Msg.LobbyState, 0, 0,
                    lobby with { Members = lobby.Members.Append(bot).Append(guestBot).ToArray(), RosterRevision = lobby.RosterRevision + 1 }), true));
                TestRunner.Assert(world.Session.OwnedActors.Count() == 2 && world.BotBrain(40) != null && world.RemoteBodies.Count() == 2,
                    "arrivals join the world live, owned bots with brains");
                var arrival = LobbyLayout.Arrival(30, world.World.Tuning.LobbySpawnRing).At;
                var owned = world.Session.Actor(40)!.Character;
                TestRunner.Assert(new Vector2(owned.GlobalPosition.X - (float)arrival.X, owned.GlobalPosition.Z - (float)arrival.Z).Length() < 0.01f,
                    "an arrival stands at its slot's spot");
                var recolored = guestBot with { Appearance = AppearanceRecord.FromPalette(6) };
                client.Handle(MessageCodec.Encode(new NetMessage(Msg.LobbyState, 0, 0,
                    lobby with { Members = lobby.Members.Append(recolored).ToArray(), RosterRevision = lobby.RosterRevision + 2 }), true));
                TestRunner.Assert(world.Session.Actor(40) == null && world.BotBrain(40) == null && world.Session.Actor(41)!.Character.BodyColor == recolored.Appearance.Color,
                    "a removed bot leaves with its brain; a changed color follows");
                client.Handle(MessageCodec.Encode(new NetMessage(Msg.LobbyState, 0, 0, lobby with
                {
                    Members = lobby.Members.Append(recolored).ToArray(), RosterRevision = lobby.RosterRevision + 3,
                    Phase = LobbyServer.LobbyPhase.Staging, StartsIn = 3,
                }), true));
                TestRunner.Assert(world.Player.InputLocked && client.StageLeft is > 2.9 and <= 3, "the stage holds this player still and counts down");
                client.Handle(MessageCodec.Encode(new NetMessage(Msg.LobbyState, 0, 0, lobby with
                {
                    Members = lobby.Members.Append(recolored).ToArray(), RosterRevision = lobby.RosterRevision + 4,
                }), true));
                TestRunner.Assert(!world.Player.InputLocked && client.StageLeft == 0, "a canceled stage frees them");
            }
            finally { world?.Session.Dispose(); world?.Free(); client.Free(); }
        });
    }
}
