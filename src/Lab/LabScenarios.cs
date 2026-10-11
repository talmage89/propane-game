using System;
using System.Collections.Generic;
using System.Linq;

namespace Propane.Lab;

public enum LabEnvironment { Void, Suburb }

/// <summary>Compiled extension point for later prototypes. Setup owns fixtures; the facade owns their session.</summary>
public sealed record LabScenario(string Id, string Name, string Description, IReadOnlyList<LabEnvironment> Environments,
    Action<LabSession> Setup, bool Banking = false, bool OpensTuning = false);

public static class LabScenarios
{
    public static IReadOnlyList<LabScenario> All { get; } = Array.AsReadOnly(new[]
    {
        new LabScenario("stack", "Huge stack", "Build an upright pyramid. Shoot twice and watch it chain.",
            new[] { LabEnvironment.Void }, lab => lab.BuildStack()),
        new LabScenario("duel", "Duel vs a banking bot", "Bank tanks, steal the bot’s bank, and beat the clock.",
            new[] { LabEnvironment.Void, LabEnvironment.Suburb }, lab => lab.BuildDuel(), Banking: true),
        new LabScenario("range", "Range", "Practice on single tanks and piles at marked distances.",
            new[] { LabEnvironment.Void }, lab => lab.BuildRange()),
        new LabScenario("tuning", "Free tuning", "Try live dials on your own session. Save only when you choose.",
            new[] { LabEnvironment.Void, LabEnvironment.Suburb }, lab => lab.BuildTuning(), OpensTuning: true),
    });

    public static LabScenario Get(string id) => All.FirstOrDefault(scenario => scenario.Id == id)
        ?? throw new ArgumentException($"Unknown Lab scenario: {id}", nameof(id));
}
