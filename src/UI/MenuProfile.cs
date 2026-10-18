using Godot;
using Propane.Core;
using Propane.Net;

namespace Propane.UI;

/// <summary>Local identity defaults and persistence through PlayerSettings.</summary>
internal sealed class MenuProfile(IPlayerSettings settings)
{
    public string Server { get => settings.Server; set => settings.Server = value; }

    public string DefaultName()
    {
        var saved = settings.Name;
        if (saved.Length > 0)
        {
            return saved;
        }
        var user = System.Environment.UserName;
        return Protocol.CleanName(user.Length > 0 ? char.ToUpperInvariant(user[0]) + user[1..] : "Player");
    }

    public AppearanceRecord DefaultAppearance()
    {
        return settings.Appearance ?? AppearanceRecord.FromPalette((int)(GD.Randi() % AppearancePalette.PlayerColors.Length));
    }

    public void Save(string name, AppearanceRecord appearance)
    {
        SaveName(name);
        settings.Appearance = appearance;
    }

    public void SaveName(string name) => settings.Name = Protocol.CleanName(name);
}
