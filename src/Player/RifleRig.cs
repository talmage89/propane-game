using Godot;
using Propane.Core;

namespace Propane.Player;

/// <summary>
/// The rifle stance, layered over the locomotion clips:
/// 1. twists the spine toward the aim and spreads the aim pitch through the spine, neck and head;
/// 2. places the rifle in one of its authored stances, hung from the chest and right shoulder: a relaxed carry,
///    the hip ready, shouldered with the head down at the sights, the sprint port, the reload and the butt strike,
///    blended by state, plus a spring recoil;
/// 3. puts both hands on the rifle with two-bone IK and curls the fingers around its grips.
/// It never decides anything: shots come from the logical aim, whatever this pose shows.
/// </summary>
public partial class RifleRig : SkeletonModifier3D
{
    public required WorldContext World { get; init; }

    private int[] spine = null!;
    private int neck, head;
    private readonly int[,] fingers = new int[10, 3];
    private int upperR, lowerR, handR;
    private int upperL, lowerL, handL;
    private bool resolved;
    private float aimTargetAmount, readyTargetAmount, sprintTargetAmount;
    private float aimBlend, readyBlend, sprintBlend;
    private readonly RecoilSpring recoil = new();
    private float reloadTarget, reloadPhase;
    private bool meleeReloadForward, hadMelee;
    private float reloadBlend, grip;
    private Vector3 lookDirection;
    private float torsoYaw;
    private bool hasTorsoYaw;

    /// <summary>Wrist targets of the last pose, for contact diagnostics.</summary>
    public Transform3D RightTarget { get; private set; }
    public Transform3D LeftTarget { get; private set; }

    /// <summary>Right-eye landmark in the mannequin head frame (meters).</summary>
    public static Vector3 EyePosition(Skeleton3D skeleton, Tuning tuning) =>
        skeleton.GlobalTransform * (skeleton.GetBoneGlobalPose(skeleton.FindBone("Head")) * EyeLandmark(tuning));

    private static Vector3 EyeLandmark(Tuning t) => new(t.RifleEyeSide, t.RifleEyeUp, t.RifleEyeForward);

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
        // A strike cancels the reload: an early one retraces the hand to the foregrip, a late one finishes it.
        if (melee)
        {
            if (!hadMelee) meleeReloadForward = reloadPhase > 0.5f;
            hadMelee = true;
            reloadPhase = Mathf.MoveToward(reloadPhase, meleeReloadForward ? 1 : 0, dt * World.Tuning.MeleeReloadReturnSpeed);
            if (reloadPhase >= 1) reloadPhase = 0;
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
        using var profile = ActorProfile.Measure(ActorProfile.System.RiflePose);
        var skeleton = GetSkeleton();
        if (skeleton == null || Rifle == null)
        {
            return;
        }
        if (!resolved)
        {
            Resolve(skeleton);
        }
        var dt = (float)delta;
        var t = World.Tuning;
        // Logical aim may clear in one tick (release, jump, sprint or reload). The character keeps that
        // decision; the stance eases over a few frames, so a hip shot reads as a fast raise, never a snap.
        var blendStep = t.RifleStanceBlendSpeed * dt;
        aimBlend = Mathf.MoveToward(aimBlend, aimTargetAmount, blendStep);
        readyBlend = Mathf.MoveToward(readyBlend, readyTargetAmount, blendStep);
        sprintBlend = Mathf.MoveToward(sprintBlend, sprintTargetAmount, blendStep);
        recoil.Update(dt, t.RifleRecoilFrequency, t.RifleRecoilDamping);
        if (Weight <= 0.001f)
        {
            // Ragdolling or getting up: RifleHandFollow keeps the rifle in the right hand. The fingers let go
            // over a moment rather than snapping open as the body is thrown.
            recoil.Reset();
            reloadBlend = 0;
            lookDirection = Vector3.Zero;
            hasTorsoYaw = false;
            Rifle.Animate(dt, 0);
            grip = Mathf.MoveToward(grip, 0, blendStep);
            if (grip > 0) PoseFingers(skeleton, grip);
            return;
        }
        Rifle.PreviewReloadContact(Reload);
        var from = skeleton.GlobalTransform;
        var to = from.AffineInverse();
        var aim = Mathf.SmoothStep(0, 1, AimAmount);
        var sprint = Mathf.SmoothStep(0, 1, SprintAmount);
        var strike = Melee ? MeleeStrike.Sample(t, MeleeProgress) : default;

        // The model faces +Z in skeleton space.
        var forward = from.Basis * Vector3.Back;
        forward = new Vector3(forward.X, 0, forward.Z).Normalized();
        var right = forward.Cross(Vector3.Up).Normalized();
        var chestWorld = from * skeleton.GetBoneGlobalPose(spine[2]).Origin;
        // The pose follows the aim at a bounded rate, so a mouse flick swings the body over a few frames instead of
        // one. Only the shown pose eases; logical aim and shots stay immediate.
        var aimDir = (AimTarget - chestWorld).Normalized();
        if (lookDirection.LengthSquared() > 0.5f)
        {
            var between = lookDirection.AngleTo(aimDir);
            var step = Mathf.DegToRad(t.RifleAimTurnSpeed) * dt;
            if (between > step) aimDir = lookDirection.Slerp(aimDir, step / between).Normalized();
        }
        lookDirection = aimDir;
        var target = chestWorld + aimDir * Mathf.Max(AimTarget.DistanceTo(chestWorld), 1f);
        var flatAim = new Vector3(aimDir.X, 0, aimDir.Z);
        flatAim = flatAim.LengthSquared() > 0.0001f ? flatAim.Normalized() : forward;
        var aimRight = flatAim.Cross(Vector3.Up).Normalized();
        var kick = recoil.Position;

        // 1. Torso: turn to the aim, bladed so the left shoulder comes forward to the foregrip, and pitch
        // part of the way with it. The neck and head take the rest of the pitch below.
        var blade = Mathf.DegToRad(Mathf.Lerp(t.RifleHipBladeDegrees, t.RifleAimBladeDegrees, aim)) * (1 - sprint);
        // A relaxed player standing still can look anywhere; past the spine's comfortable twist the body stays put.
        var limit = Mathf.DegToRad(t.CombatSpineTwistDegrees);
        var yaw = Mathf.Clamp(forward.SignedAngleTo(flatAim, Vector3.Up), -limit, limit) - blade;
        yaw = Mathf.Lerp(yaw, Mathf.DegToRad(strike.TorsoYaw), strike.Weight);
        // Crossing the rear direction changes SignedAngleTo from +pi to -pi. Clamping can then switch
        // twist limits even when aimDir moved only a fraction of a degree (especially after a get-up).
        // Bound the applied torso turn with the same tuner as the visual aim; logical aim stays immediate.
        torsoYaw = hasTorsoYaw ? Mathf.MoveToward(torsoYaw, yaw, Mathf.DegToRad(t.RifleAimTurnSpeed) * dt) : yaw;
        hasTorsoYaw = true;
        yaw = torsoYaw;
        var pitch = Mathf.Asin(Mathf.Clamp(aimDir.Y, -1, 1));
        var pitchShare = Mathf.Lerp(t.RifleHipPitchShare, t.RifleAimPitchShare, aim) * (1 - sprint * t.RifleSprintPitchSuppression);
        // Positive pitches the chest up: a shot rocks the shoulder back with the climbing muzzle.
        var torsoPitch = pitch * pitchShare * (1 - strike.Weight) - Mathf.DegToRad(strike.TorsoLean)
            + Mathf.DegToRad(t.RifleRecoilLiftDegrees) * kick * t.RifleRecoilTorsoShare;
        var torso = new Quaternion(aimRight, torsoPitch) * new Quaternion(Vector3.Up, yaw);
        var shares = new Vector3(t.RifleSpineLowerShare, t.RifleSpineMiddleShare, t.RifleSpineUpperShare);
        shares /= Mathf.Max(shares.X + shares.Y + shares.Z, 0.0001f);
        for (var k = 0; k < spine.Length; k++)
        {
            // Distribute one world rotation; composing partial yaw/pitch about different axes would bend the
            // upper body sideways when a strafing player aims near the pitch limits.
            RotateBoneWorld(skeleton, spine[k], Quaternion.Identity.Slerp(torso, shares[k] * Weight), from, to);
        }

        // 2. Head: the neck and head turn the rest of the way toward the aim, more so when shouldered.
        var follow = Mathf.Lerp(t.RifleHipHeadFollow, t.RifleAimHeadFollow, aim) * (1 - strike.Weight * 0.5f) * Weight;
        var headPose = from * skeleton.GetBoneGlobalPose(head);
        var turn = new Quaternion((headPose.Basis * Vector3.Back).Normalized(), (target - headPose.Origin).Normalized());
        RotateBoneWorld(skeleton, neck, Quaternion.Identity.Slerp(turn, follow * t.RifleNeckShare), from, to);
        LookAt(skeleton, head, target, follow, from, to);

        // 3. Rifle stances, hung from a stable chest frame: the body's yaw plus the torso twist, and world up.
        // The animated chest bone swings with the stride; only the shoulder position carries the clip's motion.
        var shoulder = from * skeleton.GetBoneGlobalPose(upperR).Origin;
        var chestForward = forward.Rotated(Vector3.Up, yaw * Weight).Normalized();
        var chestRight = chestForward.Cross(Vector3.Up).Normalized();
        var up = Vector3.Up;

        // Shouldered: butt in the pocket inside the shoulder joint, the sight line through the aim point.
        var pocket = shoulder - chestRight * t.RifleShoulderInboard + flatAim * t.RifleShoulderForward + up * t.RifleShoulderUp;
        var sightHeight = Rifle.EyeLocal.Origin.Y - Rifle.ButtLocal.Origin.Y;
        var aimBasis = Basis.LookingAt((target - (pocket + up * sightHeight)).Normalized(), Vector3.Up);
        var aimed = new Transform3D(aimBasis, pocket - aimBasis * Rifle.ButtLocal.Origin);

        // Hip ready: stock tucked under the arm against the ribs, muzzle on the target.
        var hipPocket = shoulder + chestRight * t.RifleHipOutboard - up * t.RifleHipDown + chestForward * t.RifleHipForward;
        var hipDir = (target - (shoulder - up * t.RifleHipSightReferenceDown)).Normalized();
        var hipBasis = Basis.LookingAt(hipDir, Vector3.Up).Rotated(hipDir, Mathf.DegToRad(t.RifleReadyRollDegrees));
        var hip = new Transform3D(hipBasis, hipPocket - hipBasis * Rifle.ButtLocal.Origin);
        // Relaxed carry between shots: muzzle dipped and angled across the body.
        var relaxedDir = CarryDirection(chestForward, chestRight, t.RifleCarryPitchDegrees, t.RifleCarryYawDegrees);
        var relaxedBasis = Basis.LookingAt(relaxedDir, Vector3.Up).Rotated(relaxedDir, Mathf.DegToRad(t.RifleCarryRollDegrees));
        var relaxed = new Transform3D(relaxedBasis, hipPocket - relaxedBasis * Rifle.ButtLocal.Origin);
        hip = Blend(relaxed, hip, Mathf.SmoothStep(0, 1, Mathf.Max(ReadyAmount, AimAmount)));

        // Sprint: low port, muzzle down and slightly inboard, stock still tucked.
        var lowDir = CarryDirection(chestForward, chestRight, t.RifleSprintPitchDegrees, t.RifleSprintYawDegrees);
        var lowBasis = Basis.LookingAt(lowDir, Vector3.Up);
        var port = new Transform3D(lowBasis, hipPocket + up * (t.RifleHipDown - t.RifleSprintDown) - lowBasis * Rifle.ButtLocal.Origin);

        var stance = Blend(hip, aimed, aim);
        stance = Blend(stance, port, sprint);

        // Reload: the rifle comes in front of the chest, canted, with the magazine well where the left hand works.
        // Eased like the other stances: a strike that cancels an early reload swings the rifle out over a few frames.
        var reloadPose = Reload > 0 ? Mathf.SmoothStep(0, t.RifleReloadBlendFraction, Reload) * (1f - Mathf.SmoothStep(1f - t.RifleReloadBlendFraction, 1f, Reload)) : 0f;
        var reloadWeight = reloadBlend = Mathf.MoveToward(reloadBlend, reloadPose, blendStep);
        if (reloadWeight > 0)
        {
            var reloadDir = CarryDirection(chestForward, chestRight, t.RifleReloadPitchDegrees, t.RifleReloadYawDegrees);
            var reloadBasis = Basis.LookingAt(reloadDir, Vector3.Up).Rotated(reloadDir, Mathf.DegToRad(t.RifleReloadRollDegrees));
            var shoulderL = from * skeleton.GetBoneGlobalPose(upperL).Origin;
            var well = shoulder.Lerp(shoulderL, 0.5f) + chestForward * t.RifleReloadForward - up * t.RifleReloadDown;
            stance = Blend(stance, new Transform3D(reloadBasis, well - reloadBasis * Rifle.MagWellLocal.Origin), reloadWeight);
        }

        // Butt strike: the rifle pivots near the support hand while the stock swings; keys are in the twisted chest frame.
        if (strike.Weight > 0)
        {
            var dir = CarryDirection(chestForward, chestRight, -strike.Pitch, strike.Heading);
            var basis = Basis.LookingAt(dir, Vector3.Up).Rotated(dir, Mathf.DegToRad(strike.Roll));
            var foregrip = shoulder + chestRight * strike.Foregrip.X + up * strike.Foregrip.Y + chestForward * strike.Foregrip.Z;
            stance = Blend(stance, new Transform3D(basis, foregrip - basis * Rifle.ForegripLocal.Origin), strike.Weight);
        }

        // 4. Cheek to the stock: drop the head toward the sight rather than raising the rifle to the eye.
        if (aim * Weight > 0.001f && reloadWeight < 1 && strike.Weight < 1)
        {
            // Toward where the sight sits once shouldered: steady while the rifle swings up from the hip or port.
            var sight = aimed * Rifle.EyeLocal.Origin;
            var weld = aim * t.RifleCheekWeld * (1 - reloadWeight) * (1 - strike.Weight) * Weight;
            FitEye(skeleton, to * sight, weld, t);
            var gaze = (target - sight).Normalized();
            RotateBoneWorld(skeleton, head, new Quaternion(gaze, Mathf.DegToRad(t.RifleCheekRollDegrees) * weld), from, to);
        }

        // Recoil: the stock comes straight back and the muzzle climbs, about the butt.
        var pivot = stance * Rifle.ButtLocal.Origin;
        var lift = new Basis(stance.Basis.Column0.Normalized(), Mathf.DegToRad(t.RifleRecoilLiftDegrees) * kick * (1 - t.RifleRecoilTorsoShare));
        stance = new Transform3D(lift * stance.Basis, pivot + lift * (stance.Origin - pivot)
            + stance.Basis.Column2.Normalized() * t.RifleRecoilBack * kick).Orthonormalized();

        // A stance can hold a grip a little beyond its arm (a long reach to the foregrip, say): slide the rifle
        // toward that shoulder by the shortfall, so the hand closes on the grip instead of stopping short.
        var upperRWorld = from * skeleton.GetBoneGlobalPose(upperR).Origin;
        var upperLWorld = from * skeleton.GetBoneGlobalPose(upperL).Origin;
        stance.Origin += Shortfall(upperLWorld, LeftHandTransform(stance, Rifle.LeftHandLocal, Rifle.LeftGripBasis).Origin, ArmLength(skeleton, upperL, lowerL, handL));
        stance.Origin += Shortfall(upperRWorld, RightHandTransform(stance).Origin, ArmLength(skeleton, upperR, lowerR, handR));

        // While the rig fades in at the end of a get-up, hand the rifle over from wherever the hand already is.
        var handNow = (from * skeleton.GetBoneGlobalPose(handR)).Orthonormalized();
        var leftNow = (from * skeleton.GetBoneGlobalPose(handL)).Orthonormalized();
        if (Weight < 0.999f)
        {
            stance = (handNow * RightHandInRifle.AffineInverse()).InterpolateWith(stance, Mathf.SmoothStep(0, 1, Weight));
        }
        Rifle.GlobalTransform = stance;

        // 5. Hands onto the grips: the pistol grip, and the foregrip or, mid-reload, the magazine and bolt catch.
        RightTarget = handNow.InterpolateWith(RightHandTransform(stance), Weight);
        LeftTarget = leftNow.InterpolateWith(LeftHandTransform(stance, Rifle.LeftHandLocal, Rifle.LeftGripBasis), Weight);
        // Elbows down, out and a little back: the back share keeps the bend defined when a hand drops by the hip,
        // where down-and-out alone would line up with the arm. It vanishes from the bend when a hand reaches forward.
        var poleR = upperRWorld + chestRight * t.RifleElbowOut - up * t.RifleElbowDown - chestForward * t.RifleElbowBack;
        var poleL = upperLWorld - chestRight * t.RifleElbowOut - up * t.RifleElbowDown - chestForward * t.RifleElbowBack;
        SolveTwoBone(skeleton, upperR, lowerR, handR, to * RightTarget, to * poleR, Weight);
        SolveTwoBone(skeleton, upperL, lowerL, handL, to * LeftTarget, to * poleL, Weight);
        Rifle.Animate(dt, Reload);
        grip = Weight;
        PoseFingers(skeleton, grip);
    }

    /// <summary>Blends two rifle placements about the butt, so the stock stays where both stances hold it and the
    /// rifle swings around it as a person raises one, instead of the pistol grip cutting a straight line.</summary>
    private Transform3D Blend(Transform3D from, Transform3D to, float weight)
    {
        if (weight <= 0) return from;
        if (weight >= 1) return to;
        var butt = Rifle!.ButtLocal.Origin;
        var basis = new Basis(from.Basis.GetRotationQuaternion().Slerp(to.Basis.GetRotationQuaternion(), weight));
        return new Transform3D(basis, (from * butt).Lerp(to * butt, weight) - basis * butt);
    }

    /// <summary>How far to move a grip toward its shoulder so a two-bone arm, slightly bent, reaches it.</summary>
    private static Vector3 Shortfall(Vector3 shoulder, Vector3 grip, float reach)
    {
        var offset = shoulder - grip;
        var excess = offset.Length() - reach * 0.98f;
        return excess > 0 ? offset.Normalized() * excess : Vector3.Zero;
    }

    private static float ArmLength(Skeleton3D s, int upper, int lower, int hand) =>
        s.GetBoneGlobalPose(upper).Origin.DistanceTo(s.GetBoneGlobalPose(lower).Origin) +
        s.GetBoneGlobalPose(lower).Origin.DistanceTo(s.GetBoneGlobalPose(hand).Origin);

    /// <summary>Turns a bone so its forward (+Z) moves toward <paramref name="target"/>, by a fraction.</summary>
    private static void LookAt(Skeleton3D skeleton, int bone, Vector3 target, float weight, Transform3D from, Transform3D to)
    {
        if (weight <= 0.0001f) return;
        var pose = from * skeleton.GetBoneGlobalPose(bone);
        var current = (pose.Basis * Vector3.Back).Normalized();
        var wanted = (target - pose.Origin).Normalized();
        RotateBoneWorld(skeleton, bone, Quaternion.Identity.Slerp(new Quaternion(current, wanted), weight), from, to);
    }

    /// <summary>Two-bone neck/head turn that brings the eye landmark toward the sight, without translating or
    /// scaling either bone. A partial weight leaves the eye above the stock, never through it.</summary>
    private void FitEye(Skeleton3D skeleton, Vector3 eyeTarget, float weight, Tuning t)
    {
        var landmark = EyeLandmark(t);
        var a = skeleton.GetBoneGlobalPose(neck).Origin;
        var b = skeleton.GetBoneGlobalPose(head).Origin;
        var c = skeleton.GetBoneGlobalPose(head) * landmark;
        var l1 = a.DistanceTo(b);
        var l2 = b.DistanceTo(c);
        var offset = eyeTarget - a;
        // Never fold the chain flat: a rising rifle passes its sight close by the neck.
        var distance = Mathf.Clamp(offset.Length(), Mathf.Abs(l1 - l2) + 0.02f, l1 + l2 - 0.001f);
        var direction = offset.Normalized();
        var along = (l1 * l1 - l2 * l2 + distance * distance) / (2 * distance);
        var height = Mathf.Sqrt(Mathf.Max(0, l1 * l1 - along * along));
        // Keep the head's current bend. Where the sight lines up with the neck that side vanishes, and a little
        // of the face's forward keeps the pole from flipping through it.
        var forwardSide = skeleton.GetBoneGlobalPose(head).Basis * Vector3.Back;
        var pole = (b - a) - direction * direction.Dot(b - a);
        pole += (forwardSide - direction * direction.Dot(forwardSide)) * 0.02f;
        pole = pole.Normalized();
        var headPosition = a + direction * along + pole * height;
        RotateBone(skeleton, neck, new Basis(Quaternion.Identity.Slerp(new Quaternion((b - a).Normalized(), (headPosition - a).Normalized()), weight)));
        b = skeleton.GetBoneGlobalPose(head).Origin;
        c = skeleton.GetBoneGlobalPose(head) * landmark;
        var reach = a + direction * distance;
        RotateBone(skeleton, head, new Basis(Quaternion.Identity.Slerp(new Quaternion((c - b).Normalized(), (reach - b).Normalized()), weight)));
    }

    private static Vector3 CarryDirection(Vector3 forward, Vector3 right, float pitch, float yaw) =>
        forward.Rotated(Vector3.Up, Mathf.DegToRad(yaw)).Rotated(right, -Mathf.DegToRad(pitch)).Normalized();

    /// <summary>Wrist frames fitted to the mannequin palm and the rifle's grip geometry.</summary>
    private Transform3D RightHandTransform(Transform3D rifle)
    {
        var t = World.Tuning;
        var angle = Mathf.DegToRad(t.RifleRightWristPitchDegrees);
        // +X is the back of the firing hand, +Y runs forward along the palm, +Z toward the thumb.
        var local = new Transform3D(new Basis(Vector3.Right, new Vector3(0, Mathf.Sin(angle), -Mathf.Cos(angle)), new Vector3(0, Mathf.Cos(angle), Mathf.Sin(angle))),
            new Vector3(t.RifleRightWristSide, t.RifleRightWristUp, t.RifleRightWristBack));
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
        return rifle * new Transform3D(gripBasis, gripLocal) * new Transform3D(wristBasis, new Vector3(-side, up, back));
    }

    private void PoseFingers(Skeleton3D skeleton, float weight)
    {
        var t = World.Tuning;
        // Each local X is the finger's flexion axis after its authored rest orientation. Set absolute curls:
        // inheriting the pistol clip's rotations crossed the fingers through the handguard and receiver.
        for (var f = 0; f < 10; f++)
        {
            var curls = FingerCurls(t, f, false);
            var magazineCurls = FingerCurls(t, f, true);
            for (var j = 0; j < 3; j++)
            {
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
                        angle = Mathf.Lerp(angle, transit[j], Rifle.BoltHold);
                    }
                    else
                    {
                        angle = Mathf.Lerp(angle, magazineCurls[j], Rifle.MagazineHold) * Rifle.SupportHold;
                    }
                }
                if (f == 5)
                {
                    var trigger = new Vector3(t.RifleTriggerIndex1Degrees, t.RifleTriggerIndex2Degrees, t.RifleTriggerIndex3Degrees);
                    angle = Mathf.Lerp(angle, trigger[j], Mathf.Clamp(Recoil, 0, 1));
                }
                var bone = fingers[f, j];
                var pose = skeleton.GetBoneRest(bone).Basis.GetRotationQuaternion() * new Quaternion(Vector3.Right, Mathf.DegToRad(angle));
                skeleton.SetBonePoseRotation(bone, skeleton.GetBonePoseRotation(bone).Normalized().Slerp(pose, weight));
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
        SetBoneGlobalBasis(skeleton, bone, skeletonRotation * global.Basis.Orthonormalized());
    }

    private static void SetBoneGlobalBasis(Skeleton3D skeleton, int bone, Basis globalBasis)
    {
        var parent = skeleton.GetBoneParent(bone);
        var parentBasis = parent >= 0 ? skeleton.GetBoneGlobalPose(parent).Basis.Orthonormalized() : Basis.Identity;
        skeleton.SetBonePoseRotation(bone, (parentBasis.Inverse() * globalBasis).Orthonormalized().GetRotationQuaternion());
    }

    /// <summary>Analytic two-bone IK in skeleton space, with a pole point for the elbow. Each bone's frame is built
    /// from the bend plane (the mannequin's elbow hinges about its local X, bending toward local +Z), so the arm's
    /// twist follows the elbow instead of the shortest turn from the clip's pose, which flips as a strike crosses the
    /// body. The wrist takes the hand's twist: splitting it with the forearm jumps a half turn wherever the bent wrist
    /// lines the hand's axis up with the forearm. A target beyond reach straightens the arm toward it.</summary>
    private static void SolveTwoBone(Skeleton3D skeleton, int upper, int lower, int end, Transform3D target, Vector3 pole, float weight = 1)
    {
        var fromUpper = skeleton.GetBoneGlobalPose(upper).Basis.GetRotationQuaternion();
        var fromLower = skeleton.GetBoneGlobalPose(lower).Basis.GetRotationQuaternion();
        var a = skeleton.GetBoneGlobalPose(upper).Origin;
        var l1 = a.DistanceTo(skeleton.GetBoneGlobalPose(lower).Origin);
        var l2 = skeleton.GetBoneGlobalPose(lower).Origin.DistanceTo(skeleton.GetBoneGlobalPose(end).Origin);
        var toTarget = target.Origin - a;
        var distance = Mathf.Clamp(toTarget.Length(), Mathf.Abs(l1 - l2) + 0.001f, (l1 + l2) * 0.9999f);
        var dir = toTarget.Normalized();
        var along = (l1 * l1 - l2 * l2 + distance * distance) / (2f * distance);
        var height = Mathf.Sqrt(Mathf.Max(0, l1 * l1 - along * along));
        // Fading in, the elbow starts where the clip has it and moves to the pole's side with the weight.
        if (weight < 0.999f) pole = skeleton.GetBoneGlobalPose(lower).Origin.Lerp(pole, weight);
        var poleDir = pole - a;
        poleDir = (poleDir - dir * dir.Dot(poleDir)).Normalized();
        var elbow = a + dir * along + poleDir * height;
        var wrist = a + dir * distance;

        var upperY = (elbow - a).Normalized();
        // The forearm bends away from the pole side; that bend direction is the upper arm's +Z.
        var upperZ = -(poleDir - upperY * upperY.Dot(poleDir)).Normalized();
        var hinge = upperY.Cross(upperZ);
        SetBoneGlobalBasis(skeleton, upper, new Basis(hinge, upperY, upperZ));
        var lowerY = (wrist - elbow).Normalized();
        var lowerBasis = new Basis(hinge, lowerY, hinge.Cross(lowerY));
        SetBoneGlobalBasis(skeleton, lower, lowerBasis);
        if (weight < 0.999f)
        {
            // Fading in (the end of a get-up): start from the arm the clip shows, then close the wrist onto its
            // target by the shortest turns, which barely move an arm that is already nearly there.
            SetBoneGlobalBasis(skeleton, upper, new Basis(fromUpper.Slerp(new Basis(hinge, upperY, upperZ).GetRotationQuaternion(), weight)));
            SetBoneGlobalBasis(skeleton, lower, new Basis(fromLower.Slerp(lowerBasis.GetRotationQuaternion(), weight)));
            var b = skeleton.GetBoneGlobalPose(lower).Origin;
            RotateBone(skeleton, upper, new Basis(new Quaternion((b - a).Normalized(), (elbow - a).Normalized())));
            b = skeleton.GetBoneGlobalPose(lower).Origin;
            var c = skeleton.GetBoneGlobalPose(end).Origin;
            RotateBone(skeleton, lower, new Basis(new Quaternion((c - b).Normalized(), (wrist - b).Normalized())));
        }
        SetBoneGlobalBasis(skeleton, end, target.Basis.Orthonormalized());
    }
}
