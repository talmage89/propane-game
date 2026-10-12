using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Godot;
using Propane.Core;

namespace Propane.Player;

/// <summary>
/// Builds and drives the mannequin's AnimationTree from a shared stride clock and latched support phases.
/// The upper-body layer stabilizes the chest and arms for the rifle rig; procedural wrists and fingers replace the pistol hold. One-shots
/// cover staggering and getting up; support phases own the jump and landing blends.
/// </summary>
[SuppressMessage("Design", "CA1001", Justification = "modelRoot owns the AnimationTree node and frees it with the character.")]
public sealed class CharacterAnimator
{
    public LocomotionPose Pose { get; }
    private readonly Tuning tuning;
    private readonly AnimationPlayer player;
    private readonly List<(string Parameter, double Length)> gaitSamples = new();
    private readonly List<(string Gait, string FrontBack, string LeftRight, string FrontDiagonal,
        string BackDiagonal, string Diagonal, string LongDiagonal, string Direction)> gaitParameters = new();
    private static readonly string[] Gaits = { "walk", "jog" };
    private static readonly string[] Directions = { "back", "left", "right", "front_left", "front_right", "back_left", "back_right" };
    private const string Air = "parameters/air/blend_amount";
    private const string Hold = "parameters/hold/blend_amount";
    private const string Hit = "parameters/hit/request";
    private const string GetUp = "parameters/getup/request";
    private const string GetUpScale = "parameters/getup_scale/scale";

    private static readonly string[] UpperBodyBones =
    {
        "upperarm_l", "lowerarm_l", "hand_l", "upperarm_r", "lowerarm_r", "hand_r",
        "index_01_l", "index_02_l", "index_03_l", "middle_01_l", "middle_02_l", "middle_03_l",
        "pinky_01_l", "pinky_02_l", "pinky_03_l", "ring_01_l", "ring_02_l", "ring_03_l",
        "thumb_01_l", "thumb_02_l", "thumb_03_l",
        "index_01_r", "index_02_r", "index_03_r", "middle_01_r", "middle_02_r", "middle_03_r",
        "pinky_01_r", "pinky_02_r", "pinky_03_r", "ring_01_r", "ring_02_r", "ring_03_r",
        "thumb_01_r", "thumb_02_r", "thumb_03_r",
    };

    private readonly AnimationTree tree;
    private float previousFacing;
    private bool hasFacing;
    private float filteredYawSpeed;

    public void Reset()
    {
        Pose.Reset();
        hasFacing = false;
        filteredYawSpeed = 0;
    }

    public CharacterAnimator(Tuning tuning, AnimationPlayer player, Node3D modelRoot, string skeletonPath)
    {
        this.tuning = tuning;
        Pose = new LocomotionPose(tuning);
        this.player = player;
        foreach (var gait in Gaits)
        {
            foreach (var direction in Directions)
                gaitSamples.Add(($"parameters/{gait}_{direction}_seek/seek_request", player.GetAnimation(DirectionalClip(gait, direction)).Length));
            gaitParameters.Add((gait, $"parameters/{gait}_front_back/blend_amount", $"parameters/{gait}_left_right/blend_amount",
                $"parameters/{gait}_front_diagonal/blend_amount", $"parameters/{gait}_back_diagonal/blend_amount",
                $"parameters/{gait}_diagonal/blend_amount", $"parameters/{gait}_long_diagonal/blend_amount", $"parameters/{gait}_direction/blend_amount"));
        }
        tree = new AnimationTree
        {
            Name = "AnimationTree",
            AnimPlayer = player.GetPath(),
            RootNode = modelRoot.GetPath(),
            TreeRoot = BuildTree(skeletonPath),
            Active = true,
            CallbackModeProcess = AnimationMixer.AnimationCallbackModeProcess.Idle,
        };
        modelRoot.AddChild(tree);
        tree.AnimPlayer = tree.GetPathTo(player);
        tree.RootNode = tree.GetPathTo(modelRoot);
        tree.Set(Hold, 1f);
    }

    public float GetUpDuration => 1.2f / GetUpSpeed;

    private const float GetUpSpeed = 1.1f;

    /// <summary>All moving clips are sampled at the same normalized phase. Blending never restarts a foot.</summary>
    public void UpdateLocomotion(Vector3 planar, float facingYaw, float delta, PlayerGait resolvedGait)
    {
        var local = new Basis(Vector3.Up, facingYaw).Inverse() * planar;
        var yawSpeed = hasFacing ? Mathf.RadToDeg(Mathf.Wrap(facingYaw - previousFacing, -Mathf.Pi, Mathf.Pi)) / Mathf.Max(delta, 0.00001f) : 0;
        previousFacing = facingYaw;
        hasFacing = true;
        filteredYawSpeed = Mathf.Lerp(filteredYawSpeed, yawSpeed, 1 - Mathf.Exp(-tuning.TurnVelocityResponse * delta));
        Pose.Update(planar.Length(), resolvedGait, delta, new Vector2(local.X, -local.Z), filteredYawSpeed);
        tree.Set("parameters/turn_left_seek/seek_request", Pose.TurnPhase * player.GetAnimation("Turn_Left").Length);
        tree.Set("parameters/turn_right_seek/seek_request", Pose.TurnPhase * player.GetAnimation("Turn_Right").Length);
        tree.Set("parameters/turn_direction/blend_amount", Pose.TurnRight);
        tree.Set("parameters/turning/blend_amount", Pose.Turn);
        tree.Set("parameters/walk_seek/seek_request", Pose.Phase * player.GetAnimation("Walk").Length);
        tree.Set("parameters/jog_seek/seek_request", Pose.Phase * player.GetAnimation("Jog_Fwd").Length);
        foreach (var sample in gaitSamples) tree.Set(sample.Parameter, Pose.Phase * sample.Length);
        foreach (var parameters in gaitParameters)
        {
            tree.Set(parameters.FrontBack, Pose.BackBlend);
            tree.Set(parameters.LeftRight, Pose.RightBlend);
            tree.Set(parameters.FrontDiagonal, Pose.RightBlend);
            tree.Set(parameters.BackDiagonal, Pose.RightBlend);
            tree.Set(parameters.Diagonal, Pose.BackBlend);
            var sideWeight = parameters.Gait == "walk" ? Pose.WalkSideBlend : Pose.JogSideBlend;
            var diagonalWeight = parameters.Gait == "walk" ? Pose.WalkDiagonalWeight : Pose.JogDiagonalWeight;
            tree.Set(parameters.LongDiagonal, Mathf.Min(1, sideWeight / diagonalWeight));
            tree.Set(parameters.Direction, Mathf.Max(0, (sideWeight - diagonalWeight) / (1 - diagonalWeight)));
        }
        tree.Set("parameters/sprint_seek/seek_request", Pose.Phase * player.GetAnimation("Sprint").Length);
        tree.Set("parameters/walk_jog/blend_amount", 1 - Pose.Walk);
        tree.Set("parameters/gaits/blend_amount", Pose.Sprint);
        tree.Set("parameters/moving/blend_amount", Pose.Moving);
        tree.Set(Air, Pose.Air);
        // Jump_Start contains an authored anticipation before its push. Start at the push on the accepted
        // physics jump, then use that clip's extension/tuck as the velocity falls through rising/apex/falling.
        var t = tuning;
        if (takeoffs != Pose.Takeoffs)
        {
            takeoffs = Pose.Takeoffs;
            jumpSample = t.TakeoffClipStart;
        }
        var airTime = Pose.Jump switch
        {
            JumpPose.Takeoff => t.TakeoffClipStart + Pose.TakeoffTime * t.TakeoffClipSpeed,
            JumpPose.Rising => t.JumpRiseClipTime,
            JumpPose.Apex => t.JumpApexClipTime,
            _ => t.JumpFallClipTime,
        };
        jumpSample = Mathf.MoveToward(jumpSample, airTime, delta * t.JumpSampleBlendSpeed);
        tree.Set("parameters/fall_seek/seek_request", jumpSample);
        tree.Set("parameters/land/blend_amount", Pose.Landing);
        tree.Set("parameters/land_seek/seek_request", t.LandingClipTime);
    }

    private float jumpSample;
    private int takeoffs;

    private static string DirectionalClip(string gait, string direction)
        => (gait == "walk" ? "Walk" : "Jog") + "_" + string.Join("_", System.Array.ConvertAll(direction.Split('_'),
            part => char.ToUpperInvariant(part[0]) + part[1..]));

    public void PlayHit() => tree.Set(Hit, (int)AnimationNodeOneShot.OneShotRequest.Fire);

    public void PlayGetUp() => tree.Set(GetUp, (int)AnimationNodeOneShot.OneShotRequest.Fire);

    public void StopGetUp() => tree.Set(GetUp, (int)AnimationNodeOneShot.OneShotRequest.Abort);

    /// <summary>Weight of the pistol-hold base pose on the arms (0 when ragdolling or getting up).</summary>
    public void SetHoldWeight(float weight) => tree.Set(Hold, weight);

    private AnimationNodeBlendTree BuildTree(string skeletonPath)
    {
        var root = new AnimationNodeBlendTree();

        AnimationNodeAnimation Clip(string name) => new() { Animation = name };

        root.AddNode("idle", Clip("Idle"));
        foreach (var (name, clip) in new[] { ("walk", "Walk"), ("jog", "Jog_Fwd"), ("sprint", "Sprint"), ("fall", "Jump_Start"), ("land", "Jump_Land") })
        {
            root.AddNode(name + "_clip", Clip(clip));
            root.AddNode(name + "_seek", new AnimationNodeTimeSeek());
            root.ConnectNode(name + "_seek", 0, name + "_clip");
        }
        void Blend(string name, string a, string b)
        {
            root.AddNode(name, new AnimationNodeBlend2());
            root.ConnectNode(name, 0, a);
            root.ConnectNode(name, 1, b);
        }
        foreach (var gait in Gaits)
        {
            foreach (var side in Directions)
            {
                var name = gait + "_" + side;
                var clip = DirectionalClip(gait, side);
                root.AddNode(name + "_clip", Clip(clip));
                root.AddNode(name + "_seek", new AnimationNodeTimeSeek());
                root.ConnectNode(name + "_seek", 0, name + "_clip");
            }
            Blend(gait + "_front_back", gait + "_seek", gait + "_back_seek");
            Blend(gait + "_left_right", gait + "_left_seek", gait + "_right_seek");
            Blend(gait + "_front_diagonal", gait + "_front_left_seek", gait + "_front_right_seek");
            Blend(gait + "_back_diagonal", gait + "_back_left_seek", gait + "_back_right_seek");
            Blend(gait + "_diagonal", gait + "_front_diagonal", gait + "_back_diagonal");
            Blend(gait + "_long_diagonal", gait + "_front_back", gait + "_diagonal");
            Blend(gait + "_direction", gait + "_long_diagonal", gait + "_left_right");
        }
        Blend("walk_jog", "walk_direction", "jog_direction");
        Blend("gaits", "walk_jog", "sprint_seek");
        Blend("moving", "idle", "gaits");
        foreach (var side in new[] { "left", "right" })
        {
            var name = "turn_" + side;
            root.AddNode(name + "_clip", Clip(side == "left" ? "Turn_Left" : "Turn_Right"));
            root.AddNode(name + "_seek", new AnimationNodeTimeSeek());
            root.ConnectNode(name + "_seek", 0, name + "_clip");
        }
        Blend("turn_direction", "turn_left_seek", "turn_right_seek");
        Blend("turning", "moving", "turn_direction");
        Blend("air", "turning", "fall_seek");
        Blend("land", "air", "land_seek");

        // Keep authored pelvis/spine motion; only the arms use the rifle hold base.
        root.AddNode("hold_clip", Clip("Pistol_Idle"), new Vector2(800, 450));
        var hold = new AnimationNodeBlend2 { FilterEnabled = true };
        foreach (var bone in UpperBodyBones)
        {
            hold.SetFilterPath(new NodePath($"{skeletonPath}:{bone}"), true);
        }
        root.AddNode("hold", hold, new Vector2(1000, 250));
        root.ConnectNode("hold", 0, "land");
        root.ConnectNode("hold", 1, "hold_clip");

        root.AddNode("hit_clip", Clip("Hit_Chest"), new Vector2(1000, 450));
        var hit = new AnimationNodeOneShot { FadeInTime = 0.05f, FadeOutTime = 0.2f };
        root.AddNode("hit", hit, new Vector2(1200, 250));
        root.ConnectNode("hit", 0, "hold");
        root.ConnectNode("hit", 1, "hit_clip");

        root.AddNode("getup_clip", Clip("LayToIdle"), new Vector2(1200, 450));
        root.AddNode("getup_scale", new AnimationNodeTimeScale(), new Vector2(1300, 450));
        root.ConnectNode("getup_scale", 0, "getup_clip");
        var getUp = new AnimationNodeOneShot { FadeInTime = 0f, FadeOutTime = 0.3f };
        root.AddNode("getup", getUp, new Vector2(1400, 250));
        root.ConnectNode("getup", 0, "hit");
        root.ConnectNode("getup", 1, "getup_scale");

        root.ConnectNode("output", 0, "getup");
        return root;
    }

    /// <summary>Call once after the tree is in the scene.</summary>
    public void Initialize()
    {
        tree.Set(GetUpScale, GetUpSpeed);
    }
}
