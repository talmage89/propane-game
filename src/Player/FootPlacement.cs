using System;
using Godot;
using Propane.Core;

namespace Propane.Player;

/// <summary>World-space stance contacts with procedural swings in actual travel direction. Runs before the
/// rifle rig: bounded pelvis correction moves the torso and both arms together. No controller/collider changes.</summary>
public partial class FootPlacement : SkeletonModifier3D
{
    public PlayerCharacter Player { get; init; } = null!;
    public LocomotionPose Pose { get; init; } = null!;
    public bool LeftPlanted => legs[0].Planted && weight >= 0.999f && resetTime <= 0;
    public bool RightPlanted => legs[1].Planted && weight >= 0.999f && resetTime <= 0;
    public Vector3 LeftAnchor => legs[0].Target.Origin;
    public Vector3 RightAnchor => legs[1].Target.Origin;
    public float ReachError { get; private set; }
    public float PelvisOffset => terrainOffset + pelvisOffset;

    private sealed class Leg
    {
        public int Thigh, Calf, Foot;
        public Transform3D Target, Start, Goal;
        public Basis RestBasis, ThighBasis, CalfBasis;
        public Vector3 UpperDirection, LowerDirection;
        public float Length;
        public bool Planted, Swinging;
        public float Swing, ProgressStart, Contact;
        public bool Adjusting, PreviousContact;
        public Vector3 RootStart, GoalRoot;
    }

    private readonly Leg[] legs = { new(), new() };
    private int pelvis = -1;
    private bool initialized;
    private Vector3 previousRoot;
    private Vector3 motionRoot;
    private Vector3 motionVelocity;
    private bool hasMotion;
    private float pelvisOffset;
    private float groundHeight;
    private float terrainOffset;
    private float weight;
    private Vector3 previousVelocity;
    private Vector3 lean;
    private int turning = -1;
    private bool wasGrounded = true;
    private bool inAir;
    private bool wasMoving;
    private Vector3 stepLead;
    private int spine = -1;
    private readonly Quaternion[] lastLegRotations = new Quaternion[6];
    private Vector3 lastPelvisPosition;
    private readonly Quaternion[] resetLegRotations = new Quaternion[6];
    private Vector3 resetPelvisPosition;
    private float resetTime;
    private bool hasPose;

    /// <summary>Discard contacts. Unexpected motion snaps can fade the previous joint pose into the new solve.</summary>
    public void Reset(bool blendPose = false)
    {
        resetTime = blendPose && hasPose ? Tuning.Current.LocomotionSettleTime : 0;
        if (resetTime > 0)
        {
            Array.Copy(lastLegRotations, resetLegRotations, lastLegRotations.Length);
            resetPelvisPosition = lastPelvisPosition;
        }
        if (!blendPose) hasPose = false;
        initialized = hasMotion = false;
        weight = pelvisOffset = terrainOffset = 0;
        previousVelocity = lean = Vector3.Zero;
        turning = -1;
        wasGrounded = true;
        inAir = wasMoving = false;
        stepLead = Vector3.Zero;
        foreach (var leg in legs) { leg.Planted = leg.Swinging = false; leg.Swing = 0; }
    }

    /// <summary>Remote clip phase follows displayed displacement, including interpolation stalls and corrections.
    /// Local animation follows the controller velocity. Both contact placement and clip clocks use this sample.</summary>
    public Vector3 SamplePlanarVelocity(float dt)
    {
        var root = Player.GlobalPosition;
        var displacement = root - motionRoot;
        motionVelocity = Player.IsRemote ? hasMotion && displacement.Length() < Tuning.Current.FootResetDistance
            ? displacement / Mathf.Max(dt, 0.00001f) : Vector3.Zero : Player.Velocity;
        motionVelocity.Y = 0;
        motionRoot = root;
        hasMotion = true;
        return motionVelocity;
    }

    public override void _ProcessModificationWithDelta(double delta)
    {
        ReachError = 0;
        var s = GetSkeleton();
        if (s == null || delta <= 0) return;
        var dt = (float)delta;
        var t = Tuning.Current;
        if (!Player.ResolvedState.Active) { Reset(); return; }
        var root = Player.GlobalPosition;
        if (initialized && root.DistanceTo(previousRoot) > t.FootResetDistance) Reset(true);
        var velocity = motionVelocity;
        var speed = velocity.Length();
        var moving = speed >= Mathf.Max(t.AnimationIdleSpeed, 0.001f);
        var facing = new Basis(Vector3.Up, Player.FacingYaw);
        if (!initialized)
        {
            Resolve(s);
            for (var i = 0; i < 2; i++)
            {
                var leg = legs[i];
                leg.Target = GroundTarget(s, i, root, facing, Vector3.Zero);
                leg.Start = leg.Target;
                leg.Planted = true;
                leg.Contact = Pose.Contact;
            }
            previousRoot = root;
            groundHeight = root.Y;
            initialized = true;
        }
        previousRoot = root;
        if (!Player.ResolvedState.Grounded)
        {
            inAir = true;
            wasMoving = false;
            weight = Mathf.MoveToward(weight, 0, dt / t.TakeoffPoseTime);
            foreach (var leg in legs) { leg.Planted = false; leg.Swinging = false; }
            if (weight > 0 && wasGrounded) BlendAir(s, weight);
            turning = -1;
            if (weight <= 0)
            {
                wasGrounded = false;
                BlendReset(s, dt);
                CapturePose(s);
            }
            return;
        }
        if (inAir)
        {
            foreach (var leg in legs) { leg.Planted = false; leg.Swinging = false; }
            inAir = false;
            groundHeight = root.Y;
        }
        wasGrounded = true;
        weight = Mathf.MoveToward(weight, 1, dt / t.LocomotionSettleTime);
        if (moving != wasMoving)
        {
            foreach (var leg in legs)
                if (leg.Swinging) BeginSwing(leg, root, 0, true);
        }
        wasMoving = moving;
        var desiredLead = moving ? velocity / speed * Mathf.Min(Pose.Stride * Pose.Contact * 0.5f, t.FootContactRadius) : Vector3.Zero;
        stepLead = stepLead.Lerp(desiredLead, 1 - Mathf.Exp(-t.FootStepTargetSpeed * dt));
        if (!moving && turning < 0)
        {
            var worst = 1f;
            for (var i = 0; i < 2; i++)
            {
                if (legs[i].Swinging) continue;
                var nominal = GroundTarget(s, i, root, facing, Vector3.Zero);
                var difference = legs[i].Target.Basis.GetRotationQuaternion().Inverse() * nominal.Basis.GetRotationQuaternion();
                var angle = 2 * Mathf.Atan2(new Vector3(difference.X, difference.Y, difference.Z).Length(), Mathf.Abs(difference.W));
                var correction = Mathf.Max(angle / Mathf.DegToRad(t.TurnStepDegrees), legs[i].Target.Origin.DistanceTo(nominal.Origin) / t.FootSettleDistance);
                if (correction > worst) { worst = correction; turning = i; }
            }
            if (turning >= 0) BeginSwing(legs[turning], root, 0, true);
        }
        for (var i = 0; i < 2; i++)
        {
            var leg = legs[i];
            var phase = Mathf.PosMod(Pose.Phase + i * 0.5f, 1);
            var contact = phase < leg.Contact;
            var target = GroundTarget(s, i, root, facing, moving ? stepLead : Vector3.Zero);
            if (!leg.Planted && !leg.Swinging)
            {
                // Spawn, landing or loss of support: old world anchors are no longer valid.
                leg.Target = target;
                leg.Planted = true;
                leg.Contact = Pose.Contact;
                contact = phase < leg.Contact;
                leg.PreviousContact = true;
            }
            if (moving)
            {
                var offset = leg.Target.Origin - root;
                offset.Y = 0;
                var future = offset - velocity * dt;
                var overreach = offset.Dot(velocity) < 0 && future.Length() > t.FootContactRadius;
                if (leg.Planted && overreach)
                {
                    // Starts, reversals and changed stride lengths can exhaust reach before the scheduled
                    // lift-off. Release into a real step; never slide the saved anchor toward the body.
                    BeginSwing(leg, root, 0, true);
                }
                else if (leg.Planted && !contact && leg.PreviousContact)
                {
                    BeginSwing(leg, root, (phase - leg.Contact) / (1 - leg.Contact));
                }
                if (leg.Swinging && !leg.Adjusting)
                {
                    if (contact)
                    {
                        Plant(leg, SupportedGoal(leg), Pose.Contact);
                    }
                    else
                    {
                        var progress = (phase - leg.Contact) / (1 - leg.Contact);
                        leg.Swing = Mathf.Clamp((progress - leg.ProgressStart) / Mathf.Max(1 - leg.ProgressStart, 0.001f), 0, 1);
                        Swing(leg, target, root, leg.Swing, Pose.Lift, dt);
                    }
                }
            }
            else if (leg.Swinging && !leg.Adjusting)
            {
                BeginSwing(leg, root, 0, true);
            }

            if (leg.Swinging && leg.Adjusting)
            {
                var duration = moving ? Pose.Stride * (1 - Pose.Contact) / speed : t.TurnStepTime;
                leg.Swing = Mathf.Min(1, leg.Swing + dt / Mathf.Max(duration, dt));
                Swing(leg, target, root, leg.Swing, moving ? Pose.Lift : t.WalkFootLift, dt);
                if (leg.Swing >= 1)
                {
                    Plant(leg, SupportedGoal(leg), Pose.Contact);
                    if (turning == i) turning = -1;
                }
            }
            leg.PreviousContact = contact;
            // A missing support surface releases the foot; do not preserve a fictitious contact over an edge.
            if (leg.Planted && (!HasGround(leg.Target.Origin, out var support, out _) ||
                Mathf.Abs(support.Y + t.FootSoleHeight - leg.Target.Origin.Y) > t.FootGroundTolerance))
            {
                leg.Planted = false;
                leg.Target = target;
            }
        }

        // Keep the body's height compatible with leg reach. Authored running clips contain large vertical
        // root motion; copying that height while pinning the soles makes a knee hit its straight-leg limit.
        var body = s.GetBoneGlobalPose(pelvis);
        var rest = s.GetBoneGlobalRest(pelvis).Origin;
        var height = t.LocomotionBodyLower + Mathf.Cos(Pose.Phase * Mathf.Tau * 2) * t.LocomotionBodyBounce * Pose.Moving;
        groundHeight = Mathf.Lerp(groundHeight, root.Y, 1 - Mathf.Exp(-t.FootPelvisSpeed * dt));
        terrainOffset = Mathf.Clamp(groundHeight - root.Y, -t.FootPelvisLimit, t.FootPelvisLimit);
        height += terrainOffset;
        height -= Pose.Landing * t.LandingCompression;
        var wantedBody = rest + Vector3.Up * height;
        body.Origin = body.Origin.Lerp(wantedBody, weight);
        SetPosition(s, pelvis, body.Origin);
        var toLocal = s.GlobalTransform.AffineInverse();
        var wantedPelvis = 0f;
        for (var i = 0; i < 2; i++)
        {
            var leg = legs[i];
            var hip = s.GlobalTransform * s.GetBoneGlobalPose(leg.Thigh).Origin;
            var d = leg.Target.Origin - hip;
            var reach = leg.Length - t.FootReachMargin;
            var vertical = Mathf.Sqrt(Mathf.Max(0, reach * reach - d.X * d.X - d.Z * d.Z));
            wantedPelvis = Mathf.Min(wantedPelvis, leg.Target.Origin.Y + vertical - hip.Y);
        }
        var lower = -t.FootPelvisLimit - terrainOffset;
        var upper = t.FootPelvisLimit - terrainOffset;
        wantedPelvis = Mathf.Clamp(wantedPelvis, lower, upper);
        // Lower immediately enough to keep a contact reachable, recover upward smoothly.
        pelvisOffset = Mathf.Clamp(Mathf.Min(wantedPelvis, Mathf.Lerp(pelvisOffset, wantedPelvis,
            1 - Mathf.Exp(-t.FootPelvisSpeed * dt))), lower, upper);
        SetPosition(s, pelvis, body.Origin + toLocal.Basis * Vector3.Up * pelvisOffset * weight);
        // Acceleration lean and stop settle are presentation only; the rig fits the rifle after this change.
        var acceleration = (velocity - previousVelocity) / dt;
        previousVelocity = velocity;
        var wantedLean = Vector3.Up.Cross(acceleration / Mathf.Max(t.Acceleration, 0.001f)).LimitLength(1) * Mathf.DegToRad(t.LocomotionLeanDegrees);
        lean = lean.Lerp(wantedLean, 1 - Mathf.Exp(-t.FootPelvisSpeed * dt));
        if (lean.LengthSquared() > 0.00000001f)
            Rotate(s, spine, new Quaternion((toLocal.Basis * lean).Normalized(), lean.Length() * weight));

        for (var i = 0; i < 2; i++)
        {
            var leg = legs[i];
            var thigh = s.GetBonePoseRotation(leg.Thigh);
            var calf = s.GetBonePoseRotation(leg.Calf);
            var foot = s.GetBonePoseRotation(leg.Foot);
            var target = toLocal * leg.Target;
            var pole = toLocal * (root + Vector3.Up * t.FootKneePoleHeight + facing * new Vector3(i == 0 ? -t.FootHalfWidth : t.FootHalfWidth, 0, -t.FootKneePoleForward));
            ReachError = Mathf.Max(ReachError, Solve(s, leg, target, pole));
            s.SetBonePoseRotation(leg.Thigh, thigh.Slerp(s.GetBonePoseRotation(leg.Thigh), weight));
            s.SetBonePoseRotation(leg.Calf, calf.Slerp(s.GetBonePoseRotation(leg.Calf), weight));
            s.SetBonePoseRotation(leg.Foot, foot.Slerp(s.GetBonePoseRotation(leg.Foot), weight));
        }
        BlendReset(s, dt);
        CapturePose(s);
    }

    private void BlendReset(Skeleton3D s, float dt)
    {
        if (resetTime > 0)
        {
            resetTime = Mathf.Max(0, resetTime - dt);
            var amount = Mathf.SmoothStep(0, 1, resetTime / Tuning.Current.LocomotionSettleTime);
            BlendPose(s, resetLegRotations, resetPelvisPosition, amount);
        }
    }

    private void CapturePose(Skeleton3D s)
    {
        for (var i = 0; i < 2; i++)
        {
            lastLegRotations[i * 3] = s.GetBonePoseRotation(legs[i].Thigh);
            lastLegRotations[i * 3 + 1] = s.GetBonePoseRotation(legs[i].Calf);
            lastLegRotations[i * 3 + 2] = s.GetBonePoseRotation(legs[i].Foot);
        }
        lastPelvisPosition = s.GetBonePosePosition(pelvis);
        hasPose = true;
    }

    private void BlendAir(Skeleton3D s, float amount) => BlendPose(s, lastLegRotations, lastPelvisPosition, amount);

    private void BlendPose(Skeleton3D s, Quaternion[] rotations, Vector3 position, float amount)
    {
        s.SetBonePosePosition(pelvis, s.GetBonePosePosition(pelvis).Lerp(position, amount));
        for (var i = 0; i < 2; i++)
        {
            var leg = legs[i];
            s.SetBonePoseRotation(leg.Thigh, s.GetBonePoseRotation(leg.Thigh).Slerp(rotations[i * 3], amount));
            s.SetBonePoseRotation(leg.Calf, s.GetBonePoseRotation(leg.Calf).Slerp(rotations[i * 3 + 1], amount));
            s.SetBonePoseRotation(leg.Foot, s.GetBonePoseRotation(leg.Foot).Slerp(rotations[i * 3 + 2], amount));
        }
    }

    private void Resolve(Skeleton3D s)
    {
        pelvis = s.FindBone("pelvis");
        spine = s.FindBone("spine_01");
        for (var i = 0; i < 2; i++)
        {
            var side = i == 0 ? "l" : "r";
            var leg = legs[i];
            leg.Thigh = s.FindBone("thigh_" + side);
            leg.Calf = s.FindBone("calf_" + side);
            leg.Foot = s.FindBone("foot_" + side);
            if (pelvis < 0 || leg.Thigh < 0 || leg.Calf < 0 || leg.Foot < 0) throw new InvalidOperationException("Mannequin leg bones missing");
            leg.RestBasis = s.GetBoneGlobalRest(leg.Foot).Basis;
            leg.ThighBasis = s.GetBoneGlobalRest(leg.Thigh).Basis;
            leg.CalfBasis = s.GetBoneGlobalRest(leg.Calf).Basis;
            leg.UpperDirection = (s.GetBoneGlobalRest(leg.Calf).Origin - s.GetBoneGlobalRest(leg.Thigh).Origin).Normalized();
            leg.LowerDirection = (s.GetBoneGlobalRest(leg.Foot).Origin - s.GetBoneGlobalRest(leg.Calf).Origin).Normalized();
            leg.Length = s.GetBoneGlobalRest(leg.Thigh).Origin.DistanceTo(s.GetBoneGlobalRest(leg.Calf).Origin)
                + s.GetBoneGlobalRest(leg.Calf).Origin.DistanceTo(s.GetBoneGlobalRest(leg.Foot).Origin);
        }
    }

    private Transform3D GroundTarget(Skeleton3D s, int side, Vector3 root, Basis facing, Vector3 lead)
    {
        var t = Tuning.Current;
        var position = root + facing * new Vector3(side == 0 ? -t.FootHalfWidth : t.FootHalfWidth, t.FootSoleHeight, 0) + lead;
        var lateral = lead.Dot(facing * Vector3.Right);
        var other = legs[1 - side];
        if (other.Planted && ((side == 0 && lateral > t.FootLateralStepThreshold) ||
            (side == 1 && lateral < -t.FootLateralStepThreshold)))
        {
            var right = facing * Vector3.Right;
            var gap = (position - other.Target.Origin).Dot(right);
            var wantedGap = side == 0 ? Mathf.Min(gap, -t.FootMinimumSeparation) : Mathf.Max(gap, t.FootMinimumSeparation);
            position += right * (wantedGap - gap);
        }
        var basis = s.GlobalBasis.Orthonormalized() * legs[side].RestBasis;
        if (HasGround(position, out var ground, out var normal))
        {
            position.Y = ground.Y + t.FootSoleHeight;
            basis = new Basis(new Quaternion(Vector3.Up, normal)) * basis;
        }
        return new Transform3D(basis, position);
    }

    private bool HasGround(Vector3 position, out Vector3 ground, out Vector3 normal)
    {
        var t = Tuning.Current;
        var ray = PhysicsRayQueryParameters3D.Create(position + Vector3.Up * t.FootProbeUp,
            position - Vector3.Up * t.FootProbeDown, Layers.World | Layers.Floor);
        var hit = Player.GetWorld3D().DirectSpaceState.IntersectRay(ray);
        ground = hit.Count > 0 ? (Vector3)hit["position"] : position;
        normal = hit.Count > 0 ? (Vector3)hit["normal"] : Vector3.Up;
        return hit.Count > 0;
    }

    private static void BeginSwing(Leg leg, Vector3 root, float progress = 0, bool adjusting = false)
    {
        leg.Start = leg.Goal = leg.Target;
        leg.RootStart = leg.GoalRoot = root;
        leg.ProgressStart = progress;
        leg.Swing = 0;
        leg.Swinging = true;
        leg.Adjusting = adjusting;
        leg.Planted = false;
    }

    private Transform3D SupportedGoal(Leg leg)
    {
        var goal = leg.Goal;
        if (HasGround(goal.Origin, out var ground, out _)) goal.Origin.Y = ground.Y + Tuning.Current.FootSoleHeight;
        return goal;
    }

    private static void Plant(Leg leg, Transform3D target, float contact)
    {
        leg.Target = target;
        leg.Swinging = leg.Adjusting = false;
        leg.Planted = true;
        leg.Contact = contact;
    }

    private static void Swing(Leg leg, Transform3D target, Vector3 root, float progress, float lift, float dt)
    {
        // Smooth changing destinations in the moving root frame, without delaying world travel. A support
        // change or reversal must not yank a swinging ankle sideways when the other foot lifts.
        leg.Goal.Origin += root - leg.GoalRoot;
        leg.GoalRoot = root;
        leg.Goal = leg.Goal.InterpolateWith(target, 1 - Mathf.Exp(-Tuning.Current.FootStepTargetSpeed * dt));
        var start = leg.Start;
        start.Origin += root - leg.RootStart;
        leg.Target = start.InterpolateWith(leg.Goal, Mathf.SmoothStep(0, 1, progress));
        leg.Target.Origin += Vector3.Up * (Mathf.Sin(Mathf.Pi * progress) * lift);
        leg.Planted = false;
    }

    private static void Rotate(Skeleton3D s, int bone, Quaternion rotation)
    {
        var basis = new Basis(rotation) * s.GetBoneGlobalPose(bone).Basis.Orthonormalized();
        SetBasis(s, bone, basis);
    }

    private static void SetPosition(Skeleton3D s, int bone, Vector3 position)
    {
        var parent = s.GetBoneParent(bone);
        s.SetBonePosePosition(bone, parent >= 0 ? s.GetBoneGlobalPose(parent).AffineInverse() * position : position);
    }

    private static void SetBasis(Skeleton3D s, int bone, Basis basis)
    {
        var parent = s.GetBoneParent(bone);
        var parentBasis = parent >= 0 ? s.GetBoneGlobalPose(parent).Basis.Orthonormalized() : Basis.Identity;
        s.SetBonePoseRotation(bone, (parentBasis.Inverse() * basis).Orthonormalized().GetRotationQuaternion());
    }

    private static float Solve(Skeleton3D s, Leg leg, Transform3D target, Vector3 pole)
    {
        var a = s.GetBoneGlobalPose(leg.Thigh).Origin;
        var b = s.GetBoneGlobalPose(leg.Calf).Origin;
        var c = s.GetBoneGlobalPose(leg.Foot).Origin;
        var l1 = a.DistanceTo(b);
        var l2 = b.DistanceTo(c);
        var offset = target.Origin - a;
        var length = offset.Length();
        var reach = Mathf.Clamp(length, Mathf.Abs(l1 - l2) + 0.00001f, l1 + l2 - 0.00001f);
        var direction = offset / Mathf.Max(length, 0.00001f);
        var along = (l1 * l1 - l2 * l2 + reach * reach) / (2 * reach);
        var height = Mathf.Sqrt(Mathf.Max(0, l1 * l1 - along * along));
        var bend = pole - a;
        bend = (bend - direction * bend.Dot(direction)).Normalized();
        var knee = a + direction * along + bend * height;
        SetBasis(s, leg.Thigh, new Basis(new Quaternion(leg.UpperDirection, (knee - a).Normalized())) * leg.ThighBasis);
        b = s.GetBoneGlobalPose(leg.Calf).Origin;
        SetBasis(s, leg.Calf, new Basis(new Quaternion(leg.LowerDirection, (a + direction * reach - b).Normalized())) * leg.CalfBasis);
        SetBasis(s, leg.Foot, target.Basis.Orthonormalized());
        return Mathf.Abs(length - reach);
    }
}
