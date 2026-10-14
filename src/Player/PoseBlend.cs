using Godot;

namespace Propane.Player;

/// <summary>
/// Captures the settled ragdoll's pose in world space, then fades from it into whatever the animation produces,
/// such as the start of the get-up clip. Runs after the physical bones and before the rifle stance, which is
/// off during a get-up and then fades in itself.
/// </summary>
public partial class PoseBlend : SkeletonModifier3D
{
    private Transform3D[]? captured;
    private bool captureRequested;
    private bool blending;
    private float duration;
    private float elapsed;
    // Each bone's animated and captured rotations on the previous fade frame, signs kept continuous.
    private Quaternion[] animatedPath = System.Array.Empty<Quaternion>(), capturedPath = System.Array.Empty<Quaternion>();
    private bool pathStarted;

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
        pathStarted = false;
        if (blending && animatedPath.Length != captured!.Length)
        {
            animatedPath = new Quaternion[captured.Length];
            capturedPath = new Quaternion[captured.Length];
        }
    }

    public override void _ProcessModificationWithDelta(double delta)
    {
        using var profile = ActorProfile.Measure(ActorProfile.System.PoseBlend);
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
            // Keep one path per bone for the whole fade: re-choosing the shortest arc every frame switches sides
            // when the animation passes half a turn from the captured pose, flipping the limb in one frame.
            var animated = current.Basis.Orthonormalized().GetRotationQuaternion();
            var target = local.Basis.GetRotationQuaternion();
            if (!pathStarted)
            {
                if (animated.Dot(target) < 0) target = -target;
            }
            else
            {
                if (animated.Dot(animatedPath[i]) < 0) animated = -animated;
                if (target.Dot(capturedPath[i]) < 0) target = -target;
            }
            animatedPath[i] = animated;
            capturedPath[i] = target;
            var rotation = animated.Slerpni(target, weight).Normalized();
            skeleton.SetBonePoseRotation(i, rotation);
            skeleton.SetBonePosePosition(i, current.Origin.Lerp(local.Origin, weight));
        }
        pathStarted = true;
    }
}
