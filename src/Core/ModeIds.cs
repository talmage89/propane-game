namespace Propane.Core;

/// <summary>Stable mode identities shared by plain wire values and composition. These do not grant eligibility.</summary>
public static class ModeIds
{
    public const byte Sandbox = 1, TankBank = 2, RingOut = 3, King = 4, ShortFuse = 5, DissolveRun = 6, Lobby = 7;
    public const ushort TankBankSchema = 1;
}
