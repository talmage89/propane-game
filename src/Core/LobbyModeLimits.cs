using System;

namespace Propane.Core;

/// <summary>Existing plain lobby setting bounds, shared by rule dials, tuning and wire validation.</summary>
public static class LobbyModeLimits
{
    public const double MintPaceMin = 0.5, MintPaceMax = 10, MintPaceStep = 0.5, MintPaceDefault = 2;
    public const int TankSupplyMinimum = 1, TankSupplyMaximum = 8;

    public static bool ValidMintPace(double seconds) => seconds is >= MintPaceMin and <= MintPaceMax &&
        Math.Abs(seconds / MintPaceStep - Math.Round(seconds / MintPaceStep)) < 1e-6;
}
