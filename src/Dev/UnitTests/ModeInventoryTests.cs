using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Propane.Core;
using Propane.Modes;
using Propane.Net;

namespace Propane.Dev.UnitTests;

/// <summary>Immediate-parent inventory of mode exposure, factories, tuners and supply policy.</summary>
[UnitSuite]
public static class ModeInventoryTests
{
    public static void Register(TestRunner runner)
    {
        runner.Run("mode inventory/registration, eligibility and supply", () =>
        {
            TestRunner.Assert(ModeRegistry.All.Select(m => m.Id).SequenceEqual(new byte[] { 1, 2, 4, 7 }), "network registration order changed");
            TestRunner.Assert(ModeRegistry.Lobby.Select(m => m.Id).SequenceEqual(new byte[] { 2, 4 }), "lobby order changed");
            TestRunner.Assert(ModeRegistry.Prototypes.Select(m => m.Id).SequenceEqual(new byte[] { 3, 4, 5, 6 }), "prototype order changed");
            var modes = ModeRegistry.All.Concat(ModeRegistry.Prototypes).Distinct().ToArray();
            using var tuning = new Tuning();
            var inventory = modes.Select(mode => new
            {
                mode.Id,
                mode.Name,
                mode.SchemaVersion,
                mode.World,
                mode.Timer,
                mode.SamplesActors,
                rules = mode.CreateRules().GetType().FullName,
                hud = mode.CreateHud().GetType().FullName,
                bot = mode.CreateBot?.Invoke(23)?.GetType().FullName,
                tuners = mode.Tuners.ToArray(),
                mode.Supply.LimitedAmmo,
                mode.Supply.Pickups,
                lobby = ReferenceEquals(ModeRegistry.ForLobby(mode.Id), mode),
                network = ReferenceEquals(ModeRegistry.Find(mode.Id, mode.SchemaVersion), mode),
                supply = new[] { 1, 2, 4, 8, 16, 32 }.SelectMany(players => Enumerable.Range(1, 8).Select(multiplier =>
                {
                    tuning.TankCountMultiplier = multiplier;
                    return new { players, multiplier, settings = mode.Supply.Suburb(tuning, players) };
                })).ToArray(),
            }).ToArray();
            var json = JsonSerializer.Serialize(new
            {
                registration = ModeRegistry.All.Select(m => m.Id).ToArray(),
                lobby = ModeRegistry.Lobby.Select(m => m.Id).ToArray(),
                prototypes = ModeRegistry.Prototypes.Select(m => m.Id).ToArray(),
                modes = inventory,
            });
            if (DevArgs.Get("mode-inventory") is { } path) File.WriteAllText(path, json + "\n");
        });
    }
}
