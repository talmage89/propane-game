using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Propane.Core;
using Propane.Dev.UnitTests.Fixtures;
using Propane.Modes;
using Propane.Net;

namespace Propane.Dev.UnitTests;

[UnitSuite]
public static class DecodeContextTests
{
    public static void Register(TestRunner runner)
    {
        runner.Run("decode/owner resolvers preserve validation and rejection order", () =>
        {
            var calls = new List<string>();
            var codec = new PacketCodec((id, version) => { calls.Add("schema"); return ModeRegistry.Schema(id, version); },
                id => { calls.Add("eligibility"); return ModeRegistry.ForLobby(id) != null; },
                (values, complete) => { calls.Add(complete ? "snapshot" : "tuner"); Tuning.ValidateSnapshot(values, complete); });
            TestRunner.Throws<InvalidDataException>(() => codec.Decode(new NetWriter(Msg.SetMode).Byte(255).Float(float.NaN).Int(0).ToArray(), false));
            TestRunner.Assert(calls.SequenceEqual(new[] { "eligibility" }), "lobby eligibility moved past scalar decoding");
            calls.Clear();
            TestRunner.Throws<InvalidDataException>(() => codec.Decode(new NetWriter(Msg.TuningSet, 1).String("unknown").Scalar(float.NaN).ToArray(), false));
            TestRunner.Assert(calls.Count == 0, "tuner validation ran before scalar validation");
            TestRunner.Throws<InvalidDataException>(() => codec.Decode(new NetWriter(Msg.TuningSet, 1).String(nameof(Tuning.BlastRadius)).Scalar(4f).Byte(1).ToArray(), false));
            TestRunner.Assert(calls.SequenceEqual(new[] { "tuner" }), "tuner validation moved past trailing-byte rejection");
            calls.Clear();
            var setup = (MatchSetup)ProtocolTests.Fixtures().Single(m => m.Type == Msg.MatchStart).Payload;
            var invalid = setup with { ModeId = 255, Plan = new byte[] { 255 }, CreatorId = 999 };
            TestRunner.Throws<InvalidDataException>(() => codec.Decode(MessageCodec.Encode(new NetMessage(Msg.MatchStart, setup.Id, 0, invalid), true), true));
            TestRunner.Assert(calls.SequenceEqual(new[] { "snapshot", "schema" }), "setup schema validation moved relative to tuner/capability/plan validation");
        });
        runner.Run("decode/captured schemas outlive registration without retaining mutable catalog selection", () =>
        {
            const byte synthetic = 240;
            var definition = ModeRegistry.TankBank with { Id = synthetic };
            var schema = ModeSchemas.TankBank() with { ModeId = synthetic };
            PacketCodec captured;
            var packet = new NetMessage(Msg.ModeCommand, 1, 0,
                new ModeEnvelope(1, 1, synthetic, 1, 1, 1, 1, ModeSchemas.TankBankKinds.Score, new TankBankScore(1, 1)));
            byte[] bytes;
            using (ModeRegistry.RegisterScoped(new ModeBundle(definition, schema, () => new TankBankWire(), (replica, _) => new BankClientAdapter(replica))))
            {
                captured = ModeRegistry.Codec;
                bytes = captured.Encode(packet, true);
            }
            TestRunner.Assert(captured.Decode(bytes, true).Payload is ModeEnvelope { ModeId: synthetic }, "owner silently changed schema selection");
            TestRunner.Throws<InvalidDataException>(() => MessageCodec.Decode(bytes, true));
        });
        runner.Run("decode/admission rejection retains the consumed actor sequence", () =>
        {
            using var f = new ServerFixture();
            using var log = new NetLogCapture();
            var bad = new NetMessage(Msg.Shot, f.Id, 0, new ShotMessage(Godot.Vector3.Zero, Godot.Vector3.Up, Godot.Vector3.Forward, 0, 999, -1), f.ActorId(2), 1, 1);
            f.Receive(MessageCodec.Encode(bad, false));
            var valid = bad with { Payload = ((ShotMessage)bad.Payload) with { Victim = 0 } };
            f.Receive(MessageCodec.Encode(valid, false));
            TestRunner.Assert(f.Packets.Count == 0, "admission rejection stopped consuming its sequence");
            f.Receive(MessageCodec.Encode(valid with { ActionId = 2 }, false));
            TestRunner.Assert(f.Packets.Count == 2 && f.Packets.All(p => p.Message.Type == Msg.Shot), "admission rejection retired a healthy owner");
            TestRunner.Assert(log.Lines.Count(l => l.Contains("bad Shot message from 2: unknown shot target", StringComparison.Ordinal)) == 1,
                "admission diagnostic/order changed");
        });
    }
}
