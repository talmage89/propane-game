using System;
using System.Collections.Generic;

namespace Propane.Dev.LabCases;

internal sealed record LabScenarioRegistration(string Name, Func<ILabScenarioHost, LabScenario> Factory);

/// <summary>CLI selections and their plain owners. Coverage remains independently enumerated.</summary>
internal static class LabScenarioRegistry
{
    internal static readonly IReadOnlyList<LabScenarioRegistration> All = new LabScenarioRegistration[]
    {
        new("dials", host => new DialsScenario(host)),
        new("lifetime", host => new LifetimeScenario(host)),
        new("tracking", host => new LifetimeScenario(host, trackingOnly: true))
    };

    internal static LabScenario? Create(string name, ILabScenarioHost host)
    {
        foreach (var entry in All)
            if (entry.Name == name) return entry.Factory(host);
        return null; // Remaining cases still run on the host during the staged extraction.
    }
}
