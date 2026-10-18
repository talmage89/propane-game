using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Modes;

namespace Propane.Dev.UnitTests.Fixtures;

/// <summary>
/// Ring Out's HUD: how many are still standing, this player's knock-offs, a clock to the next dissolve, a banner
/// while the rim warns, the out-state once off the slab, and a feed of the latest eliminations.
/// </summary>
internal sealed class LegacyRingOutHud
{
    public const int FeedLength = 4;
    private static readonly string[] Columns = { "Placement" };

    public ModeHud Present(LegacyPrototypeInput input)
    {
        if (input.Rules is not RingOutRules rules) return ModeHud.Empty;
        var elapsed = rules.Elapsed(input.Clock);
        var plan = rules.Plan;
        var next = plan.NextDissolve(elapsed);
        var band = plan.NextBand(elapsed);
        var warning = !rules.Over && band >= 0 && elapsed >= plan.WarnsAt(band);
        var me = input.LocalActor;
        var outState = rules.Place(me) is { } place && !(rules.Over && place == 1) ? $"OUT · {Ordinal(place)}" : "";
        var banner = rules.Over ? Result(rules, me) : warning ? "THE RIM IS DISSOLVING" : "";
        var credits = rules.Credits(me);
        return new ModeHud(
            new[] { new HudCounter(rules.AliveCount, "STANDING"), new HudCounter(credits, credits == 1 ? "KNOCK-OFF" : "KNOCK-OFFS") },
            Columns,
            rules.Over ? null : double.IsInfinity(next) ? new HudTimer((float)elapsed, RoundTimer.CountUp) : new HudTimer((float)(next - elapsed), RoundTimer.Countdown),
            Array.Empty<Vector3>(), false, banner, outState,
            rules.Eliminations.TakeLast(FeedLength).Select(e => Line(rules, e)).ToArray());
    }

    public static string Line(RingOutRules rules, ActorEliminated e) => e.Cause switch
    {
        EliminationCause.Left => $"{rules.Name(e.ActorId)} left",
        _ when e.CreditedTo != 0 => $"{rules.Name(e.CreditedTo)} knocked {rules.Name(e.ActorId)} off",
        EliminationCause.Dissolved => $"{rules.Name(e.ActorId)} went down with the rim",
        _ => $"{rules.Name(e.ActorId)} fell",
    };

    private static string Result(RingOutRules rules, int me)
    {
        var winners = rules.Snapshot().Standings.Where(s => rules.Place(s.ActorId) == 1).Select(s => s.Name).ToArray();
        if (rules.Place(me) == 1) return winners.Length > 1 ? "SHARED WIN" : "LAST ONE STANDING";
        return winners.Length == 0 ? "NOBODY STANDS" : $"{string.Join(" & ", winners).ToUpperInvariant()} WINS";
    }

    public static string Ordinal(int place) => place + (place % 100 is 11 or 12 or 13 ? "TH" : (place % 10) switch { 1 => "ST", 2 => "ND", 3 => "RD", _ => "TH" });
}
