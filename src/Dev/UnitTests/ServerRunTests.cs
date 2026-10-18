using System;
using System.Linq;
using Godot;
using Propane.Dev.UnitTests.Fixtures;
using Propane.Modes;
using Propane.Net;

namespace Propane.Dev.UnitTests;

/// <summary>Each exit proves exactly one native tuning release, detached routing and safe repeated retirement.</summary>
[UnitSuite]
public static class ServerRunTests
{
    public static void Register(TestRunner runner)
    {
        if (DevArgs.Get("server-native") != null)
        {
            foreach (var departure in new[] { false, true })
            {
                runner.Run($"server run/native exhaustion (departure {departure})", () =>
                {
                    using var f = new ServerNativeFixture(() => departure
                        ? new ParticipantRoster(firstRevision: uint.MaxValue - 2)
                        : new ParticipantRoster(firstLife: uint.MaxValue));
                    if (!departure) f.Start();
                    var run = f.Runs.Last();
                    if (departure)
                    {
                        f.Send(0, new NetWriter(Msg.LeaveLobby));
                    }
                    else
                    {
                        f.Clock.Seconds += 11;
                        f.Wait(() => f.Packets.Count(p => p.Message.Type == Msg.MatchEnd) == 2);
                        run.AssertLive();
                        f.Clock.Seconds += 2;
                    }
                    f.Wait(() => f.Packets.Any(p => p.Client == 1 && p.Message.Type == Msg.Rejected));
                    run.AssertOnce();
                    if (!departure)
                    {
                        f.Wait(() => f.Packets.Count(p => p.Message.Type == Msg.Rejected) == 2);
                        foreach (var client in new[] { 0, 1 })
                        {
                            var terminal = f.Packets.Where(p => p.Client == client && p.Message.Type is Msg.MatchEnd or Msg.Rejected)
                                .Select(p => p.Message.Type);
                            TestRunner.Assert(terminal.SequenceEqual(new[] { Msg.MatchEnd, Msg.Rejected }), "native terminal message order changed");
                        }
                    }
                    GD.Print($"[server-native] PASS exhaustion departure={departure} release={run.Count}");
                });
            }
        }
        foreach (var worlds in new[] { false, true })
        {
            runner.Run($"server run/results return (worlds {worlds})", () =>
            {
                using var f = new ServerRunFixture(worlds);
                f.Start();
                var match = f.Runs.Last();
                f.Driver.Advance(11);
                match.AssertLive(); // MatchEnd and Results still own tuning.
                TestRunner.Assert(f.Driver.Packets.Any(p => p.Message.Type == Msg.MatchEnd), "no terminal standings");
                f.Driver.Advance(2);
                match.AssertOnce();
                f.Driver.Advance(20);
                match.AssertOnce();
                if (worlds) f.Runs.Last().AssertLive();
                var before = f.Driver.Packets.Count;
                f.Driver.Receive(new NetWriter(Msg.TuningSet, match.Run.Id).String("BlastRadius").Scalar(5f));
                TestRunner.Assert(f.Driver.Packets.Count == before, "retired match still routes traffic");
            });
            runner.Run($"server run/empty {(worlds ? "roaming" : "match")} lobby", () =>
            {
                using var f = new ServerRunFixture(worlds);
                if (!worlds) f.Start();
                var run = f.Runs.Last();
                f.Server.OnDisconnected(2);
                run.AssertLive();
                f.Server.OnDisconnected(3);
                run.AssertOnce();
                f.Server.OnDisconnected(3);
                f.Driver.Advance(100);
                run.AssertOnce();
            });
        }
        runner.Run("server run/staging replaces roaming only when the stage ends", () =>
        {
            using var f = new ServerRunFixture(worlds: true);
            var world = f.Runs.Single();
            f.Tuning.LobbyStageTime = 2;
            f.Start();
            f.Driver.Advance(1);
            world.AssertLive();
            f.Driver.Advance(2);
            world.AssertOnce();
            TestRunner.Assert(f.Runs.Count == 2 && !f.Runs.Last().Run.IsWorld, "stage did not replace world with match");
            f.Runs.Last().AssertLive();
        });
        foreach (var exhaustion in new[] { "life", "revision", "departure", "roaming-departure" })
        {
            runner.Run($"server run/identity exhaustion {exhaustion}", () =>
            {
                using var f = new ServerRunFixture(worlds: exhaustion == "roaming-departure", roster: () => exhaustion == "life"
                    ? new ParticipantRoster(firstLife: uint.MaxValue)
                    : new ParticipantRoster(firstRevision: uint.MaxValue - 2));
                if (exhaustion != "roaming-departure") f.Start();
                var run = f.Runs.Last();
                // A rejected peer can synchronously disconnect; routes must already be detached.
                var capture = f.Server.SendOverride!;
                f.Server.SendOverride = (peer, bytes, traffic) =>
                {
                    capture(peer, bytes, traffic);
                    if (MessageCodec.Decode(bytes, true).Type == Msg.Rejected) f.Server.OnDisconnected(peer);
                };
                if (exhaustion.Contains("departure", StringComparison.Ordinal))
                {
                    f.Server.OnDisconnected(2);
                }
                else { f.Driver.Advance(11); run.AssertLive(); f.Driver.Advance(2); }
                run.AssertOnce();
                f.Driver.Advance(100);
                run.AssertOnce();
                TestRunner.Assert(f.Driver.Packets.Any(p => p.Message.Type == Msg.Rejected), "exhaustion lost terminal rejection");
            });
        }
        foreach (var failure in new[] { "generation", "rules", "loading", "departure" })
        {
            runner.Run($"server run/construction failure during {failure}", () =>
            {
                var mode = ModeRegistry.TankBank with
                {
                    Supply = failure == "generation" ? ModeRegistry.TankBank.Supply with
                    { Suburb = (_, _) => throw new InvalidOperationException("injected generation failure") } : ModeRegistry.TankBank.Supply,
                    CreateRules = failure == "rules" ? () => throw new InvalidOperationException("injected rule failure") : ModeRegistry.TankBank.CreateRules,
                };
                using var f = new ServerRunFixture(mode: mode);
                if (failure is "loading" or "departure")
                {
                    var capture = f.Server.SendOverride!;
                    f.Server.SendOverride = (peer, bytes, traffic) =>
                    {
                        if (MessageCodec.Decode(bytes, true).Type == Msg.MatchStart && failure == "loading")
                        {
                            throw new InvalidOperationException("injected start delivery failure");
                        }
                        capture(peer, bytes, traffic);
                        if (MessageCodec.Decode(bytes, true).Type == Msg.MatchStart && peer == 2 && failure == "departure")
                        {
                            f.Server.OnDisconnected(3);
                        }
                    };
                }
                f.Driver.StartMatch(f.Tuning);
                f.Runs.Single().AssertOnce();
                f.Driver.Advance(100); // Failed construction cannot leave a ticking run.
                f.Runs.Single().AssertOnce();
            });
        }
        foreach (var afterAttach in new[] { false, true })
        {
            runner.Run($"server run/roaming construction failure (attached {afterAttach})", () =>
            {
                try
                {
                    ModeWires.Register(ModeRegistry.LobbyWorldId, () => afterAttach
                        ? new FailingWorldWire()
                        : throw new InvalidOperationException("injected world initialization failure"));
                    using var f = new ServerRunFixture(worlds: true);
                    f.Runs.Single().AssertOnce();
                    f.Driver.Advance(100);
                    f.Runs.Single().AssertOnce();
                }
                finally { ModeWires.Register(ModeRegistry.LobbyWorldId, () => new LobbyWire()); }
            });
        }
        foreach (var worlds in new[] { false, true })
        {
            runner.Run($"server run/shutdown drains every lobby (worlds {worlds})", () =>
            {
                using var f = new ServerRunFixture(worlds);
                f.Start();
                // A second lobby exercises draining more than the last active run.
                f.Driver.Connect(4, 2, true);
                f.Driver.CreateLobby(4);
                if (!worlds) f.Driver.StartMatch(f.Tuning, 4);
                var active = f.Runs.Where(r => !r.Run.IsRetired).ToArray();
                TestRunner.Assert(active.Length == 2, "shutdown fixture needs two active lobbies");
                f.Server._ExitTree();
                foreach (var run in active) run.AssertOnce();
                f.Server._ExitTree();
                foreach (var run in active) run.AssertOnce();
            });
        }
        runner.Run("server run/free without entering the tree drains resources", () =>
        {
            var f = new ServerRunFixture(worlds: true);
            var run = f.Runs.Single();
            try { f.Server.Free(); run.AssertOnce(); }
            finally { f.Tuning.Dispose(); }
        });
    }

    private sealed class FailingWorldWire : IModeWire
    {
        public void Event(ModeRunner runner, IModeHost host, int peer, ModeEnvelope envelope, double now) { }
        public void Execute(ModeRunner runner, IModeHost host, System.Collections.Generic.IReadOnlyList<ModeCommand> commands, double now) =>
            throw new InvalidOperationException("injected world entry failure");
        public void Failed(ModeRunner runner, IModeHost host, int peer, ModeEnvelope envelope, double now) { }
    }
}
