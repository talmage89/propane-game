using System.Collections.Generic;
using Godot;

namespace Propane.Core;

/// <summary>
/// The user arguments after <c>--</c> on the Godot command line (<c>--key=value</c>, or <c>--key</c> alone for
/// <c>"true"</c>), parsed once per process. The shipped game reads its few launch options here (server mode, transport
/// faults, version and protocol overrides, update checks, <c>--classic-lobby</c>, <c>--release-test</c>; see
/// docs/code-quality.md), and the dev harnesses read theirs through <c>DevArgs</c>, which shares this cache.
/// </summary>
public sealed class LaunchArgs
{
    private static LaunchArgs? current;
    private readonly Dictionary<string, string> values = new();

    /// <summary>
    /// Parses <paramref name="args"/>: leading dashes are dropped, everything after the first <c>=</c> past the first
    /// character is the value, and a later argument replaces an earlier one with the same key.
    /// </summary>
    public LaunchArgs(IEnumerable<string> args)
    {
        foreach (var arg in args)
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

    /// <summary>This process's arguments, read from the engine on first use. Tests replace and restore it.</summary>
    public static LaunchArgs Current => current ??= new LaunchArgs(OS.GetCmdlineUserArgs());

    private static Override? activeOverride;

    /// <summary>Main-thread test scope; restores the exact previous snapshot, including lazy initialization.</summary>
    internal static System.IDisposable OverrideForTests(LaunchArgs replacement)
    {
        System.ArgumentNullException.ThrowIfNull(replacement);
        var scope = new Override(current, activeOverride);
        current = replacement;
        activeOverride = scope;
        return scope;
    }

    private sealed class Override(LaunchArgs? previous, Override? parent) : System.IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            if (activeOverride != this) throw new System.InvalidOperationException("launch overrides must retire in reverse order");
            current = previous;
            activeOverride = parent;
            disposed = true;
        }
    }

    internal IEnumerable<KeyValuePair<string, string>> Entries
    {
        get
        {
            foreach (var entry in values)
                yield return entry;
        }
    }

    /// <summary>The value given for <paramref name="key"/>, or null when the argument is absent.</summary>
    public string? Get(string key) => values.TryGetValue(key, out var value) ? value : null;

    /// <summary>Whether <paramref name="key"/> was given, with or without a value.</summary>
    public bool Has(string key) => Get(key) != null;

    /// <summary>The value of <paramref name="key"/> as a number (current culture), or <paramref name="fallback"/> when absent or not one.</summary>
    public float GetFloat(string key, float fallback) => float.TryParse(Get(key), out var v) ? v : fallback;
}
