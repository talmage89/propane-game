using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using Propane.Core;
using Propane.Dev.UnitTests;
using Propane.Modes;
using Propane.Net;

namespace Propane.Dev.Equivalence;

[UnitSuite(-2)]
public static class WireReferenceTests
{
    private sealed record WireCase(string Id, bool Server, byte[] Bytes);

    private static IEnumerable<WireCase> Cases()
    {
        foreach (var fixture in ProtocolTests.Fixtures())
        {
            // Freeze the input version too: a patch release must not require refreshing codec expectations.
            var message = fixture with
            {
                Payload = fixture.Payload switch
                {
                    HelloMessage hello => hello with { Version = "0.4.0" },
                    WelcomeMessage welcome => welcome with { Version = "0.4.0" },
                    _ => fixture.Payload,
                }
            };
            var server = message.Type is >= Msg.Welcome and <= Msg.ModeSnapshot;
            yield return new($"{message.Type}/{(server ? "server" : "client")}", server, MessageCodec.Encode(message, server));
            if (Protocol.IsRelay(message.Type) && message.Type is not (Msg.Claim or Msg.Puncture or Msg.Explode))
                yield return new($"{message.Type}/server", true, MessageCodec.Encode(message, true));
        }
        var body = new BodyState(BodyIds.DropTankId(0, 0), Transform3D.Identity, Vector3.Up, Vector3.Zero);
        foreach (var mode in new[] { ModeRegistry.TankBankId, ModeRegistry.KingId })
        {
            foreach (var (type, kind, payload) in new (Msg, ushort, object)[]
            {
                (Msg.ModeEvent, ModeSchemas.TankBankKinds.Spill, new TankBankSpill(SpillReason.Hit, new[] { body })),
                (Msg.ModeCommand, ModeSchemas.TankBankKinds.Score, new TankBankScore(9, 2)),
                (Msg.ModeCommand, ModeSchemas.TankBankKinds.SpillAccepted, new TankBankSpill(SpillReason.Thrown, new[] { body })),
                (Msg.ModeCommand, ModeSchemas.TankBankKinds.SpillRefused, new TankBankRefused(new[] { body.Id })),
                (Msg.ModeSnapshot, ModeSchemas.TankBankKinds.Banks, new TankBankBanks(new[] { (2, 9, true), (3, 0, false) })),
            })
            {
                var envelope = new ModeEnvelope(2, 3, mode, 1, type == Msg.ModeSnapshot ? 0 : 2, type == Msg.ModeSnapshot ? 0u : 1u, 17, kind, payload);
                var message = new NetMessage(type, 7, 0, envelope, type == Msg.ModeEvent ? 2 : 0, type == Msg.ModeEvent ? 1u : 0, type == Msg.ModeEvent ? 17u : 0);
                yield return new($"mode/{mode}/{type}/{kind}", type != Msg.ModeEvent, MessageCodec.Encode(message, type != Msg.ModeEvent));
            }
        }
        foreach (var mode in new[] { ModeRegistry.KingId, ModeRegistry.LobbyWorldId })
        {
            var extra = new List<(Msg Type, ushort Kind, object Payload)>
            {
                (Msg.ModeEvent, ModeSchemas.KingKinds.PilePlaced, new KingPilePlaced(2, new[] { Transform3D.Identity })),
                (Msg.ModeCommand, ModeSchemas.KingKinds.PileWanted, new KingPileWanted(2, Vector3.One, 3, 4, 5)),
                (Msg.ModeCommand, ModeSchemas.KingKinds.PileSpawned, new KingPileSpawned(2, 2, new[] { body with { Id = BodyIds.MapTankId(3) } })),
            };
            if (mode == ModeRegistry.KingId)
            {
                extra.Add((Msg.ModeSnapshot, ModeSchemas.KingKinds.Zone,
                new KingState(2, new ModeSpot(1, 2, 3), new ModeSpot(4, 5, 6), 7, 2, new[] { 2 }, 9, 1, 12, 3,
                    new[] { new KingMinted(2, 4) }, 8)));
            }

            foreach (var (type, kind, payload) in extra)
            {
                var message = new NetMessage(type, 7, 0, new ModeEnvelope(2, 3, mode, 1, type == Msg.ModeEvent ? 2 : 0,
                    type == Msg.ModeEvent ? 1u : 0, 17, kind, payload), type == Msg.ModeEvent ? 2 : 0, type == Msg.ModeEvent ? 1u : 0, type == Msg.ModeEvent ? 17u : 0);
                yield return new($"mode/{mode}/{type}/{kind}", type != Msg.ModeEvent, MessageCodec.Encode(message, type != Msg.ModeEvent));
            }
        }
        foreach (var (id, server, bytes) in new[]
        {
            ("hello/debug-only", false, new NetWriter(Msg.Hello).Int(3).String("0.4.0").String("test").String("Test").Int(1).Bool(true).ToArray()),
            ("hello/legacy", false, new NetWriter(Msg.Hello).Int(1).String("0.2.0").String("old").String("Test").Int(0).ToArray()),
            ("welcome/legacy", true, new NetWriter(Msg.Welcome).Int(2).ToArray()),
            ("rejected/legacy", true, new NetWriter(Msg.Rejected).String("Update").ToArray()),
            ("welcome/future", true, new NetWriter(Msg.Welcome).Int(2).String("0.3.0").Int(1234).ToArray()),
        })
        {
            yield return new(id, server, bytes);
        }
    }

    public static void Register(TestRunner runner)
    {
        runner.Run("reference/wire inventory, frozen encode bytes and independent decoded fields", () =>
        {
            var cases = Cases().ToArray();
            TestRunner.Assert(cases.Select(c => c.Id).Distinct().Count() == cases.Length, "duplicate wire fixture id");
            var observations = cases.Select(c => new { c.Id, c.Server, bytes = Convert.ToBase64String(c.Bytes), fields = ReferenceFile.Value(MessageCodec.Decode(c.Bytes, c.Server)) }).ToArray();
            if (DevArgs.Get("write-reference") != null) { ReferenceFile.Check("wire", observations); return; }
            var expected = ReferenceFile.Read("wire");
            ReferenceFile.Compare(ReferenceFile.Element(expected.EnumerateArray().Select(c => c.GetProperty("Id").GetString()).ToArray()),
                ReferenceFile.Element(cases.Select(c => c.Id).ToArray()), "wire.inventory");
            for (var i = 0; i < cases.Length; i++)
            {
                var fixture = cases[i]; var saved = expected[i];
                TestRunner.Assert(saved.GetProperty("Server").GetBoolean() == fixture.Server, $"wire.{fixture.Id}.direction changed");
                var bytes = Convert.FromBase64String(saved.GetProperty("bytes").GetString()!);
                ReferenceFile.Bytes(bytes, fixture.Bytes, "wire." + fixture.Id);
                ReferenceFile.Compare(saved.GetProperty("fields"), ReferenceFile.Element(ReferenceFile.Value(MessageCodec.Decode(bytes, fixture.Server))), $"wire.{fixture.Id}.decode");
            }
        });
        runner.Run("reference/tuner defaults, attributes, declaration and UI order, shared classification", () =>
        {
            using var tuning = new Tuning();
            using var snapshot = tuning.Snapshot();
            var properties = typeof(Tuning).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(p => p.CanWrite && (p.PropertyType == typeof(float) || p.PropertyType == typeof(int) || p.PropertyType == typeof(bool)));
            var schema = properties.Select(p => new
            {
                p.Name,
                type = p.PropertyType.Name,
                value = ReferenceFile.Value(p.GetValue(tuning)),
                shared = snapshot.ContainsKey(p.Name),
                attributes = p.GetCustomAttributesData().Select(a => new
                {
                    type = a.AttributeType.Name,
                    arguments = a.ConstructorArguments.Select(v => ReferenceFile.Value(v.Value)).ToArray(),
                    named = a.NamedArguments.Select(v => new { v.MemberName, value = ReferenceFile.Value(v.TypedValue.Value) }).ToArray()
                }).ToArray()
            }).ToArray();
            var ui = tuning.GetPropertyList().Where(p => p["name"].AsString() != "script" &&
                (p["usage"].AsInt64() & (long)(PropertyUsageFlags.ScriptVariable | PropertyUsageFlags.Group | PropertyUsageFlags.Subgroup)) != 0)
                .Select(p => p.ToDictionary(pair => pair.Key.AsString(), pair => ReferenceFile.Value(pair.Value))).ToArray();
            ReferenceFile.Check("tuners", new { schema, ui, snapshot = ReferenceFile.Value(snapshot) });
        });
    }
}
