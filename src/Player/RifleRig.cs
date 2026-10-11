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
    private int[] spine = null!;
    private int head, neck, clavicleR;
    private readonly int[,] fingers = new int[10, 3];
    private int upperR, lowerR, handR;
    private int upperL, lowerL, handL;
    private bool resolved;
    private bool reportedContactFailure;
    private float aimTargetAmount, readyTargetAmount, sprintTargetAmount;
    private float aimBlend, readyBlend, sprintBlend;

    /// <summary>Unclamped wrist targets, for final-pose contact diagnostics.</summary>
    public Transform3D RightTarget { get; private set; }
    public Transform3D LeftTarget { get; private set; }

    /// <summary>Right-eye landmark in the mannequin head frame (meters).</summary>
    public static Vector3 EyePosition(Skeleton3D skeleton) =>
        skeleton.GlobalTransform * (skeleton.GetBoneGlobalPose(skeleton.FindBone("Head")) * EyeLandmark);

    private static Vector3 EyeLandmark => new(Tuning.Current.RifleEyeSide, Tuning.Current.RifleEyeUp, Tuning.Current.RifleEyeForward);

    /// <summary>The rifle visual this rig places.</summary>
    public Rifle? Rifle { get; set; }

    /// <summary>World-space point the rifle and torso aim at.</summary>
    public Vector3 AimTarget { get; set; }

    /// <summary>Visual aim blend: set the character target; reads return the applied blend. 0 = hip, 1 = shouldered.</summary>
    public float AimAmount { get => aimBlend; set => aimTargetAmount = value; }

    /// <summary>Visual ready blend: set the character target; reads return the applied blend. 0 = carry, 1 = ready.</summary>
    public float ReadyAmount { get => readyBlend; set => readyTargetAmount = value; }

    /// <summary>Visual sprint blend: set the character target; reads return the applied 0..1 low-ready blend.</summary>
    public float SprintAmount { get => sprintBlend; set => sprintTargetAmount = value; }

    /// <summary>0..1, decays after each shot.</summary>
    public float Recoil { get; set; }

    /// <summary>Reload progress 0..1: the rifle cants in while the left hand swaps the magazine. 0 when not reloading.</summary>
    public float Reload { get; set; }

    /// <summary>Weight of the whole rig; 0 while ragdolling or getting up.</summary>
    public float Weight { get; set; } = 1f;

    /// <summary>The right hand's wrist transform in rifle space, for carrying the rifle while ragdolling.</summary>
    public static Transform3D RightHandInRifle => RightHandTransform(Transform3D.Identity);

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

        var tuning = Tuning.Current;
        // Logical aim may clear in one tick (release, jump, sprint or reload). Keep that decision in the
        // character, but blend the presentation here, including while the rig is off during a ragdoll.
        var blendStep = tuning.RifleStanceBlendSpeed * (float)delta;
        aimBlend = Mathf.MoveToward(aimBlend, aimTargetAmount, blendStep);
        readyBlend = Mathf.MoveToward(readyBlend, readyTargetAmount, blendStep);
        sprintBlend = Mathf.MoveToward(sprintBlend, sprintTargetAmount, blendStep);
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
        var blade = Mathf.DegToRad(Mathf.Lerp(tuning.RifleHipBladeDegrees, tuning.RifleAimBladeDegrees, AimAmount)) * (1f - SprintAmount);
        yaw -= blade;
        var pitch = Mathf.Asin(Mathf.Clamp(aimDir.Y, -1, 1));
        var pitchShare = Mathf.Lerp(tuning.RifleHipPitchShare, tuning.RifleAimPitchShare, AimAmount) * (1f - SprintAmount * tuning.RifleSprintPitchSuppression);
        var aimRight = flatAim.LengthSquared() > 0.0001f ? flatAim.Normalized().Cross(Vector3.Up).Normalized() : right;
        var torsoRotation = new Quaternion(aimRight, pitch * pitchShare) * new Quaternion(Vector3.Up, yaw);
        var spineWeights = new Vector3(tuning.RifleSpineLowerShare, tuning.RifleSpineMiddleShare, tuning.RifleSpineUpperShare);
        spineWeights /= Mathf.Max(spineWeights.X + spineWeights.Y + spineWeights.Z, 0.0001f);
        for (var k = 0; k < spine.Length; k++)
        {
            // Distribute one world rotation; composing partial yaw/pitch about different axes makes the
            // upper body bend sideways when a strafing player aims near the pitch limits.
            RotateBoneWorld(skeleton, spine[k], Quaternion.Identity.Slerp(torsoRotation, spineWeights[k] * Weight), fromSkeleton, toSkeleton);
        }
        var torsoForward = forward.Rotated(Vector3.Up, yaw);
        RotateBoneWorld(skeleton, clavicleR, new Quaternion(torsoForward,
            -Mathf.DegToRad(tuning.RifleShoulderRaiseDegrees) * AimAmount * Weight), fromSkeleton, toSkeleton);

        // Head follows the aim line before rifle placement; the eye socket then determines cheek weld.
        var headWorld = fromSkeleton * skeleton.GetBoneGlobalPose(head).Origin;
        var headBasis = (fromSkeleton.Basis * skeleton.GetBoneGlobalPose(head).Basis).Orthonormalized();
        var lookDir = (AimTarget - headWorld).Normalized();
        var look = new Quaternion((headBasis * Vector3.Back).Normalized(), lookDir);
        RotateBoneWorld(skeleton, head, Quaternion.Identity.Slerp(look, Mathf.Lerp(tuning.RifleHipHeadFollow, tuning.RifleAimHeadFollow, AimAmount) * Weight), fromSkeleton, toSkeleton);

        RotateBoneWorld(skeleton, head, new Quaternion(lookDir, Mathf.DegToRad(tuning.RifleCheekRollDegrees) * AimAmount * Weight), fromSkeleton, toSkeleton);
        headBasis = (fromSkeleton.Basis * skeleton.GetBoneGlobalPose(head).Basis).Orthonormalized();

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
        var shoulderPocket = shoulder - chestRight * tuning.RifleShoulderInboard + -aimBasis.Column2 * tuning.RifleShoulderForward + aimBasis.Column1 * tuning.RifleShoulderUp;
        // Lean the neck toward the stock without stretching its bones. The actual eye, rather than a
        // guessed face height, anchors the shouldered rifle; the reachable projection below keeps both wrists.
        var desiredEye = shoulderPocket + aimBasis * (Rifle.EyeLocal.Origin - Rifle.ButtLocal.Origin);
        var desiredHead = desiredEye - headBasis * EyeLandmark;
        FitEye(skeleton, neck, head, toSkeleton * desiredEye, toSkeleton * desiredHead, AimAmount * Weight);
        var aimed = new Transform3D(aimBasis, shoulderPocket - aimBasis * Rifle.ButtLocal.Origin);

        // Hip: stock tucked under the arm against the ribs, muzzle on the target.
        var hipDir = (AimTarget - (shoulder - chestUp * tuning.RifleHipSightReferenceDown)).Normalized();
        var hipBasis = Basis.LookingAt(hipDir, Vector3.Up).Rotated(hipDir, Mathf.DegToRad(tuning.RifleReadyRollDegrees));
        var hipPocket = shoulder + chestRight * tuning.RifleHipOutboard - chestUp * tuning.RifleHipDown + chestForward * tuning.RifleHipForward;
        var hip = new Transform3D(hipBasis, hipPocket - hipBasis * Rifle.ButtLocal.Origin);
        // Relaxed carry between shots: muzzle dipped and angled across the body, so the rifle reads clearly
        // from the over-the-shoulder camera instead of pointing straight down the view.
        var relaxedDir = CarryDirection(chestForward, chestRight, tuning.RifleCarryPitchDegrees, tuning.RifleCarryYawDegrees);
        var relaxedBasis = Basis.LookingAt(relaxedDir, Vector3.Up).Rotated(relaxedDir, Mathf.DegToRad(tuning.RifleCarryRollDegrees));
        var relaxed = new Transform3D(relaxedBasis, hipPocket - relaxedBasis * Rifle.ButtLocal.Origin);
        hip = relaxed.InterpolateWith(hip, Mathf.SmoothStep(0, 1, Mathf.Max(ReadyAmount, AimAmount)));

        // Sprint: low ready, muzzle down and slightly inboard, stock still tucked.
        var lowDir = CarryDirection(chestForward, chestRight, tuning.RifleSprintPitchDegrees, tuning.RifleSprintYawDegrees);
        var lowBasis = Basis.LookingAt(lowDir, Vector3.Up);
        var lowPocket = hipPocket + chestUp * (tuning.RifleHipDown - tuning.RifleSprintDown);
        var port = new Transform3D(lowBasis, lowPocket - lowBasis * Rifle.ButtLocal.Origin);

        var stance = hip.InterpolateWith(aimed, Mathf.SmoothStep(0, 1, AimAmount));
        stance = stance.InterpolateWith(port, Mathf.SmoothStep(0, 1, SprintAmount));

        // Reload: the rifle comes in front of the chest, pointing across the body and canted over, with the magazine
        // well where the left hand can work it.
        var reloadWeight = Reload > 0 ? Mathf.SmoothStep(0, tuning.RifleReloadBlendFraction, Reload) * (1f - Mathf.SmoothStep(1f - tuning.RifleReloadBlendFraction, 1f, Reload)) : 0f;
        if (reloadWeight > 0)
        {
            var reloadDir = CarryDirection(chestForward, chestRight, tuning.RifleReloadPitchDegrees, tuning.RifleReloadYawDegrees);
            var reloadBasis = Basis.LookingAt(reloadDir, Vector3.Up).Rotated(reloadDir, Mathf.DegToRad(tuning.RifleReloadRollDegrees));
            var shoulderL = fromSkeleton * skeleton.GetBoneGlobalPose(upperL).Origin;
            var well = shoulder.Lerp(shoulderL, 0.5f) + chestForward * tuning.RifleReloadForward - chestUp * tuning.RifleReloadDown;
            var reloadPose = new Transform3D(reloadBasis, well - reloadBasis * Rifle.MagWellLocal.Origin);
            stance = stance.InterpolateWith(reloadPose, reloadWeight);
        }

        // Recoil: straight back, and the muzzle climbs.
        var kick = Recoil * Recoil;
        var lift = new Basis(stance.Basis.Column0.Normalized(), Mathf.DegToRad(tuning.RifleRecoilLiftDegrees) * kick);
        stance = new Transform3D(lift * stance.Basis, stance.Origin + stance.Basis.Column2.Normalized() * tuning.RifleRecoilBack * kick);
        stance = stance.Orthonormalized();
        var shoulderR = fromSkeleton * skeleton.GetBoneGlobalPose(upperR).Origin;
        var shoulderLWorld = fromSkeleton * skeleton.GetBoneGlobalPose(upperL).Origin;
        var reachR = ArmLength(skeleton, upperR, lowerR, handR) - tuning.RifleReachMargin;
        var reachL = ArmLength(skeleton, upperL, lowerL, handL) - tuning.RifleReachMargin;
        stance = ReachablePlacement(stance, shoulderR, shoulderLWorld, reachR, reachL, Rifle.LeftHandLocal, Rifle.LeftGripBasis);
        // While the rig fades in (end of a get-up), hand the rifle over from wherever the hand already is.
        var handNow = (fromSkeleton * skeleton.GetBoneGlobalPose(handR)).Orthonormalized();
        var leftNow = (fromSkeleton * skeleton.GetBoneGlobalPose(handL)).Orthonormalized();
        if (Weight < 0.999f)
        {
            stance = (handNow * RightHandInRifle.AffineInverse()).InterpolateWith(stance, Weight);
            // The rigid rotation in the handoff can move a wrist outside the earlier spheres. Refit the
            // carried rifle with its firing hand attached and the support hand still blending from the clip.
            // Grow the reach margin smoothly from zero; the get-up clip may start with a straight arm.
            var fadeMargin = tuning.RifleReachMargin * Weight * Weight;
            stance = ReachablePlacement(stance, shoulderR, shoulderLWorld,
                reachR + tuning.RifleReachMargin - fadeMargin, reachL + tuning.RifleReachMargin - fadeMargin,
                Rifle.LeftHandLocal, Rifle.LeftGripBasis, leftNow.Origin, Weight);
        }
        Rifle.GlobalTransform = stance;

        // 3. Hands onto the grips.
        var rightHand = RightHandTransform(stance);
        // The foregrip, or mid-reload wherever the rifle's reload needs the left hand: the magazine, the bolt catch.
        var leftHand = leftNow.InterpolateWith(LeftHandTransform(stance, Rifle.LeftHandLocal, Rifle.LeftGripBasis), Weight);

        RightTarget = rightHand;
        LeftTarget = leftHand;

        var upperRWorld = fromSkeleton * skeleton.GetBoneGlobalPose(upperR).Origin;
        var upperLWorld = fromSkeleton * skeleton.GetBoneGlobalPose(upperL).Origin;
        var poleR = upperRWorld + chestRight * tuning.RifleElbowOut - chestUp * tuning.RifleElbowDown;
        var poleL = upperLWorld - chestRight * tuning.RifleElbowOut - chestUp * tuning.RifleElbowDown;
        SolveTwoBone(skeleton, upperR, lowerR, handR, toSkeleton * rightHand, toSkeleton * poleR);
        SolveTwoBone(skeleton, upperL, lowerL, handL, toSkeleton * leftHand, toSkeleton * poleL);
        var rightError = (fromSkeleton * skeleton.GetBoneGlobalPose(handR).Origin).DistanceTo(rightHand.Origin);
        var leftError = (fromSkeleton * skeleton.GetBoneGlobalPose(handL).Origin).DistanceTo(leftHand.Origin);
        if (!reportedContactFailure && (rightError > 0.003f || leftError > 0.003f))
        {
            reportedContactFailure = true;
            GD.PushError($"[rifle] contact failure: right={rightError:F6}m left={leftError:F6}m weight={Weight:F3}");
        }
        PoseFingers(skeleton);
    }

    /// <summary>Two-bone neck/head fit to the authored eye socket. The head's desired gaze supplies the
    /// pole, so the cheek settles on the stock without translating or scaling either bone.</summary>
    private static void FitEye(Skeleton3D skeleton, int neckBone, int headBone, Vector3 eyeTarget, Vector3 headPole, float weight)
    {
        var a = skeleton.GetBoneGlobalPose(neckBone).Origin;
        var b = skeleton.GetBoneGlobalPose(headBone).Origin;
        var c = skeleton.GetBoneGlobalPose(headBone) * EyeLandmark;
        var l1 = a.DistanceTo(b);
        var l2 = b.DistanceTo(c);
        var offset = eyeTarget - a;
        var distance = offset.Length();
        if (distance >= l1 + l2 || distance <= Mathf.Abs(l1 - l2)) return;
        var direction = offset / distance;
        var along = (l1 * l1 - l2 * l2 + distance * distance) / (2 * distance);
        var height = Mathf.Sqrt(Mathf.Max(0, l1 * l1 - along * along));
        var pole = headPole - a;
        pole = (pole - direction * direction.Dot(pole)).Normalized();
        var headPosition = a + direction * along + pole * height;
        var neckRotation = new Quaternion((b - a).Normalized(), (headPosition - a).Normalized());
        RotateBone(skeleton, neckBone, new Basis(Quaternion.Identity.Slerp(neckRotation, weight)));
        b = skeleton.GetBoneGlobalPose(headBone).Origin;
        c = skeleton.GetBoneGlobalPose(headBone) * EyeLandmark;
        var headRotation = new Quaternion((c - b).Normalized(), (eyeTarget - b).Normalized());
        RotateBone(skeleton, headBone, new Basis(Quaternion.Identity.Slerp(headRotation, weight)));
    }

    private static Vector3 CarryDirection(Vector3 forward, Vector3 right, float pitch, float yaw) =>
        forward.Rotated(Vector3.Up, Mathf.DegToRad(yaw)).Rotated(right, -Mathf.DegToRad(pitch)).Normalized();

    /// <summary>Wrist frames fitted to the mannequin palm and the rifle's grip geometry.</summary>
    private static Transform3D RightHandTransform(Transform3D rifle)
    {
        var t = Tuning.Current;
        var local = new Transform3D(Basis.Identity, new Vector3(t.RifleRightWristSide, t.RifleRightWristUp, t.RifleRightWristBack));
        // +X is the back of the firing hand, +Y runs forward along the palm, +Z toward the thumb.
        local.Basis = new Basis(Vector3.Right, new Vector3(0, Mathf.Sin(Mathf.DegToRad(t.RifleRightWristPitchDegrees)), -Mathf.Cos(Mathf.DegToRad(t.RifleRightWristPitchDegrees))),
            new Vector3(0, Mathf.Cos(Mathf.DegToRad(t.RifleRightWristPitchDegrees)), Mathf.Sin(Mathf.DegToRad(t.RifleRightWristPitchDegrees))));
        return rifle * local;
    }

    private Transform3D LeftHandTransform(Transform3D rifle, Vector3 gripLocal, Basis gripBasis)
    {
        var t = Tuning.Current;
        var magazine = Rifle?.MagazineHold ?? 0;
        var bolt = Rifle?.BoltHold ?? 0;
        var angle = Mathf.DegToRad(Mathf.Lerp(t.RifleLeftWristPitchDegrees, t.RifleMagazineWristPitchDegrees, magazine));
        var side = Mathf.Lerp(t.RifleLeftWristSide, t.RifleMagazineWristSide, magazine);
        side = Mathf.Lerp(side, t.RifleBoltWristSide, bolt);
        var up = Mathf.Lerp(t.RifleLeftWristUp, t.RifleMagazineWristUp, magazine);
        var back = Mathf.Lerp(t.RifleLeftWristBack, t.RifleMagazineWristBack, magazine);
        var fingers = new Vector3(0, Mathf.Sin(angle), -Mathf.Cos(angle));
        var palm = Vector3.Right;
        var wristBasis = new Basis(palm, fingers, palm.Cross(fingers));
        wristBasis = new Basis(wristBasis.GetRotationQuaternion().Slerp(Quaternion.Identity, bolt));
        up = Mathf.Lerp(up, -t.RifleBoltWristDown, bolt);
        back = Mathf.Lerp(back, 0, bolt);
        var local = new Transform3D(wristBasis, gripLocal + new Vector3(-side, up, back));
        return rifle * new Transform3D(gripBasis, gripLocal) * new Transform3D(local.Basis, local.Origin - gripLocal);
    }

    private static float ArmLength(Skeleton3D s, int upper, int lower, int hand) =>
        s.GetBoneGlobalPose(upper).Origin.DistanceTo(s.GetBoneGlobalPose(lower).Origin) +
        s.GetBoneGlobalPose(lower).Origin.DistanceTo(s.GetBoneGlobalPose(hand).Origin);

    /// <summary>Project the rigid rifle into the intersection of the two arms' reachable spheres. Wrist
    /// offsets are fixed in rifle space, so translating the rifle moves both contacts together. Projection
    /// happens before IK; the solver never substitutes a nearer target and calls that a successful hold.</summary>
    private Transform3D ReachablePlacement(Transform3D rifle, Vector3 shoulderR, Vector3 shoulderL,
        float reachR, float reachL, Vector3 leftGrip, Basis leftBasis, Vector3 leftStart = default, float leftWeight = 1f)
    {
        var offsetR = RightHandTransform(new Transform3D(rifle.Basis, Vector3.Zero)).Origin;
        var offsetL = LeftHandTransform(new Transform3D(rifle.Basis, Vector3.Zero), leftGrip, leftBasis).Origin;
        // The support hand leaves its get-up pose gradually. Express that blended wrist's reach as a
        // sphere for the rifle origin, so the handoff is reachable too without clamping an IK target.
        var leftCenter = (shoulderL - leftStart * (1 - leftWeight)) / leftWeight - offsetL;
        for (var i = 0; i < 32; i++)
        {
            var correctionR = ProjectSphere(rifle.Origin, shoulderR - offsetR, reachR) - rifle.Origin;
            rifle.Origin += correctionR;
            var correctionL = ProjectSphere(rifle.Origin, leftCenter, reachL / leftWeight) - rifle.Origin;
            rifle.Origin += correctionL;
            if (correctionR.LengthSquared() + correctionL.LengthSquared() < 1e-12f) break;
        }
        return rifle;
    }

    private static Vector3 ProjectSphere(Vector3 point, Vector3 center, float radius)
    {
        var offset = point - center;
        return offset.LengthSquared() > radius * radius ? center + offset.Normalized() * radius : point;
    }

    private void PoseFingers(Skeleton3D skeleton)
    {
        var t = Tuning.Current;
        // Each local X is the finger's flexion axis after its authored rest orientation. Set absolute curls:
        // inheriting the pistol clip's rotations caused fingers to cross the handguard and receiver.
        for (var f = 0; f < 10; f++)
        {
            var curls = FingerCurls(t, f, false);
            var magazineCurls = FingerCurls(t, f, true);
            for (var j = 0; j < 3; j++)
            {
                var bone = fingers[f, j];
                var angle = curls[j];
                if (f < 5 && Rifle != null)
                {
                    // Keep the thumb wrapped until the hand has cleared the magazine. Opening it at once
                    // sweeps its distal joint through the seated magazine during the tactical return.
                    if (f == 4)
                    {
                        var transit = new Vector3(t.RifleBoltThumb1Degrees, t.RifleBoltThumb2Degrees, t.RifleBoltThumb3Degrees);
                        var foregrip = Mathf.SmoothStep(t.RifleForegripThumbCloseStart, 1, 1 - Rifle.MagazineHold - Rifle.BoltHold);
                        var magazine = Mathf.SmoothStep(0, t.RifleMagazineThumbRelease, Rifle.MagazineHold);
                        angle = Mathf.Lerp(Mathf.Lerp(transit[j], angle, foregrip), magazineCurls[j], magazine);
                    }
                    else
                    {
                        angle = Mathf.Lerp(angle, magazineCurls[j], Rifle.MagazineHold) * Rifle.SupportHold;
                    }
                }
                if (f == 4 && Rifle != null)
                {
                    var thumb = new Vector3(t.RifleBoltThumb1Degrees, t.RifleBoltThumb2Degrees, t.RifleBoltThumb3Degrees);
                    angle = Mathf.Lerp(angle, thumb[j], Rifle.BoltHold);
                }
                if (f == 5)
                {
                    var trigger = new Vector3(t.RifleTriggerIndex1Degrees, t.RifleTriggerIndex2Degrees, t.RifleTriggerIndex3Degrees);
                    angle = Mathf.Lerp(angle, trigger[j], Recoil);
                }
                var pose = skeleton.GetBoneRest(bone).Basis.GetRotationQuaternion() * new Quaternion(Vector3.Right, Mathf.DegToRad(angle));
                skeleton.SetBonePoseRotation(bone, skeleton.GetBonePoseRotation(bone).Slerp(pose, Weight));
            }
        }
    }

    // Read live tuners once per digit; no pose arrays are allocated in the per-player render path.
    private static Vector3 FingerCurls(Tuning t, int digit, bool magazine)
    {
        if (magazine)
        {
            return digit switch
            {
                0 => new Vector3(t.RifleMagazineIndex1Degrees, t.RifleMagazineIndex2Degrees, t.RifleMagazineIndex3Degrees),
                1 => new Vector3(t.RifleMagazineMiddle1Degrees, t.RifleMagazineMiddle2Degrees, t.RifleMagazineMiddle3Degrees),
                2 => new Vector3(t.RifleMagazineRing1Degrees, t.RifleMagazineRing2Degrees, t.RifleMagazineRing3Degrees),
                3 => new Vector3(t.RifleMagazinePinky1Degrees, t.RifleMagazinePinky2Degrees, t.RifleMagazinePinky3Degrees),
                4 => new Vector3(t.RifleMagazineThumb1Degrees, t.RifleMagazineThumb2Degrees, t.RifleMagazineThumb3Degrees),
                _ => Vector3.Zero,
            };
        }
        return digit switch
        {
            0 => new Vector3(t.RifleLeftIndex1Degrees, t.RifleLeftIndex2Degrees, t.RifleLeftIndex3Degrees),
            1 => new Vector3(t.RifleLeftMiddle1Degrees, t.RifleLeftMiddle2Degrees, t.RifleLeftMiddle3Degrees),
            2 => new Vector3(t.RifleLeftRing1Degrees, t.RifleLeftRing2Degrees, t.RifleLeftRing3Degrees),
            3 => new Vector3(t.RifleLeftPinky1Degrees, t.RifleLeftPinky2Degrees, t.RifleLeftPinky3Degrees),
            4 => new Vector3(t.RifleLeftThumb1Degrees, t.RifleLeftThumb2Degrees, t.RifleLeftThumb3Degrees),
            5 => new Vector3(t.RifleRightIndex1Degrees, t.RifleRightIndex2Degrees, t.RifleRightIndex3Degrees),
            6 => new Vector3(t.RifleRightMiddle1Degrees, t.RifleRightMiddle2Degrees, t.RifleRightMiddle3Degrees),
            7 => new Vector3(t.RifleRightRing1Degrees, t.RifleRightRing2Degrees, t.RifleRightRing3Degrees),
            8 => new Vector3(t.RifleRightPinky1Degrees, t.RifleRightPinky2Degrees, t.RifleRightPinky3Degrees),
            9 => new Vector3(t.RifleRightThumb1Degrees, t.RifleRightThumb2Degrees, t.RifleRightThumb3Degrees),
            _ => Vector3.Zero,
        };
    }

    private void Resolve(Skeleton3D skeleton)
    {
        spine = new[] { skeleton.FindBone("spine_01"), skeleton.FindBone("spine_02"), skeleton.FindBone("spine_03") };
        head = skeleton.FindBone("Head");
        neck = skeleton.FindBone("neck_01");
        clavicleR = skeleton.FindBone("clavicle_r");
        var names = new[] { "index", "middle", "ring", "pinky", "thumb" };
        for (var side = 0; side < 2; side++)
        {
            for (var f = 0; f < names.Length; f++)
            {
                for (var j = 0; j < 3; j++)
                    fingers[side * 5 + f, j] = skeleton.FindBone($"{names[f]}_{j + 1:00}_{(side == 0 ? "l" : "r")}");
            }
        }

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
        var distance = toTarget.Length();
        if (distance >= l1 + l2 || distance <= Mathf.Abs(l1 - l2) + 0.00001f)
        {
            GD.PushError($"[rifle] unreachable wrist: distance={distance:F6}, lengths={l1:F6}/{l2:F6}");
            return;
        }
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
