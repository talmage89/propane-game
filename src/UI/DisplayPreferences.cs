using System.Linq;
using Godot;
using Propane.Core;

namespace Propane.UI;

/// <summary>Main-owned process display binding. Borrows preferences; detaches its synchronous hook on tree exit.</summary>
public partial class DisplayPreferences : Node
{
    public required IPlayerSettings Settings { get; init; }

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        Settings.PreferenceChanged += Apply;
        foreach (var preference in PreferenceCatalog.All.Where(p => p.Target == PreferenceTarget.Display))
        {
            if (preference == PreferenceCatalog.Vsync && OS.GetCmdlineArgs().Contains("--disable-vsync")) continue;
            Apply(preference);
        }
    }

    public override void _ExitTree() => Settings.PreferenceChanged -= Apply;

    private void Apply(PreferenceDefinition preference)
    {
        var value = Settings.GetPreference(preference);
        if (preference == PreferenceCatalog.FrameCap)
        {
            Engine.MaxFps = preference.OptionValues![(int)value];
        }
        else if (preference == PreferenceCatalog.RenderScale)
        {
            GetViewport().Scaling3DScale = (float)value;
        }
        else if (DisplayServer.GetName() != "headless")
        {
            if (preference == PreferenceCatalog.WindowMode)
                DisplayServer.WindowSetMode(value == 0 ? DisplayServer.WindowMode.Windowed : value == 1 ? DisplayServer.WindowMode.Fullscreen : DisplayServer.WindowMode.ExclusiveFullscreen);
            else if (preference == PreferenceCatalog.Vsync)
                DisplayServer.WindowSetVsyncMode(value == 0 ? DisplayServer.VSyncMode.Disabled : DisplayServer.VSyncMode.Enabled);
        }
    }

    /// <summary>Native display fallbacks are observable immediately; keep the saved request for other displays.</summary>
    public static string FallbackNotice(IPlayerSettings settings)
    {
        if (DisplayServer.GetName() == "headless") return "";
        var vsync = settings.GetPreference(PreferenceCatalog.Vsync) == 0 && DisplayServer.WindowGetVsyncMode() != DisplayServer.VSyncMode.Disabled;
        var exclusive = settings.GetPreference(PreferenceCatalog.WindowMode) == 2 && DisplayServer.WindowGetMode() != DisplayServer.WindowMode.ExclusiveFullscreen;
        const string vsyncNotice = "VSync is forced on by this display.";
        const string exclusiveNotice = "Exclusive mode is unavailable; using fullscreen.";
        return vsync ? exclusive ? vsyncNotice + " " + exclusiveNotice : vsyncNotice : exclusive ? exclusiveNotice : "";
    }

    /// <summary>All existing fullscreen shortcuts edit the same preference, including in standalone dev scenes.</summary>
    public static bool ToggleFullscreen()
    {
        var next = DisplayServer.WindowGetMode() is DisplayServer.WindowMode.Fullscreen or DisplayServer.WindowMode.ExclusiveFullscreen ? 0 : 1;
        var saved = PlayerPreferences.Current?.SetPreference(PreferenceCatalog.WindowMode, next) ?? true;
        // A standalone Game/Lab can bind preferences without Main's process display hook.
        var mode = next == 0 ? DisplayServer.WindowMode.Windowed : DisplayServer.WindowMode.Fullscreen;
        if (DisplayServer.GetName() != "headless" && DisplayServer.WindowGetMode() != mode) DisplayServer.WindowSetMode(mode);
        return saved;
    }
}
