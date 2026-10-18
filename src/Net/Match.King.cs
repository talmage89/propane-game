using System;
using Propane.Fx;
using Propane.Modes;

namespace Propane.Net;

public partial class Match
{
    private KingZone? kingZone;

    /// <summary>
    /// The mode's own state as this peer knows it now, for the HUD, bots and harnesses: King's zone, from the server's
    /// latest Zone snapshot read at this peer's round clock. Retained for results; null before the first snapshot.
    /// </summary>
    public IModeView? ModeView => clientMode?.ViewAt(Math.Max(0, Setup.Length - timeLeft));

    /// <summary>The King zone's presenter, when this peer has a view and plays King.</summary>
    private void BuildKingZone()
    {
        if (clientMode is not KingClientAdapter || !CreateLocalView) return;
        kingZone = new KingZone
        {
            World = World,
            Name = "KingZone",
            State = () => IsPlaying ? ModeView as KingState : null,
            ActorColor = id => profiles.TryGetValue(id, out var profile) ? profile.Appearance.Color : null,
        };
        suburb.AddChild(kingZone);
    }
}
