using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Godot;
using Propane.Net;
using Propane.World.Plan;

namespace Propane.Dev.UnitTests;

public static class PlanTests
{
    public static SuburbPlan Fixture()
    {
        var plan = new SuburbPlan { Seed = 23, Bounds = new Rect2(-10, -10, 20, 20) };
        plan.Spawns.Add(new SpawnPoint(Vector2.Zero, 0));
        foreach (var formation in Enum.GetValues<PileFormation>())
        {
            plan.Tanks.Add(new TankPlacement(new Vector2((int)formation, 0), 0.7f, 0.156f, "test",
                new Vector3(Mathf.Pi / 2, 0, 0), (int)formation, formation));
        }
        return plan;
    }

    public static void Register(TestRunner runner)
    {
        runner.Run("plan/pile transforms and identity round trip", () =>
        {
            var plan = Fixture();
            var decoded = PlanCodec.Decode(PlanCodec.Encode(plan));
            TestRunner.Assert(plan.Tanks.SequenceEqual(decoded.Tanks), "pile fields changed");
            TestRunner.Assert(plan.Spawns.SequenceEqual(decoded.Spawns), "starts changed");
            TestRunner.Assert(plan.Bounds == decoded.Bounds, "bounds changed");
        });
        runner.Run("plan/reject malformed pile fields through MatchStart", () =>
        {
            var fixture = ProtocolTests.Fixtures().Single(p => p.Type == Msg.MatchStart);
            var setup = (MatchSetup)fixture.Payload;
            var tank = Fixture().Tanks[0];
            var invalid = new[]
            {
                tank with { Position = new Vector2(float.NaN, 0) },
                tank with { Position = new Vector2(1_000_001, 0) },
                tank with { Yaw = float.PositiveInfinity },
                tank with { Elevation = -1 },
                tank with { Elevation = float.NaN },
                tank with { Rotation = new Vector3(0, float.NaN, 0) },
                tank with { Rotation = new Vector3(0, 0, 1_000_001) },
                tank with { Pile = -1 },
                tank with { Pile = Protocol.MaxCollection },
                tank with { Formation = (PileFormation)255 },
                tank with { Reason = new string('x', 1025) },
            };
            foreach (var bad in invalid)
            {
                var plan = Fixture();
                plan.Tanks[0] = bad;
                var message = fixture with { Payload = setup with { Plan = PlanCodec.Encode(plan) } };
                TestRunner.Throws<InvalidDataException>(() => MessageCodec.Decode(MessageCodec.Encode(message, true), true));
            }
        });
        runner.Run("plan/reject incompatible, truncated, trailing and excessive collections", () =>
        {
            var raw = Inflate(PlanCodec.Encode(Fixture()));
            foreach (var version in new[] { 1, 3 })
            {
                var invalid = raw.ToArray();
                BitConverter.GetBytes(version).CopyTo(invalid, 0);
                TestRunner.Throws<InvalidDataException>(() => PlanCodec.Decode(Pack(invalid)));
            }
            foreach (var count in new[] { -1, Protocol.MaxCollection + 1 })
            {
                var invalid = raw.ToArray();
                // Version, seed, bounds, then the road count.
                BitConverter.GetBytes(count).CopyTo(invalid, 24);
                TestRunner.Throws<InvalidDataException>(() => PlanCodec.Decode(Pack(invalid)));
            }
            TestRunner.Throws<Exception>(() => PlanCodec.Decode(Pack(raw[..^1])));
            TestRunner.Throws<InvalidDataException>(() => PlanCodec.Decode(Pack(raw.Append((byte)0).ToArray())));
            TestRunner.Throws<InvalidDataException>(() => PlanCodec.Decode(Array.Empty<byte>()));
            TestRunner.Throws<InvalidDataException>(() => PlanCodec.Decode(new byte[Protocol.MaxPacketBytes + 1]));
            TestRunner.Throws<InvalidDataException>(() => PlanCodec.Decode(Pack(new byte[PlanCodec.MaxRawBytes + 1])));
        });
        runner.Run("plan/match starts must match player assignments", () =>
        {
            var fixture = ProtocolTests.Fixtures().Single(p => p.Type == Msg.MatchStart);
            var setup = (MatchSetup)fixture.Payload;
            var plan = Fixture();
            plan.Spawns.Clear();
            var mismatch = fixture with { Payload = setup with { Plan = PlanCodec.Encode(plan) } };
            TestRunner.Throws<InvalidDataException>(() => MessageCodec.Decode(MessageCodec.Encode(mismatch, true), true));
            mismatch = fixture with { Payload = setup with { Players = new[] { (setup.Players[0].Player, 1) } } };
            TestRunner.Throws<InvalidDataException>(() => MessageCodec.Decode(MessageCodec.Encode(mismatch, true), true));
        });
    }

    private static byte[] Inflate(byte[] packed)
    {
        using var input = new MemoryStream(packed);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        deflate.CopyTo(raw);
        return raw.ToArray();
    }

    private static byte[] Pack(byte[] raw)
    {
        using var packed = new MemoryStream();
        using (var deflate = new DeflateStream(packed, CompressionLevel.Optimal))
        {
            deflate.Write(raw);
        }
        return packed.ToArray();
    }
}
