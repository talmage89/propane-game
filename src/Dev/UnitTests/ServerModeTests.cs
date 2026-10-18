using System;
using System.Collections.Generic;
using System.Linq;
using Propane.Core;
using Propane.Dev.UnitTests.Fixtures;
using Propane.Modes;
using Propane.Net;

namespace Propane.Dev.UnitTests;

/// <summary>Proves server projections retain definition policy without retaining a presentation definition.</summary>
[UnitSuite]
public static class ServerModeTests
{
    private sealed class ProbeWire : IModeWire
    {
        public void Event(ModeRunner runner, IModeHost host, int peer, ModeEnvelope envelope, double now) { }
        public void Execute(ModeRunner runner, IModeHost host, IReadOnlyList<ModeCommand> commands, double now) { }
        public void Failed(ModeRunner runner, IModeHost host, int peer, ModeEnvelope envelope, double now) { }
    }

    public static void Register(TestRunner runner)
    {
        runner.Run("server modes/project metadata, eligibility, supply and live tuner copies", () =>
        {
            var cases = 0;
            using var tuning = new Tuning();
            foreach (var definition in ModeRegistry.All)
            {
                using var f = new ServerModeFixture(definition);
                TestRunner.Assert(!f.CapturesCatalog, "server callbacks retain the presentation catalog");
                var mode = f.Modes.Default;
                TestRunner.Assert(mode.Id == definition.Id && mode.Name == definition.Name && mode.SchemaVersion == definition.SchemaVersion &&
                    mode.World == definition.World && mode.Timer == definition.Timer && mode.SamplesActors == definition.SamplesActors &&
                    mode.Tuners.SequenceEqual(definition.Tuners) && mode.CreateRules == definition.CreateRules &&
                    ReferenceEquals(mode.Schema, ModeRegistry.Schema(definition.Id, definition.SchemaVersion)), "server metadata differs from definition");
                TestRunner.Assert(f.Modes.Lobby.Select(m => m.Id).SequenceEqual(ModeRegistry.Lobby.Select(m => m.Id)) &&
                    f.Modes.Roaming.Id == ModeRegistry.LobbyWorldId, "server exposure/order changed");
                foreach (var players in new[] { 1, 2, 4, 8, 16, 32 })
                {
                    foreach (var multiplier in Enumerable.Range(1, 8))
                    {
                        tuning.TankCountMultiplier = multiplier;
                        TestRunner.Assert(f.Supply(mode, tuning, players) == definition.Supply.Suburb(tuning, players), "projected supply differs");
                        cases++;
                    }
                }
                foreach (var changed in new[] { false, true })
                {
                    tuning.KingMintSeconds = changed ? 1.5f : 2;
                    tuning.LobbyPileCount = changed ? 12 : 6;
                    tuning.HitDropCount = changed ? 2 : 1;
                    var expected = definition.CopyTuners(tuning);
                    var actual = f.Tuners(mode, tuning);
                    TestRunner.Assert(actual.Names.SequenceEqual(expected.Names) && actual.Names.All(name => actual.Float(name) == expected.Float(name)), "projected live tuner sample differs");
                }
                TestRunner.Throws<NotSupportedException>(() => ((IList<string>)mode.Tuners).Add("extra"));
                TestRunner.Throws<NotSupportedException>(() => ((IList<ServerMode>)f.Modes.Lobby).Clear());
            }
            TestRunner.Assert(cases == 192, "server supply case coverage changed");
        });
        runner.Run("server modes/captured bindings survive scope retirement without global resolution", () =>
        {
            var all = ModeRegistry.All;
            var wire = new ProbeWire();
            ServerModeFixture fixture;
            using (ModeRegistry.RegisterScoped(ModeRegistry.Shipped.Find(ModeRegistry.LobbyWorldId, 1)! with { CreateServerWire = () => wire }))
                fixture = new ServerModeFixture();
            using (fixture)
            {
                TestRunner.Assert(ReferenceEquals(ModeRegistry.All, all), "projection retained test registration");
                TestRunner.Assert(ReferenceEquals(fixture.Modes.Roaming.CreateWire(), wire), "server consulted the restored global catalog");
                using var next = new ServerModeFixture();
                TestRunner.Assert(next.Modes.Roaming.CreateWire() is LobbyWire, "new server inherited retired scope");
            }
        });
        runner.Run("server modes/prototypes remain excluded and default override remains private", () =>
        {
            foreach (var mode in new[] { ModeRegistry.RingOut, ModeRegistry.ShortFuse, ModeRegistry.DissolveRun })
                TestRunner.Throws<InvalidOperationException>(() => { using var f = new ServerModeFixture(mode); });
            var replacement = ModeRegistry.TankBank with { Name = "Initial probe" };
            using var fixture = new ServerModeFixture(replacement);
            TestRunner.Assert(fixture.Modes.Default.Name == replacement.Name && fixture.Modes.ForLobby(2)!.Name == ModeRegistry.TankBank.Name,
                "test initial mode replaced the creator's ordinary selection");
        });
        runner.Run("server modes/wire pile observations remain live, plain and allocation-free", () =>
        {
            using var fixture = new ServerRunFixture(worlds: true);
            var run = fixture.Runs.Single().Run;
            var tuning = run.Tuning;
            var host = (IModeHost)run;
            ModeWireTuners Expected() => new(tuning.KingPileCount, tuning.KingPileReach, tuning.KingPileClearance,
                tuning.LobbyPileCount, tuning.LobbyPileReach, tuning.LobbyPileClearance,
                new Godot.Vector3(tuning.PileSpacing, tuning.PileLayerHeight, tuning.PileDepthSpacing), tuning.PileUprightLayerHeight);
            var before = host.WireTuners;
            TestRunner.Assert(before == Expected(), "wire values differ from native tuning");
            tuning.KingPileCount = 9;
            tuning.LobbyPileReach = 17;
            tuning.PileSpacing = .42f;
            TestRunner.Assert(host.WireTuners == Expected() && before != host.WireTuners, "wire values froze at run construction");
            var total = 0;
            for (var i = 0; i < 100; i++) total += host.WireTuners.KingPileCount;
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++) total += host.WireTuners.KingPileCount;
            var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            TestRunner.Assert(total == 9900 && bytes == 0, $"wire values allocated {bytes} bytes");
        });
    }
}
