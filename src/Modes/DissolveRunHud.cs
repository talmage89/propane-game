using System;
using System.Linq;
using Godot;

namespace Propane.Modes;

/// <summary>
/// Dissolve Run's HUD: the score (large), meters run and tanks detonated, the run's clock counting up, a bar for how far
/// ahead of the front you are (hot when it is close), the open chain as a banner, and the latest scoring chains.
/// </summary>
public sealed class DissolveRunHud : IModeHudPresenter
{
    public const int FeedLength = 4;
    /// <summary>A gap under this many meters turns the bar hot.</summary>
    public const float CloseGap = 8f;
    /// <summary>The bar is full at this gap.</summary>
    public const float BarGap = 40f;
    private static readonly string[] Columns = { "Score" };

    public ModeHud Present(ModeHudInput input)
    {
        if (input.Rules is not DissolveRunRules rules) return ModeHud.Empty;
        var gap = (float)Math.Max(0, rules.Gap);
        var chain = rules.OpenChain;
        var banner = rules.Caught ? "CAUGHT BY THE FRONT" : chain >= 2 ? $"CHAIN ×{chain}" : "";
        return new ModeHud(
            new[]
            {
                new HudCounter(rules.Score, "SCORE"),
                new HudCounter(rules.Meters, rules.Meters == 1 ? "METER" : "METERS"),
                new HudCounter(rules.Tanks, rules.Tanks == 1 ? "TANK" : "TANKS"),
            },
            Columns,
            rules.Over ? null : new HudTimer((float)rules.Elapsed(input.Clock), RoundTimer.CountUp),
            Array.Empty<Vector3>(), false,
            banner,
            rules.Caught ? $"RUN OVER · {rules.Score}" : "",
            rules.Chains.Where(c => c.Bonus > 0).TakeLast(FeedLength).Select(c => $"Chain of {c.Tanks} +{c.Bonus}").ToArray(),
            rules.Caught ? null : new HudBar(Mathf.Min(gap, BarGap), BarGap, $"FRONT {Mathf.FloorToInt(gap)} M BEHIND", gap < CloseGap));
    }
}
