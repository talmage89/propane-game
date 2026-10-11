using System;
using System.Linq;
using System.Text;
using Godot;
using Propane.Player;

namespace Propane.Dev;

/// <summary>Final-pose wrist residuals and bone-center penetration into calibrated solid rifle proxies.
/// The proxies omit hollow rail/trigger spaces; this is a sampled regression check, not a mesh collision proof.</summary>
public partial class GripProbe : SkeletonModifier3D
{
    public PlayerCharacter Player { get; init; } = null!;
    private readonly StringBuilder csv = new("time,aim,sprint,reload,right_m,left_m,right_deg,left_deg,penetration_m,eye_m\n");
    private readonly float[] maxima = new float[6];
    private int count;
    private double time;
    private bool failed;

    public override void _ProcessModificationWithDelta(double delta)
    {
        time += delta;
        var bones = GetSkeleton();
        if (bones == null) return;
        var rig = bones.GetNode<RifleRig>("RifleRig");
        var rifle = rig.Rifle!;
        if (rig.Weight < 0.999f || time < 0.1) return;
        var right = (bones.GlobalTransform * bones.GetBoneGlobalPose(bones.FindBone("hand_r"))).Orthonormalized();
        var left = (bones.GlobalTransform * bones.GetBoneGlobalPose(bones.FindBone("hand_l"))).Orthonormalized();
        var eye = RifleRig.EyePosition(bones);
        var values = new[]
        {
            right.Origin.DistanceTo(rig.RightTarget.Origin), left.Origin.DistanceTo(rig.LeftTarget.Origin),
            Angle(right.Basis, rig.RightTarget.Basis), Angle(left.Basis, rig.LeftTarget.Basis),
            Penetration(bones, rifle), rig.AimAmount > 0.99f ? eye.DistanceTo(rifle.GlobalTransform * rifle.EyeLocal.Origin) : 0,
        };
        for (var i = 0; i < maxima.Length; i++)
        {
            maxima[i] = Mathf.Max(maxima[i], values[i]);
            failed |= !float.IsFinite(values[i]);
        }
        count++;
        csv.AppendLine(FormattableString.Invariant($"{time:F6},{rig.AimAmount:F3},{rig.SprintAmount:F3},{rig.Reload:F3},{values[0]:F6},{values[1]:F6},{values[2]:F4},{values[3]:F4},{values[4]:F6},{values[5]:F6}"));
        if (DevArgs.Get("dump-bones") != null && count == 40)
        {
            foreach (var name in new[] { "Head", "upperarm_l", "upperarm_r", "hand_l", "hand_r", "index_01_l", "index_02_l", "index_03_l", "index_04_leaf_l" })
                GD.Print($"[bone] {name} world={bones.GlobalTransform * bones.GetBoneGlobalPose(bones.FindBone(name))} rifle={rifle.GlobalTransform.AffineInverse() * bones.GlobalTransform * bones.GetBoneGlobalPose(bones.FindBone(name))}");
        }
    }

    private static float Angle(Basis a, Basis b) => Mathf.RadToDeg(a.GetRotationQuaternion().AngleTo(b.Orthonormalized().GetRotationQuaternion()));

    private static float Penetration(Skeleton3D bones, Rifle rifle)
    {
        var toRifle = rifle.GlobalTransform.AffineInverse() * bones.GlobalTransform;
        var deepest = 0f;
        for (var i = 0; i < bones.GetBoneCount(); i++)
        {
            var name = bones.GetBoneName(i).ToString();
            if (!(name.StartsWith("hand_") || new[] { "index_", "middle_", "ring_", "pinky_", "thumb_" }.Any(name.StartsWith))) continue;
            var p = toRifle * bones.GetBoneGlobalPose(i).Origin;
            // Solid cores inside the handguard, receiver and vertical foregrip, measured from the authored asset.
            deepest = Mathf.Max(deepest, InsideBox(p, new Vector3(0, 0.083f, -0.30f), new Vector3(0.014f, 0.024f, 0.135f)));
            deepest = Mathf.Max(deepest, InsideBox(p, new Vector3(0, 0.083f, -0.09f), new Vector3(0.014f, 0.023f, 0.075f)));
            deepest = Mathf.Max(deepest, InsideBox(p, new Vector3(0, 0.02f, -0.3015f), new Vector3(0.011f, 0.032f, 0.012f)));
        }
        return deepest;
    }

    private static float InsideBox(Vector3 p, Vector3 center, Vector3 half)
    {
        var d = half - (p - center).Abs();
        return Mathf.Max(0, Mathf.Min(d.X, Mathf.Min(d.Y, d.Z)));
    }

    public void Finish()
    {
        var assert = DevArgs.Get("assert-grip") != null;
        failed |= count < 100 || maxima[0] > 0.003f || maxima[1] > 0.003f || maxima[2] > 0.1f || maxima[3] > 0.1f || maxima[4] > 0.002f;
        GD.Print(FormattableString.Invariant($"[grip] {(assert && failed ? "FAIL" : "MEASURED")} frames={count} right={maxima[0]:F6}m left={maxima[1]:F6}m rotation={maxima[2]:F4}/{maxima[3]:F4}deg penetration={maxima[4]:F6}m eye={maxima[5]:F6}m"));
        var directory = DevArgs.Get("capture-dir");
        if (directory != null)
        {
            DirAccess.MakeDirRecursiveAbsolute(directory);
            using var file = FileAccess.Open($"{directory}/grip.csv", FileAccess.ModeFlags.Write);
            file.StoreString(csv.ToString());
        }
        if (assert && failed) GetTree().Quit(1);
    }
}
