using System;
using Godot;
using Propane.Core;

namespace Propane.Player;

/// <summary>Bounded terrain and stance corrections to the authored gait. Clips own every swing and
/// knee path; this modifier never schedules or generates a step.</summary>
public partial class FootPlacement : SkeletonModifier3D
{
    public PlayerCharacter Player { get; init; } = null!;
    public LocomotionPose Pose { get; init; } = null!;
    public bool Settled => weight >= 0.999f && !recovering;
    public bool AirSettled => inAir && weight <= 0 && !recovering;
    public bool LeftPlanted => legs[0].Planted && Settled;
    public bool RightPlanted => legs[1].Planted && Settled;
    public Vector3 LeftAnchor => legs[0].Anchor;
    public Vector3 RightAnchor => legs[1].Anchor;
    public float ReachError { get; private set; }
    public float PelvisOffset { get; private set; }

    private sealed class Leg
    {
        public int Thigh, Calf, Foot, Side;
        public int[] Joints = Array.Empty<int>();
        public Vector3 Anchor, Correction, PreviousAuthored;
        public float SupportHeight;
        public bool Planted, Released, HasAuthored;
    }
    private readonly Leg[] legs = { new(), new() };
    private int pelvis = -1;
    private bool initialized, hasMotion, hasPose, inAir, recovering;
    private Vector3 motionRoot, previousRoot, motionVelocity;
    private float weight;
    private readonly Quaternion[] previous = new Quaternion[6];
    private readonly Transform3D[] authoredFeet = new Transform3D[2];
    private readonly Vector3[] supports = new Vector3[2], normals = new Vector3[2];
    private readonly bool[] hasSupport = new bool[2];

    public void Reset(bool blendPose = false)
    {
        initialized = hasMotion = inAir = false;
        if (!blendPose) hasPose = false;
        weight = PelvisOffset = 0;
        foreach (var leg in legs)
        {
            leg.Planted = leg.Released = false;
            leg.HasAuthored = false;
            leg.Correction = Vector3.Zero;
        }
    }

    /// <summary>Remote clips follow displayed displacement, including interpolation stalls and corrections.</summary>
    public Vector3 SamplePlanarVelocity(float dt)
    {
        var root = Player.GlobalPosition;
        var displacement = root - motionRoot;
        motionVelocity = Player.IsRemote ? hasMotion && displacement.Length() < Player.World.Tuning.FootResetDistance
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
        if (!Player.ResolvedState.Active) { Reset(); return; }
        var dt = (float)delta;
        var t = Player.World.Tuning;
        var root = Player.GlobalPosition;
        if (initialized && root.DistanceTo(previousRoot) > t.FootResetDistance) Reset(true);
        if (!initialized)
        {
            pelvis = s.FindBone("pelvis");
            for (var i = 0; i < legs.Length; i++)
            {
                var side = i == 0 ? "l" : "r";
                legs[i].Thigh = s.FindBone("thigh_" + side);
                legs[i].Calf = s.FindBone("calf_" + side);
                legs[i].Foot = s.FindBone("foot_" + side);
                legs[i].Side = i == 0 ? 1 : -1;
                legs[i].Joints = new[] { legs[i].Thigh, legs[i].Calf, legs[i].Foot };
                if (pelvis < 0 || legs[i].Thigh < 0 || legs[i].Calf < 0 || legs[i].Foot < 0)
                    throw new InvalidOperationException("Mannequin leg bones missing");
            }
            initialized = true;
        }
        previousRoot = root;
        inAir = !Player.ResolvedState.Grounded || !HasGround(root, out var ground, out _) ||
            root.Y - ground.Y > t.FootPelvisLimit;
        weight = Mathf.MoveToward(weight, inAir ? 0 : 1, dt / (inAir ? t.TakeoffPoseTime : t.LocomotionSettleTime));
        if (inAir)
        {
            // Physics owns flight displacement. Preserve the authored joint poses, without its root lift.
            SetPosition(s, pelvis, s.GetBoneGlobalRest(pelvis).Origin);
            PelvisOffset = 0;
            foreach (var leg in legs) { leg.Planted = leg.Released = leg.HasAuthored = false; }
            LimitPose(s, dt);
            return;
        }
        var toLocal = s.GlobalTransform.AffineInverse();
        var lower = 0f;
        for (var i = 0; i < legs.Length; i++)
        {
            authoredFeet[i] = s.GlobalTransform * s.GetBoneGlobalPose(legs[i].Foot);
            hasSupport[i] = HasGround(authoredFeet[i].Origin, out supports[i], out normals[i]);
            if (hasSupport[i]) lower = Mathf.Min(lower, supports[i].Y - root.Y);
        }
        PelvisOffset = Mathf.Lerp(PelvisOffset, Mathf.Clamp(lower, -t.FootPelvisLimit, 0),
            1 - Mathf.Exp(-t.FootPelvisSpeed * dt));
        var pelvisPosition = s.GetBoneGlobalPose(pelvis).Origin;
        SetPosition(s, pelvis, pelvisPosition + toLocal.Basis * Vector3.Up * (PelvisOffset * weight));
        recovering = false;
        for (var i = 0; i < legs.Length; i++)
        {
            var leg = legs[i];
            var authored = authoredFeet[i];
            var displacement = authored.Origin - leg.PreviousAuthored;
            var supportSpeed = new Vector2(displacement.X, displacement.Z).Length() / dt;
            var canPlant = !leg.HasAuthored || supportSpeed <= t.FootPlantSpeed;
            leg.PreviousAuthored = authored.Origin;
            leg.HasAuthored = true;
            if (!hasSupport[i])
            {
                leg.Planted = false;
                continue;
            }
            var support = supports[i];
            var normal = normals[i];
            // Correct the terrain height, not the clip's lift. On flat ground a free foot is untouched.
            var terrain = Mathf.Clamp(support.Y - root.Y, -t.FootPelvisLimit, t.FootPelvisLimit);
            var target = authored;
            target.Origin.Y += terrain;
            var stance = authored.Origin.Y - root.Y <= t.FootSoleHeight + t.FootPlantHeight;
            if (!stance)
            {
                leg.Planted = leg.Released = false;
            }
            else
            {
                target.Origin.Y = Mathf.Max(target.Origin.Y, support.Y + t.FootSoleHeight);
                // Height alone also describes late swing. Wait for the authored support path to
                // counter the body's travel before fixing its world contact.
                if (!leg.Planted && !leg.Released && canPlant)
                {
                    leg.Anchor = target.Origin + new Vector3(leg.Correction.X, 0, leg.Correction.Z);
                    leg.Anchor.Y = support.Y + t.FootSoleHeight;
                    leg.SupportHeight = support.Y;
                    // Seat the sole on touchdown; retain the authored heel/toe rotation. Capturing the
                    // ankle's threshold-crossing height would suspend the whole stance above the floor.
                    leg.Planted = true;
                }
            }
            if (leg.Planted)
            {
                var correction = leg.Anchor - target.Origin;
                if (new Vector2(correction.X, correction.Z).Length() > t.FootCorrectionLimit ||
                    Mathf.Abs(support.Y - leg.SupportHeight) > t.FootGroundTolerance)
                {
                    // An exhausted correction releases the anchor until the clip lifts. No replacement swing.
                    leg.Planted = false;
                    leg.Released = true;
                }
                leg.Correction = leg.Anchor - target.Origin;
                if (leg.Planted) target.Origin = leg.Anchor;
            }
            if (!leg.Planted)
            {
                leg.Correction = leg.Correction.MoveToward(Vector3.Zero, t.FootCorrectionReleaseSpeed * dt);
                target.Origin += leg.Correction;
            }
            // A fading recovery offset must not pull an approaching sole through its support surface.
            target.Origin.Y = Mathf.Max(target.Origin.Y, support.Y + t.FootSoleHeight);
            if (stance) target.Basis = new Basis(new Quaternion(Vector3.Up, normal)) * authored.Basis;
            var knee = s.GetBoneGlobalPose(leg.Calf).Origin;
            var targetLocal = toLocal * authored.InterpolateWith(target, weight);
            var reachError = Solve(s, leg, targetLocal, knee, t);
            ReachError = Mathf.Max(ReachError, reachError);
            if (reachError > t.FootPlantReachTolerance) leg.Planted = false;
            // When a contact is infeasible at the bounded joint speed, release it before displaying a
            // recovery pose. A planted leg is never clamped away from its anchor and reported as attached.
            var limit = Mathf.DegToRad(t.FootPoseRecoverySpeed) * dt;
            var bones = leg.Joints;
            for (var joint = 0; joint < bones.Length && hasPose; joint++)
            {
                var proposed = s.GetBonePoseRotation(bones[joint]).Normalized();
                var difference = previous[i * 3 + joint].Inverse() * proposed;
                var angle = 2 * Mathf.Atan2(new Vector3(difference.X, difference.Y, difference.Z).Length(), Mathf.Abs(difference.W));
                if (angle <= limit) continue;
                leg.Planted = false;
                leg.Released = true;
                recovering = true;
                s.SetBonePoseRotation(bones[joint], previous[i * 3 + joint].Slerp(proposed, limit / angle));
            }
            if (!leg.Planted)
                leg.Correction = (s.GlobalTransform * s.GetBoneGlobalPose(leg.Foot)).Origin - authored.Origin - Vector3.Up * terrain;
        }
        // PoseBlend bounds transitions before IK. Remember the displayed joints for airborne recovery,
        // but never smooth a solved contact afterward: doing so detaches its world anchor.
        for (var i = 0; i < legs.Length; i++)
        {
            previous[i * 3] = s.GetBonePoseRotation(legs[i].Thigh).Normalized();
            previous[i * 3 + 1] = s.GetBonePoseRotation(legs[i].Calf).Normalized();
            previous[i * 3 + 2] = s.GetBonePoseRotation(legs[i].Foot).Normalized();
        }
        hasPose = true;
    }

    private void LimitPose(Skeleton3D s, float dt)
    {
        recovering = false;
        var limit = Mathf.DegToRad(Player.World.Tuning.FootPoseRecoverySpeed) * dt;
        for (var i = 0; i < legs.Length; i++)
        {
            var leg = legs[i];
            Limit(leg.Thigh, i * 3);
            Limit(leg.Calf, i * 3 + 1);
            Limit(leg.Foot, i * 3 + 2);
        }
        hasPose = true;
        void Limit(int bone, int index)
        {
            var current = s.GetBonePoseRotation(bone).Normalized();
            if (hasPose)
            {
                var difference = previous[index].Inverse() * current;
                var angle = 2 * Mathf.Atan2(new Vector3(difference.X, difference.Y, difference.Z).Length(), Mathf.Abs(difference.W));
                if (angle > limit)
                {
                    recovering = true;
                    current = previous[index].Slerp(current, limit / angle);
                    s.SetBonePoseRotation(bone, current);
                }
            }
            previous[index] = current;
        }
    }

    private bool HasGround(Vector3 position, out Vector3 ground, out Vector3 normal)
    {
        var t = Player.World.Tuning;
        // Each player samples several native queries per frame; release the temporary query and results now.
        using var ray = PhysicsRayQueryParameters3D.Create(position + Vector3.Up * t.FootProbeUp,
            position - Vector3.Up * t.FootProbeDown, Layers.World | Layers.Floor);
        using var hit = Player.GetWorld3D().DirectSpaceState.IntersectRay(ray);
        ground = hit.Count > 0 ? (Vector3)hit["position"] : position;
        normal = hit.Count > 0 ? (Vector3)hit["normal"] : Vector3.Up;
        return hit.Count > 0;
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

    private static float Solve(Skeleton3D s, Leg leg, Transform3D target, Vector3 pole, Tuning tuning)
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
        var center = a + direction * along;
        var knee = center + bend * height;
        var sign = leg.Side;
        var minimum = tuning.FootKneeSeparation / 2;
        if (sign * knee.X < minimum)
        {
            var outer = Vector3.Right * sign;
            var projected = outer - direction * outer.Dot(direction);
            var available = projected.Length() * height;
            outer = projected.Normalized();
            var cosine = Mathf.Clamp((minimum - sign * center.X) / Mathf.Max(available, 0.00001f), -1, 1);
            var tangent = bend - outer * bend.Dot(outer);
            if (tangent.LengthSquared() < 0.00001f) tangent = direction.Cross(outer);
            knee = center + (outer * cosine + tangent.Normalized() * Mathf.Sqrt(Mathf.Max(0, 1 - cosine * cosine))) * height;
        }
        SetBasis(s, leg.Thigh, new Basis(Between(b - a, knee - a)) * s.GetBoneGlobalPose(leg.Thigh).Basis.Orthonormalized());
        b = s.GetBoneGlobalPose(leg.Calf).Origin;
        SetBasis(s, leg.Calf, new Basis(Between(s.GetBoneGlobalPose(leg.Foot).Origin - b, a + direction * reach - b)) * s.GetBoneGlobalPose(leg.Calf).Basis.Orthonormalized());
        SetBasis(s, leg.Foot, target.Basis.Orthonormalized());
        return Mathf.Abs(length - reach);
    }

    // The vector-to-vector quaternion constructor treats very small angles as identical. Those
    // submillimeter corrections matter to a saved contact; atan2 retains their rotation precision.
    private static Quaternion Between(Vector3 from, Vector3 to)
    {
        from = from.Normalized();
        to = to.Normalized();
        var axis = from.Cross(to);
        var sine = axis.Length();
        var cosine = Mathf.Clamp(from.Dot(to), -1, 1);
        if (sine > 0.00000001f) return new Quaternion(axis / sine, Mathf.Atan2(sine, cosine));
        if (cosine >= 0) return Quaternion.Identity;
        axis = from.Cross(Mathf.Abs(from.X) < .9f ? Vector3.Right : Vector3.Up).Normalized();
        return new Quaternion(axis, Mathf.Pi);
    }
}
