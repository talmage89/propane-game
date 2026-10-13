using System;
using System.Collections.Generic;
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
    public required WorldContext World { get; init; }

    private int[] spine = null!;
    private int head, neck, clavicleR;
    private readonly int[,] fingers = new int[10, 3];
    private int upperR, lowerR, handR;
    private int upperL, lowerL, handL;
    private bool resolved;
    private bool reportedContactFailure;
    private float aimTargetAmount, readyTargetAmount, sprintTargetAmount;
    private float aimBlend, readyBlend, sprintBlend;
    private readonly RecoilSpring recoil = new();
    private Vector3 lookDirection;
    private Transform3D previousStance;
    private bool hasStance;
    private Transform3D handoffRifle, handoffLeft, handoffChest;
    private Vector3 handoffElbowR, handoffElbowL;
    private Quaternion[]? previousBones;
    private Quaternion[] desiredBones = null!;
    private Vector3[] previousPositions = null!, desiredPositions = null!;
    private int[] upperBones = null!;
    private Transform3D lastRifle, lastChest, lastUpperParent;
    private float lastReloadPhase;
    private bool meleeReloadForward, hadMelee;
    private bool hasUpperPose;
    private float lastFitAim;
    private float reloadTarget, reloadPhase;

    public LocomotionPose Pose { get; init; } = null!;

    /// <summary>Unclamped wrist targets, for final-pose contact diagnostics.</summary>
    public Transform3D RightTarget { get; private set; }
    public Transform3D LeftTarget { get; private set; }
    public float ForegripResidual => Rifle == null ? 0 : LeftTarget.Origin.DistanceTo(
        LeftHandTransform(Rifle.GlobalTransform, Rifle.ForegripLocal.Origin, Basis.Identity).Origin);

    /// <summary>Right-eye landmark in the mannequin head frame (meters).</summary>
    public static Vector3 EyePosition(Skeleton3D skeleton, Tuning tuning) =>
        skeleton.GlobalTransform * (skeleton.GetBoneGlobalPose(skeleton.FindBone("Head")) * new Vector3(tuning.RifleEyeSide, tuning.RifleEyeUp, tuning.RifleEyeForward));

    private Vector3 EyeLandmark => new(World.Tuning.RifleEyeSide, World.Tuning.RifleEyeUp, World.Tuning.RifleEyeForward);

    /// <summary>The rifle visual this rig places.</summary>
    public Rifle? Rifle { get; set; }

    /// <summary>World-space point the rifle and torso aim at.</summary>
    public Vector3 AimTarget { get; set; }

    public bool AlignedShotThisFrame { get; private set; }
    public bool TrackingBarrelThisFrame { get; private set; }
    private float alignmentRemaining;
    /// <summary>Exact aligned shot placement, retained until the firing frame has posed both grips.</summary>
    private Transform3D? shotPose;
    private Transform3D shotSkeletonFrame;
    private Vector3 lastShoulderR, lastShoulderL, lastNeck;
    private readonly Queue<Vector3> remoteShots = new();
    public event Action<Vector3>? RemoteShotAligned;

    /// <summary>Reliable shot echoes can precede snapshot movement and reload presentation. Fit them only
    /// when this frame's body transform is final; local authoritative shots still fit immediately.</summary>
    public void QueueRemoteShot(Vector3 target) => remoteShots.Enqueue(target);

    /// <summary>Place the barrel and both reachable grips before the authoritative muzzle obstruction ray.</summary>
    public void AlignShot(Vector3 target)
    {
        var skeleton = GetSkeleton();
        if (skeleton == null || Rifle == null || !resolved) return;
        // Physics can fire on the first tick after a reload finishes, before presentation clears its
        // magazine/bolt contact. Fit the firing foregrip that this frame will actually render.
        reloadPhase = reloadTarget = lastReloadPhase = 0;
        meleeReloadForward = false;
        Rifle.PreviewReloadContact(0);
        var from = skeleton.GlobalTransform;
        var shoulderR = from * (hasUpperPose ? lastShoulderR : skeleton.GetBoneGlobalPose(upperR).Origin);
        var shoulderL = from * (hasUpperPose ? lastShoulderL : skeleton.GetBoneGlobalPose(upperL).Origin);
        var margin = World.Tuning.RifleReachMargin * Weight * Weight;
        var neckCenter = from * (hasUpperPose ? lastNeck : skeleton.GetBoneGlobalPose(neck).Origin);
        var eyeReach = skeleton.GetBoneGlobalPose(neck).Origin.DistanceTo(skeleton.GetBoneGlobalPose(head).Origin) + EyeLandmark.Length() - margin;
        var pivot = LeftHandTransform(Transform3D.Identity, Rifle.LeftHandLocal, Rifle.LeftGripBasis).Origin.Lerp(Rifle.EyeLocal.Origin, AimAmount);
        var pose = AlignPlacement(Rifle.GlobalTransform, target, pivot, shoulderR, shoulderL,
            ArmLength(skeleton, upperR, lowerR, handR) - margin, ArmLength(skeleton, upperL, lowerL, handL) - margin,
            AimAmount >= 0.99f ? neckCenter : null, eyeReach);
        // The obstruction origin now agrees with the aligned model. Preserve it for this rendered firing frame.
        Rifle.GlobalTransform = Rifle.AlignBarrel(pose, Rifle.MuzzleLocal.Origin, target);
        shotPose = Rifle.GlobalTransform;
        shotSkeletonFrame = from;
        alignmentRemaining = 1 / Mathf.Max(World.Tuning.FireRate, 0.1f) + 1f / Engine.PhysicsTicksPerSecond;
    }

    /// <summary>Visual aim blend: set the character target; reads return the applied blend. 0 = hip, 1 = shouldered.</summary>
    public float AimAmount { get => aimBlend; set => aimTargetAmount = value; }

    /// <summary>Visual ready blend: set the character target; reads return the applied blend. 0 = carry, 1 = ready.</summary>
    public float ReadyAmount { get => readyBlend; set => readyTargetAmount = value; }

    /// <summary>Visual sprint blend: set the character target; reads return the applied 0..1 low-ready blend.</summary>
    public float SprintAmount { get => sprintBlend; set => sprintTargetAmount = value; }

    /// <summary>Live spring displacement, including accumulation during a burst.</summary>
    public float Recoil => recoil.Position;

    public bool Melee { get; set; }
    public float MeleeProgress { get; set; }

    public void Kick() => recoil.Kick(World.Tuning.RifleRecoilFrequency, World.Tuning.RifleRecoilImpulse);

    /// <summary>Reload progress 0..1: the rifle cants in while the left hand swaps the magazine. 0 when not reloading.</summary>
    public float Reload { get => reloadPhase; set => reloadTarget = value; }

    /// <summary>Snapshots may skip reload progress or its last frames. Finish the visual hand return at a
    /// bounded rate before releasing carry; local ammo/state timing stays authoritative and immediate.</summary>
    public void AdvanceReload(float dt, bool remote, bool melee = false)
    {
        // Cancel ammo immediately; early reloads retrace contact, late ones finish the hand return during windup.
        // Snapping an off-grip left hand directly to the foregrip defeats the coherent pose bound.
        if (melee)
        {
            if (!hadMelee) meleeReloadForward = reloadPhase > 0.5f;
            hadMelee = true;
            reloadPhase = Mathf.MoveToward(reloadPhase, meleeReloadForward ? 1 : 0, dt * World.Tuning.MeleeReloadReturnSpeed);
            return;
        }
        hadMelee = false;
        if (!remote) { reloadPhase = reloadTarget; return; }
        if (Weight <= 0.001f) { reloadPhase = 0; return; }
        var target = reloadTarget > 0 ? reloadTarget : reloadPhase > 0 ? 1 : 0;
        reloadPhase = Mathf.MoveToward(reloadPhase, target, dt * World.Tuning.RifleReloadPhaseSpeed);
        if (reloadTarget == 0 && reloadPhase >= 1) reloadPhase = 0;
    }

    /// <summary>Weight of the whole rig; 0 while ragdolling or getting up.</summary>
    public float Weight { get; set; } = 1f;

    /// <summary>The right hand's wrist transform in rifle space, for carrying the rifle while ragdolling.</summary>
    public Transform3D RightHandInRifle => RightHandTransform(Transform3D.Identity);

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

        while (remoteShots.TryDequeue(out var target))
        {
            if (Weight > 0.001f)
            {
                // A newer snapshot can already be striking. The echo owns this one firing frame's
                // weapon presentation; the character restores its current action on the next frame.
                Melee = false;
                AlignShot(target);
            }
            RemoteShotAligned?.Invoke(target);
        }

        var tuning = World.Tuning;
        // Logical aim may clear in one tick (release, jump, sprint or reload). Keep that decision in the
        // character, but blend the presentation here, including while the rig is off during a ragdoll.
        AlignedShotThisFrame = shotPose != null;
        TrackingBarrelThisFrame = alignmentRemaining > 0 && Reload == 0 && !Melee;
        var blendStep = tuning.RifleStanceBlendSpeed * (float)delta;
        aimBlend = Mathf.MoveToward(aimBlend, aimTargetAmount, blendStep);
        readyBlend = Mathf.MoveToward(readyBlend, readyTargetAmount, blendStep);
        sprintBlend = Mathf.MoveToward(sprintBlend, sprintTargetAmount, blendStep);
        var toSkeleton = skeleton.GlobalTransform.AffineInverse();
        var fromSkeleton = skeleton.GlobalTransform;

        recoil.Update((float)delta, tuning.RifleRecoilFrequency, tuning.RifleRecoilDamping);
        if (Weight <= 0.001f)
        {
            recoil.Reset();
            hasStance = false;
            lookDirection = Vector3.Zero;
            CacheBones(skeleton);
            shotPose = null;
            alignmentRemaining = 0;
            hasUpperPose = false;
            lastFitAim = aimBlend;
            lastReloadPhase = 0;
            Rifle.Animate((float)delta, 0);
            // Ragdolling or getting up: RifleHandFollow keeps the rifle in the right hand.
            return;
        }

        Rifle.PreviewReloadContact(Reload);
        previousBones ??= new Quaternion[skeleton.GetBoneCount()];
        if (!hasStance)
        {
            CacheBones(skeleton);
            handoffRifle = toSkeleton * Rifle.GlobalTransform;
            handoffLeft = skeleton.GetBoneGlobalPose(handL);
            handoffChest = skeleton.GetBoneGlobalPose(spine[2]);
            handoffElbowR = skeleton.GetBoneGlobalPose(lowerR).Origin;
            handoffElbowL = skeleton.GetBoneGlobalPose(lowerL).Origin;
        }
        // The model faces +Z in skeleton space.
        var forward = (fromSkeleton.Basis * Vector3.Back).Normalized();
        forward.Y = 0;
        forward = forward.Normalized();
        var right = forward.Cross(Vector3.Up).Normalized();

        var chestWorld = fromSkeleton * skeleton.GetBoneGlobalPose(spine[2]).Origin;
        var aimDir = (AimTarget - chestWorld).Normalized();
        if (lookDirection.LengthSquared() > 0)
        {
            var turn = new Quaternion(lookDirection, aimDir);
            var angle = 2 * Mathf.Atan2(new Vector3(turn.X, turn.Y, turn.Z).Length(), Mathf.Abs(turn.W));
            var fraction = angle > 0 ? Mathf.Min(1, Mathf.DegToRad(tuning.RifleAimTurnSpeed) * (float)delta / angle) : 1;
            aimDir = (Quaternion.Identity.Slerp(turn, fraction) * lookDirection).Normalized();
        }
        lookDirection = aimDir;
        // Only the displayed pose follows this eased point; the shot ray still uses the camera/spread.
        var visualTarget = chestWorld + aimDir * (AimTarget - chestWorld).Length();
        var gaitScale = Mathf.Lerp(Mathf.Lerp(1, tuning.RifleWalkSwayScale, Pose.Walk), tuning.RifleSprintSwayScale, Pose.Sprint);
        var sway = gaitScale * Pose.Moving * (1 - Pose.Air) * Mathf.Lerp(1, tuning.RifleAimSwayScale, AimAmount) * Weight;
        var lateral = Mathf.Sin(Pose.Phase * Mathf.Tau);
        var bounce = Mathf.Cos(Pose.Phase * Mathf.Tau * 2);
        var kick = recoil.Position * Weight;
        var total = tuning.MeleeWindup + tuning.MeleeActive + tuning.MeleeRecovery;
        var elapsed = MeleeProgress * total;
        var windup = Mathf.SmoothStep(0, tuning.MeleeWindup, elapsed);
        var contact = Mathf.SmoothStep(tuning.MeleeWindup, tuning.MeleeWindup + tuning.MeleeActive, elapsed);
        var recover = Mathf.SmoothStep(tuning.MeleeWindup + tuning.MeleeActive, total, elapsed);
        var meleeWeight = Melee ? windup * (1 - recover) : 0;
        var meleeYaw = Mathf.Lerp(tuning.MeleeWindupYawDegrees, tuning.MeleeStrikeYawDegrees, contact);

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
        var torsoRotation = new Quaternion(right, -Mathf.DegToRad(tuning.MeleeTorsoLeanDegrees) * meleeWeight)
            * new Quaternion(Vector3.Up, Mathf.DegToRad(tuning.MeleeTorsoTwistDegrees) * (1 - 2 * contact) * meleeWeight)
            * new Quaternion(aimDir, Mathf.DegToRad(tuning.RifleTorsoSwayDegrees) * lateral * sway)
            * new Quaternion(aimRight, pitch * pitchShare - Mathf.DegToRad(tuning.RifleRecoilLiftDegrees) * kick * tuning.RifleRecoilTorsoShare)
            * new Quaternion(Vector3.Up, yaw);
        var lowerSpine = skeleton.GetBoneGlobalPose(spine[0]);
        lowerSpine.Origin += toSkeleton.Basis * (-aimDir * tuning.RifleRecoilBack * kick * tuning.RifleRecoilTorsoShare);
        var lowerParent = skeleton.GetBoneParent(spine[0]);
        skeleton.SetBonePosePosition(spine[0], skeleton.GetBoneGlobalPose(lowerParent).AffineInverse() * lowerSpine.Origin);
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
        var lookDir = (visualTarget - headWorld).Normalized();
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
        var rifleDir = (visualTarget - shoulder).Normalized();
        var aimBasis = Basis.LookingAt(rifleDir, Vector3.Up);

        // Shouldered: butt in the shoulder pocket, sight near the eye line.
        var shoulderPocket = shoulder - chestRight * tuning.RifleShoulderInboard + -aimBasis.Column2 * tuning.RifleShoulderForward + aimBasis.Column1 * tuning.RifleShoulderUp;
        // Lean the neck toward the stock without stretching its bones. The actual eye, rather than a
        // guessed face height, anchors the shouldered rifle; the reachable projection below keeps both wrists.
        var aimed = new Transform3D(aimBasis, shoulderPocket - aimBasis * Rifle.ButtLocal.Origin);
        var aimedLift = new Basis(aimed.Basis.Column0.Normalized(), Mathf.DegToRad(tuning.RifleRecoilLiftDegrees) * kick);
        aimed = new Transform3D(aimedLift * aimed.Basis, shoulderPocket + aimedLift * (aimed.Origin - shoulderPocket)
            + aimed.Basis.Column2.Normalized() * tuning.RifleRecoilBack * kick * (1 - tuning.RifleRecoilTorsoShare));
        var desiredEye = aimed * Rifle.EyeLocal.Origin;
        FitEye(skeleton, neck, head, toSkeleton * desiredEye, toSkeleton * (desiredEye - headBasis * EyeLandmark), AimAmount * Weight);

        // Hip: stock tucked under the arm against the ribs, muzzle on the target.
        var hipDir = (visualTarget - (shoulder - chestUp * tuning.RifleHipSightReferenceDown)).Normalized();
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

        if (meleeWeight > 0)
        {
            // Rotate the muzzle back across the body so the stock leads the outward arc. Both wrist targets
            // stay rigidly attached; the same reachable placement and coherent pose bound solve the strike.
            var strikeDir = CarryDirection(forward, right, tuning.MeleePitchDegrees, meleeYaw);
            var strikeBasis = Basis.LookingAt(strikeDir, Vector3.Up).Rotated(strikeDir, Mathf.DegToRad(tuning.MeleeRollDegrees));
            var center = shoulder + forward * tuning.MeleeRifleForward - Vector3.Up * tuning.MeleeRifleDown;
            var strike = new Transform3D(strikeBasis, center);
            stance = stance.InterpolateWith(strike, meleeWeight);
        }

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

        // The carry breathes in the same stride as the feet. Roll about the stock rather than the grip.
        var pivot = stance * Rifle.ButtLocal.Origin;
        var roll = new Basis(-stance.Basis.Column2.Normalized(), Mathf.DegToRad(tuning.RifleSwayRollDegrees) * lateral * sway);
        stance = new Transform3D(roll * stance.Basis, pivot + roll * (stance.Origin - pivot)
            + chestRight * (lateral * tuning.RifleSwaySide * sway)
            + chestUp * (bounce * tuning.RifleSwayUp * sway)
            + chestForward * (bounce * tuning.RifleSwayForward * sway));
        if (hasStance && !Melee)
        {
            stance = (fromSkeleton * previousStance).InterpolateWith(stance, Mathf.Lerp(1 - Mathf.Exp(-tuning.RifleMotionResponse * (float)delta), 1, AimAmount));
        }
        previousStance = toSkeleton * stance;
        hasStance = true;
        // The torso takes most of the rearward travel. The remaining stock compression and muzzle climb
        // use the spring directly, so smoothing carry/reload never rounds off a shot's kick.
        pivot = stance * Rifle.ButtLocal.Origin;
        var lift = new Basis(stance.Basis.Column0.Normalized(), Mathf.DegToRad(tuning.RifleRecoilLiftDegrees) * kick * (1 - AimAmount));
        stance = new Transform3D(lift * stance.Basis, pivot + lift * (stance.Origin - pivot)
            + stance.Basis.Column2.Normalized() * tuning.RifleRecoilBack * kick * (1 - tuning.RifleRecoilTorsoShare) * (1 - AimAmount));
        stance = stance.Orthonormalized();
        var shoulderR = fromSkeleton * skeleton.GetBoneGlobalPose(upperR).Origin;
        var shoulderLWorld = fromSkeleton * skeleton.GetBoneGlobalPose(upperL).Origin;
        var reachR = ArmLength(skeleton, upperR, lowerR, handR) - tuning.RifleReachMargin;
        var reachL = ArmLength(skeleton, upperL, lowerL, handL) - tuning.RifleReachMargin;
        stance = ReachablePlacement(stance, shoulderR, shoulderLWorld, reachR, reachL, Rifle.LeftHandLocal, Rifle.LeftGripBasis);
        // While the rig fades in (end of a get-up), hand the rifle over from wherever the hand already is.
        var leftNow = (fromSkeleton * skeleton.GetBoneGlobalPose(handL)).Orthonormalized();
        if (Weight < 0.999f)
        {
            stance = (fromSkeleton * handoffRifle).InterpolateWith(stance, Mathf.SmoothStep(0, 1, Weight));
            leftNow = fromSkeleton * skeleton.GetBoneGlobalPose(spine[2]) * handoffChest.AffineInverse() * handoffLeft;
            // The rigid rotation in the handoff can move a wrist outside the earlier spheres. Refit the
            // carried rifle with its firing hand attached and the support hand still blending from the clip.
            // Grow the reach margin smoothly from zero; the get-up clip may start with a straight arm.
            var fadeMargin = tuning.RifleReachMargin * Weight * Weight;
            stance = ReachablePlacement(stance, shoulderR, shoulderLWorld,
                reachR + tuning.RifleReachMargin - fadeMargin, reachL + tuning.RifleReachMargin - fadeMargin,
                Rifle.LeftHandLocal, Rifle.LeftGripBasis, leftNow.Origin, Weight);
        }
        // 3. Solve both contacts while bounding the coherent upper pose.
        var upperRWorld = fromSkeleton * skeleton.GetBoneGlobalPose(upperR).Origin;
        var upperLWorld = fromSkeleton * skeleton.GetBoneGlobalPose(upperL).Origin;
        var poleR = upperRWorld + chestRight * tuning.RifleElbowOut - chestUp * tuning.RifleElbowDown;
        var poleL = upperLWorld - chestRight * tuning.RifleElbowOut - chestUp * tuning.RifleElbowDown;
        poleR = (fromSkeleton * handoffElbowR).Lerp(poleR, Mathf.SmoothStep(0, 1, Weight));
        poleL = (fromSkeleton * handoffElbowL).Lerp(poleL, Mathf.SmoothStep(0, 1, Weight));
        ConstrainUpperPose(skeleton, stance, leftNow, poleR, poleL, (float)delta, fromSkeleton, toSkeleton);
        var rightError = (fromSkeleton * skeleton.GetBoneGlobalPose(handR).Origin).DistanceTo(RightTarget.Origin);
        var leftError = (fromSkeleton * skeleton.GetBoneGlobalPose(handL).Origin).DistanceTo(LeftTarget.Origin);
        if (!reportedContactFailure && (rightError > 0.003f || leftError > 0.003f))
        {
            reportedContactFailure = true;
            GD.PushError($"[rifle] contact failure: right={rightError:F6}m left={leftError:F6}m weight={Weight:F3}");
        }
        Rifle.Animate((float)delta, Reload);
        PoseFingers(skeleton);
        for (var f = 0; f < 10; f++)
            for (var j = 0; j < 3; j++) LimitBone(skeleton, fingers[f, j], (float)delta);
        CacheBones(skeleton);
        shotPose = null;
        alignmentRemaining = Mathf.Max(0, alignmentRemaining - (float)delta);
    }

    // Interpolate the coherent upper pose and rifle, then solve contacts again. A post-IK bone clamp
    // would detach wrists. Reducing the candidate instead also covers unusual get-up arm orientations.
    private void ConstrainUpperPose(Skeleton3D s, Transform3D wanted, Transform3D leftStart,
        Vector3 poleR, Vector3 poleL, float dt, Transform3D from, Transform3D to)
    {
        foreach (var bone in upperBones)
        {
            desiredBones[bone] = s.GetBonePoseRotation(bone).Normalized();
            desiredPositions[bone] = s.GetBonePosePosition(bone);
        }
        var startChest = s.GetBoneGlobalPose(spine[2]);
        if (!hasUpperPose)
        {
            lastRifle = handoffRifle;
            lastChest = startChest;
            lastReloadPhase = Reload;
            hasUpperPose = true;
        }
        var limit = Mathf.DegToRad(Melee ? World.Tuning.MeleePoseAngularSpeed
            : shotPose != null ? World.Tuning.RifleShotPoseAngularSpeed : World.Tuning.PoseAngularSpeed) * dt;
        var amount = 1f;
        var proposedAim = aimBlend;
        var fittedAim = proposedAim;
        var releasingReload = Reload == 0 && !Melee && lastReloadPhase > 0 || Melee && meleeReloadForward;
        var proposedReload = releasingReload ? 1 : Reload;
        var fittedReload = proposedReload;
        if (shotPose != null)
        {
            // Animation can advance the hips after physics fitted this muzzle against the cached shoulders.
            // Movement can also advance the body between physics ticks. Preserve the fitting frame in world
            // space, then rebase it into this skeleton and parent before easing toward this clip sample.
            var root = spine[0];
            var previousRoot = from.AffineInverse() * shotSkeletonFrame * lastUpperParent *
                new Transform3D(new Basis(previousBones![root]), previousPositions[root]);
            var rebased = s.GetBoneGlobalPose(s.GetBoneParent(root)).AffineInverse() * previousRoot;
            previousBones[root] = rebased.Basis.Orthonormalized().GetRotationQuaternion();
            previousPositions[root] = rebased.Origin;
        }
        for (var attempt = 0; attempt < 12; attempt++)
        {
            foreach (var bone in upperBones)
            {
                s.SetBonePoseRotation(bone, previousBones![bone].Slerp(desiredBones[bone], amount));
                s.SetBonePosePosition(bone, previousPositions[bone].Lerp(desiredPositions[bone], amount));
            }
            var carried = from * s.GetBoneGlobalPose(spine[2]) * lastChest.AffineInverse() * lastRifle;
            var rifle = carried.InterpolateWith(wanted, amount);
            fittedAim = Mathf.Lerp(lastFitAim, proposedAim, amount);
            if (Reload == 0)
                rifle.Origin += (EyePosition(s, World.Tuning) - rifle * Rifle!.EyeLocal.Origin) * Mathf.SmoothStep(0, 1, fittedAim);
            if (Weight < 0.999f)
                leftStart = from * s.GetBoneGlobalPose(spine[2]) * handoffChest.AffineInverse() * handoffLeft;
            var shoulderR = from * s.GetBoneGlobalPose(upperR).Origin;
            var shoulderL = from * s.GetBoneGlobalPose(upperL).Origin;
            var margin = World.Tuning.RifleReachMargin * Weight * Weight;
            // Bound reload progress with the coherent pose. Sampling the authored curved contact path keeps
            // the hand clear of the receiver; interpolating wrist frames would cut across that clearance.
            fittedReload = Mathf.Lerp(lastReloadPhase, proposedReload, amount);
            Rifle!.PreviewReloadContact(fittedReload);
            rifle = ReachablePlacement(rifle, shoulderR, shoulderL,
                ArmLength(s, upperR, lowerR, handR) - margin, ArmLength(s, upperL, lowerL, handL) - margin,
                Rifle!.LeftHandLocal, Rifle.LeftGripBasis, leftStart.Origin, Weight);
            // Kick can lift/compress the weapon, but its firing axis follows the logical target immediately.
            // The shot frame uses the sampled spread endpoint; idle carry/reload/melee keep their authored pose.
            if (Reload == 0 && !Melee && TrackingBarrelThisFrame)
            {
                // Reprojecting a shot placement would move the rendered muzzle away from its tracer origin.
                if (shotPose is Transform3D firingPose)
                {
                    rifle = firingPose;
                }
                else
                {
                    var pivot = LeftHandTransform(Transform3D.Identity, Rifle.LeftHandLocal, Rifle.LeftGripBasis).Origin.Lerp(Rifle.EyeLocal.Origin, fittedAim);
                    rifle = AlignPlacement(rifle, AimTarget, pivot, shoulderR, shoulderL,
                        ArmLength(s, upperR, lowerR, handR) - margin, ArmLength(s, upperL, lowerL, handL) - margin,
                        fittedAim >= 0.99f ? from * s.GetBoneGlobalPose(neck).Origin : null,
                        s.GetBoneGlobalPose(neck).Origin.DistanceTo(s.GetBoneGlobalPose(head).Origin) + EyeLandmark.Length() - margin,
                        leftStart.Origin, Weight);
                }
            }
            var right = RightHandTransform(rifle);
            var left = leftStart.InterpolateWith(LeftHandTransform(rifle, Rifle.LeftHandLocal, Rifle.LeftGripBasis), Weight);
            var reachable = shotPose == null || right.Origin.DistanceTo(shoulderR) <= ArmLength(s, upperR, lowerR, handR) - margin + 1e-6f &&
                left.Origin.DistanceTo(shoulderL) <= ArmLength(s, upperL, lowerL, handL) - margin + 1e-6f;
            // Blend elbow destinations as well as wrists; canonical poles alone snap a returning arm's twist.
            s.SetBonePoseRotation(upperR, previousBones![upperR]);
            s.SetBonePoseRotation(lowerR, previousBones[lowerR]);
            s.SetBonePoseRotation(upperL, previousBones[upperL]);
            s.SetBonePoseRotation(lowerL, previousBones[lowerL]);
            var oldR = from * s.GetBoneGlobalPose(lowerR).Origin;
            var oldL = from * s.GetBoneGlobalPose(lowerL).Origin;
            // A fixed firing muzzle can put a trial torso outside arm reach while turning. Reject that
            // trial before invoking IK; only the final trial reports a genuinely unreachable contact.
            if (attempt < 11 && (right.Origin.DistanceTo(shoulderR) >= ArmLength(s, upperR, lowerR, handR) ||
                left.Origin.DistanceTo(shoulderL) >= ArmLength(s, upperL, lowerL, handL)))
            {
                amount *= 0.5f;
                continue;
            }
            SolveTwoBone(s, upperR, lowerR, handR, to * right, to * oldR.Lerp(poleR, amount));
            SolveTwoBone(s, upperL, lowerL, handL, to * left, to * oldL.Lerp(poleL, amount));
            if (TrackingBarrelThisFrame && fittedAim >= 0.99f)
            {
                var eye = rifle * Rifle!.EyeLocal.Origin;
                var headBasis = (from.Basis * s.GetBoneGlobalPose(head).Basis).Orthonormalized();
                FitEye(s, neck, head, to * eye, to * (eye - headBasis * EyeLandmark), fittedAim * Weight);
            }
            var largest = 0f;
            foreach (var bone in upperBones)
            {
                // A rigid barrel correction determines both wrists immediately. Bound the body around it;
                // interpolating the contact frames would aim the displayed barrel away from this shot.
                if (TrackingBarrelThisFrame && (bone == upperR || bone == lowerR || bone == handR || bone == upperL || bone == lowerL || bone == handL)) continue;
                var difference = previousBones[bone].Inverse() * s.GetBonePoseRotation(bone).Normalized();
                largest = Mathf.Max(largest, 2 * Mathf.Atan2(new Vector3(difference.X, difference.Y, difference.Z).Length(), Mathf.Abs(difference.W)));
            }
            Rifle.GlobalTransform = rifle;
            RightTarget = right;
            LeftTarget = left;
            // Keep the firing muzzle fixed; ease the surrounding body when that pose approaches either arm's reach.
            if (largest <= limit && reachable) break;
            amount *= 0.5f;
        }
        reloadPhase = lastReloadPhase = releasingReload && fittedReload >= 1 ? 0 : fittedReload;
        if (releasingReload && fittedReload >= 1) meleeReloadForward = false;
        lastRifle = to * Rifle!.GlobalTransform;
        lastChest = s.GetBoneGlobalPose(spine[2]);
        aimBlend = lastFitAim = fittedAim;
    }

    private void CacheBones(Skeleton3D skeleton)
    {
        lastUpperParent = skeleton.GetBoneGlobalPose(skeleton.GetBoneParent(spine[0]));
        lastNeck = skeleton.GetBoneGlobalPose(neck).Origin;
        lastShoulderR = skeleton.GetBoneGlobalPose(upperR).Origin;
        lastShoulderL = skeleton.GetBoneGlobalPose(upperL).Origin;
        previousBones ??= new Quaternion[skeleton.GetBoneCount()];
        for (var i = 0; i < previousBones.Length; i++)
        {
            previousBones[i] = skeleton.GetBonePoseRotation(i).Normalized();
            previousPositions[i] = skeleton.GetBonePosePosition(i);
        }
    }

    private void LimitBone(Skeleton3D skeleton, int bone, float dt)
    {
        var target = skeleton.GetBonePoseRotation(bone).Normalized();
        var difference = previousBones![bone].Inverse() * target;
        var angle = 2 * Mathf.Atan2(new Vector3(difference.X, difference.Y, difference.Z).Length(), Mathf.Abs(difference.W));
        var limit = Mathf.DegToRad(World.Tuning.PoseAngularSpeed) * dt;
        if (angle > limit) skeleton.SetBonePoseRotation(bone, previousBones[bone].Slerp(target, limit / angle));
    }

    /// <summary>Two-bone neck/head fit to the authored eye socket. The head's desired gaze supplies the
    /// pole, so the cheek settles on the stock without translating or scaling either bone.</summary>
    private void FitEye(Skeleton3D skeleton, int neckBone, int headBone, Vector3 eyeTarget, Vector3 headPole, float weight)
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
    private Transform3D RightHandTransform(Transform3D rifle)
    {
        var t = World.Tuning;
        var local = new Transform3D(Basis.Identity, new Vector3(t.RifleRightWristSide, t.RifleRightWristUp, t.RifleRightWristBack));
        // +X is the back of the firing hand, +Y runs forward along the palm, +Z toward the thumb.
        local.Basis = new Basis(Vector3.Right, new Vector3(0, Mathf.Sin(Mathf.DegToRad(t.RifleRightWristPitchDegrees)), -Mathf.Cos(Mathf.DegToRad(t.RifleRightWristPitchDegrees))),
            new Vector3(0, Mathf.Cos(Mathf.DegToRad(t.RifleRightWristPitchDegrees)), Mathf.Sin(Mathf.DegToRad(t.RifleRightWristPitchDegrees))));
        return rifle * local;
    }

    private Transform3D LeftHandTransform(Transform3D rifle, Vector3 gripLocal, Basis gripBasis)
    {
        var t = World.Tuning;
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
        float reachR, float reachL, Vector3 leftGrip, Basis leftBasis, Vector3 leftStart = default, float leftWeight = 1f, Vector3? eyeCenter = null, float eyeReach = 0)
    {
        var offsetR = RightHandTransform(new Transform3D(rifle.Basis, Vector3.Zero)).Origin;
        var offsetL = LeftHandTransform(new Transform3D(rifle.Basis, Vector3.Zero), leftGrip, leftBasis).Origin;
        // The support hand leaves its get-up pose gradually. Express that blended wrist's reach as a
        // sphere for the rifle origin, so the handoff is reachable too without clamping an IK target.
        var leftCenter = (shoulderL - leftStart * (1 - leftWeight)) / leftWeight - offsetL;
        var eyeOffset = rifle.Basis * Rifle!.EyeLocal.Origin;
        for (var i = 0; i < 32; i++)
        {
            var correctionEye = eyeCenter is Vector3 center ? ProjectSphere(rifle.Origin, center - eyeOffset, eyeReach) - rifle.Origin : Vector3.Zero;
            rifle.Origin += correctionEye;
            var correctionR = ProjectSphere(rifle.Origin, shoulderR - offsetR, reachR) - rifle.Origin;
            rifle.Origin += correctionR;
            var correctionL = ProjectSphere(rifle.Origin, leftCenter, reachL / leftWeight) - rifle.Origin;
            rifle.Origin += correctionL;
            if (correctionEye.LengthSquared() + correctionR.LengthSquared() + correctionL.LengthSquared() < 1e-12f) break;
        }
        return rifle;
    }

    // Alignment and reach projection influence each other near the muzzle. Iterate the geometry to convergence,
    // retaining the calibrated contact pivot; these are solver tolerances, not shot or animation delays.
    private Transform3D AlignPlacement(Transform3D rifle, Vector3 target, Vector3 pivotLocal,
        Vector3 shoulderR, Vector3 shoulderL, float reachR, float reachL, Vector3? eyeCenter, float eyeReach,
        Vector3 leftStart = default, float leftWeight = 1)
    {
        for (var fit = 0; fit < 16; fit++)
        {
            var previous = rifle;
            var pivotWorld = rifle * pivotLocal;
            rifle = Rifle.AlignBarrel(rifle, Rifle!.MuzzleLocal.Origin, target);
            rifle.Origin += pivotWorld - rifle * pivotLocal;
            rifle = ReachablePlacement(rifle, shoulderR, shoulderL, reachR, reachL,
                Rifle.LeftHandLocal, Rifle.LeftGripBasis, leftStart, leftWeight, eyeCenter, eyeReach);
            if (rifle.IsEqualApprox(previous)) break;
        }
        var aligned = Rifle.AlignBarrel(rifle, Rifle!.MuzzleLocal.Origin, target);
        if (RightHandTransform(aligned).Origin.DistanceTo(shoulderR) <= reachR + 1e-6f &&
            leftStart.Lerp(LeftHandTransform(aligned, Rifle.LeftHandLocal, Rifle.LeftGripBasis).Origin, leftWeight).DistanceTo(shoulderL) <= reachL + 1e-6f)
        {
            return rifle;
        }
        // The pivot fit can oscillate when a close endpoint crosses the muzzle. Instead put the
        // endpoint on the positive barrel ray and intersect that ray's two wrist-reach intervals.
        // This fallback preserves the endpoint and both grips; the ordinary cheek/pivot fit above
        // remains unchanged. A delayed remote echo need not admit its observer's current cheek hold.
        foreach (var center in new[] { shoulderR, shoulderL, (shoulderR + shoulderL) * .5f })
        {
            var forward = (target - center).Normalized();
            if (forward.LengthSquared() < .5f) continue;
            var basis = new Basis(new Quaternion(-rifle.Basis.Z.Normalized(), forward)) * rifle.Basis;
            var atTarget = new Transform3D(basis, target - basis * Rifle.MuzzleLocal.Origin);
            var low = 0.0001f;
            var high = float.PositiveInfinity;
            var right = RightHandTransform(atTarget).Origin;
            var left = leftStart.Lerp(LeftHandTransform(atTarget, Rifle.LeftHandLocal, Rifle.LeftGripBasis).Origin, leftWeight);
            if (!ReachInterval(right - shoulderR, forward, reachR, ref low, ref high) ||
                !ReachInterval(left - shoulderL, forward * leftWeight, reachL, ref low, ref high))
            {
                continue;
            }
            // Use the interval interior: a muzzle almost on the endpoint magnifies float roundoff and
            // gives the next exact barrel correction no stable direction.
            var distance = (low + high) * .5f;
            atTarget.Origin -= forward * distance;
            return atTarget;
        }
        return rifle;
    }

    // offset - axis*t must remain inside a reach sphere. The same t places both wrists along the barrel.
    private static bool ReachInterval(Vector3 offset, Vector3 axis, float reach, ref float low, ref float high)
    {
        var lengthSquared = axis.LengthSquared();
        if (lengthSquared < 1e-12f) return offset.LengthSquared() <= reach * reach;
        var center = offset.Dot(axis) / lengthSquared;
        var perpendicular = offset - axis * center;
        var radiusSquared = (reach * reach - perpendicular.LengthSquared()) / lengthSquared;
        if (radiusSquared < 0) return false;
        var radius = Mathf.Sqrt(radiusSquared);
        low = Mathf.Max(low, center - radius);
        high = Mathf.Min(high, center + radius);
        return low <= high;
    }

    private static Vector3 ProjectSphere(Vector3 point, Vector3 center, float radius)
    {
        var offset = point - center;
        return offset.LengthSquared() > radius * radius ? center + offset.Normalized() * radius : point;
    }

    private void PoseFingers(Skeleton3D skeleton)
    {
        var t = World.Tuning;
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
                    angle = Mathf.Lerp(angle, trigger[j], Mathf.Clamp(Recoil, 0, 1));
                }
                var pose = skeleton.GetBoneRest(bone).Basis.GetRotationQuaternion() * new Quaternion(Vector3.Right, Mathf.DegToRad(angle));
                skeleton.SetBonePoseRotation(bone, skeleton.GetBonePoseRotation(bone).Normalized().Slerp(pose, Weight));
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
        upperBones = new[] { spine[0], spine[1], spine[2], skeleton.FindBone("clavicle_l"), clavicleR, neck, head, upperR, lowerR, handR, upperL, lowerL, handL };
        desiredBones = new Quaternion[skeleton.GetBoneCount()];
        previousPositions = new Vector3[skeleton.GetBoneCount()];
        desiredPositions = new Vector3[skeleton.GetBoneCount()];
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

    /// <summary>How far past full reach a wrist target may round and still be solved as a straight arm (meters).</summary>
    private const float StraightArmTolerance = 0.0001f;

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
        // A target placed exactly at full reach (a get-up handoff starts with a straight arm and no margin) can land a
        // rounding error past it: that is a straight arm, not a miss.
        if (distance >= l1 + l2 && distance < l1 + l2 + StraightArmTolerance) distance = l1 + l2 - 0.000001f;
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
