using System;
using System.IO;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Modes;
using Propane.Net;
using Propane.UI;

namespace Propane.Dev.UnitTests;

/// <summary>Lobby tank supply is authoritative over local tuning, including for a client that builds after Go.</summary>
[UnitSuite]
public static class LobbySupplyTests
{
    private static byte[] Choice(ModeSettings settings) => MessageCodec.Encode(new NetMessage(Msg.SetMode, 0, 0, settings), false);

    public static void Register(TestRunner runner)
    {
        runner.Run("lobby supply/all steps round-trip in choices and lobby state", () =>
        {
            var lobby = (LobbyInfo)ProtocolTests.Fixtures().Single(m => m.Type == Msg.LobbyState).Payload;
            foreach (var mode in ModeRegistry.Lobby)
            {
                for (var supply = Tuning.TankSupplyMinimum; supply <= Tuning.TankSupplyMaximum; supply++)
                {
                    var settings = new ModeSettings(mode.Id, 2, supply);
                    TestRunner.Assert(MessageCodec.Decode(Choice(settings), false).Payload.Equals(settings), "choice lost supply");
                    var state = MessageCodec.Encode(new NetMessage(Msg.LobbyState, 0, 0, lobby with { Mode = settings }), true);
                    TestRunner.Assert(((LobbyInfo)MessageCodec.Decode(state, true).Payload).Mode == settings, "lobby lost supply");
                }
            }
        });
        runner.Run("lobby supply/rejects invalid values, missing fields and trailing bytes", () =>
        {
            var lobby = (LobbyInfo)ProtocolTests.Fixtures().Single(m => m.Type == Msg.LobbyState).Payload;
            foreach (var supply in new[] { int.MinValue, -1, 0, 9, int.MaxValue })
            {
                var settings = ModeSettings.Default with { TankSupply = supply };
                TestRunner.Assert(!settings.Valid, "invalid supply admitted");
                TestRunner.Throws<InvalidDataException>(() => MessageCodec.Decode(Choice(settings), false));
                var state = MessageCodec.Encode(new NetMessage(Msg.LobbyState, 0, 0, lobby with { Mode = settings }), true);
                TestRunner.Throws<InvalidDataException>(() => MessageCodec.Decode(state, true));
            }
            var bytes = Choice(ModeSettings.Default with { TankSupply = 4 });
            for (var length = 1; length < bytes.Length; length++)
                TestRunner.Throws<Exception>(() => MessageCodec.Decode(bytes[..length], false));
            TestRunner.Throws<InvalidDataException>(() => MessageCodec.Decode(bytes.Append((byte)0).ToArray(), false));
        });
        runner.Run("lobby supply/server overrides conflicting creator tuning in every mode", () =>
        {
            foreach (var mode in ModeRegistry.Lobby)
            {
                foreach (var supply in new[] { 1, 4, 8 })
                {
                    using var fixture = new ServerTests.Fixture(modeSettings: ModeSettings.Default with { ModeId = mode.Id, TankSupply = supply },
                        creatorTankSupply: supply == 8 ? 1 : 8, bots: 2);
                    using var tuning = Tuning.FromSnapshot(fixture.Setup.Tuning);
                    using var defaults = new Tuning();
                    var expected = mode.Supply.Suburb(defaults, fixture.Setup.Players.Count).TankCount * supply;
                    TestRunner.Assert(tuning.TankCountMultiplier == supply && PlanCodec.Decode(fixture.Setup.Plan).Tanks.Count == expected,
                        "server generated or transmitted the creator's local multiplier instead of the lobby choice");
                    fixture.Receive(Choice(ModeSettings.Default with { TankSupply = 2 }));
                    TestRunner.Assert(fixture.Packets.Count == 0, "playing creator changed supply");
                    fixture.Receive(MessageCodec.Encode(new NetMessage(Msg.TuningSet, fixture.Id, 0,
                        new TuningMessage(nameof(Tuning.TankCountMultiplier), 2)), false));
                    TestRunner.Assert(fixture.Packets.Count == 0, "debug tuning overrode locked lobby supply");
                }
            }
        });
        runner.Run("lobby supply/loading locks creator and guest choices", () =>
        {
            using var fixture = new ServerTests.Fixture(playing: false, modeSettings: ModeSettings.Default with { TankSupply = 4 });
            fixture.Receive(Choice(ModeSettings.Default with { TankSupply = 8 }), peer: 3);
            fixture.Receive(Choice(ModeSettings.Default with { TankSupply = 8 }));
            TestRunner.Assert(fixture.Packets.Count == 0, "loading lobby allowed a supply change");
        });
        runner.Run("lobby supply/match panel omits supply and Defaults preserves it; offline panel keeps it", () =>
        {
            using var tuning = new Tuning { TankCountMultiplier = 4, RunSpeed = 7 };
            foreach (var locked in new[] { true, false })
            {
                var panel = new TuningPanel { Target = tuning, LockedProperties = locked ? new[] { nameof(Tuning.TankCountMultiplier) } : Array.Empty<string>() };
                try
                {
                    runner.AddChild(panel);
                    TestRunner.Assert(panel.FindChildren("*", "Label", owned: false).OfType<Label>()
                        .Any(label => label.Text == "Tank count multiplier") != locked, "wrong supply slider availability");
                    if (!locked) continue;
                    var changedSupply = false;
                    panel.ValueChanged += (name, _) => changedSupply |= name == nameof(Tuning.TankCountMultiplier);
                    panel.FindChildren("*", "Button", owned: false).OfType<Button>().Single(button => button.Text == "Defaults")
                        .EmitSignal(Button.SignalName.Pressed);
                    using var defaults = new Tuning();
                    TestRunner.Assert(!changedSupply && tuning.TankCountMultiplier == 4 && tuning.RunSpeed == defaults.RunSpeed,
                        "Defaults changed locked supply or failed to reset editable values");
                }
                finally { panel.Free(); }
            }
        });
        runner.Run("lobby supply/client sends consistent snapshot without changing offline template", () =>
        {
            var network = new LoopbackNetwork();
            var listener = network.Create();
            var transport = network.Create();
            using var template = new Tuning { TankCountMultiplier = 8 };
            var client = new NetClient(transport) { Template = template };
            try
            {
                var endpoint = new PacketEndpoint("", 24883);
                TestRunner.Assert(listener.Listen(endpoint, 2).Success, "listener failed");
                client._Ready(); client.Connect("", endpoint.Port, "Creator", 0);
                for (var i = 0; i < 4; i++) { listener.Poll(); client._Process(0); }
                client.Handle(MessageCodec.Encode(new NetMessage(Msg.Welcome, 0, 0, new WelcomeMessage(2, null)), true));
                var lobby = (LobbyInfo)ProtocolTests.Fixtures().Single(m => m.Type == Msg.LobbyState).Payload;
                client.Handle(MessageCodec.Encode(new NetMessage(Msg.LobbyState, 0, 0, lobby with { Mode = ModeSettings.Default with { TankSupply = 4 } }), true));
                var received = 0;
                listener.Packet += (_, _, data) =>
                {
                    var message = MessageCodec.Decode(data.ToArray(), false);
                    if (message.Type != Msg.StartMatch) return;
                    using var tuning = Tuning.FromSnapshot((Godot.Collections.Dictionary)message.Payload);
                    TestRunner.Assert(tuning.TankCountMultiplier == 4, "client snapshot contradicted lobby supply");
                    received++;
                };
                client.StartMatch(); listener.Poll();
                TestRunner.Assert(received == 1 && template.TankCountMultiplier == 8, "start mutated offline tuning or never sent");
            }
            finally { client.Free(); listener.Close(); transport.Close(); }
        });
        runner.Run("lobby supply/late builder uses shared plan and tuning after buffered Go", () =>
        {
            using var fixture = new ServerTests.Fixture(modeSettings: ModeSettings.Default with { TankSupply = 4 }, creatorTankSupply: 8);
            var built = new System.Collections.Generic.List<(uint Id, Transform3D Pose)>();
            foreach (var late in new[] { false, true })
            {
                using var template = new Tuning { TankCountMultiplier = late ? 8 : 1 };
                var client = new NetClient { Template = template };
                Match? match = null;
                try
                {
                    client.Handle(MessageCodec.Encode(new NetMessage(Msg.Welcome, 0, 0, new WelcomeMessage(late ? 3 : 2, null)), true));
                    var lobby = new LobbyInfo(1, fixture.Setup.CreatorId, 10, LobbyServer.LobbyPhase.Loading,
                        fixture.Setup.Players.Select(p => p.Player).ToArray())
                    { Mode = ModeSettings.Default with { TankSupply = 4 } };
                    client.Handle(MessageCodec.Encode(new NetMessage(Msg.LobbyState, 0, 0, lobby), true));
                    MatchSetup? setup = null;
                    client.MatchStarting += incoming => setup = incoming;
                    client.Handle(MessageCodec.Encode(new NetMessage(Msg.MatchStart, fixture.Id, 0, fixture.Setup), true));
                    TestRunner.Assert(setup != null, "peer did not receive match setup");
                    if (late) client.Handle(MessageCodec.Encode(new NetMessage(Msg.MatchGo, fixture.Id, 0, new FloatMessage(10)), true));
                    match = new Match { Net = client, Setup = setup!, CreateLocalView = false };
                    runner.AddChild(match);
                    var tanks = match.LiveTanks.OrderBy(t => t.NetId).Select(t => (t.NetId, t.Transform)).ToArray();
                    TestRunner.Assert(match.World.Tuning.TankCountMultiplier == 4 && tanks.Length == PlanCodec.Decode(fixture.Setup.Plan).Tanks.Count,
                        "builder used its local multiplier");
                    if (late) TestRunner.Assert(match.IsPlaying && built.SequenceEqual(tanks), "late builder changed tank identities or poses");
                    else built.AddRange(tanks);
                }
                finally { match?.Session.Dispose(); match?.Free(); client.Free(); }
            }
        });
    }
}
