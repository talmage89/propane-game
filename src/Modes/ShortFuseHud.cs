using System;
using System.Linq;
using Godot;

namespace Propane.Modes;

/// <summary>
/// Short Fuse's HUD: round wins (first to the match's count), who is still standing, this player's kills, the round
/// clock, a health bar, a kill feed for the round, the round's result as a banner, and while dead a caption naming
/// the player being watched.
/// </summary>
public sealed class ShortFuseHud : IModeHudPresenter
{
    public const int FeedLength = 5;
    private static readonly string[] Columns = { "Round wins" };

    public ModeHud Present(ModeHudInput input)
    {
        if (input.View is not ShortFuseState state) return ModeHud.Empty;
        var me = input.LocalActor;
        var actor = state.Actor(me);
        var alive = actor.Alive;
        var value = alive ? (float)actor.Health : 0f;
        var shielded = alive && actor.Shielded;
        var wins = state.Wins(me);
        var kills = state.Kills(me);
        var outState = "";
        if (!alive && actor.Started && !state.Over)
        {
            var watched = actor.SpectateTarget;
            outState = watched != 0 ? $"SPECTATING {state.Name(watched).ToUpperInvariant()}" : "OUT";
        }
        return new ModeHud(
            new[]
            {
                new HudCounter(wins, $"{(wins == 1 ? "ROUND" : "ROUNDS")} WON · FIRST TO {state.WinsToMatch}"),
                new HudCounter(state.AliveCount, "STANDING"),
                new HudCounter(kills, kills == 1 ? "KILL" : "KILLS"),
            },
            Columns,
            input.Phase is ModePhase.Round ? new HudTimer(input.TimeLeft, RoundTimer.Countdown) : null,
            Array.Empty<Vector3>(), false,
            Banner(state, me),
            outState,
            state.RoundDeaths.TakeLast(FeedLength).Select(d => Line(state, d)).ToArray(),
            actor.Started
                ? new HudBar(value, (float)state.MaxHealth, alive ? (shielded ? "GETTING UP · PROTECTED" : $"{Mathf.CeilToInt(value)} HEALTH") : "DEAD", value <= state.MaxHealth * 0.3)
                : null);
    }

    /// <summary>The round's or the match's result once decided, else nothing.</summary>
    public static string Banner(ShortFuseState state, int me)
    {
        if (!state.Over || state.Rounds.Count == 0) return "";
        var last = state.Rounds[^1];
        if (state.MatchWinner is { } champion)
        {
            var score = string.Join("–", state.Actors.Select(a => state.Wins(a.ActorId)).OrderByDescending(w => w).Take(2));
            return champion == me ? $"YOU WIN THE MATCH {score}" : $"{state.Name(champion).ToUpperInvariant()} WINS THE MATCH {score}";
        }
        return last.WinnerId == 0 ? "NOBODY WINS THE ROUND" : last.WinnerId == me ? "YOU WIN THE ROUND" :
            $"{state.Name(last.WinnerId).ToUpperInvariant()} WINS THE ROUND";
    }

    /// <summary>One kill feed line: who got whom and how.</summary>
    public static string Line(ShortFuseState state, ActorDied death)
    {
        var victim = state.Name(death.VictimId);
        if (death.CreditedTo == 0)
            return death.Kind == DamageKind.Blast && death.KilledBy == death.VictimId ? $"{victim} blew themself up" : $"{victim} went down";
        var killer = state.Name(death.CreditedTo);
        if (death.CreditedTo != death.KilledBy) return $"{killer} got {victim}";
        return death.Kind == DamageKind.Blast ? $"{killer} blew up {victim}" : $"{killer} shot {victim}";
    }
}
