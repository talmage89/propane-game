using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;
using Propane.Core;
using Propane.Player;

namespace Propane.Dev;

/// <summary>Final-pose wrist residuals and sampled hand-surface penetration into calibrated solid rifle proxies.
/// The proxies omit hollow rail/trigger spaces; this is a sampled regression check, not a mesh collision proof.</summary>
public partial class GripProbe : SkeletonModifier3D
{
    public PlayerCharacter Player { get; init; } = null!;
    public bool RequireCoverage { get; init; }
    private readonly HashSet<string> coverage = new();

    public static GripProbe Attach(PlayerCharacter player, bool requireCoverage = false)
    {
        var bones = player.FindChildren("*", "Skeleton3D", owned: false).OfType<Skeleton3D>().Single(s => s.FindBone("hand_l") >= 0);
        var probe = new GripProbe { Player = player, RequireCoverage = requireCoverage };
        bones.AddChild(probe);
        return probe;
    }
    private readonly StringBuilder csv = new("time,gait,pitch,ready,aim,sprint,reload,recoil,right_m,left_m,right_deg,left_deg,penetration_m,eye_m\n");
    private readonly float[] maxima = new float[6];
    private int count;
    private float settledEye;
    private float reachMargin = float.PositiveInfinity;
    private Vector3 previousBlends;
    private double previousSampleTime;
    private double time;
    private bool failed;
    private Vector3 worstPoint;
    private string worstBone = "";
    private double worstTime;
    private readonly List<(Vector3 Vertex, int[] Bones, float[] Weights)> skinSamples = new();
    private Transform3D[] inverseRest = Array.Empty<Transform3D>();

    public override void _ProcessModificationWithDelta(double delta)
    {
        try { Sample(delta); }
        catch (Exception error)
        {
            failed = true;
            GD.PushError($"[grip] FAIL sampling: {error}");
            ShutdownGuard.Quit(GetTree(), 1);
        }
    }

    private void Sample(double delta)
    {
        time += delta;
        var bones = GetSkeleton();
        if (bones == null) return;
        var rig = bones.GetNode<RifleRig>("RifleRig");
        var rifle = rig.Rifle!;
        if (rig.Weight <= 0.001f || time < 0.1) return;
        var right = (bones.GlobalTransform * bones.GetBoneGlobalPose(bones.FindBone("hand_r"))).Orthonormalized();
        var left = (bones.GlobalTransform * bones.GetBoneGlobalPose(bones.FindBone("hand_l"))).Orthonormalized();
        var eye = RifleRig.EyePosition(bones, Player.World.Tuning);
        var blends = new Vector3(rig.AimAmount, rig.ReadyAmount, rig.SprintAmount);
        if (count > 0)
        {
            var change = (blends - previousBlends).Abs();
            var limit = Player.World.Tuning.RifleStanceBlendSpeed * (float)(time - previousSampleTime) + 0.00001f;
            if (change.X > limit || change.Y > limit || change.Z > limit)
            {
                failed = true;
                GD.PushError($"[grip] FAIL stance blend discontinuity: change={change} limit={limit}");
            }
        }
        previousBlends = blends;
        previousSampleTime = time;
        if (rig.Weight >= 0.999f)
        {
            foreach (var side in new[] { "r", "l" })
            {
                var upper = bones.GetBoneGlobalPose(bones.FindBone($"upperarm_{side}")).Origin;
                var lower = bones.GetBoneGlobalPose(bones.FindBone($"lowerarm_{side}")).Origin;
                var wrist = bones.GetBoneGlobalPose(bones.FindBone($"hand_{side}")).Origin;
                reachMargin = Mathf.Min(reachMargin, upper.DistanceTo(lower) + lower.DistanceTo(wrist) - upper.DistanceTo(wrist));
            }
        }
        var meleeTime = Player.MeleeProgress * (Player.World.Tuning.MeleeWindup + Player.World.Tuning.MeleeActive + Player.World.Tuning.MeleeRecovery);
        if (Player.ResolvedState.Weapon == WeaponAction.Melee && meleeTime >= Player.World.Tuning.MeleeWindup && rig.ForegripResidual > 0.003f)
        {
            failed = true;
            GD.PushError($"[grip] FAIL support hand not back on foregrip during contact/recovery: {rig.ForegripResidual:F6} m");
        }
        var values = new[]
        {
            right.Origin.DistanceTo(rig.RightTarget.Origin), left.Origin.DistanceTo(rig.LeftTarget.Origin),
            Angle(right.Basis, rig.RightTarget.Basis), Angle(left.Basis, rig.LeftTarget.Basis),
            rig.Weight >= 0.999f ? Penetration(bones, rifle) : 0, rig.Weight >= 0.999f && rig.AimAmount > 0.99f ? eye.DistanceTo(rifle.GlobalTransform * rifle.EyeLocal.Origin) : 0,
        };
        for (var i = 0; i < maxima.Length; i++)
        {
            maxima[i] = Mathf.Max(maxima[i], values[i]);
            failed |= !float.IsFinite(values[i]);
        }
        if (rig.AimAmount > 0.99f) coverage.Add("aim");
        if (rig.Weight >= 0.999f && rig.AimAmount >= 0.999f && rig.Recoil < 0.01f && rig.Reload == 0)
        {
            settledEye = Mathf.Max(settledEye, values[5]);
            coverage.Add("settled-aim");
        }
        if (rig.ReadyAmount > 0.99f && rig.AimAmount < 0.01f && rig.Reload == 0) coverage.Add("ready");
        if (rig.AimAmount < 0.01f && rig.ReadyAmount < 0.01f && rig.Reload == 0) coverage.Add("carry");
        if (rig.SprintAmount > 0.99f) coverage.Add("sprint");
        if (rig.Recoil > 0.5f) coverage.Add("recoil");
        if (rig.Reload > 0.2f && rig.Reload < 0.8f) coverage.Add(rifle.ReloadFromEmpty ? "empty-reload" : "tactical-reload");
        if (Player.CameraRig?.Pitch <= -74.9f) coverage.Add("pitch-down");
        if (Player.CameraRig?.Pitch >= 44.9f) coverage.Add("pitch-up");
        coverage.Add(Player.ResolvedState.Gait.ToString());
        var pitchLimit = Player.CameraRig?.Pitch <= -74.9f ? "down" : Player.CameraRig?.Pitch >= 44.9f ? "up" : null;
        if (pitchLimit != null && rig.Weight >= 0.999f)
        {
            if (rig.Reload > 0.2f && rig.Reload < 0.8f)
            {
                coverage.Add($"reload-{pitchLimit}");
            }
            else if (rig.AimAmount >= 0.999f)
            {
                coverage.Add($"aim-{pitchLimit}");
            }
            else if (rig.SprintAmount >= 0.999f)
            {
                coverage.Add($"sprint-{pitchLimit}");
            }
            else if (rig.ReadyAmount >= 0.999f)
            {
                coverage.Add($"ready-{pitchLimit}");
            }
            else if (rig.ReadyAmount < 0.01f)
            {
                coverage.Add($"carry-{pitchLimit}");
                if (new Vector2(Player.Velocity.X, Player.Velocity.Z).Length() > 0.1f) coverage.Add($"run-{pitchLimit}");
            }
        }
        count++;
        csv.AppendLine(FormattableString.Invariant($"{time:F6},{Player.ResolvedState.Gait},{Player.CameraRig?.Pitch ?? 0:F3},{rig.ReadyAmount:F3},{rig.AimAmount:F3},{rig.SprintAmount:F3},{rig.Reload:F3},{rig.Recoil:F3},{values[0]:F6},{values[1]:F6},{values[2]:F4},{values[3]:F4},{values[4]:F6},{values[5]:F6}"));

    }

    private static float Angle(Basis a, Basis b)
    {
        // atan2 keeps sub-degree errors measurable; acos(dot) quantizes float roundoff to ~0.08 degrees.
        var difference = a.Orthonormalized().GetRotationQuaternion().Inverse() * b.Orthonormalized().GetRotationQuaternion();
        return Mathf.RadToDeg(2 * Mathf.Atan2(new Vector3(difference.X, difference.Y, difference.Z).Length(), Mathf.Abs(difference.W)));
    }

    private float Penetration(Skeleton3D bones, Rifle rifle)
    {
        if (inverseRest.Length == 0) CacheSkin(bones);
        var toRifle = rifle.GlobalTransform.AffineInverse() * bones.GlobalTransform;
        var transforms = Enumerable.Range(0, bones.GetBoneCount())
            .Select(i => toRifle * bones.GetBoneGlobalPose(i) * inverseRest[i]).ToArray();
        var deepest = 0f;
        var deepestPoint = Vector3.Zero;
        var deepestBone = "";
        foreach (var sample in skinSamples)
        {
            var p = Vector3.Zero;
            for (var j = 0; j < sample.Bones.Length; j++)
                p += transforms[sample.Bones[j]] * sample.Vertex * sample.Weights[j];
            var depth = SolidDepth(p, rifle);
            if (depth > deepest)
            {
                deepest = depth;
                deepestPoint = p;
                deepestBone = bones.GetBoneName(sample.Bones[Array.IndexOf(sample.Weights, sample.Weights.Max())]);
            }
        }
        if (deepest > maxima[4]) { worstPoint = deepestPoint; worstBone = deepestBone; worstTime = time; }
        return deepest;
    }

    private void CacheSkin(Skeleton3D bones)
    {
        inverseRest = Enumerable.Range(0, bones.GetBoneCount()).Select(i => bones.GetBoneGlobalRest(i).AffineInverse()).ToArray();
        foreach (var mesh in GameAssets.MeshInstances(Player))
        {
            if (mesh.Skin == null || mesh.GetNodeOrNull<Skeleton3D>(mesh.Skeleton) != bones) continue;
            var skin = mesh.Skin;
            var boneMap = Enumerable.Range(0, skin.GetBindCount()).Select(i =>
                skin.GetBindName(i).ToString().Length > 0 ? bones.FindBone(skin.GetBindName(i)) : skin.GetBindBone(i)).ToArray();
            var inSkeleton = bones.GlobalTransform.AffineInverse() * mesh.GlobalTransform;
            for (var surface = 0; surface < mesh.Mesh.GetSurfaceCount(); surface++)
            {
                var arrays = mesh.Mesh.SurfaceGetArrays(surface);
                var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                var ids = arrays[(int)Mesh.ArrayType.Bones].AsInt32Array();
                var weights = arrays[(int)Mesh.ArrayType.Weights].AsFloat32Array();
                var perVertex = ids.Length / vertices.Length;
                // Sample every fourth vertex of the actual skinned palms and fingers, including their surface.
                for (var v = 0; v < vertices.Length; v += 4)
                {
                    var indices = Enumerable.Range(0, perVertex).Select(j => boneMap[ids[v * perVertex + j]]).ToArray();
                    var w = weights.Skip(v * perVertex).Take(perVertex).ToArray();
                    var handWeight = indices.Select((b, j) => IsHand(bones.GetBoneName(b).ToString()) ? w[j] : 0).Sum();
                    if (handWeight > 0.5f) skinSamples.Add((inSkeleton * vertices[v], indices, w));
                }
            }
        }
        if (skinSamples.Count == 0) { GD.PushError("[grip] FAIL no skinned hand samples"); failed = true; }
        GD.Print($"[grip] sampled {skinSamples.Count} hand surface vertices");
    }

    private static bool IsHand(string name) => name.StartsWith("hand_") ||
        new[] { "index_", "middle_", "ring_", "pinky_", "thumb_" }.Any(name.StartsWith);

    private static float SolidDepth(Vector3 p, Rifle rifle)
    {
        // Conservative solid cores measured from the authored geometry, avoiding hollow rail/trigger space.
        var depth = InsideBox(p, new Vector3(0, 0.083f, -0.30f), new Vector3(0.014f, 0.024f, 0.135f));
        depth = Mathf.Max(depth, InsideBox(p, new Vector3(0, 0.083f, -0.09f), new Vector3(0.014f, 0.023f, 0.075f)));
        depth = Mathf.Max(depth, InsideBox(p, new Vector3(0, 0.02f, -0.3015f), new Vector3(0.011f, 0.032f, 0.012f)));
        depth = Mathf.Max(depth, PistolDepth(p));
        if (rifle.MagazineVisible)
            depth = Mathf.Max(depth, InsideBox(rifle.MagazinePose.AffineInverse() * p, new Vector3(0, -0.08f, 0), new Vector3(0.01f, 0.065f, 0.023f)));
        return depth;
    }

    // Cross-sections of the traced pistol-grip outline, inset 4 mm to form a conservative solid core.
    // X = rifle height, Y = fore/aft center, Z = depth half-width, W = lateral half-width (meters).
    private static readonly Vector4[] PistolSections =
    {
        new(-0.055700f, 0.012550f, 0.024550f, 0.010600f),
        new(-0.045000f, 0.011895f, 0.023795f, 0.010818f),
        new(-0.035000f, 0.007700f, 0.021554f, 0.010972f),
        new(-0.025000f, 0.001089f, 0.021836f, 0.011055f),
        new(-0.015000f, -0.009481f, 0.025519f, 0.011057f),
        new(-0.005000f, -0.010145f, 0.019033f, 0.010924f),
        new(0.005000f, -0.015641f, 0.017303f, 0.010517f),
        new(0.015000f, -0.020748f, 0.015155f, 0.009987f),
        new(0.025000f, -0.024890f, 0.012190f, 0.009320f),
        new(0.035000f, -0.027686f, 0.009644f, 0.008653f),
        new(0.041800f, -0.028500f, 0.009000f, 0.008200f),
    };

    private static float PistolDepth(Vector3 p)
    {
        if (p.Y <= PistolSections[0].X || p.Y >= PistolSections[^1].X) return 0;
        for (var i = 1; i < PistolSections.Length; i++)
        {
            if (p.Y > PistolSections[i].X) continue;
            var a = PistolSections[i - 1];
            var b = PistolSections[i];
            var section = a.Lerp(b, (p.Y - a.X) / (b.X - a.X));
            var radius = Mathf.Sqrt(Mathf.Pow(p.X / section.W, 2) + Mathf.Pow((p.Z - section.Y) / section.Z, 2));
            return Mathf.Max(0, Mathf.Min(section.W, section.Z) * (1 - radius));
        }
        return 0;
    }

    private static float InsideBox(Vector3 p, Vector3 center, Vector3 half)
    {
        var d = half - (p - center).Abs();
        return Mathf.Max(0, Mathf.Min(d.X, Mathf.Min(d.Y, d.Z)));
    }

    public void Finish()
    {
        var assert = DevArgs.Get("assert-grip") != null;
        failed |= count < 100 || maxima[0] > 0.003f || maxima[1] > 0.003f || maxima[2] > 0.1f || maxima[3] > 0.1f || maxima[4] > 0.0005f || maxima[5] > 0.035f || settledEye > 0.003f;
        if (RequireCoverage)
        {
            foreach (var required in new[] { "carry", "ready", "aim", "settled-aim", "sprint", "recoil", "empty-reload", "tactical-reload", "pitch-down", "pitch-up", "Default", "Sprint", "Creep" })
            {
                if (coverage.Contains(required)) continue;
                failed = true;
                GD.PushError($"[grip] FAIL missing coverage: {required}");
            }
        }

        if (RequireCoverage && DevArgs.Get("grip-sweep") != null)
        {
            foreach (var hold in new[] { "carry", "ready", "aim", "run", "sprint", "reload" })
            {
                foreach (var pitch in new[] { "down", "up" })
                {
                    if (coverage.Contains($"{hold}-{pitch}")) continue;
                    failed = true;
                    GD.PushError($"[grip] FAIL missing pitch coverage: {hold}-{pitch}");
                }
            }
        }
        failed |= !float.IsFinite(reachMargin) || reachMargin < Player.World.Tuning.RifleReachMargin - 0.0005f;
        GD.Print(FormattableString.Invariant($"[grip] minimum reach margin={reachMargin:F6}m"));
        GD.Print(FormattableString.Invariant($"[grip] {(assert && failed ? "FAIL" : assert ? "PASS" : "MEASURED")} frames={count} right={maxima[0]:F6}m left={maxima[1]:F6}m rotation={maxima[2]:F4}/{maxima[3]:F4}deg penetration={maxima[4]:F6}m eye={maxima[5]:F6}m settled-eye={settledEye:F6}m"));
        GD.Print($"[grip] worst penetration time={worstTime:F6} bone={worstBone} point={worstPoint}");
        var directory = DevArgs.Get("capture-dir");
        if (directory != null)
        {
            DirAccess.MakeDirRecursiveAbsolute(directory);
            using var file = FileAccess.Open($"{directory}/grip.csv", FileAccess.ModeFlags.Write);
            file.StoreString(csv.ToString());
        }
        Active = false;
        if (assert && failed) ShutdownGuard.Quit(GetTree(), 1);
    }
}
