using Godot;
using Propane.Core;

namespace Propane.Player;

/// <summary>
/// The authored rifle-butt strike: a horizontal butt stroke keyed to the gameplay phases, so changing
/// <see cref="Tuning.MeleeWindup"/>, <see cref="Tuning.MeleeActive"/> or <see cref="Tuning.MeleeRecovery"/> retimes it
/// without changing its shape. The windup coils the torso right and draws the stock back by the right hip, muzzle up
/// and forward across the body. The strike unwinds left and leans in: the rifle pivots near the support hand, which
/// pulls the barrel back past the left shoulder while the firing hand drives the stock through a wide arc to chin
/// height in front. It follows through, then the recovery returns the rifle to the hip. The rig poses the rifle
/// from these values; hits use only the gameplay volume.
/// </summary>
public readonly record struct MeleeStrike(float Weight, float TorsoYaw, float TorsoLean, Vector3 Foregrip, float Heading, float Pitch, float Roll)
{
    // Each key: phase (0 windup, 1 active, 2 recovery), fraction of that phase, blend weight, torso yaw and lean
    // (fractions of the twist and lean tuners; + turns left, + leans in), the foregrip from the right shoulder in the
    // twisted chest frame (right, up, forward; meters), and the muzzle's heading (degrees, + to the left,
    // 180 = straight back), pitch (+ up) and roll.
    private static readonly float[,] Keys =
    {
        { 0, 0.00f, 0.0f, -0.2f, 0.0f, -0.10f, -0.12f, 0.55f, 5, 0, -10 },
        { 0, 0.40f, 0.9f, -0.7f, -0.2f, -0.22f, -0.08f, 0.45f, 20, 15, -20 },
        { 0, 1.00f, 1.0f, -1.0f, -0.3f, -0.30f, -0.05f, 0.35f, 30, 25, -30 },
        { 1, 0.40f, 1.0f, 0.1f, 0.5f, -0.38f, -0.01f, 0.24f, 100, 12, -60 },
        { 1, 0.75f, 1.0f, 0.9f, 1.0f, -0.42f, 0.02f, 0.16f, 145, 6, -80 },
        { 1, 1.00f, 1.0f, 1.1f, 1.0f, -0.42f, 0.01f, 0.12f, 155, 2, -85 },
        { 2, 0.30f, 1.0f, 1.0f, 0.7f, -0.40f, -0.02f, 0.14f, 150, 0, -75 },
        { 2, 0.70f, 0.5f, 0.3f, 0.2f, -0.22f, -0.08f, 0.40f, 60, 0, -35 },
        { 2, 1.00f, 0.0f, -0.2f, 0.0f, -0.10f, -0.12f, 0.55f, 5, 0, -10 },
    };

    /// <summary>The strike pose at <paramref name="progress"/> (0..1 over windup, active and recovery).</summary>
    public static MeleeStrike Sample(Tuning t, float progress)
    {
        var total = t.MeleeWindup + t.MeleeActive + t.MeleeRecovery;
        var time = Mathf.Clamp(progress, 0, 1) * total;
        var count = Keys.GetLength(0);
        var k = 0;
        while (k < count - 2 && KeyTime(t, k + 1) <= time) k++;
        var a = KeyTime(t, k);
        var b = KeyTime(t, k + 1);
        var u = b > a ? Mathf.Clamp((time - a) / (b - a), 0, 1) : 1;
        float Value(int column)
        {
            // Catmull-Rom through the keys: the strike accelerates through its middle keys instead of stopping.
            var p0 = Keys[Mathf.Max(k - 1, 0), column];
            var p1 = Keys[k, column];
            var p2 = Keys[k + 1, column];
            var p3 = Keys[Mathf.Min(k + 2, count - 1), column];
            return 0.5f * (2 * p1 + (-p0 + p2) * u + (2 * p0 - 5 * p1 + 4 * p2 - p3) * u * u + (-p0 + 3 * p1 - 3 * p2 + p3) * u * u * u);
        }
        return new MeleeStrike(Mathf.Clamp(Value(2), 0, 1), Value(3) * t.MeleeTorsoTwistDegrees, Value(4) * t.MeleeTorsoLeanDegrees,
            new Vector3(Value(5), Value(6), Value(7)), Value(8), Value(9), Value(10));
    }

    private static float KeyTime(Tuning t, int key)
    {
        var fraction = Keys[key, 1];
        return (int)Keys[key, 0] switch
        {
            0 => t.MeleeWindup * fraction,
            1 => t.MeleeWindup + t.MeleeActive * fraction,
            _ => t.MeleeWindup + t.MeleeActive + t.MeleeRecovery * fraction,
        };
    }
}
