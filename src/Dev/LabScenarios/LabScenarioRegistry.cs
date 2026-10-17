using System;
using System.Collections.Generic;

namespace Propane.Dev.LabCases;

internal sealed record LabScenarioRegistration(string Name, Func<ILabScenarioHost, LabScenario> Factory);

/// <summary>CLI selections and their plain owners. Coverage remains independently enumerated.</summary>
internal static class LabScenarioRegistry
{
    internal static readonly IReadOnlyList<LabScenarioRegistration> All = new LabScenarioRegistration[]
    {
        new("lifetime", host => new LifetimeScenario(host)),
        new("frames", host => new FramesScenario(host)),
        new("rules", host => new BankRulesScenario(host)),
        new("duel", host => new BankScenario(host)),
        new("soak", host => new BankScenario(host, soak: true)),
        new("effects", host => new EffectsScenario(host)),
        new("measure", host => new MeasureScenario(host)),
        new("measure-play", host => new MeasureScenario(host, play: true)),
        new("stackfire", host => new StackFireScenario(host)),
        new("aimstall", host => new AimStallScenario(host)),
        new("dials", host => new DialsScenario(host)),
        new("prototype", host => new PrototypeScenario(host)),
        new("shortfuse", host => new ShortFuseScenario(host)),
        new("shortfuse-combo", host => new ShortFuseScenario(host, combo: true)),
        new("tracking", host => new LifetimeScenario(host, trackingOnly: true)),
        new("reference-retirement", host => new RetirementScenario(host)),
        new("dissolve", host => new DissolveScenario(host))
    };

    internal static LabScenario Create(string name, ILabScenarioHost host)
    {
        foreach (var entry in All)
            if (entry.Name == name) return entry.Factory(host);
        throw new ArgumentException($"[lab] unknown scenario '{name}'");
    }
}
