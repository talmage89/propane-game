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
        if (input.View is not DissolveRunState state) return ModeHud.Empty;
        var gap = (float)Math.Max(0, state.Gap);
        var chain = state.OpenChain;
        var banner = state.Caught ? "CAUGHT BY THE FRONT" : chain >= 2 ? $"CHAIN ×{chain}" : "";
        return new ModeHud(
            new[]
            {
                new HudCounter(state.Score, "SCORE"),
                new HudCounter(state.Meters, state.Meters == 1 ? "METER" : "METERS"),
                new HudCounter(state.Tanks, state.Tanks == 1 ? "TANK" : "TANKS"),
            },
            Columns,
            state.Over ? null : new HudTimer((float)state.Elapsed, RoundTimer.CountUp),
            Array.Empty<Vector3>(), false,
            banner,
            state.Caught ? $"RUN OVER · {state.Score}" : "",
            state.Chains.Where(c => c.Bonus > 0).TakeLast(FeedLength).Select(c => $"Chain of {c.Tanks} +{c.Bonus}").ToArray(),
            state.Caught ? null : new HudBar(Mathf.Min(gap, BarGap), BarGap, $"FRONT {Mathf.FloorToInt(gap)} M BEHIND", gap < CloseGap));
    }
}
