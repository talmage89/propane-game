using System;
using Godot;
using Propane.Core;
using Propane.Player;
using static Propane.Dev.UnitTests.TestRunner;

namespace Propane.Dev;

/// <summary>Advance an animation parent between physics shot fitting and final-pose grip assertions.</summary>
public partial class ShotParentProbe : SkeletonModifier3D
{
    public required RifleRig Rig { get; init; }
    public ShotParentProbe? Source { get; init; }
    public bool Pending { get; set; }
    public int Samples { get; private set; }

    public override void _ProcessModificationWithDelta(double delta)
    {
        var skeleton = GetSkeleton();
        if (skeleton == null) return;
        if (Source == null)
        {
            if (!Pending) return;
            Pending = false;
            var parent = skeleton.GetBoneParent(skeleton.FindBone("spine_01"));
            // A larger step than the observed 8 cm clip sample makes the contact regression deterministic.
            skeleton.SetBonePosePosition(parent, skeleton.GetBonePosePosition(parent) + Vector3.Up * .25f);
            // A second physics tick can translate/turn the actor before the fitted muzzle is rendered.
            var frame = skeleton.GlobalTransform;
            skeleton.GlobalTransform = new Transform3D(new Basis(Vector3.Up, .2f) * frame.Basis, frame.Origin + Vector3.Back * .1f);
            Samples++;
            return;
        }
        if (Samples == Source.Samples) return;
        try
        {
            Assert(Rig.AlignedShotThisFrame, "parent displacement did not exercise a firing frame");
            var right = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(skeleton.FindBone("hand_r")).Origin;
            var left = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(skeleton.FindBone("hand_l")).Origin;
            Assert(right.DistanceTo(Rig.RightTarget.Origin) < .003f && left.DistanceTo(Rig.LeftTarget.Origin) < .003f,
                "advancing the animation parent detached a firing grip");
            Samples = Source.Samples;
        }
        catch (Exception error)
        {
            GD.PushError($"[shot parent] FAIL {error}");
            ShutdownGuard.Quit(GetTree(), 1);
        }
    }
}
