using System;
using System.Collections.Generic;
using Propane.Bots;
using Propane.Core;
using Propane.World.Plan;

namespace Propane.Modes;

/// <summary>
/// Supply and ammo policy: how a mode's suburb is stocked for a player count, whether rifles carry limited
/// ammunition, and whether ammo cans appear.
/// </summary>
public sealed record ModeSupply(Func<Tuning, int, SuburbSettings> Suburb, bool LimitedAmmo, bool Pickups);

/// <summary>
/// The existing local mode factories and metadata: its rules, world source, supply, round timer, HUD presenter, bot goal policy, the
/// tuners its rules copy and its envelope schema version. Network integration pairs this definition with its schema and server wire in the
/// composition-owned compiled catalog; the definition itself has no networking dependency.
/// </summary>
public sealed record ModeDefinition(
    byte Id,
    string Name,
    ushort SchemaVersion,
    Func<IModeRules> CreateRules,
    ModeWorldSource World,
    ModeSupply Supply,
    RoundTimer Timer,
    Func<IModeHudPresenter> CreateHud,
    Func<ulong?, IBotGoalPolicy>? CreateBot,
    IReadOnlyList<string> Tuners)
{
    /// <summary>
    /// The rules read where bodies are: a runner feeds them <see cref="ActorsSampled"/> from the positions it already
    /// has (a server, from its clients' player states; the Lab, from its own actors).
    /// </summary>
    public bool SamplesActors { get; init; }

    /// <summary>Copies the tuners the rules read from a live tuning instance; rules never see the resource.</summary>
    public ModeTuners CopyTuners(Tuning tuning)
    {
        var values = new Dictionary<string, double>();
        foreach (var name in Tuners)
        {
            var property = typeof(Tuning).GetProperty(name) ?? throw new InvalidOperationException($"no tuner {name}");
            values[name] = Convert.ToDouble(property.GetValue(tuning), System.Globalization.CultureInfo.InvariantCulture);
        }
        return new ModeTuners(values);
    }
}
