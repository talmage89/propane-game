using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using Propane.Dev.UnitTests.Fixtures;
using Propane.Modes;
using Propane.Net;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev.UnitTests;

/// <summary>Native client feeds checked against independent rule states and explicit HUD values on either revision.</summary>
[UnitSuite]
public static class ModeClientTests
{
    private static ModeHud Hud(Match match) => (ModeHud)typeof(Match).GetMethod("PresentHud", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(match, new object[] { new[] { new Vector3(2, 3, 4) }, true })!;

    public static void Register(TestRunner runner)
    {
        runner.Run("mode client/Bank rule banks feed loading, play and results HUD values", () =>
        {
            using var server = new ServerFixture();
            using var client = new MatchClient(runner, server.Setup, server.Clock, 2);
            var match = client.Match;
            var rules = ModeTests.Bank(1, 2);
            var actor = match.Player.ActorId;
            Assert(actor == 1, "fixture actor ordering changed");
            var banks = rules.Snapshot().Standings.Select(s => (s.ActorId, s.Score, s.Present)).ToArray();
            match.OnMessage(new NetMessage(Msg.ModeSnapshot, server.Id, 0, new ModeEnvelope(1, 1, match.Setup.ModeId, 1, 0, 0, 1,
                ModeSchemas.TankBankKinds.Banks, new TankBankBanks(banks))));
            var loading = Hud(match);
            Assert(loading.Counters.Single() == new HudCounter(1, "TANK BANKED") && loading.Timer == null && !loading.ObjectivesVisible,
                "loading feed changed");
            match.OnMessage(new NetMessage(Msg.MatchGo, server.Id, 0, new FloatMessage(10)));
            foreach (var command in rules.Handle(new TankDetonated(actor, TankOrigin.Map), new ModeClock(ModePhase.Round, 1, 0, 10)).OfType<ScoreChanged>())
            {
                match.OnMessage(new NetMessage(Msg.ModeCommand, server.Id, 0, new ModeEnvelope(1, 1, match.Setup.ModeId, 1, actor, 1, 2,
                    ModeSchemas.TankBankKinds.Score, new TankBankScore(command.Score, command.Delta))));
            }

            var playing = Hud(match);
            Assert(playing.Counters.Single() == new HudCounter(2, "TANKS BANKED") && playing.Timer == new HudTimer(10, RoundTimer.Countdown) &&
                playing.ObjectivesVisible && playing.Objectives.SequenceEqual(new[] { new Vector3(2, 3, 4) }), "play feed changed");
            match.OnMessage(new NetMessage(Msg.MatchEnd, server.Id, 0, Array.Empty<Standing>()));
            var results = Hud(match);
            Assert(results.Counters.Single().Value == rules.Bank(actor) && results.Columns.Single() == "Tanks banked" &&
                results.Timer == new HudTimer(0, RoundTimer.Countdown) && results.Objectives.Count == 0 && !results.ObjectivesVisible,
                "results feed changed");
        });
        runner.Run("mode client/King rule state feeds hold, contest, warning, minted and read-time clocks", () =>
        {
            using var server = new ServerFixture(mode: ModeRegistry.King, length: 60);
            using var client = new MatchClient(runner, server.Setup, server.Clock, 2);
            var match = client.Match;
            var rules = new KingRules();
            using var tuning = new Propane.Core.Tuning { KingMintSeconds = 2, KingMoveSeconds = 45, KingWarningSeconds = 3 };
            rules.Initialize(new ModeSetup(server.Setup.Players.Select(p => new ModeActor(p.Player.Id, p.Player.Name)).ToArray(),
                new ModeWorld(ModeWorldSource.Void, 1, 0, new[] { new ModeSpot(0, 0, 0), new ModeSpot(20, 0, 0) }),
                ModeRegistry.King.CopyTuners(tuning), 3));
            ModeClock At(double now) => new(ModePhase.Round, 1, now, 60);
            rules.Handle(new PhaseEntered(ModePhase.Round, new RoundInfo(1, 1, RoundTimer.Countdown, 0, 60)), At(0));
            match.OnMessage(new NetMessage(Msg.MatchGo, server.Id, 0, new FloatMessage(60)));
            uint sequence = 0;
            void Sample(double now, bool contested)
            {
                var spot = rules.SpotOf(0);
                var samples = new List<ActorSample> { new(match.Player.ActorId, spot.X, spot.Y, spot.Z) };
                if (contested) samples.Add(new ActorSample(2, spot.X, spot.Y, spot.Z));
                rules.Handle(new ActorsSampled(samples), At(now));
                var state = rules.State(At(now))!;
                match.OnMessage(new NetMessage(Msg.Clock, server.Id, 0, new FloatMessage((float)(60 - now))));
                match.OnMessage(new NetMessage(Msg.ModeSnapshot, server.Id, 0, new ModeEnvelope(1, 1, match.Setup.ModeId, 1, 0, 0, ++sequence,
                    ModeSchemas.KingKinds.Zone, state)));
                Assert(match.ModeView is KingState view && view.Matches(state) && view.Elapsed == now && view.MintProgress == state.MintProgress &&
                    view.UntilMove == state.UntilMove, "King rule/replica view differs");
            }
            Sample(0, false);
            var hold = Hud(match);
            Assert(hold.Banner == "HOLDING THE ZONE" && hold.Bar == new HudBar(0, 1, "NEXT TANK") && hold.Objectives.Count == 1, "hold HUD changed");
            Sample(3, false);
            Assert(Hud(match).Counters[1].Value == 1 && Hud(match).Bar == new HudBar(.5f, 1, "NEXT TANK"), "mint/progress HUD changed");
            Sample(4, true);
            Assert(Hud(match).Banner == "CONTESTED" && Hud(match).Bar == null, "contest HUD changed");
            Sample(43, false);
            Assert(Hud(match).Banner == "THE ZONE MOVES IN 2" && Hud(match).Objectives.Count == 2, "warning HUD changed");
            match.OnMessage(new NetMessage(Msg.Clock, server.Id, 0, new FloatMessage(16)));
            Assert(((KingState)match.ModeView!).Elapsed == 44 && ((KingState)match.ModeView!).UntilMove == 1 && Hud(match).Banner == "THE ZONE MOVES IN 1",
                "view stopped advancing at explicit read time");
        });
        runner.Run("mode client/Bank predicted spill confirms without respawn and refuses without retaining bodies", () =>
        {
            using var server = new ServerFixture();
            using var client = new MatchClient(runner, server.Setup, server.Clock, 2);
            var match = client.Match;
            var actor = match.Player;
            match.OnMessage(new NetMessage(Msg.MatchGo, server.Id, 0, new FloatMessage(10)));
            match.OnMessage(ModeWireFixtures.Score(server.Id, actor.ActorId, 4, 0));
            var before = match.LiveTanks.Count();
            typeof(Match).GetMethod("OnRagdolled", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(match, new object[] { actor, new Vector3(0, 3, 0), Vector3.Zero, Vector3.Zero });
            var drops = match.LiveTanks.Where(t => actor.Runtime.DropPredictions.PendingIds.Contains(t.NetId)).ToArray();
            Assert(drops.Length == 4 && match.LiveTanks.Count() == before + 4, "prediction did not spill the available bank");
            var accepted = drops.Take(2).Select(t => new BodyState(t.NetId, t.GlobalTransform, t.LinearVelocity, t.AngularVelocity, 1, 0)).ToArray();
            match.OnMessage(ModeWireFixtures.Accepted(server.Id, actor.ActorId, actor.LifeId, SpillReason.Thrown, accepted));
            Assert(accepted.All(body => !actor.Runtime.DropPredictions.PendingIds.Contains(body.Id)) && match.LiveTanks.Count() == before + 4,
                "committed spill respawned its prediction");
            match.OnMessage(new NetMessage(Msg.ModeCommand, server.Id, 0, new ModeEnvelope(1, 1, match.Setup.ModeId, 1, actor.ActorId, 1, uint.MaxValue,
                ModeSchemas.TankBankKinds.SpillRefused, new TankBankRefused(drops.Skip(2).Select(t => t.NetId).ToArray()))));
            Assert(match.LiveTanks.Count() == before + 2 && drops.Skip(2).All(t => match.Bodies.Get(t.NetId) == null), "refused spill retained a replicated body");
        });
        runner.Run("mode client/King and Lobby place piles and suppress duplicate births", () =>
        {
            using var king = new ServerFixture(mode: ModeRegistry.King);
            using var lobby = new LobbyWorldFixture(3);
            foreach (var setup in new[] { king.Setup, lobby.World() })
            {
                using var client = new MatchClient(runner, setup, king.Clock, 2);
                var match = client.Match;
                void Apply(uint sequence, ushort kind, object payload) => match.OnMessage(new NetMessage(Msg.ModeCommand, setup.Id, 0,
                    new ModeEnvelope(1, 1, setup.ModeId, 1, 0, 0, sequence, kind, payload)));
                Apply(1, ModeSchemas.KingKinds.PileWanted, new KingPileWanted(1, new Vector3(0, 300, 0), 1, 1, 1));
                var before = match.LiveTanks.Count();
                var body = new BodyState(800000, new Transform3D(Basis.Identity, new Vector3(4, 3, 0)), Vector3.Zero, Vector3.Zero, 1, 0);
                var pile = new KingPileSpawned(1, 2, new[] { body });
                Apply(2, ModeSchemas.KingKinds.PileSpawned, pile);
                Apply(3, ModeSchemas.KingKinds.PileSpawned, pile);
                Assert(match.LiveTanks.Count() == before + 1 && match.LiveTanks.Single(t => t.NetId == body.Id).PilePose != null,
                    "pile birth duplicated or lost its pile pose");
                Assert(!match.IsLobby || match.LobbyRefills == 1, "lobby pile did not materialize exactly once");
            }
        });
        runner.Run("mode client/Lobby rule standings stay zero and its mode HUD stays empty", () =>
        {
            using var server = new LobbyWorldFixture(3);
            using var client = new MatchClient(runner, server.World(), server.Clock, 2);
            var rules = new LobbyRules();
            rules.Initialize(new ModeSetup(server.Lobby().Members.Select(m => new ModeActor(m.Id, m.Name)).ToArray(),
                new ModeWorld(ModeWorldSource.Void), ModeTuners.Empty, 1));
            client.Match.OnMessage(new NetMessage(Msg.MatchGo, server.World().Id, 0, new FloatMessage(3600)));
            Assert(rules.Snapshot().Standings.All(s => s.Score == 0) && client.Match.Scores.Values.All(s => s == 0), "lobby gained a score");
            client.Match._PhysicsProcess(1d / 120);
            var hud = Hud(client.Match);
            Assert(ReferenceEquals(hud, ModeHud.Empty) && client.Match.ModeView == null, "lobby mode HUD gained content");
        });
    }
}
