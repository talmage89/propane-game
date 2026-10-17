using System;
using System.Collections.Generic;

namespace Propane.Dev.NetScenarios;

internal enum NetScenarioCompletion { None, Assertions, Movement, FinalChecks }

internal sealed record NetScenarioRegistration(string Name, Func<INetScenarioHost, NetScenario>? Factory, NetScenarioCompletion Completion);

/// <summary>The CLI selection and its completion obligation are declared together exactly once.</summary>
internal static class NetScenarioRegistry
{
    internal static readonly IReadOnlyList<NetScenarioRegistration> All = new NetScenarioRegistration[]
    {
        new("", null, NetScenarioCompletion.None),
        new("watch", host => new WatchScenario(host), NetScenarioCompletion.None),
        new("duel", host => new DuelScenario(host), NetScenarioCompletion.Assertions),
        new("drop-refusal", host => new DuelScenario(host), NetScenarioCompletion.Assertions),
        new("gather", host => new DuelScenario(host), NetScenarioCompletion.Assertions),
        new("pickup", host => new PickupScenario(host), NetScenarioCompletion.Assertions),
        new("melee", host => new MeleeScenario(host), NetScenarioCompletion.Assertions),
        new("movement", host => new MovementScenario(host), NetScenarioCompletion.Movement),
        new("puncture-race", host => new PunctureRaceScenario(host), NetScenarioCompletion.Assertions),
        new("body-stream", host => new BodyStreamScenario(host), NetScenarioCompletion.Assertions),
        new("vent-contact", host => new VentContactScenario(host), NetScenarioCompletion.Assertions),
        new("king", host => new KingScenario(host), NetScenarioCompletion.Assertions),
        new("king-late", host => new KingLateScenario(host), NetScenarioCompletion.Assertions),
        new("appearance", null, NetScenarioCompletion.Assertions),
        new("capacity", host => new CapacityScenario(host), NetScenarioCompletion.FinalChecks)
    };

    internal static NetScenarioRegistration Find(string? name)
    {
        foreach (var registration in All)
            if (registration.Name == (name ?? "")) return registration;
        throw new ArgumentException($"[nettest] unknown scenario '{name}'");
    }
}
