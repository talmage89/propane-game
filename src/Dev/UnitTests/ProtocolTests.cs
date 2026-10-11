using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Net;
using Propane.Player;

namespace Propane.Dev.UnitTests;

public static class ProtocolTests
{
    public static IReadOnlyList<NetMessage> Fixtures()
    {
        var profile = new PlayerProfile(2, "Test", 1);
        var body = new BodyState(BodySync.MapTankId(0), Transform3D.Identity, new Vector3(1, 2, 3), Vector3.Zero);
        using var tuning = new Tuning();
        var snapshot = tuning.Snapshot();
        return new NetMessage[]
        {
            Packet(Msg.Hello, new HelloMessage(Protocol.Version, BuildInfo.GameVersion, "test", "Test", 1, true)),
            Packet(Msg.CreateLobby, new EmptyMessage()), Packet(Msg.JoinLobby, new IntMessage(1)), Packet(Msg.LeaveLobby, new EmptyMessage()),
            Packet(Msg.SetProfile, new ProfileMessage("Test", 1)), Packet(Msg.SetMatchLength, new FloatMessage(90)),
            Packet(Msg.StartMatch, snapshot), Packet(Msg.Loaded, new EmptyMessage()), Packet(Msg.ClaimPickup, new IntMessage(0)),
            Packet(Msg.Ping, new TimeMessage(1)), Packet(Msg.Welcome, new WelcomeMessage(2, BuildInfo.GameVersion)),
            Packet(Msg.Rejected, new RejectedMessage("Update", "0.3.0")),
            Packet(Msg.LobbyList, new LobbyListMessage(5, new[] { new LobbySummary(1, "Test", 1, LobbyServer.LobbyPhase.Waiting) })),
            Packet(Msg.LobbyState, new LobbyInfo(1, 2, 90, LobbyServer.LobbyPhase.Waiting, new[] { profile })),
            Packet(Msg.MatchStart, new MatchSetup(7, PlanCodec.Encode(PlanTests.Fixture()), 2, true, 90, snapshot, new[] { (profile, 0) })),
            Packet(Msg.Countdown, new FloatMessage(4)), Packet(Msg.MatchGo, new FloatMessage(90)), Packet(Msg.Clock, new FloatMessage(42)),
            Packet(Msg.Score, new ScoreMessage(2, 3, -1)), Packet(Msg.Notice, new TextMessage("Test")),
            Packet(Msg.PickupTaken, new PickupMessage(0, 2)), Packet(Msg.PickupRespawn, new IntMessage(0)),
            Packet(Msg.MatchEnd, new[] { new Standing(2, "Test", 1, 3, true) }), Packet(Msg.PlayerLeft, new PlayerLeftMessage(3, 2)),
            Packet(Msg.Pong, new PongMessage(1, 2)),
            Packet(Msg.DropRefused, new DropRefusedMessage(new[] { BodySync.DropTankId(0, 0) })),
            Packet(Msg.Authority, new AuthorityMessage(2, 1, ClaimRefusal.None,
                new[] { new BodyAuthority(body with { Epoch = 2 }, 2, TankLedger.State.Venting, false, new VentParameters(Vector3.One, Vector3.Up, -1), Vector3.Zero) })),
            Packet(Msg.PlayerState, new PlayerStateMessage(1, new PlayerNetState { Position = Vector3.One, Velocity = Vector3.Zero,
                Yaw = 1, AimTarget = Vector3.Forward, Aim = 17 / 255f, Sprint = 1, Ready = 128 / 255f, Reload = 0,
                Grounded = true, State = PlayerNetState.Mode.GettingUp, PelvisPosition = Vector3.Up, PelvisVelocity = Vector3.Down, Staggers = 2,
                Gait = PlayerGait.Creep, Support = PlayerSupport.Landing, Weapon = WeaponAction.Aim })),
            Packet(Msg.Shot, new ShotMessage(Vector3.Zero, Vector3.Up, Vector3.Forward, 7, 3, -1)),
            Packet(Msg.Puncture, new PunctureMessage(body.Id, body.Transform, body.Linear, body.Angular, Vector3.One, Vector3.Up, -1)),
            Packet(Msg.Explode, new ExplodeMessage(body.Id, Vector3.One)), Packet(Msg.Claim, new ClaimMessage(1, new[] { body })),
            Packet(Msg.BodyStates, new BodyStatesMessage(1, new[] { body with { Sequence = 1 } })), Packet(Msg.BodyRest, new BodyRestMessage(body.Id, body.Transform, 1)),
            Packet(Msg.Drop, new DropMessage(1, 1, new[] { body with { Id = BodySync.DropTankId(0, 0) } })),
            Packet(Msg.Ragdoll, new RagdollMessage(Vector3.One, Vector3.Up, Vector3.Right)), Packet(Msg.GetUp, new GetUpMessage(Vector3.One, 1)),
            Packet(Msg.TuningSet, new TuningMessage(nameof(Tuning.BlastRadius), 5f)),
        };
    }

    private static NetMessage Packet(Msg type, object payload) => new(type, Protocol.IsMatch(type) ? 7 : 0, Protocol.IsRelay(type) ? 2 : 0, payload);

    public static void Register(TestRunner runner)
    {
        var fixtures = Fixtures();
        runner.Run("protocol/fixture covers every message", () => TestRunner.Assert(
            Enum.GetValues<Msg>().Order().SequenceEqual(fixtures.Select(f => f.Type).Order()), "missing or duplicate message fixture"));
        foreach (var fixture in fixtures)
        {
            var fromServer = fixture.Type is >= Msg.Welcome and <= Msg.Authority;
            RoundTrip(runner, fixture, fromServer);
            if (Protocol.IsRelay(fixture.Type) && fixture.Type is not (Msg.Claim or Msg.Puncture or Msg.Explode)) { RoundTrip(runner, fixture, true); }
        }
        runner.Run("protocol/player movement enums survive client and relay snapshots", () =>
        {
            var fixture = fixtures.Single(f => f.Type == Msg.PlayerState);
            var original = (PlayerStateMessage)fixture.Payload;
            foreach (var gait in Enum.GetValues<PlayerGait>())
            {
                foreach (var support in Enum.GetValues<PlayerSupport>())
                {
                    foreach (var weapon in Enum.GetValues<WeaponAction>())
                    {
                        foreach (var fromServer in new[] { false, true })
                        {
                            var state = original.State;
                            state.Gait = gait; state.Support = support; state.Weapon = weapon;
                            var message = fixture with { Payload = new PlayerStateMessage(original.Time, state) };
                            var decoded = MessageCodec.Decode(MessageCodec.Encode(message, fromServer), fromServer);
                            var actual = ((PlayerStateMessage)decoded.Payload).State;
                            TestRunner.Assert(actual.Gait == gait && actual.Support == support && actual.Weapon == weapon,
                                $"movement fields lost: {gait}/{support}/{weapon}, fromServer={fromServer}");
                        }
                    }
                }
            }
        });
        runner.Run("protocol/player movement enum ranges reject invalid bytes", () =>
        {
            var fixture = fixtures.Single(f => f.Type == Msg.PlayerState);
            var maxima = new[] { (byte)PlayerGait.Creep, (byte)PlayerSupport.Landing, (byte)WeaponAction.Melee };
            foreach (var fromServer in new[] { false, true })
            {
                for (var field = 0; field < maxima.Length; field++)
                {
                    foreach (var invalid in new[] { (byte)(maxima[field] + 1), byte.MaxValue })
                    {
                        var bytes = MessageCodec.Encode(fixture, fromServer);
                        // The three enum bytes are the snapshot's trailing fields, in gait/support/weapon order.
                        bytes[bytes.Length - 3 + field] = invalid;
                        TestRunner.Throws<InvalidDataException>(() => MessageCodec.Decode(bytes, fromServer));
                    }
                }
            }
        });
        runner.Run("protocol/authority requests and ordering fields are validated", () =>
        {
            foreach (var type in new[] { Msg.Claim, Msg.Puncture, Msg.Explode })
            {
                TestRunner.Throws<InvalidDataException>(() => MessageCodec.Decode(MessageCodec.Encode(fixtures.Single(f => f.Type == type), true), true));
            }
            var puncture = (PunctureMessage)fixtures.Single(f => f.Type == Msg.Puncture).Payload;
            foreach (var payload in new[] { puncture with { Epoch = 0 }, puncture with { Request = 0 }, puncture with { Swirl = 0 }, puncture with { Swirl = .5f } })
            {
                TestRunner.Throws<InvalidDataException>(() => MessageCodec.Decode(MessageCodec.Encode(Packet(Msg.Puncture, payload), false), false));
            }
            var states = (BodyStatesMessage)fixtures.Single(f => f.Type == Msg.BodyStates).Payload;
            TestRunner.Throws<InvalidDataException>(() => MessageCodec.Decode(MessageCodec.Encode(Packet(Msg.BodyStates,
                states with { Bodies = states.Bodies.Select(b => b with { Sequence = 0 }).ToArray() }), false), false));
            var authority = (AuthorityMessage)fixtures.Single(f => f.Type == Msg.Authority).Payload;
            foreach (var payload in new[] { authority with { Reason = (ClaimRefusal)255 }, authority with { Bodies = authority.Bodies.Select(b => b with { Vent = null }).ToArray() } })
            {
                TestRunner.Throws<InvalidDataException>(() => MessageCodec.Decode(MessageCodec.Encode(Packet(Msg.Authority, payload), true), true));
            }
        });
        runner.Run("protocol/unknown and wrong direction", () =>
        {
            TestRunner.Throws<InvalidDataException>(() => MessageCodec.Decode(new byte[] { 255 }, false));
            TestRunner.Throws<InvalidDataException>(() => MessageCodec.Decode(new NetWriter(Msg.CreateLobby).ToArray(), true));
            TestRunner.Throws<InvalidDataException>(() => MessageCodec.Decode(new NetWriter(Msg.Welcome).Int(2).ToArray(), false));
        });
        runner.Run("protocol/malformed ranges, counts, floats and rotations", () =>
        {
            var bad = new[]
            {
                new NetWriter(Msg.Drop, 7).Byte(0).Int(-1).Int(1).ToArray(),
                new NetWriter(Msg.Explode, 7).UInt(0).Vec3(Vector3.One).ToArray(),
                new NetWriter(Msg.Explode, 7).UInt(BodySync.MapTankId(0)).Vec3(new Vector3(float.NaN, 0, 0)).ToArray(),
                new NetWriter(Msg.GetUp, 7).Vec3(Vector3.Zero).Float(float.PositiveInfinity).ToArray(),
                new NetWriter(Msg.PlayerState, 7).Double(double.NaN).ToArray(),
                new NetWriter(Msg.BodyRest, 7).UInt(bodyId).Vec3(Vector3.Zero).Quat(new Quaternion(0, 0, 0, 0)).Double(1).ToArray(),
                new NetWriter(Msg.Claim, 7).UShort(2).UInt(bodyId).UInt(bodyId).ToArray(),
                new NetWriter(Msg.TuningSet, 7).String(nameof(Tuning.BlastRadius)).Scalar(float.NaN).ToArray(),
                new NetWriter(Msg.TuningSet, 7).String(nameof(Tuning.BlastRadius)).Scalar(41f).ToArray(),
                new NetWriter(Msg.TuningSet, 7).String(nameof(Tuning.BlastRadius)).Scalar(2).ToArray(),
                new NetWriter(Msg.TuningSet, 7).String("NotATuner").Scalar(2f).ToArray(),
                new NetWriter(Msg.StartMatch).Int(-1).ToArray(),
                new NetWriter(Msg.SetMatchLength).Float(float.NaN).ToArray(),
                new NetWriter(Msg.SetProfile).String("Test").Int(-1).ToArray(),
            };
            foreach (var bytes in bad) { TestRunner.Throws<InvalidDataException>(() => MessageCodec.Decode(bytes, false)); }
        });
        runner.Run("tuning/apply is atomic and bounds are inclusive", () =>
        {
            using var tuning = new Tuning();
            var before = tuning.BlastRadius;
            TestRunner.Throws<InvalidDataException>(() => tuning.Apply(new Godot.Collections.Dictionary
            { [nameof(Tuning.BlastRadius)] = 3f, [nameof(Tuning.FireRate)] = float.NaN }));
            TestRunner.Assert(tuning.BlastRadius == before, "valid prefix applied before invalid suffix");
            tuning.Apply(new Godot.Collections.Dictionary { [nameof(Tuning.BlastRadius)] = 1f });
            TestRunner.Assert(tuning.BlastRadius == 1, "lower endpoint rejected");
            tuning.Apply(new Godot.Collections.Dictionary { [nameof(Tuning.BlastRadius)] = 40f, [nameof(Tuning.FixedSeed)] = int.MinValue });
            TestRunner.Assert(tuning.BlastRadius == 40 && tuning.FixedSeed == int.MinValue, "upper endpoint or int32 seed rejected");
        });
        runner.Run("protocol/handshake extensions and legacy rejection", () =>
        {
            var bytes = new NetWriter(Msg.Hello).Int(1).String("0.2.0").String("old").String("Test").Int(0).ToArray();
            var hello = (HelloMessage)MessageCodec.Decode(bytes, false).Payload;
            TestRunner.Assert(!hello.Debug && BuildInfo.Mismatch(hello.Protocol, hello.Version) != null, "legacy hello cannot be rejected");
            MessageCodec.Decode(new NetWriter(Msg.Welcome).Int(2).ToArray(), true);
            MessageCodec.Decode(new NetWriter(Msg.Rejected).String("Update").ToArray(), true);
            MessageCodec.Decode(new NetWriter(Msg.Welcome).Int(2).String("0.3.0").Int(1234).ToArray(), true);
        });
    }

    private const uint bodyId = BodySync.MapTankBase;

    private static void RoundTrip(TestRunner runner, NetMessage fixture, bool fromServer)
    {
        var label = $"protocol/{fixture.Type}/{(fromServer ? "server" : "client")}";
        runner.Run(label, () =>
        {
            var encoded = MessageCodec.Encode(fixture, fromServer);
            var decoded = MessageCodec.Decode(encoded, fromServer);
            var again = MessageCodec.Encode(decoded, fromServer);
            TestRunner.Assert(encoded.SequenceEqual(again), "encode/decode changed fields");
            // Every prefix is incomplete, except the explicitly backward-compatible handshake suffixes.
            for (var length = 0; length < encoded.Length; length++)
            {
                if (fixture.Type == Msg.Welcome && length == 5 || fixture.Type == Msg.Rejected && length == 8) { continue; }
                TestRunner.Throws<Exception>(() => MessageCodec.Decode(encoded[..length], fromServer));
            }
            if (fixture.Type is not (Msg.Hello or Msg.Welcome or Msg.Rejected))
            {
                TestRunner.Throws<InvalidDataException>(() => MessageCodec.Decode(encoded.Append((byte)0).ToArray(), fromServer));
            }
        });
    }
}
