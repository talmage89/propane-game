using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Propane.Bots;
using Propane.Core;
using Propane.Net;
using Propane.Player;

namespace Propane.Dev.UnitTests;

/// <summary>Lobby bots (A8): roster requests, difficulty profiles, the owner's runtime and departure.</summary>
[UnitSuite]
public static class BotTests
{
    private static byte[] Request(Msg type, object payload) => MessageCodec.Encode(new NetMessage(type, 0, 0, payload), false);
    private static void Assert(bool condition, string reason) => TestRunner.Assert(condition, reason);

    /// <summary>A fixture whose first match has ended, with both peers back in the waiting lobby.</summary>
    private static ServerTests.Fixture Waiting()
    {
        var f = new ServerTests.Fixture(playing: false);
        f.Advance(50); f.Advance(); f.Advance(20); f.Advance(2);
        f.Packets.Clear();
        return f;
    }

    private static ActorRequestResultMessage Result(ServerTests.Fixture f, int peer) =>
        f.Packets.Where(p => p.Peer == peer).Select(p => p.Message.Payload).OfType<ActorRequestResultMessage>().Single();

    private static IReadOnlyList<PlayerProfile> Roster(ServerTests.Fixture f) =>
        f.Packets.Select(p => p.Message.Payload).OfType<RosterStateMessage>().Last().Actors;

    private static ActorRequestResultMessage Add(ServerTests.Fixture f, uint request, string name, int peer = 3, byte difficulty = 1,
        AppearanceRecord? appearance = null, uint? revision = null, ActorKind kind = ActorKind.Bot)
    {
        f.Packets.Clear();
        var config = kind == ActorKind.Bot ? ControllerConfig.ForBot(difficulty) : ControllerConfig.Human;
        f.Receive(Request(Msg.AddActor, new AddActorMessage(request, kind, name, appearance ?? AppearanceRecord.FromPalette(2), config,
            revision ?? f.Revision(peer))), peer);
        return Result(f, peer);
    }

    private static ActorRequestResultMessage Update(ServerTests.Fixture f, uint request, int actor, string name, int peer = 3, byte difficulty = 1,
        AppearanceRecord? appearance = null, uint? revision = null)
    {
        f.Packets.Clear();
        f.Receive(Request(Msg.UpdateActor, new UpdateActorMessage(request, actor, name, appearance ?? AppearanceRecord.FromPalette(2),
            ControllerConfig.ForBot(difficulty), revision ?? f.Revision(peer))), peer);
        return Result(f, peer);
    }

    private static ActorRequestResultMessage Remove(ServerTests.Fixture f, uint request, int actor, int peer = 3, uint? revision = null)
    {
        f.Packets.Clear();
        f.Receive(Request(Msg.RemoveActor, new RemoveActorMessage(request, actor, revision ?? f.Revision(peer))), peer);
        return Result(f, peer);
    }

    private static bool Fails(Action action)
    {
        try { action(); }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException) { return true; }
        return false;
    }

    public static void Register(TestRunner runner)
    {
        runner.Run("bots/a lobby member adds, edits and removes its own bots", () =>
        {
            using var f = Waiting();
            var custom = new AppearanceRecord(0x12, 0xAB, 0xEF);
            var revision = f.Revision();
            Assert(Add(f, 1, "Bot 1", difficulty: 2, appearance: custom) is { Accepted: true } added && added.Revision == revision + 1,
                "joiner could not add a bot");
            var bot = Roster(f).Single(a => a.Kind == ActorKind.Bot);
            Assert(bot.OwnerPeerId == 3 && bot.Name == "Bot 1" && bot.Appearance == custom && bot.ControllerConfig == ControllerConfig.ForBot(2),
                "bot profile was not accepted exactly");
            Assert(f.Packets.Any(p => p.Peer == 2 && p.Message.Payload is LobbyInfo l && l.Members.Any(m => m.Id == bot.Id)),
                "other members never saw the bot");
            Assert(Update(f, 2, bot.Id, "Bot Prime", difficulty: 0, appearance: AppearanceRecord.FromPalette(5)).Accepted, "owner could not edit its bot");
            var edited = Roster(f).Single(a => a.Id == bot.Id);
            Assert(edited.Name == "Bot Prime" && edited.ControllerConfig.Difficulty == 0 && edited.Appearance == AppearanceRecord.FromPalette(5) &&
                edited.AllocationSlot == bot.AllocationSlot, "edit did not change only the requested fields");
            f.Server.OnConnected(4);
            f.Receive(new NetWriter(Msg.Hello).Int(Protocol.Version).String(BuildInfo.GameVersion).String("test").String("Browser").Int(0).Bool(true), 4);
            Assert(Remove(f, 3, bot.Id).Accepted && Roster(f).All(a => a.Id != bot.Id), "owner could not remove its bot");
            Assert(f.Packets.Any(p => p.Peer == 4 && p.Message.Payload is LobbyListMessage list && list.Lobbies.Single().Players == 2),
                "the lobby browser count was not refreshed");
        });
        runner.Run("bots/only the owner can change a bot", () =>
        {
            using var f = Waiting();
            Add(f, 1, "Bot 1");
            var bot = Roster(f).Single(a => a.Kind == ActorKind.Bot);
            Assert(Update(f, 1, bot.Id, "Stolen", peer: 2) is { Accepted: false } stolen && stolen.Reason.Contains("own", StringComparison.Ordinal),
                "another peer edited the bot");
            Assert(!f.Packets.Any(p => p.Message.Payload is RosterStateMessage), "a refused edit broadcast a roster");
            Assert(!Remove(f, 2, bot.Id, peer: 2).Accepted && !f.Packets.Any(p => p.Message.Payload is RosterStateMessage),
                "another peer removed the bot");
            var human = f.Setup.Players.Single(p => p.Player.OwnerPeerId == 2).Player;
            Assert(!Update(f, 2, human.Id, "Mine now").Accepted && !Remove(f, 3, human.Id).Accepted, "a peer changed another peer's player");
            Assert(Update(f, 4, bot.Id, "Still mine").Accepted, "the owner lost control after refused requests");
        });
        runner.Run("bots/requests on a stale revision change nothing", () =>
        {
            using var f = Waiting();
            var stale = f.Revision();
            Add(f, 1, "Bot 1", peer: 2);
            Assert(Add(f, 2, "Bot 2", revision: stale) is { Accepted: false } refused && refused.Reason.Contains("changed", StringComparison.Ordinal) &&
                refused.Revision == stale + 1, "a stale add was accepted");
            Assert(!f.Packets.Any(p => p.Message.Payload is RosterStateMessage), "a stale add mutated the roster");
            var bot = Add(f, 3, "Bot 2") is { Accepted: true } ? Roster(f).Single(a => a.Name == "Bot 2") : throw new InvalidOperationException("add failed");
            var before = f.Revision();
            Assert(!Update(f, 4, bot.Id, "Late", revision: before - 1).Accepted && !Remove(f, 5, bot.Id, revision: before - 1).Accepted &&
                f.Revision() == before, "a stale edit or removal was accepted");
            Assert(!Add(f, 6, "Bot 3", revision: before + 1).Accepted, "a future revision was accepted");
        });
        runner.Run("bots/count toward capacity and the roster freezes during a match", () =>
        {
            using var f = Waiting();
            f.Receive(Request(Msg.SetCapacity, new IntMessage(4)));
            Assert(Add(f, 1, "Bot 1").Accepted && Add(f, 2, "Bot 2", peer: 2).Accepted, "bots within capacity were refused");
            var roster = Roster(f);
            Assert(roster.Count == 4 && Add(f, 3, "Bot 3") is { Accepted: false } full && full.Reason == "That lobby is full." &&
                !f.Packets.Any(p => p.Message.Payload is RosterStateMessage), "a bot exceeded capacity");
            f.Packets.Clear(); f.Receive(Request(Msg.SetCapacity, new IntMessage(3)));
            Assert(f.Packets.Count == 0, "capacity shrank below the bots in the lobby");
            f.Packets.Clear(); f.Receive(new NetWriter(Msg.JoinLobby).Int(1), 3);
            f.Server.OnConnected(4);
            f.Receive(new NetWriter(Msg.Hello).Int(Protocol.Version).String(BuildInfo.GameVersion).String("test").String("Late").Int(0).Bool(true), 4);
            f.Packets.Clear(); f.Receive(new NetWriter(Msg.JoinLobby).Int(1), 4);
            Assert(f.Packets.Any(p => p.Peer == 4 && p.Message.Payload is TextMessage { Value: "That lobby is full." }), "bots did not fill the lobby");
            var bot = roster.First(a => a.Kind == ActorKind.Bot && a.OwnerPeerId == 3);
            Assert(Remove(f, 4, bot.Id).Accepted, "removing a bot failed");
            f.Packets.Clear(); f.Receive(new NetWriter(Msg.StartMatch).Snapshot(f.Tuning.Snapshot()));
            var setup = f.Packets.Select(p => p.Message.Payload).OfType<MatchSetup>().First();
            Assert(setup.Players.Count(p => p.Player.Kind == ActorKind.Bot) == 1 && setup.Peers.Count == 2,
                "bots were not frozen into the match, or counted as readiness peers");
            Assert(Add(f, 5, "Bot 9") is { Accepted: false } frozen && frozen.Reason.Contains("frozen", StringComparison.Ordinal), "added a bot during a match");
            var running = setup.Players.Single(p => p.Player.Kind == ActorKind.Bot).Player;
            Assert(!Update(f, 6, running.Id, "Renamed", peer: 2).Accepted && !Remove(f, 7, running.Id, peer: 2).Accepted,
                "changed a bot during a match");
        });
        runner.Run("bots/names are validated and unique in the lobby", () =>
        {
            using var f = Waiting();
            uint request = 0;
            foreach (var name in new[] { "", " ", " Bot", "Bot ", "Bot\n1", "Bot\t1", "Bot​1", "Bot‮1", "Bot 1", "Bot1",
                         new string('b', Protocol.MaxNameLength + 1) })
            {
                Assert(Add(f, ++request, name) is { Accepted: false } refused && refused.Reason.StartsWith("Names need", StringComparison.Ordinal),
                    $"invalid bot name {name.Replace("\n", "\\n", StringComparison.Ordinal)} was accepted");
            }
            Assert(!f.Packets.Any(p => p.Message.Payload is RosterStateMessage), "an invalid name mutated the roster");
            Assert(Add(f, ++request, "Bót-9 ★ (é)").Accepted, "a valid name was refused");
            var bot = Roster(f).Single(a => a.Name == "Bót-9 ★ (é)");
            Assert(Add(f, ++request, new string('b', Protocol.MaxNameLength)).Accepted, "a full-length name was refused");
            foreach (var taken in new[] { "bót-9 ★ (É)", "Player2", "PLAYER3" })
            {
                Assert(Add(f, ++request, taken) is { Accepted: false } duplicate && duplicate.Reason.Contains("already called", StringComparison.Ordinal),
                    $"duplicate name {taken} was accepted");
            }

            Assert(Update(f, ++request, bot.Id, "BÓT-9 ★ (é)").Accepted, "a bot could not keep its own name in another case");
            Assert(!Update(f, ++request, bot.Id, "Player2").Accepted && !Update(f, ++request, bot.Id, "Bot\n").Accepted,
                "an edit bypassed name validation");
            Assert(Add(f, ++request, "Second human", kind: ActorKind.Human) is { Accepted: false } human && human.Reason == "Only bots can be added.",
                "a client added a human actor");
        });
        runner.Run("bots/malformed requests are rejected before the roster", () =>
        {
            using var f = Waiting();
            var good = Request(Msg.AddActor, new AddActorMessage(1, ActorKind.Bot, "Bot 1", AppearanceRecord.FromPalette(0), ControllerConfig.Bot, f.Revision()));
            var malformed = new List<byte[]> { good[..^1], good.Append((byte)0).ToArray() };
            foreach (var config in new[] { new ControllerConfig(1, ControllerKind.Bot, 3), new ControllerConfig(2, ControllerKind.Bot, 1),
                         new ControllerConfig(1, ControllerKind.Device, 1), new ControllerConfig(1, (ControllerKind)2, 1) })
            {
                var bytes = new NetWriter(Msg.AddActor).UInt(1).Byte((byte)ActorKind.Bot).String("Bot 1").Appearance(AppearanceRecord.FromPalette(0))
                    .Byte(config.Version).Byte((byte)config.Kind).Byte(config.Difficulty).UInt(f.Revision()).ToArray();
                malformed.Add(bytes);
            }
            malformed.Add(new NetWriter(Msg.AddActor).UInt(1).Byte(7).String("Bot 1").Appearance(AppearanceRecord.FromPalette(0))
                .Byte(1).Byte(1).Byte(1).UInt(f.Revision()).ToArray());
            malformed.Add(new NetWriter(Msg.AddActor).UInt(0).Byte(1).String("Bot 1").Appearance(AppearanceRecord.FromPalette(0))
                .Byte(1).Byte(1).Byte(1).UInt(f.Revision()).ToArray());
            var badAppearance = Request(Msg.AddActor, new AddActorMessage(1, ActorKind.Bot, "Bot 1", AppearanceRecord.FromPalette(0), ControllerConfig.Bot, f.Revision()));
            badAppearance[1 + 4 + 1 + 1 + "Bot 1".Length + 4] = 9; // Unsupported appearance record version.
            malformed.Add(badAppearance);
            malformed.Add(Request(Msg.UpdateActor, new UpdateActorMessage(1, 0, "Bot", AppearanceRecord.FromPalette(0), ControllerConfig.Bot, f.Revision())));
            malformed.Add(Request(Msg.RemoveActor, new RemoveActorMessage(1, 0, f.Revision())));
            foreach (var bytes in malformed)
            {
                Assert(Fails(() => MessageCodec.Decode(bytes, false)), $"malformed {(Msg)bytes[0]} decoded");
                f.Reject(bytes, 3);
            }
            Assert(Add(f, 1, "Bot 1").Accepted, "malformed input consumed a request id or broke the lobby");
        });
        runner.Run("bots/difficulties map to named local profiles", () =>
        {
            using var tuning = new Tuning();
            var profiles = BotProfile.All.Select(d => BotProfile.For(d, tuning)).ToArray();
            Assert(profiles.Select(p => p.Name).SequenceEqual(new[] { "Easy", "Normal", "Hard" }), "difficulty names changed");
            Assert(profiles[0].Skill == 0.2f && profiles[1].Skill == 0.6f && profiles[2].Skill == 1f, "profile skills changed");
            Assert(profiles[1].Skill == tuning.LabBotSkill, "Normal no longer plays like the Lab's default bot");
            Assert(profiles[0].Reaction(tuning) > profiles[1].Reaction(tuning) && profiles[1].Reaction(tuning) > profiles[2].Reaction(tuning) &&
                profiles[0].AimError(tuning) > profiles[1].AimError(tuning) && profiles[1].AimError(tuning) > profiles[2].AimError(tuning),
                "harder profiles do not react faster and aim tighter");
            Assert(Mathf.IsEqualApprox(profiles[2].AimError(tuning), tuning.BotAimErrorHard) &&
                Mathf.IsEqualApprox(profiles[2].Reaction(tuning), tuning.BotReactionHard), "Hard is not the hard endpoint");
            for (byte wire = 0; wire <= 2; wire++)
                Assert(BotProfile.For(wire, tuning) == profiles[wire] && ControllerConfig.ForBot(wire).ValidFor(ActorKind.Bot), "wire difficulty mismatch");
            Assert(BotProfile.Nearest(0.25f, tuning) == BotDifficulty.Easy && BotProfile.Nearest(0.6f, tuning) == BotDifficulty.Normal &&
                BotProfile.Nearest(0.95f, tuning) == BotDifficulty.Hard, "nearest profile classification");
            tuning.BotHardSkill = 0.9f;
            using var copy = tuning.Copy();
            Assert(BotProfile.For(BotDifficulty.Hard, copy).Skill == 0.9f, "world copies dropped the local profile dials");
            var snapshot = copy.Snapshot();
            Assert(!snapshot.ContainsKey(nameof(Tuning.BotEasySkill)) && !snapshot.ContainsKey(nameof(Tuning.BotNormalSkill)) &&
                !snapshot.ContainsKey(nameof(Tuning.BotHardSkill)), "difficulty dials entered the shared match snapshot");
        });
        runner.Run("bots/the owner simulates its bots with the mode's brain and no view", () =>
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
                Assert(human.InputOverride is not IBotGoalPolicy && match.BotBrain(human.ActorId) == null, "the human was given a brain");
                Assert(match.BotBrain(bot.ActorId) is BankingBot brain && ReferenceEquals(bot.InputOverride, brain) &&
                    brain.Skill == BotProfile.For(BotDifficulty.Normal, match.World.Tuning).Skill, "the bot has no Normal Tank Bank brain");
                Assert(!bot.IsRemote && bot.Ammo != null && bot.View == null && match.Session.LocalViews.Count == 0 &&
                    match.FindChildren("*", "Camera3D", owned: false).Count == 0 && match.FindChildren("*", "AudioListener3D", owned: false).Count == 0,
                    "the bot is not an owned, camera-free actor");
                var remote = match.Session.Actors.Single(a => a.Character.IsRemote).Character;
                Assert(remote.InputOverride is ReplicatedIntent, "a remote actor lost replicated intent");
                match._PhysicsProcess(1 / 60.0);
                Assert(match.BotCost.Bots == 1 && match.BotCost.Thinks == 1, "the bot did not think every physics tick");
                match.OnMessage(new NetMessage(Msg.MatchGo, f.Id, 0, new FloatMessage(10)));
                for (var i = 0; i < 3; i++) match._PhysicsProcess(1 / 60.0);
                Assert(match.BotCost.Thinks == 4 && !bot.InputLocked, "the bot did not play once the match started");
                // The departing peer's actor is freed; the bot's view of the others must not keep it.
                match.OnMessage(new NetMessage(Msg.PlayerLeft, f.Id, 0, new PlayerLeftMessage(3, 2)));
                Assert(!GodotObject.IsInstanceValid(remote) && match.Session.Actors.Count == 2, "the departed peer's actor remained");
                for (var i = 0; i < 3; i++) match._PhysicsProcess(1 / 60.0);
                Assert(match.BotCost.Thinks == 7, "the bot stopped after another peer left");
            }
            finally { match.Free(); client.Free(); }
            Assert(true, "brains disposed with the session");
        });
        runner.Run("bots/depart with their creator in the lobby and in a match", () =>
        {
            using (var lobby = Waiting())
            {
                Add(lobby, 1, "Bot 1"); Add(lobby, 2, "Bot 2");
                Assert(Roster(lobby).Count(a => a.OwnerPeerId == 3) == 3, "bots were not added");
                lobby.Packets.Clear();
                lobby.Server.OnDisconnected(3);
                Assert(Roster(lobby).All(a => a.OwnerPeerId != 3) && lobby.Packets.Any(p => p.Peer == 2 && p.Message.Payload is LobbyInfo l && l.Members.Count == 1),
                    "a departed creator's lobby bots stayed");
            }
            using var f = new ServerTests.Fixture(bots: 2);
            var departing = f.Setup.Players.Where(p => p.Player.OwnerPeerId == 2).Select(p => p.Player.Id).ToArray();
            Assert(departing.Length == 3 && f.Setup.Players.Count(p => p.Player.Kind == ActorKind.Bot) == 2, "fixture bots");
            f.Server.OnDisconnected(2);
            Assert(f.Packets.Any(p => p.Peer == 3 && p.Message.Payload is PlayerLeftMessage { Player: 2, Host: 3 }), "survivor was not told who left");
            Assert(f.Packets.Any(p => p.Message.Payload is RosterStateMessage r && r.Actors.All(a => a.OwnerPeerId != 2)), "bots survived their creator");
            f.Packets.Clear(); f.Advance(11);
            var standings = f.Packets.Select(p => p.Message.Payload).OfType<IReadOnlyList<Standing>>().Single();
            Assert(standings.Count == 4 && departing.All(id => standings.Any(s => s.Id == id && !s.Present && s.Score == 4)) &&
                standings.Single(s => s.Present).Id == f.ActorId(3), "departed bots lost their standings or banks");
        });
    }
}
