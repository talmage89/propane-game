using System;
using System.Linq;
using Godot;

namespace Propane.Assets;

/// <summary>The existing canonical rig, not a general humanoid adapter. No character manifest or new art.</summary>
public static class DefaultCharacter
{
    public const string Id = "mannequin";
    public const string ScenePath = "res://assets/third_party/quaternius/ual/mannequin.glb";
    public const string DirectionalPath = "res://assets/third_party/100style/neutral/directional.tres";
    public const string HeadBone = "Head";
    private static bool validated;

    /// <summary>Validate borrowed immutable art once per process, before the first actor binds its rig.</summary>
    public static void Validate(Skeleton3D skeleton, AnimationPlayer player)
    {
        if (validated) return;
        Check(skeleton, player);
        validated = true;
    }

    public static void Check(Skeleton3D skeleton, AnimationPlayer player)
    {
        if (skeleton.GetBoneCount() != 65) throw new InvalidOperationException("Default character requires the canonical 65-bone rig");
        var bones = "root pelvis spine_01 spine_02 spine_03 neck_01 Head".Split(' ')
            .Concat(new[] { "l", "r" }.SelectMany(side =>
                "clavicle upperarm lowerarm hand thigh calf foot ball".Split(' ').Select(b => b + "_" + side)
                    .Concat("index middle pinky ring thumb".Split(' ').SelectMany(digit => Enumerable.Range(1, 3).Select(i => $"{digit}_{i:00}_{side}")))));
        foreach (var bone in bones)
        {
            var index = skeleton.FindBone(bone);
            if (index < 0 || !skeleton.GetBoneGlobalRest(index).IsFinite() || Mathf.IsZeroApprox(skeleton.GetBoneGlobalRest(index).Basis.Determinant()))
                throw new InvalidOperationException($"Invalid default character bone: {bone}");
        }
        foreach (var clip in "Idle Walk Jog_Fwd Sprint Jump_Start Jump_Land Pistol_Idle Hit_Chest LayToIdle".Split(' '))
            if (!player.HasAnimation(clip)) throw new InvalidOperationException($"Missing default character clip: {clip}");
    }

    /// <summary>Socket axes match model axes at rest; its origin is Head's rest origin, not the crown.</summary>
    public static Transform3D HeadSocket(Skeleton3D skeleton)
    {
        var rest = skeleton.GetBoneGlobalRest(skeleton.FindBone(HeadBone));
        return rest.AffineInverse() * new Transform3D(Basis.Identity, rest.Origin);
    }
}
