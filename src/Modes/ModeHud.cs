using System;
using System.Collections.Generic;
using Godot;

namespace Propane.Modes;

/// <summary>A number the HUD shows large, with its caption (the bank in Tank Bank).</summary>
public sealed record HudCounter(int Value, string Caption);

/// <summary>A round clock: seconds to show, and whether they count down or up.</summary>
public sealed record HudTimer(float Seconds, RoundTimer Kind);

/// <summary>
/// Plain HUD data a mode supplies. MatchHud and the results screen render it; local views add the crosshair, ammo,
/// hit markers and other feedback. Objectives generalize the tank arrows: positions the HUD points toward.
/// An out-state ("eliminated", "spectating") replaces play feedback when a mode with health needs one. A feed lists
/// recent events, newest last (Ring Out's knock-offs).
/// </summary>
public sealed record ModeHud(
    IReadOnlyList<HudCounter> Counters,
    IReadOnlyList<string> Columns,
    HudTimer? Timer,
    IReadOnlyList<Vector3> Objectives,
    bool ObjectivesVisible,
    string Banner = "",
    string OutState = "",
    IReadOnlyList<string>? Feed = null)
{
    public static readonly ModeHud Empty = new(Array.Empty<HudCounter>(), Array.Empty<string>(), null, Array.Empty<Vector3>(), false);
}

/// <summary>
/// What a presenter reads: mode state as the local peer knows it, plus the world facts it may point at. Where the
/// peer runs the rules itself (the Lab), <paramref name="Rules"/> and <paramref name="Clock"/> let a presenter read
/// mode-specific state such as a boundary or a zone.
/// </summary>
public sealed record ModeHudInput(int LocalActor, ModePhase Phase, float TimeLeft, IReadOnlyDictionary<int, int> Scores,
    IReadOnlyList<Vector3> Objectives, bool ShowObjectives, IModeRules? Rules = null, ModeClock Clock = default);

public interface IModeHudPresenter
{
    ModeHud Present(ModeHudInput input);
}

/// <summary>The sandbox HUD: tank arrows while they are on, nothing else.</summary>
public sealed class SandboxHud : IModeHudPresenter
{
    public ModeHud Present(ModeHudInput input) => ModeHud.Empty with { Objectives = input.Objectives, ObjectivesVisible = input.ShowObjectives };
}

/// <summary>Tank Bank's HUD: the bank and its caption, the match clock, and arrows toward the tank groups.</summary>
public sealed class TankBankHud : IModeHudPresenter
{
    public const string Column = "Tanks banked";
    private static readonly string[] Columns = { Column };

    public ModeHud Present(ModeHudInput input)
    {
        var bank = input.Scores.GetValueOrDefault(input.LocalActor);
        var live = input.Phase is ModePhase.Round or ModePhase.Countdown;
        return new ModeHud(
            new[] { new HudCounter(bank, bank == 1 ? "TANK BANKED" : "TANKS BANKED") },
            Columns,
            input.Phase is ModePhase.Round or ModePhase.RoundEnd or ModePhase.MatchEnd or ModePhase.Results
                ? new HudTimer(input.TimeLeft, RoundTimer.Countdown) : null,
            live ? input.Objectives : Array.Empty<Vector3>(),
            input.ShowObjectives && live);
    }
}
