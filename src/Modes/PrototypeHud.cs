using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Propane.Modes;

/// <summary>
/// Ring Out's HUD: how many are still standing, this player's knock-offs, a clock to the next dissolve, a banner
/// while the rim warns, the out-state once off the slab, and a feed of the latest eliminations.
/// </summary>
public sealed class RingOutHud : IModeHudPresenter
{
    public const int FeedLength = 4;
    private static readonly string[] Columns = { "Placement" };

    public ModeHud Present(ModeHudInput input)
    {
        if (input.View is not RingOutState state) return ModeHud.Empty;
        var elapsed = state.Elapsed;
        var plan = state.Plan;
        var next = plan.NextDissolve(elapsed);
        var band = plan.NextBand(elapsed);
        var warning = !state.Over && band >= 0 && elapsed >= plan.WarnsAt(band);
        var me = input.LocalActor;
        var outState = state.Place(me) is { } place && !(state.Over && place == 1) ? $"OUT · {Ordinal(place)}" : "";
        var banner = state.Over ? Result(state, me) : warning ? "THE RIM IS DISSOLVING" : "";
        var credits = state.Credits(me);
        return new ModeHud(
            new[] { new HudCounter(state.AliveCount, "STANDING"), new HudCounter(credits, credits == 1 ? "KNOCK-OFF" : "KNOCK-OFFS") },
            Columns,
            state.Over ? null : double.IsInfinity(next) ? new HudTimer((float)elapsed, RoundTimer.CountUp) : new HudTimer((float)(next - elapsed), RoundTimer.Countdown),
            Array.Empty<Vector3>(), false, banner, outState,
            state.Eliminations.TakeLast(FeedLength).Select(e => Line(state, e)).ToArray());
    }

    public static string Line(RingOutState state, ActorEliminated e) => e.Cause switch
    {
        EliminationCause.Left => $"{state.Name(e.ActorId)} left",
        _ when e.CreditedTo != 0 => $"{state.Name(e.CreditedTo)} knocked {state.Name(e.ActorId)} off",
        EliminationCause.Dissolved => $"{state.Name(e.ActorId)} went down with the rim",
        _ => $"{state.Name(e.ActorId)} fell",
    };

    private static string Result(RingOutState state, int me)
    {
        var winners = state.Actors.Where(s => state.Place(s.ActorId) == 1).Select(s => s.Name).ToArray();
        if (state.Place(me) == 1) return winners.Length > 1 ? "SHARED WIN" : "LAST ONE STANDING";
        return winners.Length == 0 ? "NOBODY STANDS" : $"{string.Join(" & ", winners).ToUpperInvariant()} WINS";
    }

    public static string Ordinal(int place) => place + (place % 100 is 11 or 12 or 13 ? "TH" : (place % 10) switch { 1 => "ST", 2 => "ND", 3 => "RD", _ => "TH" });
}

/// <summary>
/// King of the Cul-de-sac's HUD: the bank as in Tank Bank, the tanks this player minted, the match clock, an arrow
/// to the zone (and to the next one while it warns), a banner for holding, contesting or the coming move, and the
/// holder's progress toward its next tank. It reads the zone from <see cref="KingState"/>, which the Lab produces
/// from its rules and a match decodes from the server's snapshot.
/// </summary>
public sealed class KingHud : IModeHudPresenter
{
    private static readonly string[] Columns = { TankBankHud.Column };

    public ModeHud Present(ModeHudInput input)
    {
        var me = input.LocalActor;
        var bank = input.Scores.GetValueOrDefault(me);
        var live = input.Phase is ModePhase.Round;
        var state = input.View as KingState;
        var objectives = new List<Vector3>();
        var banner = "";
        HudBar? bar = null;
        if (live && state != null)
        {
            objectives.Add(Point(state.Spot));
            if (state.Warning)
            {
                objectives.Add(Point(state.Next));
                banner = $"THE ZONE MOVES IN {Math.Ceiling(state.UntilMove):0}";
            }
            else if (state.Holder == me)
            {
                banner = "HOLDING THE ZONE";
            }
            else if (state.Contested && state.Occupants.Contains(me))
            {
                banner = "CONTESTED";
            }
            if (state.Holder == me) bar = new HudBar((float)state.MintProgress, 1, "NEXT TANK");
        }
        var minted = state?.MintedBy(me) ?? 0;
        return new ModeHud(
            new[] { new HudCounter(bank, bank == 1 ? "TANK BANKED" : "TANKS BANKED"), new HudCounter(minted, "MINTED IN THE ZONE") },
            Columns,
            input.Phase is ModePhase.Round or ModePhase.RoundEnd or ModePhase.MatchEnd or ModePhase.Results
                ? new HudTimer(input.TimeLeft, RoundTimer.Countdown) : null,
            objectives, live, banner, Bar: bar);
    }

    private static Vector3 Point(ModeSpot spot) => new((float)spot.X, (float)spot.Y + 1f, (float)spot.Z);
}
