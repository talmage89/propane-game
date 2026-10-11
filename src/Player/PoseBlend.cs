using System;
using Godot;
using Propane.Core;

namespace Propane.Player;

/// <summary>
/// Blends the settled ragdoll into the get-up and bounds authored animation changes. Runs after physical
/// bones, before feet and rifle contacts, so smoothing never detaches a solved hand or planted foot.
/// </summary>
public partial class PoseBlend : SkeletonModifier3D
{
    public required WorldContext World { get; init; }

    private Transform3D[]? captured;
    private bool captureRequested;
    private bool blending;
    private float duration;
    private float elapsed;
    private Quaternion[]? previous;
    private Quaternion[]? displayed;
    private bool[] digitBones = Array.Empty<bool>();

    public bool HasCapture => captured != null && !captureRequested;

    /// <summary>Captures the pose at the end of this frame's modification pass.</summary>
    public void RequestCapture()
    {
        captureRequested = true;
        captured = null;
        blending = false;
    }

    /// <summary>Drops any captured pose and pending capture or blend.</summary>
    public void Cancel()
    {
        captured = null;
        captureRequested = false;
        blending = false;
    }

    /// <summary>Starts fading from the captured pose to the live pose over <paramref name="seconds"/>.</summary>
    public void BeginBlend(float seconds)
    {
        duration = Mathf.Max(seconds, 0.01f);
        elapsed = 0;
        blending = captured != null;
    }

    public override void _ProcessModificationWithDelta(double delta)
    {
        var skeleton = GetSkeleton();
        if (skeleton == null)
        {
            return;
        }
        var count = skeleton.GetBoneCount();
        if (captureRequested)
        {
            captured = new Transform3D[count];
            var toWorld = skeleton.GlobalTransform;
            for (var i = 0; i < count; i++)
            {
                captured[i] = toWorld * skeleton.GetBoneGlobalPose(i);
            }
            captureRequested = false;
            return;
        }
        if (!blending || captured == null)
        {
            SmoothAnimation(skeleton, (float)delta);
            return;
        }

        elapsed += (float)delta;
        var weight = 1f - Mathf.SmoothStep(0f, 1f, elapsed / duration);
        if (weight <= 0f)
        {
            blending = false;
            captured = null;
            SmoothAnimation(skeleton, (float)delta);
            return;
        }

        var toSkeleton = skeleton.GlobalTransform.AffineInverse();
        for (var i = 0; i < count; i++)
        {
            var parent = skeleton.GetBoneParent(i);
            var global = toSkeleton * captured[i];
            var local = parent >= 0 ? (toSkeleton * captured[parent]).AffineInverse() * global : global;
            local = local.Orthonormalized();
            var current = skeleton.GetBonePose(i);
            var rotation = current.Basis.Orthonormalized().GetRotationQuaternion()
                .Slerp(local.Basis.GetRotationQuaternion(), weight);
            skeleton.SetBonePoseRotation(i, rotation);
            skeleton.SetBonePosePosition(i, current.Origin.Lerp(local.Origin, weight));
        }
        SmoothAnimation(skeleton, (float)delta);
    }
    /// <summary>Remember the complete displayed pose, including IK, for the next animation handoff.</summary>
    public void ObserveFinalPose(Skeleton3D skeleton)
    {
        displayed ??= new Quaternion[skeleton.GetBoneCount()];
        for (var i = 0; i < displayed.Length; i++) displayed[i] = skeleton.GetBonePoseRotation(i).Normalized();
    }

    // Bound authored animation changes before contact solvers, never after them. Physical ragdoll poses
    // remain authoritative; cache them so the first get-up frame starts from the actual settled body.
    private void SmoothAnimation(Skeleton3D skeleton, float dt)
    {
        var simulating = skeleton.GetNode<Ragdoll>("Ragdoll").Simulating;
        var count = skeleton.GetBoneCount();
        if (skeleton.GetNode<RifleRig>("RifleRig").Weight <= 0.001f && displayed != null)
        {
            previous ??= new Quaternion[count];
            Array.Copy(displayed, previous, count);
        }
        if (digitBones.Length != count)
        {
            digitBones = new bool[count];
            for (var i = 0; i < count; i++)
            {
                var name = skeleton.GetBoneName(i).ToString();
                digitBones[i] = name.StartsWith("index_", StringComparison.Ordinal) || name.StartsWith("middle_", StringComparison.Ordinal) ||
                    name.StartsWith("ring_", StringComparison.Ordinal) || name.StartsWith("pinky_", StringComparison.Ordinal) || name.StartsWith("thumb_", StringComparison.Ordinal);
            }
        }
        var initialized = previous != null;
        previous ??= new Quaternion[count];
        var limit = Mathf.DegToRad(World.Tuning.PoseAngularSpeed) * dt;
        for (var i = 0; i < count; i++)
        {
            var target = skeleton.GetBonePoseRotation(i).Normalized();
            // Digits have no physical bodies. Keep their release smooth even when the arm is thrown.
            if (initialized && (!simulating || digitBones[i]))
            {
                var difference = previous[i].Inverse() * target;
                var angle = 2 * Mathf.Atan2(new Vector3(difference.X, difference.Y, difference.Z).Length(), Mathf.Abs(difference.W));
                if (angle > limit) target = previous[i].Slerp(target, limit / angle);
                skeleton.SetBonePoseRotation(i, target);
            }
            // Repeated tiny blends can accumulate roundoff; Inverse requires a unit quaternion next frame.
            previous[i] = target.Normalized();
        }
    }
}
