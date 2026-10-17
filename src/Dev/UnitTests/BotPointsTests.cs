using System.Linq;
using Propane.Bots;
using Propane.Core;
using Propane.Dev.UnitTests.Fixtures;
using Propane.Net;

namespace Propane.Dev.UnitTests;

[UnitSuite]
public static class BotPointsTests
{
    public static void Register(TestRunner runner)
    {
        using var tuning = Tuning.LoadBaked();
        runner.Run("points/spills favor leaders, cap at the available bank and discount return fire", () =>
        {
            float Fight(int mine, int bank, int leading, float distance = 12, bool down = false) =>
                BotPoints.FightRate(tuning, mine, bank, leading, distance, down);
            TestRunner.Assert(Fight(0, 5, 5) > Fight(0, 5, 10), "leader denial has no value");
            TestRunner.Assert(Fight(0, 1, 5) < Fight(0, 5, 5), "small bank valued as a full spill");
            TestRunner.Assert(Fight(20, 5, 20) < Fight(0, 5, 5), "return fire costs nothing");
            TestRunner.Assert(Fight(0, 5, 5, 60) < Fight(0, 5, 5), "travel costs nothing");
            TestRunner.Assert(Fight(0, 0, 5) == 0 && Fight(0, 5, 5, down: true) == 0, "empty/down rival offers points");
        });
        runner.Run("points/a reachable lone zone pays while a contest or relocation reduces it", () =>
        {
            float Zone(int competitors = 0, float travel = 0, float remaining = 30, bool rival = false) =>
                BotPoints.ZoneRate(tuning, 0, 5, 5, rival, competitors, travel, remaining);
            TestRunner.Assert(Zone() > Zone(competitors: 3), "contest never discounts minting");
            TestRunner.Assert(Zone() > Zone(travel: 10), "zone travel costs nothing");
            TestRunner.Assert(Zone(travel: 29) == 0, "arrives too late to mint yet expects points");
            TestRunner.Assert(Zone(rival: true) > Zone(), "denying a leader's mint has no value");
            TestRunner.Assert(BotPoints.ZoneRate(tuning, 10, 0, 10, false, 5, 0, 30) < Zone(competitors: 5),
                "risking an existing bank in a crowd costs nothing");
        });
        runner.Run("points/local estimates survive copies without changing the shared tuner schema", () =>
        {
            tuning.BotPointsDeny = 0.4f;
            tuning.LabHardBots = 3;
            using var copy = tuning.Copy();
            using var snapshot = copy.Snapshot();
            TestRunner.Assert(copy.BotPointsDeny == 0.4f && copy.LabHardBots == 3, "local estimates lost on world copy");
            TestRunner.Assert(!snapshot.ContainsKey(nameof(Tuning.BotPointsDeny)) && !snapshot.ContainsKey(nameof(Tuning.LabHardBots)),
                "points policy entered the shared schema");
        });
        runner.Run("points/only Hard lobby actors use the points policy in both scoring modes", () =>
        {
            foreach (var mode in new[] { Modes.ModeRegistry.TankBank, Modes.ModeRegistry.King })
            {
                using var fixture = new ServerFixture(bots: 3, mode: mode);
                var players = fixture.Setup.Players.Select((p, i) => p.Player.Kind == ActorKind.Bot
                    ? p with { Player = p.Player with { ControllerConfig = ControllerConfig.ForBot((byte)(i - 2)) } } : p).ToArray();
                // The fixture's two humans precede its bots.
                var client = new NetClient();
                client.Handle(MessageCodec.Encode(new NetMessage(Msg.Welcome, 0, 0, new WelcomeMessage(2, null)), true));
                var match = new Match { Net = client, Setup = fixture.Setup with { Players = players }, CreateLocalView = false };
                runner.AddChild(match);
                try
                {
                    foreach (var profile in players.Where(p => p.Player.Kind == ActorKind.Bot))
                    {
                        var brain = match.BotBrain(profile.Player.Id)!;
                        TestRunner.Assert(brain.PointsDriven == (profile.Player.ControllerConfig.Difficulty == (byte)BotDifficulty.Hard),
                            "difficulty did not select the points policy");
                    }
                }
                finally { match.Free(); client.Free(); }
            }
        });
    }
}
