using Godot;
using Propane.Core;

namespace Propane.Player;

/// <summary>
/// Procedural upper body for holding the rifle, applied on top of the animation:
/// 1. twists and bends the spine (and turns the head) toward the aim point;
/// 2. places the rifle for the current stance: tucked at the hip or shouldered (both pointing at the aim point), or
///    carried muzzle-down while sprinting, plus recoil;
/// 3. solves two-bone IK so both hands hold the rifle's grips.
/// The camera never moves between stances; only the body and rifle do.
/// </summary>
public partial class RifleRig : SkeletonModifier3D
{
    // Spine twist weights for spine_01..03; they sum to 1.
    private static readonly float[] SpineWeights = { 0.25f, 0.35f, 0.4f };

    private const float HipPitchShare = 0.3f;
    private const float AimPitchShare = 0.7f;
    private const float RecoilBack = 0.045f;
    private const float HipBladeDegrees = 28f;
    private const float AimBladeDegrees = 38f;
    private const float RecoilLiftDegrees = 5f;

    private int[] spine = null!;
    private int head;
    private int upperR, lowerR, handR;
    private int upperL, lowerL, handL;
    private bool resolved;

    /// <summary>The rifle visual this rig places.</summary>
    public Rifle? Rifle { get; set; }

    /// <summary>World-space point the rifle and torso aim at.</summary>
    public Vector3 AimTarget { get; set; }

    /// <summary>0 = hip fire stance, 1 = shouldered.</summary>
    public float AimAmount { get; set; }

    /// <summary>0 = relaxed carry, 1 = pointing at the aim point (just fired or about to).</summary>
    public float ReadyAmount { get; set; }

    /// <summary>0..1 blend toward the muzzle-down sprint carry.</summary>
    public float SprintAmount { get; set; }

    /// <summary>0..1, decays after each shot.</summary>
    public float Recoil { get; set; }

    /// <summary>Reload progress 0..1: the rifle cants in and the left hand drops to the magazine. 0 when not reloading.</summary>
    public float Reload { get; set; }

    /// <summary>Weight of the whole rig; 0 while ragdolling or getting up.</summary>
    public float Weight { get; set; } = 1f;

    /// <summary>The right hand's wrist transform in rifle space, for carrying the rifle while ragdolling.</summary>
    public static readonly Transform3D RightHandInRifle = RightHandTransform(Transform3D.Identity);

    public override void _ProcessModificationWithDelta(double delta)
    {
        var skeleton = GetSkeleton();
        if (skeleton == null || Rifle == null)
        {
            return;
        }
        if (!resolved)
        {
            Resolve(skeleton);
        }

        var toSkeleton = skeleton.GlobalTransform.AffineInverse();
        var fromSkeleton = skeleton.GlobalTransform;

        if (Weight <= 0.001f)
        {
            // Ragdolling or getting up: RifleHandFollow keeps the rifle in the right hand.
            return;
        }

        // The model faces +Z in skeleton space.
        var forward = (fromSkeleton.Basis * Vector3.Back).Normalized();
        forward.Y = 0;
        forward = forward.Normalized();
        var right = forward.Cross(Vector3.Up).Normalized();

        var chestWorld = fromSkeleton * skeleton.GetBoneGlobalPose(spine[2]).Origin;
        var aimDir = (AimTarget - chestWorld).Normalized();

        // 1. Torso: yaw toward the aim point, pitch partly with it.
        var flatAim = new Vector3(aimDir.X, 0, aimDir.Z);
        var yaw = flatAim.LengthSquared() > 0.0001f ? forward.SignedAngleTo(flatAim.Normalized(), Vector3.Up) : 0f;
        // Bladed stance: the chest turns right of the aim line so the left shoulder comes forward and the left
        // hand can reach the foregrip, as a real shooter stands.
        var blade = Mathf.DegToRad(Mathf.Lerp(HipBladeDegrees, AimBladeDegrees, AimAmount)) * (1f - SprintAmount);
        yaw -= blade;
        var pitch = Mathf.Asin(Mathf.Clamp(aimDir.Y, -1, 1));
        var pitchShare = Mathf.Lerp(HipPitchShare, AimPitchShare, AimAmount) * (1f - SprintAmount * 0.7f);
        for (var k = 0; k < spine.Length; k++)
        {
            var w = SpineWeights[k] * Weight;
            var yawRot = new Quaternion(Vector3.Up, yaw * w);
            var axis = yawRot * right;
            var pitchRot = new Quaternion(axis.Normalized(), -pitch * pitchShare * w);
            RotateBoneWorld(skeleton, spine[k], pitchRot * yawRot, fromSkeleton, toSkeleton);
        }

        // 2. Rifle placement.
        // A stable torso frame: the body's yaw plus the spine twist just applied, and world up. The animated
        // chest bone swings too much (sprint twists it ±45°) to hang the rifle from directly; the shoulder
        // position still carries the animation's bob.
        var shoulder = fromSkeleton * skeleton.GetBoneGlobalPose(upperR).Origin;
        var chestForward = forward.Rotated(Vector3.Up, yaw * Weight).Normalized();
        var chestRight = chestForward.Cross(Vector3.Up).Normalized();
        var chestUp = Vector3.Up;
        var rifleDir = (AimTarget - shoulder).Normalized();
        var aimBasis = Basis.LookingAt(rifleDir, Vector3.Up);

        // Shouldered: butt in the shoulder pocket, sight near the eye line.
        var shoulderPocket = shoulder - chestRight * 0.035f + chestForward * 0.03f - chestUp * 0.015f;
        var aimed = new Transform3D(aimBasis, shoulderPocket - aimBasis * Rifle.ButtLocal.Origin);

        // Hip: stock tucked under the arm against the ribs, muzzle on the target.
        var hipDir = (AimTarget - (shoulder - chestUp * 0.2f)).Normalized();
        var hipBasis = Basis.LookingAt(hipDir, Vector3.Up).Rotated(hipDir, Mathf.DegToRad(-8f));
        var hipPocket = shoulder + chestRight * 0.01f - chestUp * 0.17f + chestForward * 0.02f;
        var hip = new Transform3D(hipBasis, hipPocket - hipBasis * Rifle.ButtLocal.Origin);
        // Relaxed carry between shots: muzzle dipped and angled across the body, so the rifle reads clearly
        // from the over-the-shoulder camera instead of pointing straight down the view.
        var relaxedDir = (chestForward * 0.86f - Vector3.Up * 0.36f - chestRight * 0.24f).Normalized();
        var relaxedBasis = Basis.LookingAt(relaxedDir, Vector3.Up).Rotated(relaxedDir, Mathf.DegToRad(-14f));
        var relaxed = new Transform3D(relaxedBasis, hipPocket - relaxedBasis * Rifle.ButtLocal.Origin);
        hip = relaxed.InterpolateWith(hip, Mathf.SmoothStep(0, 1, Mathf.Max(ReadyAmount, AimAmount)));

        // Sprint: low ready, muzzle down and slightly inboard, stock still tucked.
        var lowDir = (chestForward * 0.8f - Vector3.Up * 0.62f - chestRight * 0.18f).Normalized();
        var lowBasis = Basis.LookingAt(lowDir, Vector3.Up);
        var lowPocket = shoulder - chestUp * 0.12f + chestForward * 0.03f;
        var port = new Transform3D(lowBasis, lowPocket - lowBasis * Rifle.ButtLocal.Origin);

        var stance = hip.InterpolateWith(aimed, Mathf.SmoothStep(0, 1, AimAmount));
        stance = stance.InterpolateWith(port, Mathf.SmoothStep(0, 1, SprintAmount));

        // Reload: the rifle comes in front of the chest, canted over, while the left hand swaps the magazine.
        var reloadWeight = Reload > 0 ? Mathf.SmoothStep(0, 0.18f, Reload) * (1f - Mathf.SmoothStep(0.82f, 1f, Reload)) : 0f;
        if (reloadWeight > 0)
        {
            var reloadDir = (chestForward * 0.9f - Vector3.Up * 0.2f - chestRight * 0.22f).Normalized();
            var reloadBasis = Basis.LookingAt(reloadDir, Vector3.Up).Rotated(reloadDir, Mathf.DegToRad(-38f));
            var reloadPocket = shoulder - chestUp * 0.2f + chestForward * 0.12f - chestRight * 0.06f;
            var reloadPose = new Transform3D(reloadBasis, reloadPocket - reloadBasis * Rifle.ButtLocal.Origin);
            stance = stance.InterpolateWith(reloadPose, reloadWeight);
        }

        // Recoil: straight back, and the muzzle climbs.
        var kick = Recoil * Recoil;
        var lift = new Basis(stance.Basis.Column0.Normalized(), Mathf.DegToRad(RecoilLiftDegrees) * kick);
        stance = new Transform3D(lift * stance.Basis, stance.Origin + stance.Basis.Column2.Normalized() * RecoilBack * kick);
        stance = stance.Orthonormalized();
        // While the rig fades in (end of a get-up), hand the rifle over from wherever the hand already is.
        var handNow = (fromSkeleton * skeleton.GetBoneGlobalPose(handR)).Orthonormalized();
        var leftNow = (fromSkeleton * skeleton.GetBoneGlobalPose(handL)).Orthonormalized();
        if (Weight < 0.999f)
        {
            stance = (handNow * RightHandInRifle.AffineInverse()).InterpolateWith(stance, Weight);
        }
        Rifle.GlobalTransform = stance;

        // 3. Hands onto the grips.
        var rightHand = handNow.InterpolateWith(RightHandTransform(stance), Weight);
        // Mid-reload the left hand leaves the foregrip for the magazine well, just ahead of the pistol grip.
        var toMagazine = Reload > 0 ? Mathf.SmoothStep(0.2f, 0.42f, Reload) * (1f - Mathf.SmoothStep(0.6f, 0.82f, Reload)) : 0f;
        var grip = Rifle.ForegripLocal;
        if (toMagazine > 0)
        {
            grip = new Transform3D(grip.Basis, grip.Origin.Lerp(new Vector3(0, -0.16f, -0.12f), toMagazine));
        }
        var leftHand = leftNow.InterpolateWith(LeftHandTransform(stance, grip), Weight);

        var upperRWorld = fromSkeleton * skeleton.GetBoneGlobalPose(upperR).Origin;
        var upperLWorld = fromSkeleton * skeleton.GetBoneGlobalPose(upperL).Origin;
        var poleR = upperRWorld + chestRight * 0.35f - chestUp * 0.6f - chestForward * 0.15f;
        var poleL = upperLWorld - chestRight * 0.45f - chestUp * 0.6f + chestForward * 0.05f;
        SolveTwoBone(skeleton, upperR, lowerR, handR, toSkeleton * rightHand, toSkeleton * poleR);
        SolveTwoBone(skeleton, upperL, lowerL, handL, toSkeleton * leftHand, toSkeleton * poleL);
        if (DevLog.Enabled && Engine.GetProcessFrames() % 20 == 0)
        {
            var errR = (skeleton.GetBoneGlobalPose(handR).Origin - (toSkeleton * rightHand).Origin).Length();
            var errL = (skeleton.GetBoneGlobalPose(handL).Origin - (toSkeleton * leftHand).Origin).Length();
            var reachL = (toSkeleton * leftHand).Origin.DistanceTo(skeleton.GetBoneGlobalPose(upperL).Origin);
            GD.Print($"[rig] frame {Engine.GetProcessFrames()} errR {errR:0.000} errL {errL:0.000} reachL {reachL:0.000} sprint {SprintAmount:0.00} aim {AimAmount:0.00} rifleFwd {-stance.Basis.Column2.Normalized()} rifle {Rifle.GlobalPosition} visible {Rifle.IsVisibleInTree()} target {AimTarget} skel {skeleton.GlobalPosition} scale {Rifle.GlobalBasis.Scale}");
        }

        // Head follows the aim point, more so when shouldered.
        var headWorld = fromSkeleton * skeleton.GetBoneGlobalPose(head).Origin;
        var headBasis = (fromSkeleton.Basis * skeleton.GetBoneGlobalPose(head).Basis).Orthonormalized();
        var headForward = (headBasis * Vector3.Back).Normalized();
        var lookDir = (AimTarget - headWorld).Normalized();
        var look = new Quaternion(headForward, lookDir);
        RotateBoneWorld(skeleton, head, Quaternion.Identity.Slerp(look, Mathf.Lerp(0.45f, 0.8f, AimAmount) * Weight), fromSkeleton, toSkeleton);
    }

    /// <summary>Wrist transform for the right hand on the pistol grip, in world space.</summary>
    private static Transform3D RightHandTransform(Transform3D rifle)
    {
        var rx = rifle.Basis.Column0.Normalized();
        var ry = rifle.Basis.Column1.Normalized();
        var rz = rifle.Basis.Column2.Normalized();
        // Mannequin right hand: +Y runs to the fingers, +X is the back of the hand, +Z the thumb side.
        var fingers = (-rz * 0.55f - ry * 0.75f - rx * 0.25f).Normalized();
        var back = (rx - fingers * rx.Dot(fingers)).Normalized();
        var thumb = back.Cross(fingers);
        var basis = new Basis(back, fingers, thumb);
        // The grip sits in the palm, a little past the wrist toward the fingers.
        var wrist = rifle.Origin - fingers * 0.085f + back * 0.035f;
        return new Transform3D(basis, wrist);
    }

    /// <summary>Wrist transform for the left hand on the vertical foregrip, in world space.</summary>
    private static Transform3D LeftHandTransform(Transform3D rifle, Transform3D foregrip)
    {
        var rx = rifle.Basis.Column0.Normalized();
        var ry = rifle.Basis.Column1.Normalized();
        var rz = rifle.Basis.Column2.Normalized();
        // Mannequin left hand: +Y to the fingers, +X is the palm normal, +Z the thumb side.
        var fingers = (-rz * 0.8f + rx * 0.45f - ry * 0.15f).Normalized();
        var palm = (rx - fingers * rx.Dot(fingers)).Normalized();
        var thumb = palm.Cross(fingers);
        var basis = new Basis(palm, fingers, thumb);
        var grip = rifle * foregrip.Origin;
        var wrist = grip - fingers * 0.06f - palm * 0.03f + ry * 0.02f;
        return new Transform3D(basis, wrist);
    }

    private void Resolve(Skeleton3D skeleton)
    {
        spine = new[] { skeleton.FindBone("spine_01"), skeleton.FindBone("spine_02"), skeleton.FindBone("spine_03") };
        head = skeleton.FindBone("Head");
        upperR = skeleton.FindBone("upperarm_r");
        lowerR = skeleton.FindBone("lowerarm_r");
        handR = skeleton.FindBone("hand_r");
        upperL = skeleton.FindBone("upperarm_l");
        lowerL = skeleton.FindBone("lowerarm_l");
        handL = skeleton.FindBone("hand_l");
        resolved = true;
    }

    /// <summary>Rotates a bone about its own origin by a world-space rotation.</summary>
    private static void RotateBoneWorld(Skeleton3D skeleton, int bone, Quaternion worldRotation, Transform3D fromSkeleton, Transform3D toSkeleton)
    {
        var skeletonRotation = new Basis(toSkeleton.Basis.Orthonormalized().GetRotationQuaternion() * worldRotation
                                         * fromSkeleton.Basis.Orthonormalized().GetRotationQuaternion());
        RotateBone(skeleton, bone, skeletonRotation);
    }

    /// <summary>Rotates a bone about its own origin by a skeleton-space rotation.</summary>
    private static void RotateBone(Skeleton3D skeleton, int bone, Basis skeletonRotation)
    {
        var global = skeleton.GetBoneGlobalPose(bone);
        var newBasis = skeletonRotation * global.Basis.Orthonormalized();
        SetBoneGlobalBasis(skeleton, bone, newBasis);
    }

    private static void SetBoneGlobalBasis(Skeleton3D skeleton, int bone, Basis globalBasis)
    {
        var parent = skeleton.GetBoneParent(bone);
        var parentBasis = parent >= 0 ? skeleton.GetBoneGlobalPose(parent).Basis.Orthonormalized() : Basis.Identity;
        var local = (parentBasis.Inverse() * globalBasis).Orthonormalized();
        skeleton.SetBonePoseRotation(bone, local.GetRotationQuaternion());
    }

    /// <summary>Analytic two-bone IK in skeleton space, with a pole point for the elbow.</summary>
    private static void SolveTwoBone(Skeleton3D skeleton, int upper, int lower, int end, Transform3D target, Vector3 pole)
    {
        var a = skeleton.GetBoneGlobalPose(upper).Origin;
        var b = skeleton.GetBoneGlobalPose(lower).Origin;
        var c = skeleton.GetBoneGlobalPose(end).Origin;
        var l1 = (b - a).Length();
        var l2 = (c - b).Length();
        var toTarget = target.Origin - a;
        var distance = Mathf.Clamp(toTarget.Length(), 0.02f, (l1 + l2) * 0.999f);
        var dir = toTarget.Normalized();

        var along = (l1 * l1 - l2 * l2 + distance * distance) / (2f * distance);
        var height = Mathf.Sqrt(Mathf.Max(0, l1 * l1 - along * along));
        var poleDir = pole - a;
        poleDir = (poleDir - dir * dir.Dot(poleDir)).Normalized();
        var elbow = a + dir * along + poleDir * height;

        RotateBone(skeleton, upper, new Basis(new Quaternion((b - a).Normalized(), (elbow - a).Normalized())));
        b = skeleton.GetBoneGlobalPose(lower).Origin;
        c = skeleton.GetBoneGlobalPose(end).Origin;
        var reach = a + dir * distance;
        RotateBone(skeleton, lower, new Basis(new Quaternion((c - b).Normalized(), (reach - b).Normalized())));
        SetBoneGlobalBasis(skeleton, end, target.Basis.Orthonormalized());
    }
}
