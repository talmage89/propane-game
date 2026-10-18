using System;
using Godot;

namespace Propane.Core;

/// <summary>Composition applies launch overrides to an explicit world target or the application template.</summary>
public static class LaunchTuningOverrides
{
    /// <summary>
    /// Applies <c>--tune.Name=value</c> arguments to <paramref name="target"/>, or to the live saved template when it is
    /// null (loaded only if an override names a tuning value), for trying dial values from the command line.
    /// </summary>
    public static void ApplyTuningOverrides(this LaunchArgs args, Tuning? target = null)
    {
        foreach (var (key, value) in args.Entries)
        {
            if (!key.StartsWith("tune."))
            {
                continue;
            }
            var property = typeof(Tuning).GetProperty(key[5..]);
            if (property == null)
            {
                GD.PushError($"[dev] no tuning value named {key[5..]}");
                continue;
            }
            property.SetValue(target ?? Tuning.SavedTemplate, Convert.ChangeType(value, property.PropertyType, System.Globalization.CultureInfo.InvariantCulture));
            GD.Print($"[dev] tuning {key[5..]} = {value}");
        }
    }
}
