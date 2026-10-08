using Godot;

namespace Propane.Player;

/// <summary>
/// Builds and drives the mannequin's AnimationTree. Locomotion is a crossfade between discrete gaits, each
/// time-scaled to the measured ground speed of its clip so feet do not slide. Arms and fingers take the pistol-hold
/// pose as a base for the rifle IK. One-shots cover landing, staggering and getting up.
/// </summary>
public sealed class CharacterAnimator
{
    // Ground speed of each clip, measured from foot contact in the character lab.
    private const float WalkClipSpeed = 0.98f;
    private const float JogClipSpeed = 5.9f;
    private const float SprintClipSpeed = 9.0f;

    private const string Gait = "parameters/gait/transition_request";
    private const string IdleScale = "parameters/idle_scale/scale";
    private const string WalkScale = "parameters/walk_scale/scale";
    private const string JogScale = "parameters/jog_scale/scale";
    private const string SprintScale = "parameters/sprint_scale/scale";
    private const string Air = "parameters/air/blend_amount";
    private const string Hold = "parameters/hold/blend_amount";
    private const string Land = "parameters/land/request";
    private const string Hit = "parameters/hit/request";
    private const string GetUp = "parameters/getup/request";
    private const string GetUpScale = "parameters/getup_scale/scale";

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

    private readonly AnimationTree tree;
    private string currentGait = "idle";
    private float airBlend;

    public CharacterAnimator(AnimationPlayer player, Node3D modelRoot, string skeletonPath)
    {
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

    /// <summary>Signed ground speed along the body's facing (negative = backpedalling).</summary>
    public void UpdateLocomotion(float forwardSpeed, float planarSpeed, bool grounded, float delta)
    {
        var speed = planarSpeed;
        var direction = forwardSpeed < -0.2f ? -1f : 1f;
        string gait;
        if (speed < 0.35f)
        {
            gait = "idle";
        }
        else if (speed < 2.4f)
        {
            gait = "walk";
        }
        else if (speed < 7.6f)
        {
            gait = "jog";
        }
        else
        {
            gait = "sprint";
        }
        if (gait != currentGait)
        {
            currentGait = gait;
            tree.Set(Gait, gait);
        }
        tree.Set(WalkScale, direction * Mathf.Clamp(speed / WalkClipSpeed, 0.5f, 2.2f));
        tree.Set(JogScale, direction * Mathf.Clamp(speed / JogClipSpeed, 0.5f, 1.35f));
        tree.Set(SprintScale, direction * Mathf.Clamp(speed / SprintClipSpeed, 0.7f, 1.3f));
        tree.Set(IdleScale, 1f);

        airBlend = Mathf.MoveToward(airBlend, grounded ? 0f : 1f, delta * (grounded ? 10f : 5f));
        tree.Set(Air, airBlend);
    }

    public void PlayLand() => tree.Set(Land, (int)AnimationNodeOneShot.OneShotRequest.Fire);

    /// <summary>Fades the landing out early, as soon as the player moves off.</summary>
    public void StopLand() => tree.Set(Land, (int)AnimationNodeOneShot.OneShotRequest.FadeOut);

    public void PlayHit() => tree.Set(Hit, (int)AnimationNodeOneShot.OneShotRequest.Fire);

    public void PlayGetUp() => tree.Set(GetUp, (int)AnimationNodeOneShot.OneShotRequest.Fire);

    public void StopGetUp() => tree.Set(GetUp, (int)AnimationNodeOneShot.OneShotRequest.Abort);

    /// <summary>Weight of the pistol-hold base pose on the arms (0 when ragdolling or getting up).</summary>
    public void SetHoldWeight(float weight) => tree.Set(Hold, weight);

    private static AnimationNodeBlendTree BuildTree(string skeletonPath)
    {
        var root = new AnimationNodeBlendTree();

        AnimationNodeAnimation Clip(string name) => new() { Animation = name };

        root.AddNode("idle", Clip("Idle"), new Vector2(0, 0));
        root.AddNode("walk", Clip("Walk"), new Vector2(0, 150));
        root.AddNode("jog", Clip("Jog_Fwd"), new Vector2(0, 300));
        root.AddNode("sprint", Clip("Sprint"), new Vector2(0, 450));
        root.AddNode("idle_scale", new AnimationNodeTimeScale(), new Vector2(200, 0));
        root.AddNode("walk_scale", new AnimationNodeTimeScale(), new Vector2(200, 150));
        root.AddNode("jog_scale", new AnimationNodeTimeScale(), new Vector2(200, 300));
        root.AddNode("sprint_scale", new AnimationNodeTimeScale(), new Vector2(200, 450));
        root.ConnectNode("idle_scale", 0, "idle");
        root.ConnectNode("walk_scale", 0, "walk");
        root.ConnectNode("jog_scale", 0, "jog");
        root.ConnectNode("sprint_scale", 0, "sprint");

        var gait = new AnimationNodeTransition { XfadeTime = 0.22f, AllowTransitionToSelf = false };
        gait.InputCount = 4;
        gait.SetInputName(0, "idle");
        gait.SetInputName(1, "walk");
        gait.SetInputName(2, "jog");
        gait.SetInputName(3, "sprint");
        root.AddNode("gait", gait, new Vector2(400, 200));
        root.ConnectNode("gait", 0, "idle_scale");
        root.ConnectNode("gait", 1, "walk_scale");
        root.ConnectNode("gait", 2, "jog_scale");
        root.ConnectNode("gait", 3, "sprint_scale");

        root.AddNode("fall", Clip("Jump"), new Vector2(400, 450));
        root.AddNode("air", new AnimationNodeBlend2(), new Vector2(600, 250));
        root.ConnectNode("air", 0, "gait");
        root.ConnectNode("air", 1, "fall");

        root.AddNode("land_clip", Clip("Jump_Land"), new Vector2(600, 450));
        var land = new AnimationNodeOneShot { FadeInTime = 0.05f, FadeOutTime = 0.15f };
        root.AddNode("land", land, new Vector2(800, 250));
        root.ConnectNode("land", 0, "air");
        root.ConnectNode("land", 1, "land_clip");

        // Arms and fingers from the pistol hold; the rifle IK then moves the arms but keeps the curled fingers.
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
        tree.Set(Gait, "idle");
    }
}
