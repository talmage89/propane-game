using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace Propane.Core;

public enum PreferenceKind { Number, Toggle, Choice }
public enum PreferenceTarget { Device, Camera, Display, Interface }

/// <summary>One immutable declaration drives editing, validation, persistence and reset. Values are client-only.</summary>
public sealed record PreferenceDefinition(string Id, string Label, string Category, PreferenceKind Kind,
    double Default, double Min, double Max, double Step, PreferenceTarget Target, string Help,
    string Format = "0.00×", IReadOnlyList<string>? Options = null, IReadOnlyList<int>? OptionValues = null, string Section = "preferences")
{
    public double Normalize(double value)
    {
        if (!double.IsFinite(value)) return Default;
        value = Math.Clamp(value, Min, Max);
        return Kind == PreferenceKind.Number ? Math.Clamp(Min + Math.Round((value - Min) / Step) * Step, Min, Max)
            : value == Math.Truncate(value) ? value : Default;
    }

    public string Describe(double value) => Kind switch
    {
        PreferenceKind.Toggle => value == 0 ? "Off" : "On",
        PreferenceKind.Choice => Options![(int)value],
        _ => value.ToString(Format, CultureInfo.InvariantCulture),
    };
}

public static class PreferenceCatalog
{
    public static readonly PreferenceDefinition MouseSensitivity = new("mouse_sensitivity", "Mouse sensitivity", "Camera & controls", PreferenceKind.Number,
        1, 0.1, 4, 0.05, PreferenceTarget.Device, "Adjust how quickly the mouse turns the camera.");
    public static readonly PreferenceDefinition AimSensitivity = new("aim_sensitivity", "Aiming sensitivity", "Camera & controls", PreferenceKind.Number,
        1, 0.1, 2, 0.05, PreferenceTarget.Device, "Mouse and right-stick speed while holding Aim, relative to normal look.");
    public static readonly PreferenceDefinition InvertMouseY = new("invert_mouse_y", "Invert mouse Y", "Camera & controls", PreferenceKind.Toggle,
        0, 0, 1, 1, PreferenceTarget.Device, "Move the mouse up to look down.");
    public static readonly PreferenceDefinition GamepadSensitivity = new("gamepad_sensitivity", "Gamepad look sensitivity", "Camera & controls", PreferenceKind.Number,
        1, 0.1, 4, 0.05, PreferenceTarget.Device, "Multiplies right-stick speed after its response curve.");
    public static readonly PreferenceDefinition InvertGamepadY = new("invert_gamepad_y", "Invert gamepad Y", "Camera & controls", PreferenceKind.Toggle,
        0, 0, 1, 1, PreferenceTarget.Device, "Push the right stick up to look down.");
    public static readonly PreferenceDefinition FieldOfView = new("field_of_view", "Field of view", "Camera & controls", PreferenceKind.Number,
        72, 40, 110, 1, PreferenceTarget.Camera, "Vertical viewing angle. Wider shows more of your surroundings.", "0°");
    public static readonly PreferenceDefinition CameraShake = new("camera_shake", "Camera shake strength", "Camera & controls", PreferenceKind.Number,
        1, 0, 1, 0.05, PreferenceTarget.Camera, "Reduces camera shake from explosions. Recoil is unchanged.", "0%");
    public static readonly PreferenceDefinition WindowMode = new("window_mode", "Window mode", "Display", PreferenceKind.Choice,
        0, 0, 2, 1, PreferenceTarget.Display, "Fullscreen uses your desktop resolution. Exclusive support depends on the platform.", Options: Array.AsReadOnly(new[] { "Windowed", "Fullscreen", "Exclusive" }));
    public static readonly PreferenceDefinition Vsync = new("vsync", "VSync", "Display", PreferenceKind.Toggle,
        1, 0, 1, 1, PreferenceTarget.Display, "Synchronizes presentation with your display to reduce tearing.");
    public static readonly PreferenceDefinition FrameCap = new("frame_cap", "Frame-rate cap", "Display", PreferenceKind.Choice,
        0, 0, 7, 1, PreferenceTarget.Display, "VSync can impose a lower limit. Unlimited uses more power.", Options: Array.AsReadOnly(new[] { "Unlimited", "30 FPS", "60 FPS", "90 FPS", "120 FPS", "144 FPS", "165 FPS", "240 FPS" }), OptionValues: Array.AsReadOnly(new[] { 0, 30, 60, 90, 120, 144, 165, 240 }));
    public static readonly PreferenceDefinition RenderScale = new("render_scale", "3D render scale", "Display", PreferenceKind.Number,
        1, 0.5, 1, 0.05, PreferenceTarget.Display, "Lower the 3D resolution for performance. Menus stay sharp.", "0%");
    // Keep the existing storage location so old files and the match shortcut continue to work.
    public static readonly PreferenceDefinition ShowLeaderboard = new("leaderboard", "Show leaderboard", "Interface", PreferenceKind.Toggle,
        1, 0, 1, 1, PreferenceTarget.Interface, "Show the standings during a multiplayer match.", Section: "hud");

    // Metadata order follows declaration order. Discover once so adding a preference needs no second registry entry.
    public static IReadOnlyList<PreferenceDefinition> All { get; } = Array.AsReadOnly(typeof(PreferenceCatalog)
        .GetFields(BindingFlags.Public | BindingFlags.Static).Where(field => field.FieldType == typeof(PreferenceDefinition))
        .OrderBy(field => field.MetadataToken).Select(field => (PreferenceDefinition)field.GetValue(null)!).ToArray());
}
