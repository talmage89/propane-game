using System.Collections.Generic;
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
    public required WorldContext World { get; init; }

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

    /// <summary>Live contact point in the magazine's frame (origin at its top).</summary>
    private Vector3 MagazineGrip => new(0, -World.Tuning.RifleMagazineGripDown, World.Tuning.RifleMagazineGripBack);

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
    private string posedClip = "";
    private readonly Dictionary<string, (StringName Name, float Length)> clips = new();
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
    public Vector3 BarrelForward => -GlobalBasis.Z.Normalized();

    /// <summary>Rotate about the muzzle so alignment preserves the obstruction ray's origin.</summary>
    public static Transform3D AlignBarrel(Transform3D rifle, Vector3 muzzleLocal, Vector3 target)
    {
        var muzzle = rifle * muzzleLocal;
        var direction = target - muzzle;
        if (direction.LengthSquared() < 0.000001f) return rifle;
        var rotation = new Basis(new Quaternion(-rifle.Basis.Z.Normalized(), direction.Normalized()));
        var basis = rotation * rifle.Basis;
        return new Transform3D(basis, muzzle - basis * muzzleLocal);
    }

    public void AlignBarrel(Vector3 target) => GlobalTransform = AlignBarrel(GlobalTransform, MuzzleLocal.Origin, target);

    /// <summary>Where the left hand belongs in rifle space: the foregrip, or the magazine and bolt catch mid-reload.</summary>
    public Vector3 LeftHandLocal { get; private set; }

    /// <summary>Support contact frame follows the moving magazine, blended back to the foregrip.</summary>
    public Basis LeftGripBasis { get; private set; } = Basis.Identity;
    public bool ReloadFromEmpty => reloadClip == "ReloadEmpty";
    public float MagazineHold { get; private set; }
    public float SupportHold { get; private set; } = 1f;
    public float BoltHold { get; private set; }
    public Transform3D MagazinePose { get; private set; }
    public bool MagazineVisible { get; private set; } = true;

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
        foreach (var name in new[] { "Idle", "Fire", "FireSemi", "FireLast", "Reload", "ReloadEmpty" })
        {
            var key = new StringName(name);
            clips.Add(name, (key, (float)animations.GetAnimation(key).Length));
        }
        Pose("Idle", 0);
        LeftHandLocal = ForegripLocal.Origin;
        MagazinePose = MagazineLocal();

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
        flashSprite.Scale = Vector3.One * World.Random.RandfRange(0.8f, 1.2f);
        flashSprite.RotateObjectLocal(Vector3.Back, World.Random.RandfRange(0, Mathf.Tau));

        boltLocked = lastRound;
        clip = lastRound ? "FireLast" : World.Tuning.FullAuto ? "Fire" : "FireSemi";
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
            PreviewReloadContact(reload);
            var magazine = MagazinePose;
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
            MagazinePose = MagazineLocal();
            LeftHandLocal = ForegripLocal.Origin;
            LeftGripBasis = Basis.Identity;
            MagazineHold = 0;
            BoltHold = 0;
            SupportHold = 1;
        }
        // The magazine is part of the skinned mesh: collapsing its bone hides it.
        MagazineVisible = !magazineHidden;
        if (magazineHidden)
        {
            skeleton.SetBonePoseScale(magazineBone, Vector3.One * 0.0001f);
        }
    }

    /// <summary>Sample presentation contacts without spawning a magazine or advancing the rifle's state.
    /// The rig can retry a bounded reload phase on its curved clearance path before committing Animate.</summary>
    public void PreviewReloadContact(float progress)
    {
        if (progress <= 0)
        {
            LeftHandLocal = ForegripLocal.Origin;
            LeftGripBasis = Basis.Identity;
            MagazineHold = BoltHold = 0;
            SupportHold = 1;
            return;
        }
        var name = reloadClip ?? (boltLocked ? "ReloadEmpty" : "Reload");
        var time = progress * ClipLength(name);
        Pose(name, time);
        // Keep this identical for trial and committed poses: contacts follow the visible magazine.
        var rest = skeleton.GetBoneRest(magazineBone).Origin;
        skeleton.SetBonePosePosition(magazineBone, rest + (skeleton.GetBonePosePosition(magazineBone) - rest) * World.Tuning.RifleMagazineTravel);
        MagazinePose = MagazineLocal();
        LeftHandLocal = ReloadHand(name == "ReloadEmpty", time, MagazinePose * MagazineGrip);
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
            var boltReach = Mathf.SmoothStep(SeatedTime, BoltCatchReached, t);
            task = magazine.Lerp(boltCatchLocal, boltReach) + Vector3.Left *
                (World.Tuning.RifleBoltApproachClearance * Mathf.Sin(Mathf.Pi * boltReach));
            back = Mathf.SmoothStep(EmptyReturnStart, EmptyReturnEnd, t);
        }
        else
        {
            back = Mathf.SmoothStep(SeatedTime, ReturnEnd, t);
        }
        var contact = reach * (1f - back);
        MagazineHold = contact * (fromEmpty ? 1f - Mathf.SmoothStep(SeatedTime, BoltCatchReached, t) : 1f);
        BoltHold = contact - MagazineHold;
        SupportHold = (1 - contact) * (1 - contact) + MagazineHold * MagazineHold;
        LeftGripBasis = new Basis(Quaternion.Identity.Slerp(MagazinePose.Basis.Orthonormalized().GetRotationQuaternion(), MagazineHold));
        // Leave the magazine on its outside before turning the wrist or returning to the foregrip.
        // A straight lerp dragged the thumb through the seated magazine and the receiver.
        // Approach the foregrip from below: lateral clearance eases out sooner so already curled
        // fingertips do not sweep sideways through the grip on the last part of the return.
        var clearance = World.Tuning.RifleReloadClearance * Mathf.Sin(Mathf.Pi * contact);
        return ForegripLocal.Origin.Lerp(task, contact) + Vector3.Down * clearance + Vector3.Left * (clearance * contact);
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
        DroppedMagazine.Drop(World, world, velocity + world.Basis * new Vector3(0, -1.2f, 0));
    }

    private void Pose(string name, float time)
    {
        if (posedClip != name)
        {
            animations.Play(clips[name].Name);
            posedClip = name;
        }
        animations.Seek(time, update: true);
    }

    private float ClipLength(string name) => clips[name].Length;

    public override void _ExitTree()
    {
        foreach (var (name, _) in clips.Values) name.Dispose();
    }

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
