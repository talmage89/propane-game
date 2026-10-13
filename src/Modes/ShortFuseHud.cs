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
        if (input.Rules is not ShortFuseRules rules) return ModeHud.Empty;
        var me = input.LocalActor;
        var now = input.Clock.Now;
        var health = rules.Health;
        var alive = rules.IsAlive(me);
        var value = alive ? (float)health.Health(me, now) : 0f;
        var shielded = alive && health.Invulnerable(me, now);
        var wins = rules.Wins(me);
        var kills = rules.Kills(me);
        var outState = "";
        if (!alive && rules.Starters.Contains(me) && !rules.Over)
        {
            var watched = rules.SpectateTarget(me);
            outState = watched != 0 ? $"SPECTATING {rules.Name(watched).ToUpperInvariant()}" : "OUT";
        }
        return new ModeHud(
            new[]
            {
                new HudCounter(wins, $"{(wins == 1 ? "ROUND" : "ROUNDS")} WON · FIRST TO {rules.WinsToMatch}"),
                new HudCounter(rules.AliveCount, "STANDING"),
                new HudCounter(kills, kills == 1 ? "KILL" : "KILLS"),
            },
            Columns,
            input.Phase is ModePhase.Round ? new HudTimer(input.TimeLeft, RoundTimer.Countdown) : null,
            Array.Empty<Vector3>(), false,
            Banner(rules, me),
            outState,
            rules.RoundDeaths.TakeLast(FeedLength).Select(d => Line(rules, d)).ToArray(),
            rules.Starters.Contains(me)
                ? new HudBar(value, (float)rules.MaxHealth, alive ? (shielded ? "GETTING UP · PROTECTED" : $"{Mathf.CeilToInt(value)} HEALTH") : "DEAD", value <= rules.MaxHealth * 0.3)
                : null);
    }

    /// <summary>The round's or the match's result once decided, else nothing.</summary>
    public static string Banner(ShortFuseRules rules, int me)
    {
        if (!rules.Over || rules.Rounds.Count == 0) return "";
        var last = rules.Rounds[^1];
        if (rules.MatchWinner is { } champion)
        {
            var score = string.Join("–", rules.Actors.Select(a => rules.Wins(a.ActorId)).OrderByDescending(w => w).Take(2));
            return champion == me ? $"YOU WIN THE MATCH {score}" : $"{rules.Name(champion).ToUpperInvariant()} WINS THE MATCH {score}";
        }
        return last.WinnerId == 0 ? "NOBODY WINS THE ROUND" : last.WinnerId == me ? "YOU WIN THE ROUND" :
            $"{rules.Name(last.WinnerId).ToUpperInvariant()} WINS THE ROUND";
    }

    /// <summary>One kill feed line: who got whom and how.</summary>
    public static string Line(ShortFuseRules rules, ActorDied death)
    {
        var victim = rules.Name(death.VictimId);
        if (death.CreditedTo == 0)
            return death.Kind == DamageKind.Blast && death.KilledBy == death.VictimId ? $"{victim} blew themself up" : $"{victim} went down";
        var killer = rules.Name(death.CreditedTo);
        if (death.CreditedTo != death.KilledBy) return $"{killer} got {victim}";
        return death.Kind == DamageKind.Blast ? $"{killer} blew up {victim}" : $"{killer} shot {victim}";
    }
}
