using System.Diagnostics.CodeAnalysis;
using Godot;
using Propane.Core;

namespace Propane.Player;

/// <summary>
/// Builds and drives the mannequin's AnimationTree. Locomotion plays the clips as authored: every cycle is
/// sampled at one shared stride phase and blended by speed and by travel relative to the body. The walk family
/// covers the upright walk and aimed creep in every direction (forward walk, backpedal, both sidesteps; diagonals
/// blend neighbors); the jog runs forward or, played in reverse, backward; sprint is forward only. Arms and
/// fingers take the pistol-hold pose as a base for the rifle stance. One-shots cover staggering and getting up;
/// latched support phases own the jump and landing blends.
/// </summary>
[SuppressMessage("Design", "CA1001", Justification = "modelRoot owns the AnimationTree node and frees it with the character.")]
public sealed class CharacterAnimator
{
    /// <summary>Directional cycles retargeted from 100STYLE by tools/animation/retarget_100style.py.</summary>
    public const string DirectionalLibrary = "res://assets/character/directional.tres";

    // Where each packaged cycle's left foot touches down, as a fraction of the clip (measured from the clips).
    // The directional cycles start there by construction.
    private const float WalkContact = 0.962f;
    private const float JogContact = 0.046f;
    private const float SprintContact = 0.992f;

    public LocomotionPose Pose { get; }
    private readonly Tuning tuning;
    private readonly AnimationTree tree;
    private readonly float walkLength, jogLength, sprintLength;
    private float jumpSample;
    private int takeoffs;

    private static readonly StringName WalkSeek = "parameters/walk_seek/seek_request";
    private static readonly StringName BackSeek = "parameters/back_seek/seek_request";
    private static readonly StringName LeftSeek = "parameters/left_seek/seek_request";
    private static readonly StringName RightSeek = "parameters/right_seek/seek_request";
    private static readonly StringName JogSeek = "parameters/jog_seek/seek_request";
    private static readonly StringName JogBackSeek = "parameters/jog_back_seek/seek_request";
    private static readonly StringName SprintSeek = "parameters/sprint_seek/seek_request";
    private static readonly StringName FallSeek = "parameters/fall_seek/seek_request";
    private static readonly StringName LandSeek = "parameters/land_seek/seek_request";
    private static readonly StringName FrontBack = "parameters/front_back/blend_amount";
    private static readonly StringName LeftRight = "parameters/left_right/blend_amount";
    private static readonly StringName Sides = "parameters/sides/blend_amount";
    private static readonly StringName JogDirection = "parameters/jog/blend_amount";
    private static readonly StringName WalkJog = "parameters/walk_jog/blend_amount";
    private static readonly StringName Gaits = "parameters/gaits/blend_amount";
    private static readonly StringName Moving = "parameters/moving/blend_amount";
    private static readonly StringName Air = "parameters/air/blend_amount";
    private static readonly StringName Land = "parameters/land/blend_amount";
    private static readonly StringName Hold = "parameters/hold/blend_amount";
    private static readonly StringName Hit = "parameters/hit/request";
    private static readonly StringName HitActive = "parameters/hit/active";
    private static readonly StringName HitInternalActive = "parameters/hit/internal_active";
    private static readonly StringName GetUp = "parameters/getup/request";
    private static readonly StringName GetUpScale = "parameters/getup_scale/scale";

    private static readonly string[] UpperBodyBones =
    {
        "clavicle_l", "upperarm_l", "lowerarm_l", "hand_l", "clavicle_r", "upperarm_r", "lowerarm_r", "hand_r",
        "index_01_l", "index_02_l", "index_03_l", "middle_01_l", "middle_02_l", "middle_03_l",
        "pinky_01_l", "pinky_02_l", "pinky_03_l", "ring_01_l", "ring_02_l", "ring_03_l",
        "thumb_01_l", "thumb_02_l", "thumb_03_l",
        "index_01_r", "index_02_r", "index_03_r", "middle_01_r", "middle_02_r", "middle_03_r",
        "pinky_01_r", "pinky_02_r", "pinky_03_r", "ring_01_r", "ring_02_r", "ring_03_r",
        "thumb_01_r", "thumb_02_r", "thumb_03_r",
    };

    public CharacterAnimator(Tuning tuning, AnimationPlayer player, Node3D modelRoot, string skeletonPath)
    {
        this.tuning = tuning;
        Pose = new LocomotionPose(tuning);
        if (!player.HasAnimationLibrary("directional"))
        {
            player.AddAnimationLibrary("directional", GD.Load<AnimationLibrary>(DirectionalLibrary));
        }
        walkLength = (float)player.GetAnimation("Walk").Length;
        jogLength = (float)player.GetAnimation("Jog_Fwd").Length;
        sprintLength = (float)player.GetAnimation("Sprint").Length;
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

    /// <summary><paramref name="travel"/> is planar velocity in the body frame: x right, y forward (m/s).</summary>
    public void UpdateLocomotion(Vector2 travel, float delta, PlayerGait resolvedGait, bool sample = true)
    {
        using var profile = ActorProfile.Measure(ActorProfile.System.Locomotion);
        Pose.Update(travel, resolvedGait, delta);
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
        if (sample) ApplyPose();
    }

    /// <summary>Write the current clocks without advancing them, including just before a physical throw.</summary>
    public void ApplyPose()
    {
        var t = tuning;
        var phase = Pose.Phase;
        tree.Set(WalkSeek, Mathf.PosMod(phase + WalkContact, 1) * walkLength);
        tree.Set(BackSeek, phase);
        tree.Set(LeftSeek, phase);
        tree.Set(RightSeek, phase);
        tree.Set(JogSeek, Mathf.PosMod(phase + JogContact, 1) * jogLength);
        // Running backward plays the jog in reverse, as the first release did: the 100STYLE backward run is a
        // slow jog whose steps cannot be stretched to the 6 m/s run without frantic cadence.
        tree.Set(JogBackSeek, Mathf.PosMod(JogContact - phase, 1) * jogLength);
        tree.Set(SprintSeek, Mathf.PosMod(phase + SprintContact, 1) * sprintLength);
        tree.Set(FrontBack, Pose.Back);
        tree.Set(LeftRight, Pose.Right);
        tree.Set(Sides, Pose.Side);
        tree.Set(JogDirection, Pose.JogBack);
        tree.Set(WalkJog, 1 - Pose.Walk);
        tree.Set(Gaits, Pose.Sprint);
        tree.Set(Moving, Pose.Moving);
        tree.Set(Air, Pose.Air);
        tree.Set(FallSeek, jumpSample);
        tree.Set(Land, Pose.Landing);
        tree.Set(LandSeek, t.LandingClipTime);
    }

    public void SetThrottled(bool throttled) => tree.CallbackModeProcess = throttled
        ? AnimationMixer.AnimationCallbackModeProcess.Manual : AnimationMixer.AnimationCallbackModeProcess.Idle;

    public void Sample(float delta)
    {
        using var profile = ActorProfile.Measure(ActorProfile.System.AnimationTree);
        tree.Advance(delta);
    }

    public void PlayHit() => tree.Set(Hit, (int)AnimationNodeOneShot.OneShotRequest.Fire);

    public bool HitPlaying => tree.Get(Hit).AsInt32() != (int)AnimationNodeOneShot.OneShotRequest.None ||
        tree.Get(HitActive).AsBool() || tree.Get(HitInternalActive).AsBool();

    public void PlayGetUp() => tree.Set(GetUp, (int)AnimationNodeOneShot.OneShotRequest.Fire);

    public void StopGetUp() => tree.Set(GetUp, (int)AnimationNodeOneShot.OneShotRequest.Abort);

    /// <summary>Weight of the pistol-hold base pose on the arms (0 when ragdolling or getting up).</summary>
    public void SetHoldWeight(float weight) => tree.Set(Hold, weight);

    private static AnimationNodeBlendTree BuildTree(string skeletonPath)
    {
        var root = new AnimationNodeBlendTree();

        AnimationNodeAnimation Clip(string name) => new() { Animation = name };
        void Seeked(string name, string clip)
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

        root.AddNode("idle", Clip("Idle"));
        Seeked("walk", "Walk");
        Seeked("back", "directional/Walk_Back");
        Seeked("left", "directional/Walk_Left");
        Seeked("right", "directional/Walk_Right");
        Seeked("jog", "Jog_Fwd");
        Seeked("jog_back", "Jog_Fwd");
        Seeked("sprint", "Sprint");
        Seeked("fall", "Jump_Start");
        Seeked("land", "Jump_Land");
        Blend("front_back", "walk_seek", "back_seek");
        Blend("left_right", "left_seek", "right_seek");
        Blend("sides", "front_back", "left_right");
        Blend("jog", "jog_seek", "jog_back_seek");
        Blend("walk_jog", "sides", "jog");
        Blend("gaits", "walk_jog", "sprint_seek");
        Blend("moving", "idle", "gaits");
        Blend("air", "moving", "fall_seek");
        Blend("land", "air", "land_seek");

        // Arms and fingers from the pistol hold; the rifle stance then moves the arms and poses the fingers.
        // The spine, neck and head keep the locomotion clip's own motion.
        root.AddNode("hold_clip", Clip("Pistol_Idle"));
        var hold = new AnimationNodeBlend2 { FilterEnabled = true };
        foreach (var bone in UpperBodyBones)
        {
            hold.SetFilterPath(new NodePath($"{skeletonPath}:{bone}"), true);
        }
        root.AddNode("hold", hold);
        root.ConnectNode("hold", 0, "land");
        root.ConnectNode("hold", 1, "hold_clip");

        root.AddNode("hit_clip", Clip("Hit_Chest"));
        var hit = new AnimationNodeOneShot { FadeInTime = 0.05f, FadeOutTime = 0.2f };
        root.AddNode("hit", hit);
        root.ConnectNode("hit", 0, "hold");
        root.ConnectNode("hit", 1, "hit_clip");

        root.AddNode("getup_clip", Clip("LayToIdle"));
        root.AddNode("getup_scale", new AnimationNodeTimeScale());
        root.ConnectNode("getup_scale", 0, "getup_clip");
        var getUp = new AnimationNodeOneShot { FadeInTime = 0f, FadeOutTime = 0.3f };
        root.AddNode("getup", getUp);
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
