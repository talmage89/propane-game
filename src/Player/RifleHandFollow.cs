using Godot;

namespace Propane.Player;

/// <summary>Final modifier: while the rifle rig is off (ragdoll, get-up), the rifle rides in the right hand.</summary>
public partial class RifleHandFollow : SkeletonModifier3D
{
    private int handR = -1;

    public Rifle? Rifle { get; set; }

    public RifleRig? Rig { get; set; }

    public override void _ProcessModificationWithDelta(double delta)
    {
        var skeleton = GetSkeleton();
        if (skeleton == null || Rifle == null || Rig == null || Rig.Weight > 0.001f)
        {
            return;
        }
        if (handR < 0)
        {
            handR = skeleton.FindBone("hand_r");
        }
        var hand = (skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(handR)).Orthonormalized();
        Rifle.GlobalTransform = hand * RifleRig.RightHandInRifle.AffineInverse();
    }
}
