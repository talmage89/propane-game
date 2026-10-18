using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Propane.Dev.UnitTests.Fixtures;
using Propane.Modes;
using Propane.Net;
using Propane.Tank;

namespace Propane.Dev.UnitTests;

/// <summary>The fixed mode envelope: codecs, bounds, schemas, ordering, deduplication and causal holding.</summary>
[UnitSuite]
public static class ModeEnvelopeTests
{
    private const byte CausalMode = 200;
    private sealed record Report(int Peer, uint Request);

    private sealed class RecordingWire : IModeWire
    {
        public readonly List<string> Seen = new();
        public void Event(ModeRunner runner, IModeHost host, int peer, ModeEnvelope envelope, double now) => Seen.Add($"event {envelope.EventId}");
        public void Execute(ModeRunner runner, IModeHost host, IReadOnlyList<ModeCommand> commands, double now) { }
        public void Failed(ModeRunner runner, IModeHost host, int peer, ModeEnvelope envelope, double now) => Seen.Add($"failed {envelope.EventId}");
    }

    private static readonly BodyState Body = new(BodySync.DropTankId(0, 0), Transform3D.Identity, Vector3.Up, Vector3.Zero);

    private static byte[] Spill(TankBankSpill spill, uint eventId = 1) =>
        MessageCodec.Encode(ModeWireFixtures.Spill(7, 2, 1, eventId, spill.Reason, spill.Tanks), false);

    /// <summary>A hand-built event header around an arbitrary payload, for malformed and oversized input.</summary>
    private static byte[] Raw(byte mode, ushort schema, ushort kind, int length, byte[] payload, int actor = 2, uint life = 1, uint eventId = 1,
        Msg type = Msg.ModeEvent) => new NetWriter(type, 7).UInt(1).UInt(1).Byte(mode).UShort(schema).Int(actor).UInt(life).UInt(eventId)
        .UShort(kind).UShort((ushort)length).Raw(payload).ToArray();

    private static byte[] SpillPayload(byte reason, params BodyState[] bodies)
    {
        var w = NetWriter.Payload().Byte(reason).Byte((byte)bodies.Length);
        foreach (var body in bodies) MessageCodec.WriteBody(w, body);
        return w.ToArray();
    }

    private static IDisposable RegisterCausalMode(RecordingWire wire)
    {
        var schema = new ModeSchema(CausalMode, 1, new[]
        {
            new ModeKind(1, "Report", ModeDirection.Event, ModeReporter.ActorOwner, 8, r => new Report(r.Int(), r.UInt()),
                (w, p) => { var report = (Report)p; w.Int(report.Peer).UInt(report.Request); },
                p => new ModeDependency(((Report)p).Peer, ((Report)p).Request)),
        });
        return ModeRegistry.RegisterScoped(new ModeBundle(ModeRegistry.TankBank with { Id = CausalMode, Name = "Causal probe" }, schema, () => wire, (replica, _) => new BankClientAdapter(replica)));
    }

    public static void Register(TestRunner runner)
    {
        runner.Run("envelope/every tank bank kind round-trips through the fixed header", () =>
        {
            var spill = new TankBankSpill(SpillReason.Thrown, new[] { Body, Body with { Id = BodySync.DropTankId(0, 1) } });
            var decoded = MessageCodec.Decode(Spill(spill, 9), false);
            TestRunner.Assert(decoded is { Type: Msg.ModeEvent, MatchId: 7, ActorId: 2, LifeId: 1, ActionId: 9 } &&
                decoded.Payload is ModeEnvelope { RoundId: 1, WorldRevision: 1, ModeId: ModeRegistry.TankBankId, Payload: TankBankSpill back } &&
                back.Reason == SpillReason.Thrown && back.Tanks.SequenceEqual(spill.Tanks), "spill event lost header or payload fields");
            foreach (var message in new[]
            {
                ModeWireFixtures.Score(7, 2, 3, -1),
                ModeWireFixtures.Accepted(7, 2, 1, SpillReason.Hit, new[] { Body }),
                new NetMessage(Msg.ModeCommand, 7, 0, ModeWireFixtures.Envelope(2, 1, 50, ModeSchemas.TankBankKinds.SpillRefused, new TankBankRefused(new[] { Body.Id }))),
                new NetMessage(Msg.ModeSnapshot, 7, 0, ModeWireFixtures.Envelope(0, 0, 51, ModeSchemas.TankBankKinds.Banks, new TankBankBanks(Array.Empty<(int, int, bool)>()))),
            })
            {
                var bytes = MessageCodec.Encode(message, true);
                TestRunner.Assert(MessageCodec.Encode(MessageCodec.Decode(bytes, true), true).SequenceEqual(bytes), $"{message.Type} changed in transit");
            }
        });
        runner.Run("envelope/malformed payloads fail in the typed codec", () =>
        {
            var bad = new[]
            {
                Raw(ModeRegistry.TankBankId, 1, 1, 2, new byte[] { 0, 0 }),                                 // no tanks
                Raw(ModeRegistry.TankBankId, 1, 1, 2, new byte[] { 2, 1 }),                                 // unknown reason
                Raw(ModeRegistry.TankBankId, 1, 1, 66, SpillPayload(0, Body)),                              // trailing payload bytes declared
                Raw(ModeRegistry.TankBankId, 1, 1, 130, SpillPayload(0, Body, Body)),                       // duplicate ids
                Raw(ModeRegistry.TankBankId, 1, 1, 66, SpillPayload(0, Body with { Id = 0 })),              // zero id
                Raw(ModeRegistry.TankBankId, 1, 1, 66, SpillPayload(0, Body with { Epoch = 0 })),           // zero epoch
                Raw(ModeRegistry.TankBankId, 1, 1, 66, SpillPayload(0, Body with { Linear = new Vector3(float.NaN, 0, 0) })),
                Raw(ModeRegistry.TankBankId, 1, 1, 66, SpillPayload(0, Body), actor: 0),                    // no reporter
                Raw(ModeRegistry.TankBankId, 1, 1, 66, SpillPayload(0, Body), life: 0),
                Raw(ModeRegistry.TankBankId, 1, 1, 66, SpillPayload(0, Body), eventId: 0),
            };
            bad[2] = bad[2].Append((byte)0).ToArray();
            foreach (var bytes in bad) TestRunner.Throws<InvalidDataException>(() => MessageCodec.Decode(bytes, false));
            TestRunner.Throws<InvalidDataException>(() => MessageCodec.Decode(new NetWriter(Msg.ModeSnapshot, 7).UInt(1).UInt(1).Byte(ModeRegistry.TankBankId)
                .UShort(1).Int(0).UInt(0).UInt(1).UShort(1).UShort(10).Byte(1).Int(2).Int(-1).Bool(true).ToArray(), true));
        });
        runner.Run("envelope/oversized and truncated payloads are bounded per kind", () =>
        {
            var tooMany = Enumerable.Range(0, ModeSchemas.MaxSpill + 1).Select(i => Body with { Id = BodySync.DropTankId(0, i) }).ToArray();
            TestRunner.Throws<InvalidDataException>(() => Spill(new TankBankSpill(SpillReason.Thrown, tooMany)));
            var full = Enumerable.Range(0, ModeSchemas.MaxSpill).Select(i => Body with { Id = BodySync.DropTankId(0, i) }).ToArray();
            var payload = SpillPayload(1, full);
            TestRunner.Assert(MessageCodec.Decode(Raw(ModeRegistry.TankBankId, 1, 1, payload.Length, payload), false) != null, "largest spill rejected");
            var padded = payload.Concat(new byte[1]).ToArray();
            TestRunner.Throws<InvalidDataException>(() => MessageCodec.Decode(Raw(ModeRegistry.TankBankId, 1, 1, padded.Length, padded), false));
            TestRunner.Throws<InvalidDataException>(() => MessageCodec.Decode(Raw(ModeRegistry.TankBankId, 1, 1, 200, SpillPayload(0, Body)), false));
            var encoded = Spill(new TankBankSpill(SpillReason.Hit, new[] { Body }));
            for (var length = 1; length < encoded.Length; length++)
                TestRunner.Throws<Exception>(() => MessageCodec.Decode(encoded[..length], false));
        });
        runner.Run("envelope/unknown schemas, kinds and directions fail explicitly", () =>
        {
            var payload = SpillPayload(0, Body);
            var unknownMode = Assert(() => MessageCodec.Decode(Raw(99, 1, 1, payload.Length, payload), false));
            TestRunner.Assert(unknownMode.Contains("unknown mode schema 99/1"), $"unknown mode was not named: {unknownMode}");
            TestRunner.Assert(Assert(() => MessageCodec.Decode(Raw(ModeRegistry.TankBankId, 2, 1, payload.Length, payload), false))
                .Contains("unknown mode schema 2/2"), "unknown schema version was not named");
            TestRunner.Assert(Assert(() => MessageCodec.Decode(Raw(ModeRegistry.TankBankId, 1, 9, payload.Length, payload), false))
                .Contains("unknown Event kind 9"), "unknown kind was not named");
            // Command kind 3 exists, but not as an event; and clients cannot send commands at all.
            TestRunner.Throws<InvalidDataException>(() => MessageCodec.Decode(Raw(ModeRegistry.TankBankId, 1, 3, 5, new byte[] { 1, 1, 0, 0, 0 }), false));
            TestRunner.Throws<InvalidDataException>(() => MessageCodec.Decode(MessageCodec.Encode(ModeWireFixtures.Score(7, 2, 1, 1), true), false));
            TestRunner.Throws<InvalidDataException>(() => MessageCodec.Decode(Spill(new TankBankSpill(SpillReason.Hit, new[] { Body })), true));
            TestRunner.Throws<InvalidDataException>(() => MessageCodec.Encode(new NetMessage(Msg.ModeEvent, 7, 0,
                ModeWireFixtures.Envelope(2, 1, 1, 9, new TankBankScore(1, 1))), false));
            var setup = (MatchSetup)ProtocolTests.Fixtures().Single(f => f.Type == Msg.MatchStart).Payload;
            TestRunner.Throws<InvalidDataException>(() => MessageCodec.Decode(MessageCodec.Encode(new NetMessage(Msg.MatchStart, 7, 0,
                setup with { ModeId = 99 }), true), true));
        });
        runner.Run("envelope/client command order drops duplicates and overtaken commands", () =>
        {
            var order = new ModeCommandOrder();
            TestRunner.Assert(order.Accept(3) && !order.Accept(3) && !order.Accept(2) && order.Accept(7) && order.Last == 7, "command order wrong");
        });
        runner.Run("envelope/server deduplicates repeated and out-of-order spill events", () =>
        {
            using var f = new ServerFixture();
            var actor = f.ActorId(2);
            byte[] Ticket(uint eventId, int counter) => MessageCodec.Encode(ModeWireFixtures.Spill(f.Id, actor, 1, eventId, SpillReason.Hit,
                new[] { Body with { Id = BodySync.DropTankId(0, counter) } }), false);
            f.Receive(Ticket(5, 0));
            f.AssertScore(2, 3, -1);
            f.Packets.Clear();
            f.Receive(Ticket(5, 0));
            f.Receive(Ticket(4, 1));
            TestRunner.Assert(f.Packets.Count == 0, "a repeated or overtaken spill reached the rules");
            f.Receive(MessageCodec.Encode(ModeWireFixtures.Spill(f.Id, actor, 2, 6, SpillReason.Hit, new[] { Body with { Id = BodySync.DropTankId(0, 2) } }), false));
            TestRunner.Assert(f.Packets.Count == 0, "a spill from another life was accepted");
            f.Receive(MessageCodec.Encode(ModeWireFixtures.Spill(f.Id, actor, 1, 7, SpillReason.Hit, new[] { Body with { Id = BodySync.DropTankId(0, 3) } }), false), peer: 3);
            TestRunner.Assert(f.Packets.Count == 0, "another peer reported this actor's spill");
            f.Receive(Ticket(8, 4));
            f.AssertScore(2, 2, -1);
        });
        runner.Run("envelope/server refuses a spill from another round or world with its ids", () =>
        {
            using var f = new ServerFixture();
            var spill = ModeWireFixtures.Spill(f.Id, f.ActorId(2), 1, 1, SpillReason.Hit, new[] { Body });
            f.Receive(MessageCodec.Encode(spill with { Payload = ((ModeEnvelope)spill.Payload) with { RoundId = 2 } }, false));
            TestRunner.Assert(f.Packets.Single() is { Peer: 2 } only && ModeWireFixtures.Refused(only.Message)?.Single() == Body.Id,
                "a stale-round spill was accepted or left its prediction");
        });
        runner.Run("envelope/round start publishes the banks as one snapshot", () =>
        {
            using var f = new ServerFixture(playing: false);
            f.Receive(new NetWriter(Msg.Loaded, f.Id));
            f.Receive(new NetWriter(Msg.Loaded, f.Id), peer: 3);
            f.Advance();
            f.Packets.Clear();
            f.Advance();
            var types = f.Packets.Where(p => p.Peer == 2).Select(p => p.Message.Type).ToArray();
            TestRunner.Assert(types.SequenceEqual(new[] { Msg.MatchGo, Msg.ModeSnapshot }), $"round start sent {string.Join(",", types)}");
            var banks = (TankBankBanks)((ModeEnvelope)f.Packets.First(p => p.Message.Type == Msg.ModeSnapshot).Message.Payload).Payload;
            TestRunner.Assert(banks.Banks.All(b => b.Bank == 4 && b.Present) && banks.Banks.Count == 2, "snapshot banks wrong");
        });
        runner.Run("envelope/client applies commands once, in order", () =>
        {
            using var f = new AuthorityFixture(runner);
            var me = f.Match.Player.ActorId;
            var newer = ModeWireFixtures.Score(f.Server.Id, me, 5, 5);
            var older = ModeWireFixtures.Score(f.Server.Id, me, 2, -3);
            f.Match.OnMessage(older with { Payload = ((ModeEnvelope)older.Payload) with { EventId = ((ModeEnvelope)newer.Payload).EventId + 100 } });
            f.Match.OnMessage(newer);
            TestRunner.Assert(f.Match.MyScore == 2, "an overtaken command replaced a newer one");
            var again = ModeWireFixtures.Score(f.Server.Id, me, 6, 4);
            var id = ((ModeEnvelope)again.Payload).EventId + 200;
            f.Match.OnMessage(again with { Payload = ((ModeEnvelope)again.Payload) with { EventId = id } });
            f.Match.OnMessage(ModeWireFixtures.Score(f.Server.Id, me, 1, -5) is var dup ? dup with { Payload = ((ModeEnvelope)dup.Payload) with { EventId = id } } : dup);
            TestRunner.Assert(f.Match.MyScore == 6, "a duplicate command applied twice");
            var foreign = ModeWireFixtures.Score(f.Server.Id, me, 9, 3);
            f.Match.OnMessage(foreign with { Payload = ((ModeEnvelope)foreign.Payload) with { EventId = id + 1, WorldRevision = 2 } });
            TestRunner.Assert(f.Match.MyScore == 6, "a command for another world revision applied");
        });
        runner.Run("envelope/causal gate holds, releases, fails and bounds events", () =>
        {
            var gate = new ModeEventGate(capacity: 2, maxAge: 2);
            NetMessage Message(uint id) => new(Msg.ModeEvent, 7, 0, id, 2, 1, id);
            var blast = new ModeDependency(2, 9);
            TestRunner.Assert(gate.Hold(2, Message(1), blast, 0) && gate.Hold(2, Message(2), blast, 1) && !gate.Hold(2, Message(3), blast, 1),
                "gate exceeded its capacity");
            TestRunner.Assert(gate.Release(new ModeDependency(3, 9)).Count == 0 && gate.Release(blast).Select(h => h.Message.ActionId).SequenceEqual(new uint[] { 1, 2 }),
                "release ignored the dependency or arrival order");
            gate.Hold(2, Message(4), blast, 10);
            gate.Hold(2, Message(5), new ModeDependency(2, 10), 11);
            TestRunner.Assert(gate.Expire(11.9).Count == 0 && gate.Expire(12).Single().Message.ActionId == 4, "expiry wrong");
            TestRunner.Assert(gate.Fail(new ModeDependency(2, 10)).Single().Message.ActionId == 5 && gate.Count == 0, "failure wrong");
        });
        runner.Run("envelope/server holds a victim report until its blast is accepted", () =>
        {
            var wire = new RecordingWire();
            using var registration = RegisterCausalMode(wire);
            using var f = new ServerFixture(mode: ModeRegistry.Find(CausalMode, 1));
            var actor = f.ActorId(2);
            byte[] Report(uint eventId, uint request) => MessageCodec.Encode(new NetMessage(Msg.ModeEvent, f.Id, 0,
                new ModeEnvelope(1, 1, CausalMode, 1, actor, 1, eventId, 1, new Report(3, request))), false);
            f.Receive(Report(1, 1), peer: 2);
            TestRunner.Assert(wire.Seen.Count == 0, "a report ran before its blast");
            var explosion = new ExplodeMessage(BodySync.MapTankId(0), Vector3.One, Request: 1);
            f.Receive(MessageCodec.Encode(new NetMessage(Msg.Explode, f.Id, 0, explosion, f.ActorId(3), 1, 1), false), peer: 3);
            TestRunner.Assert(wire.Seen.SequenceEqual(new[] { "event 1" }), "the accepted blast did not release its report");
            f.Receive(Report(2, 1), peer: 2);
            TestRunner.Assert(wire.Seen.Last() == "event 2", "a report whose blast was already accepted waited");
            f.Receive(Report(3, 2), peer: 2);
            f.Receive(MessageCodec.Encode(new NetMessage(Msg.Explode, f.Id, 0, explosion with { Request = 2 }, f.ActorId(3), 1, 2), false), peer: 3);
            TestRunner.Assert(wire.Seen.Last() == "failed 3", "a refused blast left its report waiting");
            f.Receive(Report(4, 3), peer: 2);
            f.Advance(2);
            TestRunner.Assert(wire.Seen.Last() == "failed 4", "a report outlived the gate's age bound");
        });
    }

    private static string Assert(Action action)
    {
        try { action(); }
        catch (InvalidDataException e) { return e.Message; }
        throw new InvalidOperationException("expected InvalidDataException");
    }
}
