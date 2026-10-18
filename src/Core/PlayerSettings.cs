using System.IO;
using Godot;

namespace Propane.Core;

/// <summary>What the player last typed or picked in the menus (name, color, server), kept between runs.</summary>
public static class PlayerSettings
{
    private static string path = "user://settings.cfg";

    private static ConfigFile? file;

    private static ConfigFile File
    {
        get
        {
            if (file == null)
            {
                file = new ConfigFile();
                file.Load(path);
            }
            return file;
        }
    }

    /// <summary>Keeps test runs from overwriting the player's own settings.</summary>
    public static void UseFile(string settingsPath)
    {
        path = settingsPath;
        file?.Dispose();
        file = null;
        showLeaderboard = null;
    }

    public static string Name
    {
        get => File.GetValue("player", "name", "").AsString();
        set => Set("player", "name", value);
    }

    /// <summary>The wire record, persisted unchanged. A saved palette index migrates only if no record exists.</summary>
    public static AppearanceRecord? Appearance
    {
        get
        {
            if (File.HasSectionKey("player", "appearance"))
            {
                var value = File.GetValue("player", "appearance");
                if (value.VariantType != Variant.Type.PackedByteArray) { return null; }
                try { return AppearanceCodec.Decode(value.AsByteArray()); }
                catch (InvalidDataException) { return null; }
            }
            var legacy = File.GetValue("player", "color", -1);
            if (legacy.VariantType != Variant.Type.Int || legacy.AsInt64() < 0 || legacy.AsInt64() >= AppearancePalette.PlayerColors.Length) { return null; }
            var migrated = AppearanceRecord.FromPalette(legacy.AsInt32());
            Appearance = migrated;
            return migrated;
        }
        set
        {
            if (value == null) { throw new System.ArgumentNullException(nameof(value)); }
            Set("player", "appearance", AppearanceCodec.Encode(value));
        }
    }

    /// <summary>The last server, as "host" or "host:port".</summary>
    public static string Server
    {
        get => File.GetValue("net", "server", "").AsString();
        set => Set("net", "server", value);
    }

    private static bool? showLeaderboard;

    /// <summary>Whether the match HUD shows the leaderboard; the match menu toggles it. Read every frame, so cached.</summary>
    public static bool ShowLeaderboard
    {
        get => showLeaderboard ??= File.GetValue("hud", "leaderboard", true).AsBool();
        set
        {
            showLeaderboard = value;
            Set("hud", "leaderboard", value);
        }
    }

    private static void Set(string section, string key, Variant value)
    {
        File.SetValue(section, key, value);
        File.Save(path);
    }
}
