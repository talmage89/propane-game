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
        new("watch", null, NetScenarioCompletion.None),
        new("duel", host => new DuelScenario(host), NetScenarioCompletion.Assertions),
        new("drop-refusal", host => new DuelScenario(host), NetScenarioCompletion.Assertions),
        new("gather", host => new DuelScenario(host), NetScenarioCompletion.Assertions),
        new("pickup", host => new PickupScenario(host), NetScenarioCompletion.Assertions),
        new("melee", null, NetScenarioCompletion.Assertions),
        new("movement", null, NetScenarioCompletion.Movement),
        new("puncture-race", null, NetScenarioCompletion.Assertions),
        new("body-stream", null, NetScenarioCompletion.Assertions),
        new("vent-contact", null, NetScenarioCompletion.Assertions),
        new("king", null, NetScenarioCompletion.Assertions),
        new("king-late", null, NetScenarioCompletion.Assertions),
        new("appearance", null, NetScenarioCompletion.Assertions),
        new("capacity", null, NetScenarioCompletion.FinalChecks)
    };

    internal static NetScenarioRegistration Find(string? name)
    {
        foreach (var registration in All)
            if (registration.Name == (name ?? "")) return registration;
        throw new ArgumentException($"[nettest] unknown scenario '{name}'");
    }
}
