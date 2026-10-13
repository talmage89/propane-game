using Godot;
using Propane.Core;

namespace Propane.Player;

/// <summary>
/// The authored rifle-butt strike: a horizontal butt stroke keyed to the gameplay phases, so changing
/// <see cref="Tuning.MeleeWindup"/>, <see cref="Tuning.MeleeActive"/> or <see cref="Tuning.MeleeRecovery"/> retimes it
/// without changing its shape. The torso coils right in the windup with the butt drawn back by the right hip, then
/// unwinds left: the right hand drives the stock forward at head height through the active phase while the left
/// hand pulls the barrel back past the left shoulder, and the body follows through before the recovery returns
/// the rifle to the hip. The rig poses the rifle from these values; hits use only the gameplay volume.
/// </summary>
public readonly record struct MeleeStrike(float Weight, float TorsoYaw, float TorsoLean, Vector3 Butt, float Heading, float Pitch, float Roll)
{
    // Each key: phase (0 windup, 1 active, 2 recovery), fraction of that phase, blend weight, torso yaw and lean
    // (fractions of the twist and lean tuners; + turns left, + leans in), butt from the right shoulder in the
    // twisted chest frame (right, up, forward; forward as a fraction of the reach tuner), and the muzzle's
    // heading (degrees, + to the left, 180 = straight back), pitch (+ up) and roll.
    private static readonly float[,] Keys =
    {
        { 0, 0.00f, 0.0f, -0.2f, 0.0f, 0.10f, -0.20f, 0.15f, 10, 0, -10 },
        { 0, 0.45f, 0.9f, -0.8f, -0.2f, 0.14f, -0.26f, -0.05f, 18, 18, -20 },
        { 0, 1.00f, 1.0f, -1.0f, -0.3f, 0.15f, -0.28f, -0.12f, 25, 28, -30 },
        { 1, 0.35f, 1.0f, 0.2f, 0.6f, 0.02f, -0.08f, 0.70f, 95, 12, -60 },
        { 1, 0.70f, 1.0f, 0.9f, 1.0f, -0.10f, 0.05f, 1.00f, 135, 5, -80 },
        { 1, 1.00f, 1.0f, 1.1f, 1.0f, -0.14f, 0.04f, 0.95f, 145, 0, -85 },
        { 2, 0.30f, 1.0f, 1.0f, 0.7f, -0.12f, -0.02f, 0.75f, 140, -5, -75 },
        { 2, 0.70f, 0.5f, 0.3f, 0.2f, 0.02f, -0.15f, 0.35f, 60, -5, -35 },
        { 2, 1.00f, 0.0f, -0.2f, 0.0f, 0.10f, -0.20f, 0.15f, 10, 0, -10 },
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
            new Vector3(Value(5), Value(6), Value(7) * t.MeleeRifleForward), Value(8), Value(9), Value(10));
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
