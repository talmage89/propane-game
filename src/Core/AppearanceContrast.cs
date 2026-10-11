using Godot;

namespace Propane.Core;

/// <summary>Presentation only: preserve selected sRGB bytes and add contrast at the dark/white extremes.</summary>
public static class AppearanceContrast
{
    public static float Strength(Color color)
    {
        var luminance = Luminance(color);
        var tuning = Tuning.Current;
        var strength = luminance < tuning.AppearanceDarkThreshold ? 1 - luminance / tuning.AppearanceDarkThreshold :
            luminance > tuning.AppearanceLightThreshold ? (luminance - tuning.AppearanceLightThreshold) / (1 - tuning.AppearanceLightThreshold) : 0;
        return Mathf.Clamp(strength, 0, 1);
    }

    private static float Luminance(Color color)
    {
        var linear = color.SrgbToLinear();
        return linear.R * 0.2126f + linear.G * 0.7152f + linear.B * 0.0722f;
    }

    /// <summary>Dark and saturated text needs a light outline; the presets retain their dark outline.</summary>
    public static Color ScoreOutline(Color color) => Luminance(color) < Tuning.Current.AppearanceTagThreshold ? Colors.White : new Color(0.05f, 0.06f, 0.08f);

    /// <summary>Near-black bodies/glow need a light edge; near-white needs a dark one.</summary>
    public static Color Edge(Color color) => Luminance(color) < Tuning.Current.AppearanceDarkThreshold ? Colors.White : new Color(0.05f, 0.06f, 0.08f);
}
