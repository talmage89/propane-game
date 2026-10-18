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
        runner.Run("dispatch/injected failures have explicit parent characterization", () =>
        {
            if (DevArgs.Get("dispatch-parent") == null && DevArgs.Get("dispatch-proof") == null) return;
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
                        if (afterMutation) mutation++;
                        throw new InvalidDataException("injected client handler failure");
                    };
                    client.Handle(new NetWriter(Msg.Notice).String("probe").ToArray());
                    TestRunner.Assert(mutation == (afterMutation ? 1 : 0), "client handler did not run at the intended boundary");
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
    }

    private sealed class FailureWire(bool afterMutation) : IModeWire
    {
        public bool Armed;
        private readonly TankBankWire inner = new();
        public void Event(ModeRunner runner, IModeHost host, int peer, ModeEnvelope envelope, double now)
        {
            if (!Armed) { inner.Event(runner, host, peer, envelope, now); return; }
            if (afterMutation) host.RegisterEntities(envelope.ActorId, peer, ((TankBankSpill)envelope.Payload).Tanks);
            throw new InvalidDataException("injected server handler failure");
        }
        public void Execute(ModeRunner runner, IModeHost host, System.Collections.Generic.IReadOnlyList<ModeCommand> commands, double now) =>
            inner.Execute(runner, host, commands, now);
        public void Failed(ModeRunner runner, IModeHost host, int peer, ModeEnvelope envelope, double now) => inner.Failed(runner, host, peer, envelope, now);
    }
}
