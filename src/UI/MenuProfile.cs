using Godot;
using Propane.Core;
using Propane.Net;

namespace Propane.UI;

/// <summary>Local identity defaults and persistence through PlayerSettings.</summary>
internal static class MenuProfile
{
    public static string DefaultName()
    {
        var saved = PlayerSettings.Name;
        if (saved.Length > 0)
        {
            return saved;
        }
        var user = System.Environment.UserName;
        return Protocol.CleanName(user.Length > 0 ? char.ToUpperInvariant(user[0]) + user[1..] : "Player");
    }

    public static AppearanceRecord DefaultAppearance()
    {
        return PlayerSettings.Appearance ?? AppearanceRecord.FromPalette((int)(GD.Randi() % Protocol.PlayerColors.Length));
    }

    public static void Save(string name, AppearanceRecord appearance)
    {
        SaveName(name);
        PlayerSettings.Appearance = appearance;
    }

    public static void SaveName(string name) => PlayerSettings.Name = Protocol.CleanName(name);
}
