using Godot;
using Propane.Core;
using Propane.Dev.UnitTests.Fixtures;
using Propane.Modes;
using Propane.Net;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev.UnitTests;

[UnitSuite]
public static class MatchCodecTests
{
    public static void Register(TestRunner runner)
    {
        runner.Run("match codec/mode publication uses the connection's captured schema resolver", () =>
        {
            using var server = new ServerFixture();
            var schemas = 0;
            var codec = new PacketCodec((id, version) => { schemas++; return ModeRegistry.Schema(id, version); },
                id => ModeRegistry.ForLobby(id) != null, Tuning.ValidateSnapshot);
            using var client = new MatchClient(runner, server.Setup, server.Clock, 2, codec: codec);
            Assert(ReferenceEquals(client.Client.Codec, codec), "connection replaced its injected codec");
            ((IClientModeReplica)client.Match).SendMode(client.Match.Player, 1, ModeSchemas.TankBankKinds.Spill,
                new TankBankSpill(SpillReason.Hit, new[] { new BodyState(1, Transform3D.Identity, Vector3.Zero, Vector3.Zero) }));
            Assert(schemas == 1, "mode publication bypassed the connection's schema resolver");
        });
    }
}
