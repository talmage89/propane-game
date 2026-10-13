using System;

namespace Propane.Modes;

/// <summary>Server-time lengths the phase machine reads, sampled live from the match's tuning at each call.</summary>
public readonly record struct PhaseTimes(double LoadTimeout, double Countdown, double Results);

/// <summary>A phase change the runner must announce.</summary>
public readonly record struct PhaseChange(ModePhase From, ModePhase To, RoundInfo Round);

/// <summary>
/// The runner's phases, pure: Loading → Countdown → Round → RoundEnd → [next round | MatchEnd → Results] → Lobby.
/// Countdown-timed rounds end on their deadline; count-up and untimed rounds end when the rules say so. The mode's
/// answer at RoundEnd decides whether another round starts and whether its world is rebuilt (Loading again with
/// the next world revision) or kept (straight to Countdown).
/// </summary>
public sealed class ModePhaseMachine
{
    public ModePhase Phase { get; private set; } = ModePhase.Lobby;
    public double PhaseEnds { get; private set; }
    public RoundInfo Round { get; private set; } = RoundInfo.First;

    public ModeClock Clock(double now) => new(Phase, Round.RoundId, now, PhaseEnds);

    /// <summary>The world is on its way to everyone: wait for them, up to the load timeout.</summary>
    public PhaseChange Load(double now, PhaseTimes times, RoundTimer timer, double length)
    {
        if (Phase != ModePhase.Lobby) throw new InvalidOperationException($"cannot load from {Phase}");
        Round = RoundInfo.First with { Timer = timer, Length = length };
        return Enter(ModePhase.Loading, now + times.LoadTimeout);
    }

    /// <summary>Advances on the server clock. Returns the change to announce, if any; call again until null.</summary>
    public PhaseChange? Advance(double now, PhaseTimes times, bool everyoneLoaded)
    {
        switch (Phase)
        {
            case ModePhase.Loading when everyoneLoaded || now >= PhaseEnds:
                return Enter(ModePhase.Countdown, now + times.Countdown);
            case ModePhase.Countdown when now >= PhaseEnds:
                Round = Round with { StartsAt = now };
                return Enter(ModePhase.Round, Round.Timer == RoundTimer.Countdown ? now + Round.Length : double.PositiveInfinity);
            case ModePhase.Round when now >= PhaseEnds:
                return Enter(ModePhase.RoundEnd, now);
            case ModePhase.MatchEnd:
                return Enter(ModePhase.Results, now + times.Results);
            case ModePhase.Results when now >= PhaseEnds:
                return Enter(ModePhase.Lobby, now);
            default:
                return null;
        }
    }

    /// <summary>The rules ended a round that has no deadline (count-up or untimed).</summary>
    public PhaseChange EndRound(double now) => Phase == ModePhase.Round ? Enter(ModePhase.RoundEnd, now)
        : throw new InvalidOperationException($"no round to end in {Phase}");

    /// <summary>The rules' decision at RoundEnd.</summary>
    public PhaseChange Decide(double now, PhaseTimes times, ModeCommand decision)
    {
        if (Phase != ModePhase.RoundEnd) throw new InvalidOperationException($"round decisions belong to RoundEnd, not {Phase}");
        switch (decision)
        {
            case EndMatch:
                return Enter(ModePhase.MatchEnd, now);
            case NextRound next:
                var pending = new RoundInfo(checked(Round.RoundId + 1), next.RebuildWorld ? checked(Round.WorldRevision + 1) : Round.WorldRevision,
                    next.Timer, 0, next.Length);
                Round = pending;
                return next.RebuildWorld ? Enter(ModePhase.Loading, now + times.LoadTimeout) : Enter(ModePhase.Countdown, now + times.Countdown);
            default:
                throw new ArgumentException($"{decision.GetType().Name} does not decide a round", nameof(decision));
        }
    }

    private PhaseChange Enter(ModePhase next, double ends)
    {
        var from = Phase;
        Phase = next;
        PhaseEnds = ends;
        return new PhaseChange(from, next, Round);
    }
}
