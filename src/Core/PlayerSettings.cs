using Godot;

namespace Propane.Core;

/// <summary>What the player last typed or picked in the menus (name, colour, server), kept between runs.</summary>
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
        file = null;
    }

    public static string Name
    {
        get => File.GetValue("player", "name", "").AsString();
        set => Set("player", "name", value);
    }

    public static int Color
    {
        get => File.GetValue("player", "color", -1).AsInt32();
        set => Set("player", "color", value);
    }

    /// <summary>The last server, as "host" or "host:port".</summary>
    public static string Server
    {
        get => File.GetValue("net", "server", "").AsString();
        set => Set("net", "server", value);
    }

    private static void Set(string section, string key, Variant value)
    {
        File.SetValue(section, key, value);
        File.Save(path);
    }
}
