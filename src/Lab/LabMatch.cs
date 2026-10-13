using Propane.Modes;

namespace Propane.Lab;

/// <summary>
/// A multi-round prototype's match in the Lab. Each round rebuilds the session with a new suburb; this carries what
/// outlives it: the rules (and so the wins, kills and life ids), the round, and the mode clock, which keeps running
/// across rounds as a server's would.
/// </summary>
public sealed class LabMatch(int seed)
{
    public int Seed { get; } = seed;
    public uint Round { get; internal set; } = 1;
    /// <summary>Mode-clock time at which the current round starts.</summary>
    public double ClockOffset { get; internal set; }
    public IModeRules? Rules { get; internal set; }
    /// <summary>This round's suburb: the match's seed, stepped by round.</summary>
    public int SuburbSeed => unchecked(Seed + 7919 * (int)(Round - 1));
}
