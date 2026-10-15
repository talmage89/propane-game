using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.UI;

namespace Propane.Dev.UnitTests;

/// <summary>The match leaderboard's ranking and motion, headless: order, ties, the local row and live updates.</summary>
[UnitSuite]
public static class LeaderboardTests
{
    private static LeaderboardEntry[] Roster(int count) =>
        Enumerable.Range(1, count).Select(id => new LeaderboardEntry(id, $"P{id}", Colors.White)).ToArray();

    private static Dictionary<int, int> Scores(params int[] byId) => byId.Select((score, i) => (i + 1, score)).ToDictionary(p => p.Item1, p => p.score);

    private static string Order(Leaderboard board) => string.Join(" ", board.Rows.Select(r => $"{r.Rank}:{r.Entry.Id}{(r.Detached ? "d" : "")}"));

    public static void Register(TestRunner runner)
    {
        runner.Run("leaderboard ranks by score and keeps the top rows", () =>
        {
            var board = new Leaderboard();
            board.Update(Roster(7), Scores(3, 9, 0, 4, 12, 1, 5), local: 5, count: 5);
            TestRunner.Assert(Order(board) == "1:5 2:2 3:7 4:4 5:1", $"order {Order(board)}");
            TestRunner.Assert(!board.HasDetached && board.Rows.Count(r => r.Local) == 1 && board.Rows[0].Local, "local leader is highlighted in place");
        });

        runner.Run("leaderboard ties share a rank and keep roster order", () =>
        {
            var board = new Leaderboard();
            board.Update(Roster(5), Scores(2, 5, 5, 2, 0), local: 1, count: 5);
            TestRunner.Assert(Order(board) == "1:2 1:3 3:1 3:4 5:5", $"ties {Order(board)}");
            // Ranking again with the same scores, in any dictionary order, changes nothing.
            var shuffled = new Dictionary<int, int> { [5] = 0, [4] = 2, [3] = 5, [2] = 5, [1] = 2 };
            board.Update(Roster(5), shuffled, local: 1, count: 5);
            TestRunner.Assert(Order(board) == "1:2 1:3 3:1 3:4 5:5", $"stable ties {Order(board)}");
        });

        runner.Run("leaderboard shows the local row below the divider with its true rank", () =>
        {
            var board = new Leaderboard();
            board.Update(Roster(16), Enumerable.Range(1, 16).ToDictionary(id => id, id => 20 - id), local: 12, count: 5);
            TestRunner.Assert(board.TopCount == 5 && board.HasDetached && board.Rows.Count == 6, "five rows and the local one");
            var mine = board.Rows[^1];
            TestRunner.Assert(mine is { Local: true, Detached: true, Rank: 12, Score: 8 } && mine.Entry.Id == 12, $"local row {mine}");
            TestRunner.Assert(board.Rows.Take(5).All(r => !r.Local && !r.Detached), "top rows are not local");

            // Tied with fifth place but after it in roster order: below the divider, sharing fifth's rank.
            board.Update(Roster(8), Scores(9, 8, 7, 6, 5, 5, 1, 0), local: 6, count: 5);
            TestRunner.Assert(board.HasDetached && board.Rows[^1] is { Rank: 5, Detached: true } && board.Rows[4].Rank == 5, $"tied local {Order(board)}");

            // Fewer players than rows: everyone, no divider.
            board.Update(Roster(3), Scores(0, 1, 2), local: 1, count: 5);
            TestRunner.Assert(board.Rows.Count == 3 && !board.HasDetached && board.Rows[^1].Local, $"small match {Order(board)}");

            // No rows: hidden, even for the local player.
            board.Update(Roster(8), Scores(0, 0, 0, 0, 0, 0, 0, 9), local: 1, count: 0);
            TestRunner.Assert(board.Rows.Count == 0 && board.TopCount == 0, "zero rows hides the board");
        });

        runner.Run("leaderboard updates slide, flash and pop without flicker", () =>
        {
            var board = new Leaderboard { ChainWindow = 1f };
            var roster = Roster(8);
            var scores = Scores(5, 4, 3, 2, 1, 0, 0, 0);
            board.Update(roster, scores, local: 8, count: 5);
            var first = board.Motion(1)!;
            TestRunner.Assert(first is { Slot: 0, Alpha: 1, Flash: 0 } && board.Motion(8) is { Slot: Leaderboard.DividerGap + 5 }, "first standings appear in place");

            // Same scores every frame: nothing moves or flashes.
            for (var frame = 0; frame < 30; frame++)
            {
                board.Update(roster, scores, local: 8, count: 5);
                board.Advance(1 / 60f, 0.35f, 0.9f);
            }
            TestRunner.Assert(board.Motions.All(m => m.Flash == 0 && m.PopAge > 0.4f) && board.Motion(1)!.Slot == 0 && board.Motion(5)!.Slot == 4, "steady board is still");

            // Player 3 gains 3, passing 2 and 1: it flashes, pops "+3" and slides from slot 2 to 0.
            scores[3] = 6;
            board.Update(roster, scores, local: 8, count: 5);
            var climber = board.Motion(3)!;
            TestRunner.Assert(Order(board).StartsWith("1:3 2:1 3:2", System.StringComparison.Ordinal), $"overtake {Order(board)}");
            TestRunner.Assert(climber is { Flash: 1, PopAmount: 3, PopAge: 0, PopText: "+3", Slot: 2 } && board.Motion(1)!.Flash == 0, "climb flashes and pops");
            board.Advance(0.1f, 0.35f, 0.9f);
            TestRunner.Assert(climber.Slot is > 0.05f and < 2f && board.Motion(1)!.Slot is > 0f and < 1f, $"sliding {climber.Slot}");
            // A second gain inside the chain window adds to the "+N" showing.
            scores[3] = 8;
            board.Update(roster, scores, local: 8, count: 5);
            TestRunner.Assert(climber is { PopAmount: 5, PopText: "+5" }, $"chained pop {climber.PopAmount}");
            for (var frame = 0; frame < 30; frame++) board.Advance(1 / 60f, 0.35f, 0.9f);
            TestRunner.Assert(climber.Slot == 0 && board.Motion(1)!.Slot == 1 && board.Motion(2)!.Slot == 2, "rows reach their slots");

            // A loss tints the score; a new arrival rises in while the row it displaced fades out.
            scores[6] = 2;
            scores[4] = 1;
            board.Update(roster, scores, local: 8, count: 5);
            TestRunner.Assert(board.Motion(4)!.Drop == 1, "loss tints");
            var arrival = board.Motion(6)!;
            var leaving = board.Motion(5)!;
            TestRunner.Assert(arrival is { Alpha: 0, Shown: true } && !leaving.Shown, $"arrival {Order(board)}");
            for (var frame = 0; frame < 60; frame++) board.Advance(1 / 60f, 0.35f, 0.9f);
            TestRunner.Assert(arrival.Alpha == 1 && board.Motion(5) == null && board.Motions.Count == 6, "arrival in, leaver gone");

            // The local player climbing into the top rows leaves the divider slot for its rank.
            scores[8] = 7;
            board.Update(roster, scores, local: 8, count: 5);
            TestRunner.Assert(!board.HasDetached && board.Rows[1] is { Local: true, Rank: 2 } && board.Motion(8)!.Flash == 1, $"local joins {Order(board)}");
            for (var frame = 0; frame < 60; frame++) board.Advance(1 / 60f, 0.35f, 0.9f);
            TestRunner.Assert(board.Motion(8)!.Slot == 1, "local row slides up into place");

            // After a reset (the board was hidden), the standings appear in place again.
            board.Reset();
            board.Update(roster, scores, local: 8, count: 5);
            TestRunner.Assert(board.Motions.Count == 5 && board.Motions.All(m => m.Alpha == 1 && m.Flash == 0 && m.PopAge > 1) && board.Motion(8)!.Slot == 1,
                "reset board shows in place");
        });
    }
}
