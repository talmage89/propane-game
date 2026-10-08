using Godot;

namespace Propane.Player;

/// <summary>
/// Last modifier on the skeleton. It can capture the final pose (the settled ragdoll) in world space, then fade from
/// that captured pose into whatever the animation and earlier modifiers produce, such as the start of the get-up clip.
/// </summary>
public partial class PoseBlend : SkeletonModifier3D
{
    private Transform3D[]? captured;
    private bool captureRequested;
    private bool blending;
    private float duration;
    private float elapsed;

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
            return;
        }

        elapsed += (float)delta;
        var weight = 1f - Mathf.SmoothStep(0f, 1f, elapsed / duration);
        if (weight <= 0f)
        {
            blending = false;
            captured = null;
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
    }
}
