using System;
using System.IO;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Dev.UnitTests.Fixtures;
using Propane.Modes;
using Propane.Net;

namespace Propane.Dev.UnitTests;

/// <summary>Real receive boundaries, native resource retirement, peer survival and actual diagnostic classification.</summary>
[UnitSuite]
public static class DispatchBoundaryTests
{
    public static void Register(TestRunner runner)
    {
        runner.Run("dispatch/malformed corpus leaves the active run and peer intact", () =>
        {
            using var f = new ServerRunFixture();
            f.Start();
            var run = f.Runs.Single();
            using var log = new NetLogCapture();
            var corpus = new[]
            {
                Array.Empty<byte>(), new byte[] { 255 }, new byte[] { (byte)Msg.JoinLobby },
                new NetWriter(Msg.SetCapacity).Int(-1).ToArray(),
                new NetWriter(Msg.SetMode).Byte(255).Float(float.NaN).Int(0).ToArray(),
                new NetWriter(Msg.SetProfile).Raw(new byte[] { 255, 255, 255, 255, 255 }).ToArray(),
            };
            var count = f.Driver.Packets.Count;
            foreach (var bytes in corpus) f.Driver.Receive(bytes);
            run.AssertLive();
            TestRunner.Assert(f.Driver.Packets.Count == count && log.Lines.Count(l => l.Contains("bad ", StringComparison.Ordinal)) == corpus.Length,
                "malformed packet mutated state or lost its rejection");
            f.Driver.Receive(new NetWriter(Msg.Ping).Double(1));
            TestRunner.Assert(f.Driver.Packets.Last().Message.Type == Msg.Pong, "malformed packet dropped its peer");
        });
        // Before the production change this opt-in mode proves the defect without marking it as acceptance.
        runner.Run("dispatch/handler failures retire their owner without blaming peers", () =>
        {
            var parent = DevArgs.Get("dispatch-parent") != null;
            foreach (var afterMutation in new[] { false, true })
            {
                using var log = new NetLogCapture();
                var client = new NetClient();
                try
                {
                    Handshake.Welcome(client, 2);
                    var mutation = 0;
                    client.NoticeReceived += _ =>
                    {
                        if (afterMutation) { client.SetProfile("Mutated", AppearanceRecord.FromPalette(1)); mutation++; }
                        throw new InvalidDataException("injected client handler failure");
                    };
                    client.Handle(new NetWriter(Msg.Notice).String("probe").ToArray());
                    TestRunner.Assert(mutation == (afterMutation ? 1 : 0) && client.PlayerName == (afterMutation ? "Mutated" : "Player"),
                        "client handler did not run at the intended boundary");
                    TestRunner.Assert((client.State == NetClient.Status.Offline) != parent, "client failure retirement did not match the selected expectation");
                    TestRunner.Assert(log.Lines.Any(l => l.Contains("bad message", StringComparison.Ordinal)) == parent,
                        "client handler failure classification changed unexpectedly");
                    TestRunner.Assert(log.Lines.Any(l => l.Contains("injected client handler failure", StringComparison.Ordinal)), "client cause lost");
                }
                finally { client.Free(); }

                log.Lines.Clear();
                var bundle = ModeRegistry.Shipped.Find(ModeRegistry.TankBankId, 1)!;
                var wire = new FailureWire(afterMutation);
                using var registration = ModeRegistry.RegisterScoped(bundle with { CreateServerWire = () => wire });
                using var f = new ServerRunFixture();
                f.Start();
                var failed = f.Runs.Single();
                f.Driver.Connect(4, 2, true);
                f.Driver.CreateLobby(4);
                f.Driver.StartMatch(f.Tuning, 4);
                var unrelated = f.Runs.Last();
                var body = new BodyState(BodyIds.DropTankId(0, 0), Transform3D.Identity, Vector3.Zero, Vector3.Zero);
                wire.Armed = true;
                var packet = new NetMessage(Msg.ModeEvent, failed.Run.Id, 0, new ModeEnvelope(1, 1, ModeRegistry.TankBankId, 1,
                    1, 1, 1, ModeSchemas.TankBankKinds.Spill, new TankBankSpill(SpillReason.Thrown, new[] { body })));
                f.Driver.Receive(MessageCodec.Encode(packet, false));
                TestRunner.Assert(((IModeHost)failed.Run).Body(body.Id) != null == afterMutation, "server mutation probe did not reach the ledger");
                if (parent) failed.AssertLive(); else failed.AssertOnce();
                unrelated.AssertLive();
                TestRunner.Assert(log.Lines.Any(l => l.Contains("bad ModeEvent", StringComparison.Ordinal)) == parent,
                    "server handler failure classification changed unexpectedly");
                TestRunner.Assert(log.Lines.Any(l => l.Contains("injected server handler failure", StringComparison.Ordinal)), "server cause lost");
                f.Driver.Receive(new NetWriter(Msg.Ping).Double(1));
                TestRunner.Assert(f.Driver.Packets.Last().Message.Type == Msg.Pong, "server blamed or dropped its peer");
                f.Driver.Advance(.1);
                unrelated.AssertLive();
            }
        });
        foreach (var after in new[] { false, true })
        {
            runner.Run($"dispatch/server tick failure (after mutation {after})", () =>
            {
                var bundle = ModeRegistry.Shipped.Find(ModeRegistry.TankBankId, 1)!;
                var wires = new System.Collections.Generic.List<FailureWire>();
                using var registration = ModeRegistry.RegisterScoped(bundle with
                { CreateServerWire = () => { var wire = new FailureWire(after); wires.Add(wire); return wire; } });
                using var f = new ServerRunFixture();
                f.Start();
                var failed = f.Runs.Single();
                f.Driver.Connect(4, 2, true); f.Driver.CreateLobby(4);
                f.Driver.Receive(new NetWriter(Msg.SetMatchLength).Float(10), 4);
                f.Driver.StartMatch(f.Tuning, 4);
                var unrelated = f.Runs.Last();
                f.Driver.Receive(new NetWriter(Msg.Loaded, unrelated.Run.Id), 4);
                f.Driver.Advance(); f.Driver.Advance();
                var before = ((IModeHost)failed.Run).LiveTanks;
                wires[0].TickFailure = wires[0].Armed = true;
                using var log = new NetLogCapture();
                f.Driver.Advance(11);
                failed.AssertOnce(); unrelated.AssertLive();
                TestRunner.Assert(((IModeHost)failed.Run).LiveTanks == before + (after ? 1 : 0), "tick mutation boundary not exercised");
                TestRunner.Assert(f.Driver.Packets.Any(p => p.Peer == 4 && p.Message.Type == Msg.MatchEnd), "unrelated lobby stopped ticking");
                TestRunner.Assert(log.Lines.Any(l => l.Contains("during tick", StringComparison.Ordinal) && l.Contains("injected tick handler failure", StringComparison.Ordinal)), "tick cause lost");
            });
        }
        runner.Run("dispatch/departure failure completes creator cleanup without malformed state", () =>
        {
            var bundle = ModeRegistry.Shipped.Find(ModeRegistry.TankBankId, 1)!;
            var wire = new FailureWire(false);
            using var registration = ModeRegistry.RegisterScoped(bundle with { CreateServerWire = () => wire });
            using var f = new ServerRunFixture();
            f.Start();
            var run = f.Runs.Single();
            wire.TickFailure = wire.Armed = true;
            using var log = new NetLogCapture();
            f.Server.OnDisconnected(2);
            run.AssertOnce();
            TestRunner.Assert(f.Driver.Packets.Any(p => p.Peer == 3 && p.Message.Payload is LobbyInfo { CreatorId: 3, WorldId: 0, Phase: LobbyPhase.Waiting }),
                "departure failure did not finish the normal creator/phase cleanup");
            TestRunner.Assert(!log.Lines.Any(l => l.Contains("failure notification", StringComparison.Ordinal)), "server emitted malformed failure state");
            TestRunner.Assert(log.Lines.Any(l => l.Contains("during departure", StringComparison.Ordinal) && l.Contains("injected tick handler failure", StringComparison.Ordinal)), "departure cause lost");
        });
        runner.Run("dispatch/construction preserves both original and cleanup causes", () =>
        {
            var mode = ModeRegistry.TankBank with
            {
                Supply = ModeRegistry.TankBank.Supply with
                { Suburb = (_, _) => throw new InvalidOperationException("injected original construction failure") }
            };
            using var f = new ServerRunFixture(mode: mode);
            f.Server.RunCreated += run => run.Retired += () => throw new InvalidOperationException("injected retirement callback failure");
            using var log = new NetLogCapture();
            f.Driver.StartMatch(f.Tuning);
            f.Runs.Single().AssertOnce();
            TestRunner.Assert(log.Lines.Any(l => l.Contains("injected original construction failure", StringComparison.Ordinal) &&
                l.Contains("injected retirement callback failure", StringComparison.Ordinal)), "cleanup replaced the original construction failure");
        });
    }

    private sealed class FailureWire(bool afterMutation) : IModeWire
    {
        public bool Armed, TickFailure;
        private readonly TankBankWire inner = new();
        public void Event(ModeRunner runner, IModeHost host, int peer, ModeEnvelope envelope, double now)
        {
            if (!Armed) { inner.Event(runner, host, peer, envelope, now); return; }
            if (afterMutation) host.RegisterEntities(envelope.ActorId, peer, ((TankBankSpill)envelope.Payload).Tanks);
            throw new InvalidDataException("injected server handler failure");
        }
        public void Execute(ModeRunner runner, IModeHost host, System.Collections.Generic.IReadOnlyList<ModeCommand> commands, double now)
        {
            if (!Armed || !TickFailure) { inner.Execute(runner, host, commands, now); return; }
            if (afterMutation) host.SpawnWorldTanks(new[] { Transform3D.Identity });
            throw new InvalidDataException("injected tick handler failure");
        }
        public void Failed(ModeRunner runner, IModeHost host, int peer, ModeEnvelope envelope, double now) => inner.Failed(runner, host, peer, envelope, now);
    }
}
