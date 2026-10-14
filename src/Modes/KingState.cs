using System;
using System.Collections.Generic;
using System.Linq;

namespace Propane.Modes;

/// <summary>Tanks one actor has minted in the zone this round.</summary>
public readonly record struct KingMinted(int ActorId, int Count);

/// <summary>
/// King of the Cul-de-sac as every peer sees it: the zone in force and the spot it moves to next, who holds or
/// contests it, the mint and move clocks, and each actor's minted tanks. Times are round time (seconds since the round
/// started), so a peer reads the clocks against its own round clock rather than against when the state arrived. The
/// rules produce it where a peer runs them (the Lab, <see cref="KingRules.State"/>); a networked peer decodes it from
/// the server's Zone snapshot. <see cref="Elapsed"/> is the round time it describes; <see cref="At"/> moves it.
/// </summary>
public sealed record KingState(
    int Zone,
    ModeSpot Spot,
    ModeSpot Next,
    double Radius,
    int Holder,
    IReadOnlyList<int> Occupants,
    double NextMint,
    double MintSeconds,
    double MoveAt,
    double WarningSeconds,
    IReadOnlyList<KingMinted> Minted,
    double Elapsed) : IModeView
{
    /// <summary>More than one upright actor is inside: nobody mints.</summary>
    public bool Contested => Occupants.Count > 1;

    /// <summary>Seconds until the zone moves to <see cref="Next"/>.</summary>
    public double UntilMove => Math.Max(0, MoveAt - Elapsed);

    /// <summary>The move is coming: the next spot shows and the zone pulses.</summary>
    public bool Warning => Elapsed >= MoveAt - WarningSeconds;

    /// <summary>How far the holder is toward its next tank, 0 to 1 (0 with no holder).</summary>
    public double MintProgress => Holder == 0 || MintSeconds <= 0 ? 0 : Math.Clamp(1 - (NextMint - Elapsed) / MintSeconds, 0, 1);

    public int MintedBy(int actorId)
    {
        foreach (var entry in Minted) { if (entry.ActorId == actorId) return entry.Count; }
        return 0;
    }

    /// <summary>The same state read at another round time.</summary>
    public KingState At(double elapsed) => this with { Elapsed = elapsed };

    /// <summary>
    /// Everything but the clocks' progress agrees: the zone, its spots and radius, the holder, the occupants, the mint
    /// totals and the dials. A runner restates the state only when this changes; peers advance the clocks themselves.
    /// </summary>
    public bool Matches(KingState other) => Zone == other.Zone && Spot == other.Spot && Next == other.Next && Radius == other.Radius &&
        Holder == other.Holder && Occupants.SequenceEqual(other.Occupants) && NextMint == other.NextMint &&
        MintSeconds == other.MintSeconds && MoveAt == other.MoveAt && WarningSeconds == other.WarningSeconds && Minted.SequenceEqual(other.Minted);
}
