namespace Propane.Core;

/// <summary>Verbose diagnostics for the rig, shots and ragdoll. Off in play; dev harnesses turn it on with --verbose-log.</summary>
public static class DevLog
{
    public static bool Enabled { get; set; }
}
