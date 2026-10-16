using System;
using System.Diagnostics;
using System.Reflection;
using Godot;
using Propane.Core;

namespace Propane.Dev;

/// <summary>
/// The dev harnesses' view of the <c>--key=value</c> user arguments passed after <c>--</c> on the Godot command line.
/// Parsing and the cache belong to <see cref="LaunchArgs"/>, which the shipped game reads too.
/// </summary>
public static class DevArgs
{
    public static string? Get(string key) => LaunchArgs.Current.Get(key);

    public static float GetFloat(string key, float fallback) => LaunchArgs.Current.GetFloat(key, fallback);

    /// <summary>Common setup for dev scenes: applies any <c>--tune.Name=value</c> overrides to the live tuning.</summary>
    public static void Setup() => ApplyTuningOverrides();

    /// <summary>Verify the deep build contract and optionally mark scene entry for tools/dev/time_gate.py.</summary>
    public static void MarkReady()
    {
        if (Get("full-engine-optimized") != null)
        {
            VerifyOptimizedDebug();
            GD.Print("[full-engine] optimized Debug with DEBUG checks");
        }
        if (System.Environment.GetEnvironmentVariable("PROPANE_GATE_TIMING") == "1") GD.Print("[gate-timing] ready");
    }

    private static void VerifyOptimizedDebug()
    {
#if DEBUG
        var assembly = typeof(DevArgs).Assembly;
        if (assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration == "Debug" &&
            assembly.GetCustomAttribute<DebuggableAttribute>()?.IsJITOptimizerDisabled == false)
        {
            return;
        }
#endif
        ShutdownGuard.Quit((SceneTree)Engine.GetMainLoop(), 1);
        throw new InvalidOperationException("deep probe did not load optimized Debug with DEBUG defined");
    }

    /// <summary>
    /// Applies <c>--tune.Name=value</c> arguments to the live tuning, or to <paramref name="target"/>, for trying dial
    /// values from the command line.
    /// </summary>
    public static void ApplyTuningOverrides(Tuning? target = null) => LaunchArgs.Current.ApplyTuningOverrides(target);
}
