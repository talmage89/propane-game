using System.Linq;
using Godot;
using Propane.Core;
using Propane.Fx;

namespace Propane.Player;

/// <summary>
/// The rifle model with its sockets. Its origin is the pistol grip; -Z points down the barrel. The player's rig
/// places it every frame; this node owns the visuals: the muzzle flash, the action cycling on each shot, and the
/// magazine swap of a reload, whose old magazine drops to the ground.
/// </summary>
public partial class Rifle : Node3D
{
    private const string ScenePath = "res://assets/rifle/rifle.glb";
    private const float FlashDuration = 0.06f;

    // The semi-automatic clip pulls the trigger first; a shot starts it where the hammer falls.
    private const float SemiShotTime = 0.05f;

    // Times in both reload clips (seconds of the clip, which is scaled to the reload time).
    private const float MagazineDropTime = 0.32f;
    private const float FreshMagazineTime = 0.8f;
    private const float ReachStart = 0.12f;
    private const float ReachEnd = 0.55f;
    private const float SeatedTime = 1.62f;
    private const float ReturnEnd = 1.95f;
    // ReloadEmpty only: after seating the magazine, the left hand slaps the bolt catch.
    private const float BoltCatchReached = 1.84f;
    private const float EmptyReturnStart = 1.95f;
    private const float EmptyReturnEnd = 2.3f;
    /// <summary>Share of the clip's magazine travel below the well that is kept: the mannequin's arms are short.</summary>
    private const float MagazineTravel = 0.5f;

    /// <summary>Where the left hand holds the magazine, in the magazine's frame (origin at its top).</summary>
    private static readonly Vector3 MagazineGrip = new(0, -0.1f, -0.012f);

    /// <summary>Where the left palm meets the bolt catch, beside it on the receiver's left side.</summary>
    private static readonly Vector3 BoltCatchSlap = new(-0.012f, -0.012f, 0.004f);

    private static StandardMaterial3D? bodyMaterial;

    private OmniLight3D flashLight = null!;
    private MeshInstance3D flashSprite = null!;
    private ShaderMaterial flashMaterial = null!;
    private float flashTimer;

    private AnimationPlayer animations = null!;
    private Skeleton3D skeleton = null!;
    private Transform3D skeletonLocal;
    private int magazineBone;
    private Vector3 boltCatchLocal;
    private string clip = "Idle";
    private float clipTime;
    private bool boltLocked;
    private string? reloadClip;
    private float reloadTime;
    private bool magazineDropped;
    private Vector3 lastMagazinePosition;

    /// <summary>Socket transforms in rifle space, read from the model.</summary>
    public Transform3D ForegripLocal { get; private set; }

    public Transform3D MuzzleLocal { get; private set; }

    public Transform3D ButtLocal { get; private set; }

    public Transform3D EyeLocal { get; private set; }

    public Transform3D MagWellLocal { get; private set; }

    public Vector3 MuzzlePosition => GlobalTransform * MuzzleLocal.Origin;

    /// <summary>Where the left hand belongs in rifle space: the foregrip, or the magazine and bolt catch mid-reload.</summary>
    public Vector3 LeftHandLocal { get; private set; }

    /// <summary>The textured material of the rifle and its dropped magazines.</summary>
    public static StandardMaterial3D BodyMaterial => bodyMaterial ??= CreateBodyMaterial();

    public override void _Ready()
    {
        var model = GameAssets.Instantiate(ScenePath);
        AddChild(model);
        foreach (var node in model.FindChildren("Socket_*", owned: false).OfType<Node3D>())
        {
            var local = model.Transform * node.Transform;
            switch (node.Name.ToString())
            {
                case "Socket_Foregrip": ForegripLocal = local; break;
                case "Socket_Muzzle": MuzzleLocal = local; break;
                case "Socket_Butt": ButtLocal = local; break;
                case "Socket_Eye": EyeLocal = local; break;
                case "Socket_MagWell": MagWellLocal = local; break;
            }
        }
        foreach (var mesh in GameAssets.MeshInstances(model))
        {
            mesh.Layers = RenderLayers.Actors;
            for (var surface = 0; surface < mesh.Mesh.GetSurfaceCount(); surface++)
            {
                // The lens keeps the material it was exported with.
                if (mesh.Mesh.SurfaceGetMaterial(surface)?.ResourceName == "Rifle")
                {
                    mesh.SetSurfaceOverrideMaterial(surface, BodyMaterial);
                }
            }
        }

        skeleton = model.FindChildren("*", "Skeleton3D", owned: false).OfType<Skeleton3D>().First();
        skeletonLocal = Transform3D.Identity;
        for (Node node = skeleton; node != this; node = node.GetParent())
        {
            skeletonLocal = ((Node3D)node).Transform * skeletonLocal;
        }
        magazineBone = skeleton.FindBone("Magazine");
        boltCatchLocal = skeletonLocal * skeleton.GetBoneGlobalRest(skeleton.FindBone("BoltCatch")).Origin + BoltCatchSlap;
        // The clips are posed from code each frame, so a reload follows its progress exactly.
        animations = model.FindChildren("*", "AnimationPlayer", owned: false).OfType<AnimationPlayer>().First();
        animations.CallbackModeProcess = AnimationMixer.AnimationCallbackModeProcess.Manual;
        Pose("Idle", 0);
        LeftHandLocal = ForegripLocal.Origin;

        flashMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/fx_glow.gdshader") };
        flashMaterial.SetShaderParameter("dot_texture", FxLibrary.SoftDotTexture);
        flashMaterial.SetShaderParameter("color", new Color(1f, 0.55f, 0.18f));
        flashMaterial.SetShaderParameter("intensity", 3f);
        flashMaterial.SetShaderParameter("coverage", 0.9f);
        flashMaterial.SetShaderParameter("core_heat", 1f);
        flashSprite = new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = Vector2.One * 0.42f, Material = flashMaterial },
            Transform = MuzzleLocal.Translated(new Vector3(0, 0, -0.06f)),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
        };
        AddChild(flashSprite);
        flashLight = new OmniLight3D
        {
            LightColor = new Color(1f, 0.7f, 0.4f),
            LightEnergy = 5f,
            OmniRange = 5f,
            Position = MuzzleLocal.Origin + new Vector3(0, 0, -0.1f),
            Visible = false,
        };
        AddChild(flashLight);
    }

    public override void _Process(double delta)
    {
        if (flashTimer <= 0)
        {
            return;
        }
        flashTimer -= (float)delta;
        var t = Mathf.Clamp(flashTimer / FlashDuration, 0, 1);
        flashMaterial.SetShaderParameter("opacity", t);
        flashLight.LightEnergy = 5f * t;
        if (flashTimer <= 0)
        {
            flashSprite.Visible = false;
            flashLight.Visible = false;
        }
    }

    /// <summary>A shot: the muzzle flash and one cycle of the action. The last round locks the bolt open.</summary>
    public void Fire(bool lastRound = false)
    {
        flashTimer = FlashDuration;
        flashSprite.Visible = true;
        flashLight.Visible = true;
        flashSprite.Scale = Vector3.One * (float)GD.RandRange(0.8, 1.2);
        flashSprite.RotateObjectLocal(Vector3.Back, (float)GD.RandRange(0, Mathf.Tau));

        boltLocked = lastRound;
        clip = lastRound ? "FireLast" : Tuning.Current.FullAuto ? "Fire" : "FireSemi";
        clipTime = clip == "FireSemi" ? SemiShotTime : 0f;
    }

    /// <summary>
    /// Poses the moving parts for this frame. <paramref name="reload"/> is the reload's progress, 0..1, or 0 when not
    /// reloading. Call before the rig places the hands, which read <see cref="LeftHandLocal"/>.
    /// </summary>
    public void Animate(float delta, float reload)
    {
        var magazineHidden = false;
        if (reload > 0)
        {
            var starting = reloadClip == null;
            if (starting)
            {
                reloadClip = boltLocked ? "ReloadEmpty" : "Reload";
                magazineDropped = false;
            }
            reloadTime = reload * ClipLength(reloadClip!);
            Pose(reloadClip!, reloadTime);
            // The clip swaps magazines a hand's length below the well; the mannequin's shorter reach carries the
            // fresh one from nearer in. (The old one drops as a body of its own, so its fall is not shown.)
            var rest = skeleton.GetBoneRest(magazineBone).Origin;
            skeleton.SetBonePosePosition(magazineBone, rest + (skeleton.GetBonePosePosition(magazineBone) - rest) * MagazineTravel);
            var magazine = MagazineLocal();
            if (starting)
            {
                lastMagazinePosition = GlobalTransform * magazine.Origin;
            }
            if (!magazineDropped && reloadTime >= MagazineDropTime)
            {
                magazineDropped = true;
                DropMagazine(magazine, delta);
            }
            magazineHidden = magazineDropped && reloadTime < FreshMagazineTime;
            lastMagazinePosition = GlobalTransform * magazine.Origin;
            LeftHandLocal = ReloadHand(reloadClip == "ReloadEmpty", reloadTime, magazine * MagazineGrip);
        }
        else
        {
            if (reloadClip != null)
            {
                // Finished or cut short (thrown mid-reload): settle on a clip's last pose, the bolt still locked
                // open if the swap never got there.
                var finished = reloadTime >= SeatedTime;
                boltLocked &= !finished;
                clip = boltLocked ? "FireLast" : reloadClip;
                clipTime = ClipLength(clip);
                reloadClip = null;
            }
            clipTime = Mathf.Min(clipTime + delta, ClipLength(clip));
            Pose(clip, clipTime);
            LeftHandLocal = ForegripLocal.Origin;
        }
        // The magazine is part of the skinned mesh: collapsing its bone hides it.
        if (magazineHidden)
        {
            skeleton.SetBonePoseScale(magazineBone, Vector3.One * 0.0001f);
        }
    }

    /// <summary>
    /// The left hand's target through a reload: down after the magazine as it drops, holding the fresh one up into
    /// the well, then (from empty) onto the bolt catch, and back to the foregrip.
    /// </summary>
    private Vector3 ReloadHand(bool fromEmpty, float t, Vector3 magazine)
    {
        var reach = Mathf.SmoothStep(ReachStart, ReachEnd, t);
        var task = magazine;
        float back;
        if (fromEmpty)
        {
            task = magazine.Lerp(boltCatchLocal, Mathf.SmoothStep(SeatedTime, BoltCatchReached, t));
            back = Mathf.SmoothStep(EmptyReturnStart, EmptyReturnEnd, t);
        }
        else
        {
            back = Mathf.SmoothStep(SeatedTime, ReturnEnd, t);
        }
        return ForegripLocal.Origin.Lerp(task, reach * (1f - back));
    }

    /// <summary>The magazine's transform in rifle space (origin at its top), as the current pose has it.</summary>
    private Transform3D MagazineLocal()
    {
        var rest = skeleton.GetBoneGlobalRest(magazineBone);
        var pose = skeleton.GetBoneGlobalPose(magazineBone);
        return skeletonLocal * pose * rest.AffineInverse() * new Transform3D(Basis.Identity, rest.Origin);
    }

    private void DropMagazine(Transform3D magazine, float delta)
    {
        var world = (GlobalTransform * magazine).Orthonormalized();
        var velocity = delta > 0 ? (world.Origin - lastMagazinePosition) / delta : Vector3.Zero;
        if (velocity.LengthSquared() > 16f)
        {
            velocity = velocity.Normalized() * 4f;
        }
        DroppedMagazine.Drop(world, velocity + world.Basis * new Vector3(0, -1.2f, 0));
    }

    private void Pose(string name, float time)
    {
        if (animations.CurrentAnimation != name)
        {
            animations.Play(name);
        }
        animations.Seek(time, update: true);
    }

    private float ClipLength(string name) => (float)animations.GetAnimation(name).Length;

    private static StandardMaterial3D CreateBodyMaterial()
    {
        var orm = GD.Load<Texture2D>("res://assets/rifle/T_Rifle_ORM.png");
        return new StandardMaterial3D
        {
            ResourceName = "Rifle",
            AlbedoTexture = GD.Load<Texture2D>("res://assets/rifle/T_Rifle_BaseColor.png"),
            NormalEnabled = true,
            NormalTexture = GD.Load<Texture2D>("res://assets/rifle/T_Rifle_Normal.png"),
            AOEnabled = true,
            AOTexture = orm,
            AOTextureChannel = BaseMaterial3D.TextureChannel.Red,
            Roughness = 1f,
            RoughnessTexture = orm,
            RoughnessTextureChannel = BaseMaterial3D.TextureChannel.Green,
            Metallic = 1f,
            MetallicTexture = orm,
            MetallicTextureChannel = BaseMaterial3D.TextureChannel.Blue,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
        };
    }
}
