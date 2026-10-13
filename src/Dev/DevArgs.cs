using System.Collections.Generic;
using Godot;

namespace Propane.Dev;

/// <summary>Reads <c>--key=value</c> user arguments passed after <c>--</c> on the Godot command line.</summary>
public static class DevArgs
{
    private static Dictionary<string, string>? values;

    public static string? Get(string key)
    {
        if (values == null)
        {
            values = new Dictionary<string, string>();
            foreach (var arg in OS.GetCmdlineUserArgs())
            {
                var trimmed = arg.TrimStart('-');
                var split = trimmed.IndexOf('=');
                if (split > 0)
                {
                    values[trimmed[..split]] = trimmed[(split + 1)..];
                }
                else
                {
                    values[trimmed] = "true";
                }
            }
        }
        return values.TryGetValue(key, out var value) ? value : null;
    }

    public static float GetFloat(string key, float fallback) => float.TryParse(Get(key), out var v) ? v : fallback;

    /// <summary>Common setup for dev scenes: applies any <c>--tune.Name=value</c> overrides to the live tuning.</summary>
    public static void Setup() => ApplyTuningOverrides();

    /// <summary>Optional scene-entry marker for tools/dev/time_gate.py; absent during ordinary runs.</summary>
    public static void MarkReady()
    {
        if (System.Environment.GetEnvironmentVariable("PROPANE_GATE_TIMING") == "1") GD.Print("[gate-timing] ready");
    }

    /// <summary>
    /// Applies <c>--tune.Name=value</c> arguments to the live tuning, or to <paramref name="target"/>, for trying dial
    /// values from the command line.
    /// </summary>
    public static void ApplyTuningOverrides(Core.Tuning? target = null)
    {
        Get("");
        foreach (var (key, value) in values!)
        {
            if (!key.StartsWith("tune."))
            {
                continue;
            }
            var property = typeof(Core.Tuning).GetProperty(key[5..]);
            if (property == null)
            {
                GD.PushError($"[dev] no tuning value named {key[5..]}");
                continue;
            }
            property.SetValue(target ?? Core.Tuning.SavedTemplate, System.Convert.ChangeType(value, property.PropertyType, System.Globalization.CultureInfo.InvariantCulture));
            GD.Print($"[dev] tuning {key[5..]} = {value}");
        }
    }
}
