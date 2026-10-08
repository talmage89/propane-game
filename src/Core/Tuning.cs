using Godot;

namespace Propane.Core;

/// <summary>
/// Every gameplay and effect dial in one place. Edit <c>res://tuning.tres</c> in the inspector, or press F1 in game
/// for live sliders. Systems read these values at the moment they use them, so changes apply immediately.
/// </summary>
[GlobalClass]
public partial class Tuning : Resource
{
    public const string ProjectPath = "res://tuning.tres";
    /// <summary>Where an exported build keeps the player's saved dials, since <c>res://</c> is read-only there.</summary>
    public const string PlayerPath = "user://tuning.tres";

    private static Tuning? current;

    /// <summary>
    /// The file Save writes to: the project's own tuning in the editor, the player's copy in an exported build.
    /// </summary>
    public static string FilePath => OS.HasFeature("editor") ? ProjectPath : PlayerPath;

    /// <summary>The live tuning values. Loaded on first access from <see cref="FilePath"/>, falling back to the project's.</summary>
    public static Tuning Current
    {
        get
        {
            if (current != null)
            {
                return current;
            }
            var path = ResourceLoader.Exists(FilePath) ? FilePath : ProjectPath;
            current = (ResourceLoader.Exists(path) ? ResourceLoader.Load<Tuning>(path, cacheMode: ResourceLoader.CacheMode.Ignore) : null)
                ?? new Tuning();
            return current;
        }
    }

    public static Error SaveCurrent() => ResourceSaver.Save(Current, FilePath);

    // ---------------------------------------------------------------- Tank venting
    [ExportGroup("Tank Vent")]
    [Export(PropertyHint.Range, "0,1500,1,or_greater")] public float VentThrust { get; set; } = 260f;
    [Export(PropertyHint.Range, "0,2,0.01")] public float VentThrustRampTime { get; set; } = 0.2f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float VentThrustFlutter { get; set; } = 0.25f;
    /// <summary>How far the jet leans around the tank's axis. Makes a lying tank roll and pinwheel instead of pinning itself.</summary>
    [Export(PropertyHint.Range, "0,80,1")] public float VentSwirlDegrees { get; set; } = 40f;
    /// <summary>Top spin rate while venting (rad/s), so the swirl tumbles the tank rather than blurring it into a top.</summary>
    [Export(PropertyHint.Range, "1,50,0.5")] public float VentMaxSpin { get; set; } = 12f;
    [Export(PropertyHint.Range, "0.1,4,0.05")] public float VentFlameScale { get; set; } = 1.6f;
    [Export(PropertyHint.Range, "0,40,0.5")] public float VentLightEnergy { get; set; } = 6f;
    [Export(PropertyHint.Range, "1,60,0.5")] public float TankMass { get; set; } = 17f;

    // ---------------------------------------------------------------- Blast physics
    [ExportGroup("Blast")]
    [Export(PropertyHint.Range, "1,40,0.1")] public float BlastRadius { get; set; } = 10f;
    [Export(PropertyHint.Range, "0,80,0.5")] public float BlastSpeed { get; set; } = 15f;
    [Export(PropertyHint.Range, "0.2,4,0.05")] public float BlastFalloff { get; set; } = 1.2f;
    [Export(PropertyHint.Range, "0,2,0.01")] public float BlastUpwardBias { get; set; } = 0.55f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float BlastMassInfluence { get; set; } = 0.55f;
    [Export(PropertyHint.Range, "0,40,0.5")] public float BlastSpin { get; set; } = 8f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float BlastOcclusion { get; set; } = 0.35f;
    [Export(PropertyHint.Range, "0,20,0.1")] public float ChainRadius { get; set; } = 3.5f;
    [Export(PropertyHint.Range, "0,2,0.01")] public float ChainFuseMin { get; set; } = 0.12f;
    [Export(PropertyHint.Range, "0,2,0.01")] public float ChainFuseMax { get; set; } = 0.4f;
    [Export(PropertyHint.Range, "0,30,0.1")] public float BreakawaySpeed { get; set; } = 3f;

    // ---------------------------------------------------------------- Blast effects
    [ExportGroup("Explosion Effects")]
    [Export(PropertyHint.Range, "0.1,4,0.05")] public float FireballScale { get; set; } = 1f;
    [Export(PropertyHint.Range, "0,4,0.05")] public float SmokeAmount { get; set; } = 1f;
    [Export(PropertyHint.Range, "0,4,0.05")] public float EmberAmount { get; set; } = 1f;
    [Export(PropertyHint.Range, "0,200,1")] public float FlashEnergy { get; set; } = 40f;
    [Export(PropertyHint.Range, "1,80,0.5")] public float FlashRange { get; set; } = 22f;
    [Export(PropertyHint.Range, "0.05,3,0.01")] public float FlashDuration { get; set; } = 0.55f;
    [Export(PropertyHint.Range, "0,60,0.5")] public float DebrisSpeed { get; set; } = 9f;
    [Export(PropertyHint.Range, "0,60,0.5")] public float DebrisSpin { get; set; } = 22f;
    [Export(PropertyHint.Range, "0,10,0.1")] public float ScorchSize { get; set; } = 4.2f;
    [Export(PropertyHint.Range, "0,4,0.05")] public float ShockwaveScale { get; set; } = 1f;

    // ---------------------------------------------------------------- Feedback
    [ExportGroup("Feedback")]
    [Export(PropertyHint.Range, "0,4,0.05")] public float CameraShake { get; set; } = 1f;
    [Export(PropertyHint.Range, "1,120,1")] public float CameraShakeDistance { get; set; } = 45f;
    [Export(PropertyHint.Range, "0,0.3,0.005")] public float HitstopDuration { get; set; } = 0.07f;
    [Export(PropertyHint.Range, "0,40,0.5")] public float HitstopRadius { get; set; } = 9f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float HitstopTimeScale { get; set; } = 0.05f;

    // ---------------------------------------------------------------- Player and ragdoll
    [ExportGroup("Player")]
    [Export(PropertyHint.Range, "0.0002,0.01,0.0001")] public float MouseSensitivity { get; set; } = 0.0022f;
    [Export(PropertyHint.Range, "1,20,0.1")] public float RunSpeed { get; set; } = 6f;
    [Export(PropertyHint.Range, "1,30,0.1")] public float SprintSpeed { get; set; } = 9.5f;
    [Export(PropertyHint.Range, "0.5,15,0.1")] public float AimMoveSpeed { get; set; } = 3.6f;
    [Export(PropertyHint.Range, "1,120,1")] public float Acceleration { get; set; } = 45f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float AirControl { get; set; } = 0.3f;
    [Export(PropertyHint.Range, "0,20,0.1")] public float JumpVelocity { get; set; } = 6.4f;
    [Export(PropertyHint.Range, "1,60,0.5")] public float PlayerGravity { get; set; } = 19f;
    [Export(PropertyHint.Range, "0,40,0.5")] public float RagdollRadius { get; set; } = 6.5f;
    [Export(PropertyHint.Range, "0,40,0.5")] public float StaggerRadius { get; set; } = 11f;
    [Export(PropertyHint.Range, "0,4,0.05")] public float PlayerKnockback { get; set; } = 1f;
    [Export(PropertyHint.Range, "0.2,10,0.1")] public float RagdollMinTime { get; set; } = 1.4f;
    /// <summary>Longest the ragdoll lies on the ground before getting up anyway; time in the air never counts.</summary>
    [Export(PropertyHint.Range, "0.5,20,0.1")] public float RagdollMaxTime { get; set; } = 6f;

    // ---------------------------------------------------------------- Camera
    [ExportGroup("Camera")]
    [Export(PropertyHint.Range, "30,110,1")] public float FieldOfView { get; set; } = 72f;
    [Export(PropertyHint.Range, "1,10,0.05")] public float CameraDistance { get; set; } = 3.4f;
    [Export(PropertyHint.Range, "-2,2,0.01")] public float ShoulderOffset { get; set; } = 0.62f;
    [Export(PropertyHint.Range, "0.5,3,0.01")] public float CameraHeight { get; set; } = 1.62f;

    // ---------------------------------------------------------------- Rifle
    [ExportGroup("Rifle")]
    /// <summary>Holding the trigger keeps firing at the fire rate; off, every shot is a click.</summary>
    [Export] public bool FullAuto { get; set; } = true;
    [Export(PropertyHint.Range, "1,20,0.1")] public float FireRate { get; set; } = 9f;
    [Export(PropertyHint.Range, "0,15,0.05")] public float HipSpreadDegrees { get; set; } = 3.2f;
    [Export(PropertyHint.Range, "0,10,0.01")] public float AimSpreadDegrees { get; set; } = 0.3f;
    [Export(PropertyHint.Range, "0,10,0.05")] public float MoveSpreadDegrees { get; set; } = 1.6f;
    [Export(PropertyHint.Range, "0,5,0.05")] public float ShotBloomDegrees { get; set; } = 0.7f;
    [Export(PropertyHint.Range, "0,60,0.5")] public float BloomRecovery { get; set; } = 9f;
    [Export(PropertyHint.Range, "0,5,0.05")] public float RecoilKickDegrees { get; set; } = 0.7f;
    [Export(PropertyHint.Range, "0,200,1")] public float BulletImpulse { get; set; } = 30f;

    // ---------------------------------------------------------------- World
    [ExportGroup("World")]
    [Export(PropertyHint.Range, "1,80,1")] public int TankCount { get; set; } = 30;
    /// <summary>Tanks stand in groups of between these sizes, close enough to set each other off.</summary>
    [Export(PropertyHint.Range, "1,16,1")] public int ClusterSizeMin { get; set; } = 1;
    [Export(PropertyHint.Range, "1,16,1")] public int ClusterSizeMax { get; set; } = 10;
    [Export(PropertyHint.Range, "0,15,0.1")] public float ClearCelebrationTime { get; set; } = 3f;
    [Export(PropertyHint.Range, "0.3,10,0.1")] public float TransitionTime { get; set; } = 2.4f;
    [Export] public int FixedSeed { get; set; } = 0;

    // ---------------------------------------------------------------- HUD
    [ExportGroup("HUD")]
    /// <summary>Small arrows toward every group of tanks: on the screen edge, or over the group when in view.</summary>
    [Export] public bool TankArrows { get; set; } = true;
}
