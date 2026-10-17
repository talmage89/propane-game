using System;
using System.Globalization;
using Godot;
using Propane.Net;
using Propane.Player;
using Propane.Tank;

namespace Propane.Dev.NetScenarios;

/// <summary>Stateless geometry and observation helpers shared by the host and scenario owners.</summary>
internal static class NetScenarioTools
{
    internal static string Vector(Vector3 v) => FormattableString.Invariant($"{v.X:R},{v.Y:R},{v.Z:R}");

    // No instance override exists until the new tank's first process update. Its material's
    // shield is transparent; converting Nil directly to Color instead fabricates opaque black.
    internal static Color ReadShieldColor(Variant value) => value.VariantType == Variant.Type.Nil ? Colors.Transparent : value.AsColor();

    internal static bool IsDrop(PropaneTank tank) => tank.NetId is >= BodySync.DropTankBase and < BodySync.CarBase;

    internal static void AimAt(PlayerCharacter player, Vector3 point)
    {
        var view = point - player.Aim.EyeOrigin;
        player.Aim.SetOrientation(Mathf.RadToDeg(Mathf.Atan2(-view.X, -view.Z)), Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(view.Normalized().Y, -1, 1))));
    }

    // Stopwatch is shared by the local processes; the packet-corrected countdown cannot align their samples.
    internal static double TankCheckTime() => (double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency;
    internal static AppearanceRecord ParseAppearance(string hex)
    {
        if (hex.Length != 6 || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            throw new ArgumentException("appearance must be six sRGB hex digits");
        }
        return new AppearanceRecord((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

}
