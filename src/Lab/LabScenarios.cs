using System;
using System.Collections.Generic;
using System.Linq;
using Propane.Modes;

namespace Propane.Lab;

public enum LabEnvironment { Void, Suburb }

/// <summary>
/// Compiled extension point for later prototypes. Setup owns fixtures; the facade owns their session. A scenario
/// plays a registered mode (Sandbox unless it names one); the scenario's environment choice replaces the mode's
/// world source, so prototypes can try a mode in the void or a suburb.
/// </summary>
public sealed record LabScenario(string Id, string Name, string Description, IReadOnlyList<LabEnvironment> Environments,
    Action<LabSession> Setup, ModeDefinition? Mode = null, bool OpensTuning = false)
{
    public ModeDefinition Definition => Mode ?? ModeRegistry.Sandbox;
    public bool Banking => Definition.Id == ModeRegistry.TankBankId;
}

public static class LabScenarios
{
    public static IReadOnlyList<LabScenario> All { get; } = Array.AsReadOnly(new[]
    {
        new LabScenario("stack", "Huge stack", "Build an upright pyramid. Puncture several, then detonate one.",
            new[] { LabEnvironment.Void }, lab => lab.BuildStack()),
        new LabScenario("duel", "Duel vs a banking bot", "Bank tanks, steal the bot’s bank, and beat the clock.",
            new[] { LabEnvironment.Void, LabEnvironment.Suburb }, lab => lab.BuildDuel(), Mode: ModeRegistry.TankBank),
        new LabScenario("range", "Range", "Practice on single tanks and piles at marked distances.",
            new[] { LabEnvironment.Void }, lab => lab.BuildRange()),
        new LabScenario("tuning", "Free tuning", "Try live dials on your own session. Save only when you choose.",
            new[] { LabEnvironment.Void, LabEnvironment.Suburb }, lab => lab.BuildTuning(), OpensTuning: true),
    });

    public static LabScenario Get(string id) => All.FirstOrDefault(scenario => scenario.Id == id)
        ?? throw new ArgumentException($"Unknown Lab scenario: {id}", nameof(id));
}
