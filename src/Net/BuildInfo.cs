using System;
using Godot;
using Propane.Dev;

namespace Propane.Net;

/// <summary>A MAJOR.MINOR.PATCH version, as in <c>config/version</c>.</summary>
public readonly record struct SemVer(int Major, int Minor, int Patch) : IComparable<SemVer>
{
    /// <summary>Parses "1.2.3" or "v1.2.3".</summary>
    public static SemVer? Parse(string text)
    {
        var parts = text.Trim().TrimStart('v').Split('.');
        if (parts.Length == 3 && int.TryParse(parts[0], out var major) && int.TryParse(parts[1], out var minor) &&
            int.TryParse(parts[2], out var patch) && major >= 0 && minor >= 0 && patch >= 0)
        {
            return new SemVer(major, minor, patch);
        }
        return null;
    }

    /// <summary>Whether two versions play together: the same major and minor. Patches only change things local to each player.</summary>
    public bool CompatibleWith(SemVer other) => Major == other.Major && Minor == other.Minor;

    public int CompareTo(SemVer other) =>
        Major != other.Major ? Major.CompareTo(other.Major) : Minor != other.Minor ? Minor.CompareTo(other.Minor) : Patch.CompareTo(other.Patch);

    public static bool operator >(SemVer a, SemVer b) => a.CompareTo(b) > 0;

    public static bool operator <(SemVer a, SemVer b) => a.CompareTo(b) < 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}

/// <summary>
/// The build's identity, and which other builds it plays with. Builds play together when their protocol and their
/// major and minor versions match; the patch may differ (see Versions in the README).
/// </summary>
public static class BuildInfo
{
    /// <summary>Where players download the game.</summary>
    public const string ReleasesUrl = "https://github.com/talmage89/propane-game/releases/latest";

    private static string? build;

    /// <summary>This game's version. <c>--fake-version=X.Y.Z</c> pretends to be another, for testing.</summary>
    public static string GameVersion => DevArgs.Get("fake-version") ?? ProjectSettings.GetSetting("application/config/version").AsString();

    /// <summary>
    /// The commit an exported build was made from (written to <c>res://build_id.txt</c> by the release scripts), or
    /// "dev" when running from source. Shown for diagnosis; it does not decide who plays together.
    /// </summary>
    public static string Build
    {
        get
        {
            if (build != null)
            {
                return build;
            }
            build = "dev";
            if (Godot.FileAccess.FileExists("res://build_id.txt"))
            {
                var text = Godot.FileAccess.GetFileAsString("res://build_id.txt").Trim();
                if (text.Length > 0)
                {
                    build = text;
                }
            }
            return build;
        }
    }

    /// <summary>The series this version plays with, e.g. "0.2.x".</summary>
    public static string Series(string version) => SemVer.Parse(version) is { } v ? $"{v.Major}.{v.Minor}.x" : version;

    /// <summary>Why a client's game may not join this server, or null if it may.</summary>
    public static string? Mismatch(int protocol, string version)
    {
        var mine = SemVer.Parse(GameVersion);
        var theirs = SemVer.Parse(version);
        var compatible = mine is { } m && theirs is { } t ? m.CompatibleWith(t) : version == GameVersion;
        if (compatible && protocol == Protocol.Version)
        {
            return null;
        }
        if (compatible)
        {
            // Same version, different protocol: one side runs from source with unreleased changes.
            return $"This server and your game speak different protocols ({Protocol.Version} and {protocol}) although both are {GameVersion}.";
        }
        if (mine is { } server && theirs is { } client && client < server)
        {
            return $"This server runs Propane {Series(GameVersion)}; you have {version}. Update Propane to play here.";
        }
        return $"This server runs Propane {GameVersion}, older than your {version}. Its host needs to update it to {Series(version)}.";
    }

    /// <summary>Whether <paramref name="version"/> is a newer release than this game.</summary>
    public static bool IsNewer(string version) => SemVer.Parse(version) is { } v && SemVer.Parse(GameVersion) is { } mine && v > mine;
}
