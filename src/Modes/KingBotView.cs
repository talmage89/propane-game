using Propane.Modes;

namespace Propane.Bots;

/// <summary>
/// Plain Rules observation/projection; the namespace preserves existing client call sites.
/// Only the zone facts a King brain needs, as a value: reading local rules or advancing a received snapshot's
/// clocks must not construct a snapshot (and its minted roster) for every brain every physics tick.
/// </summary>
public readonly record struct KingBotView(int Zone, ModeSpot Spot, ModeSpot Next, double Radius, int Holder,
    bool Warning, double UntilMove)
{
    public static KingBotView? Read(KingRules rules, ModeClock clock)
    {
        if (rules.Zone < 0) return null;
        var elapsed = rules.Elapsed(clock);
        return new KingBotView(rules.Zone, rules.SpotOf(rules.Zone), rules.SpotOf(rules.Zone + 1), rules.Radius,
            rules.Holder, rules.Warning(elapsed), rules.UntilMove(elapsed));
    }

    public static KingBotView Read(KingState state, double elapsed) =>
        new(state.Zone, state.Spot, state.Next, state.Radius, state.Holder,
            elapsed >= state.MoveAt - state.WarningSeconds, System.Math.Max(0, state.MoveAt - elapsed));
}
