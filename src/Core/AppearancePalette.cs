using Godot;

namespace Propane.Core;

/// <summary>Appearance presets and legacy fallback, shared by settings and network profiles.</summary>
public static class AppearancePalette
{
    /// <summary>Quick presets and legacy Hello fallback; players may choose any opaque sRGB color.</summary>
    public static readonly Color[] PlayerColors =
    {
        new(0.92f, 0.44f, 0.16f), // orange
        new(0.2f, 0.52f, 0.95f), // blue
        new(0.3f, 0.78f, 0.32f), // green
        new(0.9f, 0.22f, 0.3f), // red
        new(0.62f, 0.36f, 0.92f), // purple
        new(0.98f, 0.8f, 0.15f), // yellow
        new(0.15f, 0.8f, 0.78f), // teal
        new(0.95f, 0.42f, 0.72f), // pink
    };

    public static readonly string[] ColorNames = { "Orange", "Blue", "Green", "Red", "Purple", "Yellow", "Teal", "Pink" };

    public static Color PlayerColor(int index) => PlayerColors[Mathf.PosMod(index, PlayerColors.Length)];
}
