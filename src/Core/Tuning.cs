using System.Linq;
using System.Reflection;
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

    private static Tuning? savedTemplate;
    private static readonly PropertyInfo[] LocalTuners = typeof(Tuning)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        .Where(property => property.CanWrite && !property.IsDefined(typeof(ExportAttribute)) &&
            (property.PropertyType == typeof(float) || property.PropertyType == typeof(int) || property.PropertyType == typeof(bool)))
        .ToArray();

    /// <summary>Godot duplicates exported properties; copy local brain dials too, without adding them to snapshots.</summary>
    public Tuning Copy()
    {
        var copy = (Tuning)Duplicate();
        copy.CopyLocalTunersFrom(this);
        return copy;
    }

    internal void CopyLocalTunersFrom(Tuning source)
    {
        foreach (var property in LocalTuners) { property.SetValue(this, property.GetValue(source)); }
    }

    /// <summary>
    /// The file Save writes to: the project's own tuning in the editor, the player's copy in an exported build.
    /// </summary>
    public static string FilePath => OS.HasFeature("editor") ? ProjectPath : PlayerPath;

    /// <summary>The application saved template, used only at composition and by application UI. Worlds own copies. Loaded on first access from <see cref="FilePath"/>, falling back to the project's.</summary>
    public static Tuning SavedTemplate
    {
        get
        {
            if (savedTemplate != null)
            {
                return savedTemplate;
            }
            var path = ResourceLoader.Exists(FilePath) ? FilePath : ProjectPath;
            savedTemplate = (ResourceLoader.Exists(path) ? ResourceLoader.Load<Tuning>(path, cacheMode: ResourceLoader.CacheMode.Ignore) : null)
                ?? new Tuning();
            return savedTemplate;
        }
    }

    /// <summary>Save a panel's explicit target and update the application's template for future worlds.</summary>
    public static Error SaveTemplate(Tuning target)
    {
        var error = ResourceSaver.Save(target, FilePath);
        if (error == Error.Ok)
        {
            foreach (var name in ValueNames(target)) { SavedTemplate.Set(name, target.Get(name)); }
        }
        return error;
    }

    /// <summary>The values baked into the build (<c>res://tuning.tres</c>), ignoring any the player saved.</summary>
    public static Tuning LoadBaked() =>
        (ResourceLoader.Exists(ProjectPath) ? ResourceLoader.Load<Tuning>(ProjectPath, cacheMode: ResourceLoader.CacheMode.Ignore) : null) ?? new Tuning();

    /// <summary>Every tuning value (numbers and toggles) by property name.</summary>
    public Godot.Collections.Dictionary Snapshot()
    {
        var values = new Godot.Collections.Dictionary();
        foreach (var name in ValueNames(this))
        {
            values[name] = Get(name);
        }
        return values;
    }

    /// <summary>A tuning built from defaults with a <see cref="Snapshot"/> applied over them.</summary>
    public static Tuning FromSnapshot(Godot.Collections.Dictionary values)
    {
        var tuning = new Tuning();
        tuning.Apply(values);
        return tuning;
    }

    private static System.Collections.Generic.Dictionary<string, (Variant.Type Type, double Min, double Max)>? schema;

    /// <summary>Validates every entry before any value changes. Export hints define inclusive network limits.</summary>
    public static void ValidateSnapshot(Godot.Collections.Dictionary values, bool complete = false)
    {
        if (schema == null)
        {
            schema = new();
            using var defaults = new Tuning();
            var names = new System.Collections.Generic.HashSet<string>(ValueNames(defaults));
            foreach (var entry in defaults.GetPropertyList())
            {
                var name = entry["name"].AsString();
                if (!names.Contains(name)) { continue; }
                var type = (Variant.Type)entry["type"].AsInt64();
                var range = entry["hint_string"].AsString().Split(',');
                var min = type == Variant.Type.Int ? int.MinValue : double.MinValue;
                var max = type == Variant.Type.Int ? int.MaxValue : double.MaxValue;
                if ((PropertyHint)entry["hint"].AsInt64() == PropertyHint.Range)
                {
                    min = double.Parse(range[0], System.Globalization.CultureInfo.InvariantCulture);
                    max = double.Parse(range[1], System.Globalization.CultureInfo.InvariantCulture);
                }
                schema[name] = (type, min, max);
            }
        }
        if (complete && values.Count != schema.Count) { throw new System.IO.InvalidDataException("incomplete tuning snapshot"); }
        foreach (var (key, value) in values)
        {
            if (key.VariantType != Variant.Type.String || !schema.TryGetValue(key.AsString(), out var spec) || value.VariantType != spec.Type)
            {
                throw new System.IO.InvalidDataException($"unknown tuner or wrong type: {key}");
            }
            if (spec.Type == Variant.Type.Bool) { continue; }
            var number = value.AsDouble();
            if (!double.IsFinite(number) || number < spec.Min || number > spec.Max)
            {
                throw new System.IO.InvalidDataException($"tuner outside declared range: {key}");
            }
        }
    }

    /// <summary>Atomically applies a validated snapshot; unknown names and invalid values are refused.</summary>
    public void Apply(Godot.Collections.Dictionary values)
    {
        ValidateSnapshot(values);
        foreach (var (key, value) in values)
        {
            var name = key.AsString();
            Set(name, value);
        }
    }

    /// <summary>Names of the exported number and toggle properties, in declaration order.</summary>
    public static System.Collections.Generic.IEnumerable<string> ValueNames(Tuning tuning)
    {
        foreach (var entry in tuning.GetPropertyList())
        {
            var usage = (PropertyUsageFlags)entry["usage"].AsInt64();
            var type = (Variant.Type)entry["type"].AsInt64();
            if (usage.HasFlag(PropertyUsageFlags.ScriptVariable) && usage.HasFlag(PropertyUsageFlags.Editor) &&
                type is Variant.Type.Float or Variant.Type.Int or Variant.Type.Bool)
            {
                yield return entry["name"].AsString();
            }
        }
    }

    // ---------------------------------------------------------------- Actor presentation
    [ExportGroup("Actor Detail")]
    [Export] public bool ActorPoseLod { get; set; } = true;
    [Export(PropertyHint.Range, "0,200,1")] public float ActorPoseNearDistance { get; set; } = 30f;
    [Export(PropertyHint.Range, "0,500,1")] public float ActorPoseFarDistance { get; set; } = 80f;
    [Export(PropertyHint.Range, "0,20,0.5")] public float ActorPoseHysteresis { get; set; } = 3f;
    [Export(PropertyHint.Range, "1,240,1")] public float ActorPoseRate { get; set; } = 30f;
    [Export(PropertyHint.Range, "1,240,1")] public float ActorPoseFarRate { get; set; } = 15f;
    [Export(PropertyHint.Range, "1,240,1")] public float ActorPoseHiddenRate { get; set; } = 10f;

    // ---------------------------------------------------------------- Melee
    [ExportGroup("Melee")]
    [Export(PropertyHint.Range, "0.05,1,0.01")] public float MeleeWindup { get; set; } = 0.24f;
    [Export(PropertyHint.Range, "0.05,1,0.01")] public float MeleeActive { get; set; } = 0.16f;
    [Export(PropertyHint.Range, "0.05,2,0.01")] public float MeleeRecovery { get; set; } = 0.4f;
    [Export(PropertyHint.Range, "0.1,10,0.1")] public float MeleeReloadReturnSpeed { get; set; } = 3f;
    [Export(PropertyHint.Range, "0.1,5,0.01")] public float MeleeCooldown { get; set; } = 0.65f;
    [Export(PropertyHint.Range, "0,10,0.1")] public float MeleeLungeSpeed { get; set; } = 2.6f;
    [Export(PropertyHint.Range, "0.1,4,0.05")] public float MeleeRange { get; set; } = 1.65f;
    [Export(PropertyHint.Range, "1,90,1")] public float MeleeHalfAngleDegrees { get; set; } = 50f;
    [Export(PropertyHint.Range, "0.1,2,0.05")] public float MeleeContactHeight { get; set; } = 1f;
    [Export(PropertyHint.Range, "0,20,0.1")] public float MeleeLaunchSpeed { get; set; } = 5f;
    [Export(PropertyHint.Range, "0,10,0.1")] public float MeleeLaunchLift { get; set; } = 1.6f;
    [Export(PropertyHint.Range, "0,10,0.1")] public float MeleeSpin { get; set; } = 3f;
    [Export(PropertyHint.Range, "0,500,1")] public float MeleePropImpulse { get; set; } = 35f;
    [Export(PropertyHint.Range, "0,45,1")] public float MeleeTorsoLeanDegrees { get; set; } = 15f;
    [Export(PropertyHint.Range, "0,60,1")] public float MeleeTorsoTwistDegrees { get; set; } = 35f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float MeleeRelayGrace { get; set; } = 0.3f;
    [Export(PropertyHint.Range, "1,128,1")] public int MeleeMaxContacts { get; set; } = 32;

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
    /// <summary>Keep individual vent lights through ordinary counts; merge only beyond this crowded threshold.</summary>
    [Export(PropertyHint.Range, "0,1024,1")] public int VentLightMergeThreshold { get; set; } = 40;
    [Export(PropertyHint.Range, "1,64,1")] public int VentLightClusterCount { get; set; } = 8;
    [Export(PropertyHint.Range, "0,5,0.1")] public float VentLightMergeRadius { get; set; } = 2f;
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
    /// <summary>Right stick look at full deflection, in degrees per second.</summary>
    [Export(PropertyHint.Range, "10,720,5")] public float StickLookDegreesPerSecond { get; set; } = 180f;
    [Export(PropertyHint.Range, "1,4,0.1")] public float StickLookCurve { get; set; } = 1.8f;
    [Export(PropertyHint.Range, "0,0.9,0.01")] public float StickDeadzone { get; set; } = 0.2f;
    [Export(PropertyHint.Range, "1,20,0.1")] public float RunSpeed { get; set; } = 6f;
    [Export(PropertyHint.Range, "1,30,0.1")] public float SprintSpeed { get; set; } = 9.5f;
    /// <summary>Upright aimed walk, with playback scaled from the clip's native 0.98 m/s.</summary>
    [Export(PropertyHint.Range, "0.1,5,0.01")] public float CreepSpeed { get; set; } = 1.6f;
    [Export(PropertyHint.Range, "0,0.5,0.01")] public float CoyoteTime { get; set; } = 0.12f;
    [Export(PropertyHint.Range, "0,0.5,0.01")] public float JumpBufferTime { get; set; } = 0.12f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float SprintForwardThreshold { get; set; } = 0.3f;
    [Export(PropertyHint.Range, "0,4,0.05")] public float ReadyHoldTime { get; set; } = 1.3f;
    [Export(PropertyHint.Range, "1,40,0.5")] public float TurnSharpness { get; set; } = 14f;
    [Export(PropertyHint.Range, "1,40,0.5")] public float AimBlendSpeed { get; set; } = 9f;
    [Export(PropertyHint.Range, "1,40,0.5")] public float SprintRaiseSpeed { get; set; } = 7f;
    [Export(PropertyHint.Range, "1,40,0.5")] public float SprintLowerSpeed { get; set; } = 17.5f;
    [Export(PropertyHint.Range, "1,40,0.5")] public float ReadyRaiseSpeed { get; set; } = 14f;
    [Export(PropertyHint.Range, "0.1,20,0.1")] public float ReadyLowerSpeed { get; set; } = 2.5f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float StaggerMoveControl { get; set; } = 0.25f;
    [Export(PropertyHint.Range, "0,30,0.5")] public float GroundKnockbackFriction { get; set; } = 15f;
    [Export(PropertyHint.Range, "0,10,0.1")] public float AirKnockbackFriction { get; set; } = 1.5f;
    [Export(PropertyHint.Range, "0,2,0.05")] public float LandingMinAirTime { get; set; } = 0.3f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float LandingTraumaPerSecond { get; set; } = 0.15f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float LandingTraumaMax { get; set; } = 0.25f;
    [Export(PropertyHint.Range, "0,120,1")] public float CombatSpineTwistDegrees { get; set; } = 70f;
    [Export(PropertyHint.Range, "0,180,1")] public float CombatTurnDegrees { get; set; } = 50f;
    [Export(PropertyHint.Range, "90,180,1")] public float BackpedalAngleDegrees { get; set; } = 108f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float MovingInputThreshold { get; set; } = 0.1f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float FacingInputThreshold { get; set; } = 0.2f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float AnimationIdleSpeed { get; set; } = 0.35f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float GaitBlendTime { get; set; } = 0.22f;
    [Export(PropertyHint.Range, "1,40,0.5")] public float AirPoseRaiseSpeed { get; set; } = 5f;
    [Export(PropertyHint.Range, "1,40,0.5")] public float AirPoseLowerSpeed { get; set; } = 10f;
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

    // ---------------------------------------------------------------- Locomotion presentation
    [ExportGroup("Locomotion")]
    [Export(PropertyHint.Range, "0.05,1,0.01")] public float LocomotionSettleTime { get; set; } = 0.18f;
    [Export(PropertyHint.Range, "0.5,4,0.01")] public float WalkStrideLength { get; set; } = 1.33f;
    [Export(PropertyHint.Range, "0.5,6,0.01")] public float JogStrideLength { get; set; } = 5.5f;
    [Export(PropertyHint.Range, "0.5,8,0.01")] public float SprintStrideLength { get; set; } = 6.3f;
    /// <summary>Ground per backpedal and sidestep cycle. Longer than the captured steps (0.79 m and 0.61 m) trades a
    /// little foot slide for a natural cadence at the 1.6 m/s creep.</summary>
    [Export(PropertyHint.Range, "0.3,3,0.01")] public float BackStrideLength { get; set; } = 1.0f;
    [Export(PropertyHint.Range, "0.3,3,0.01")] public float SideStrideLength { get; set; } = 0.85f;
    /// <summary>Turning on the spot steps sideways as fast as a foot this far from the body's axis swings.</summary>
    [Export(PropertyHint.Range, "0,1,0.01")] public float TurnStepRadius { get; set; } = 0.25f;
    [Export(PropertyHint.Range, "0.05,0.4,0.01")] public float TakeoffPoseTime { get; set; } = 0.12f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float TakeoffClipStart { get; set; } = 0.14f;
    [Export(PropertyHint.Range, "0.1,10,0.1")] public float TakeoffClipSpeed { get; set; } = 2.5f;
    [Export(PropertyHint.Range, "0,1.3,0.01")] public float JumpRiseClipTime { get; set; } = 0.3f;
    [Export(PropertyHint.Range, "0,1.3,0.01")] public float JumpApexClipTime { get; set; } = 0.7f;
    [Export(PropertyHint.Range, "0,1.3,0.01")] public float JumpFallClipTime { get; set; } = 1.2f;
    [Export(PropertyHint.Range, "0.1,20,0.1")] public float JumpSampleBlendSpeed { get; set; } = 4f;
    [Export(PropertyHint.Range, "0.1,5,0.1")] public float JumpApexSpeed { get; set; } = 1.2f;
    [Export(PropertyHint.Range, "0.05,1,0.01")] public float LandingSettleTime { get; set; } = 0.24f;
    [Export(PropertyHint.Range, "1,30,0.1")] public float LandingFullImpactSpeed { get; set; } = 8f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float LandingClipTime { get; set; } = 0.2f;

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
    [Export(PropertyHint.Range, "0,10,0.05")] public float AirSpreadDegrees { get; set; } = 2.5f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float AimMoveSpreadReduction { get; set; } = 0.6f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float AimRecoilScale { get; set; } = 0.6f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float ShotSprintPoseLimit { get; set; } = 0.3f;
    [Export(PropertyHint.Range, "0,10,0.05")] public float MoveSpreadDegrees { get; set; } = 1.6f;
    [Export(PropertyHint.Range, "0,5,0.05")] public float ShotBloomDegrees { get; set; } = 0.7f;
    [Export(PropertyHint.Range, "0,60,0.5")] public float BloomRecovery { get; set; } = 9f;
    [Export(PropertyHint.Range, "0,5,0.05")] public float RecoilKickDegrees { get; set; } = 0.28f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float RecoilReturnShare { get; set; } = 0.125f;
    [Export(PropertyHint.Range, "0,10,0.1")] public float RecoilSettleSpeed { get; set; } = 1f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float RecoilYawDegrees { get; set; } = 0.175f;
    [Export(PropertyHint.Range, "10,1000,10")] public float ShotRange { get; set; } = 400f;
    /// <summary>Where the logical aim holds the muzzle for obstruction, from the feet: height, right of and along the
    /// aim, at the hip and shouldered. Never the animated rifle, so no pose can change a hit; the defaults are the
    /// mean positions of the release's fitted muzzle.</summary>
    [Export(PropertyHint.Range, "0.5,2,0.01")] public float ShotMuzzleHipHeight { get; set; } = 1.26f;
    [Export(PropertyHint.Range, "-0.5,0.5,0.01")] public float ShotMuzzleHipSide { get; set; } = 0.13f;
    [Export(PropertyHint.Range, "0,1.5,0.01")] public float ShotMuzzleHipForward { get; set; } = 0.65f;
    [Export(PropertyHint.Range, "0.5,2,0.01")] public float ShotMuzzleAimHeight { get; set; } = 1.59f;
    [Export(PropertyHint.Range, "-0.5,0.5,0.01")] public float ShotMuzzleAimSide { get; set; } = 0.07f;
    [Export(PropertyHint.Range, "0,1.5,0.01")] public float ShotMuzzleAimForward { get; set; } = 0.76f;
    [Export(PropertyHint.Range, "0,200,1")] public float BulletImpulse { get; set; } = 30f;

    // ---------------------------------------------------------------- Rifle contact
    [ExportGroup("Rifle Contact")]
    /// <summary>Maximum change per second of the rig's aim, ready and sprint blends; logical state stays immediate.</summary>
    [Export(PropertyHint.Range, "0.1,30,0.1")] public float RifleStanceBlendSpeed { get; set; } = 10f;

    // Torso, eye landmark and reach (meters and degrees).
    [Export(PropertyHint.Range, "0,1,0.01")] public float RifleSpineLowerShare { get; set; } = 0.25f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float RifleSpineMiddleShare { get; set; } = 0.35f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float RifleSpineUpperShare { get; set; } = 0.4f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float RifleSprintPitchSuppression { get; set; } = 0.7f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float RifleHipHeadFollow { get; set; } = 0.45f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float RifleAimHeadFollow { get; set; } = 1f;
    /// <summary>Share of the head's turn toward the aim taken by the neck.</summary>
    [Export(PropertyHint.Range, "0,1,0.01")] public float RifleNeckShare { get; set; } = 0.4f;
    /// <summary>Fastest turn of the shown aim, degrees per second; logical aim and shots never wait for it.</summary>
    [Export(PropertyHint.Range, "60,3600,10")] public float RifleAimTurnSpeed { get; set; } = 720f;
    /// <summary>Shouldered, how far the neck and head bring the eye down to the sight (0 = head up, 1 = on it).</summary>
    [Export(PropertyHint.Range, "0,1,0.01")] public float RifleCheekWeld { get; set; } = 0.75f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float RifleHipPitchShare { get; set; } = 0.3f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float RifleAimPitchShare { get; set; } = 0.7f;
    [Export(PropertyHint.Range, "0,80,1")] public float RifleHipBladeDegrees { get; set; } = 28f;
    [Export(PropertyHint.Range, "0,80,1")] public float RifleAimBladeDegrees { get; set; } = 25f;
    [Export(PropertyHint.Range, "0,45,1")] public float RifleCheekRollDegrees { get; set; } = 15f;
    [Export(PropertyHint.Range, "-0.1,0.1,0.001")] public float RifleEyeSide { get; set; } = -0.032f;
    [Export(PropertyHint.Range, "0,0.2,0.001")] public float RifleEyeUp { get; set; } = 0.17f;
    [Export(PropertyHint.Range, "0,0.2,0.001")] public float RifleEyeForward { get; set; } = 0.105f;

    // Rifle placement from the shoulder pocket.
    [Export(PropertyHint.Range, "0,0.5,0.001")] public float RifleHipSightReferenceDown { get; set; } = 0.2f;
    [Export(PropertyHint.Range, "-0.1,0.2,0.001")] public float RifleShoulderInboard { get; set; } = 0.08f;
    [Export(PropertyHint.Range, "-0.1,0.2,0.001")] public float RifleShoulderUp { get; set; } = 0.06f;
    [Export(PropertyHint.Range, "-0.1,0.2,0.001")] public float RifleShoulderForward { get; set; } = 0.06f;
    [Export(PropertyHint.Range, "0,0.4,0.001")] public float RifleHipDown { get; set; } = 0.17f;
    [Export(PropertyHint.Range, "-0.2,0.2,0.001")] public float RifleHipOutboard { get; set; } = -0.07f;
    [Export(PropertyHint.Range, "-0.1,0.2,0.001")] public float RifleHipForward { get; set; } = 0.02f;
    [Export(PropertyHint.Range, "0,80,1")] public float RifleCarryPitchDegrees { get; set; } = 23f;
    [Export(PropertyHint.Range, "-60,60,1")] public float RifleCarryYawDegrees { get; set; } = 15f;
    [Export(PropertyHint.Range, "-60,60,1")] public float RifleCarryRollDegrees { get; set; } = -10f;
    [Export(PropertyHint.Range, "-60,60,1")] public float RifleReadyRollDegrees { get; set; } = -8f;
    [Export(PropertyHint.Range, "0,80,1")] public float RifleSprintPitchDegrees { get; set; } = 38f;
    [Export(PropertyHint.Range, "-60,60,1")] public float RifleSprintYawDegrees { get; set; } = 12f;
    [Export(PropertyHint.Range, "0,0.4,0.001")] public float RifleSprintDown { get; set; } = 0.12f;
    [Export(PropertyHint.Range, "0.05,0.8,0.001")] public float RifleReloadForward { get; set; } = 0.6f;
    [Export(PropertyHint.Range, "0,0.4,0.001")] public float RifleReloadDown { get; set; } = 0.14f;
    [Export(PropertyHint.Range, "0,80,1")] public float RifleReloadPitchDegrees { get; set; } = 17f;
    [Export(PropertyHint.Range, "-60,60,1")] public float RifleReloadYawDegrees { get; set; } = 28f;
    [Export(PropertyHint.Range, "-80,80,1")] public float RifleReloadRollDegrees { get; set; } = -38f;
    [Export(PropertyHint.Range, "0.01,0.3,0.01")] public float RifleReloadBlendFraction { get; set; } = 0.18f;
    [Export(PropertyHint.Range, "0,0.6,0.01")] public float RifleElbowOut { get; set; } = 0.3f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float RifleElbowDown { get; set; } = 0.6f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float RifleElbowBack { get; set; } = 0.3f;

    // Reload, recoil, wrist and finger poses of the rifle stance.
    [ExportGroup("Rifle Motion")]
    [Export(PropertyHint.Range, "0.1,2,0.01")] public float RifleReloadPhaseSpeed { get; set; } = 0.75f;
    [Export(PropertyHint.Range, "1,15,0.1")] public float RifleRecoilFrequency { get; set; } = 5f;
    [Export(PropertyHint.Range, "0.1,0.99,0.01")] public float RifleRecoilDamping { get; set; } = 0.65f;
    [Export(PropertyHint.Range, "0,4,0.01")] public float RifleRecoilImpulse { get; set; } = 2f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float RifleRecoilTorsoShare { get; set; } = 0.65f;

    [Export(PropertyHint.Range, "0,5,0.01")] public float CameraRecoilKickDegrees { get; set; } = 0.35f;

    // Visual recoil; both wrists stay attached.
    [Export(PropertyHint.Range, "0,0.1,0.001")] public float RifleRecoilBack { get; set; } = 0.045f;
    [Export(PropertyHint.Range, "0,10,0.1")] public float RifleRecoilLiftDegrees { get; set; } = 5f;

    // Wrist frames fitted to the mannequin palms and the authored grips.
    [Export(PropertyHint.Range, "0,0.1,0.001")] public float RifleRightWristSide { get; set; } = 0.0478f;
    [Export(PropertyHint.Range, "-0.1,0.1,0.001")] public float RifleRightWristUp { get; set; } = -0.0419f;
    [Export(PropertyHint.Range, "0,0.2,0.001")] public float RifleRightWristBack { get; set; } = 0.121f;
    [Export(PropertyHint.Range, "-60,60,1")] public float RifleRightWristPitchDegrees { get; set; } = 13.0f;
    [Export(PropertyHint.Range, "0,0.1,0.001")] public float RifleLeftWristSide { get; set; } = 0.0425f;
    [Export(PropertyHint.Range, "-0.1,0.1,0.001")] public float RifleLeftWristUp { get; set; } = -0.0115f;
    [Export(PropertyHint.Range, "0,0.2,0.001")] public float RifleLeftWristBack { get; set; } = 0.1181f;
    [Export(PropertyHint.Range, "-60,60,1")] public float RifleLeftWristPitchDegrees { get; set; } = -1.6f;
    [Export(PropertyHint.Range, "0.03,0.1,0.001")] public float RifleMagazineWristSide { get; set; } = 0.0478f;
    [Export(PropertyHint.Range, "-0.1,0.1,0.001")] public float RifleMagazineWristUp { get; set; } = -0.01f;
    [Export(PropertyHint.Range, "0.05,0.2,0.001")] public float RifleMagazineWristBack { get; set; } = 0.1333f;
    [Export(PropertyHint.Range, "-60,60,1")] public float RifleMagazineWristPitchDegrees { get; set; } = -2.0f;
    [Export(PropertyHint.Range, "0.03,0.1,0.001")] public float RifleBoltWristSide { get; set; } = 0.075f;
    [Export(PropertyHint.Range, "0,0.15,0.001")] public float RifleBoltWristDown { get; set; } = 0.055f;

    // Reload travel and contact clearance.
    [Export(PropertyHint.Range, "0,1,0.01")] public float RifleMagazineTravel { get; set; } = 0.5f;
    [Export(PropertyHint.Range, "0,0.2,0.001")] public float RifleMagazineGripDown { get; set; } = 0.1f;
    [Export(PropertyHint.Range, "-0.1,0.1,0.001")] public float RifleMagazineGripBack { get; set; } = -0.012f;
    [Export(PropertyHint.Range, "0,0.2,0.001")] public float RifleReloadClearance { get; set; } = 0.10f;
    [Export(PropertyHint.Range, "0,0.2,0.001")] public float RifleBoltApproachClearance { get; set; } = 0.10f;
    [Export(PropertyHint.Range, "0,0.95,0.01")] public float RifleForegripThumbCloseStart { get; set; } = 0.7f;
    [Export(PropertyHint.Range, "0.1,1,0.01")] public float RifleMagazineThumbRelease { get; set; } = 0.7f;

    // Left finger flexion from each authored rest frame (proximal, middle, distal).
    [Export(PropertyHint.Range, "-90,130,1")] public float RifleLeftIndex1Degrees { get; set; } = 0.0f;
    [Export(PropertyHint.Range, "-90,130,1")] public float RifleLeftIndex2Degrees { get; set; } = 78.7f;
    [Export(PropertyHint.Range, "-90,130,1")] public float RifleLeftIndex3Degrees { get; set; } = 84.7f;
    [Export(PropertyHint.Range, "-90,130,1")] public float RifleLeftMiddle1Degrees { get; set; } = 33.1f;
    [Export(PropertyHint.Range, "-90,130,1")] public float RifleLeftMiddle2Degrees { get; set; } = 46.9f;
    [Export(PropertyHint.Range, "-90,130,1")] public float RifleLeftMiddle3Degrees { get; set; } = 80.8f;
    [Export(PropertyHint.Range, "-90,130,1")] public float RifleLeftRing1Degrees { get; set; } = 35.3f;
    [Export(PropertyHint.Range, "-90,130,1")] public float RifleLeftRing2Degrees { get; set; } = 32.0f;
    [Export(PropertyHint.Range, "-90,130,1")] public float RifleLeftRing3Degrees { get; set; } = 85.0f;
    [Export(PropertyHint.Range, "-90,130,1")] public float RifleLeftPinky1Degrees { get; set; } = 26.8f;
    [Export(PropertyHint.Range, "-90,130,1")] public float RifleLeftPinky2Degrees { get; set; } = 100.4f;
    [Export(PropertyHint.Range, "-90,130,1")] public float RifleLeftPinky3Degrees { get; set; } = 46.7f;
    [Export(PropertyHint.Range, "-90,130,1")] public float RifleLeftThumb1Degrees { get; set; } = 30f;
    [Export(PropertyHint.Range, "-90,130,1")] public float RifleLeftThumb2Degrees { get; set; } = 30f;
    [Export(PropertyHint.Range, "-90,130,1")] public float RifleLeftThumb3Degrees { get; set; } = 70f;

    // Right finger flexion from each authored rest frame (proximal, middle, distal).
    [Export(PropertyHint.Range, "-90,130,1")] public float RifleRightIndex1Degrees { get; set; } = 4.3f;
    [Export(PropertyHint.Range, "-90,130,1")] public float RifleRightIndex2Degrees { get; set; } = 4.9f;
    [Export(PropertyHint.Range, "-90,130,1")] public float RifleRightIndex3Degrees { get; set; } = 5.6f;
    [Export(PropertyHint.Range, "-90,130,1")] public float RifleRightMiddle1Degrees { get; set; } = 21.8f;
    [Export(PropertyHint.Range, "-90,130,1")] public float RifleRightMiddle2Degrees { get; set; } = 17.0f;
    [Export(PropertyHint.Range, "-90,130,1")] public float RifleRightMiddle3Degrees { get; set; } = 65.2f;
    [Export(PropertyHint.Range, "-90,130,1")] public float RifleRightRing1Degrees { get; set; } = 0.0f;
    [Export(PropertyHint.Range, "-90,130,1")] public float RifleRightRing2Degrees { get; set; } = 68.6f;
    [Export(PropertyHint.Range, "-90,130,1")] public float RifleRightRing3Degrees { get; set; } = 0.0f;
    [Export(PropertyHint.Range, "-90,130,1")] public float RifleRightPinky1Degrees { get; set; } = 19.8f;
    [Export(PropertyHint.Range, "-90,130,1")] public float RifleRightPinky2Degrees { get; set; } = 66.3f;
    [Export(PropertyHint.Range, "-90,130,1")] public float RifleRightPinky3Degrees { get; set; } = 5.1f;
    [Export(PropertyHint.Range, "-90,130,1")] public float RifleRightThumb1Degrees { get; set; } = 19.9f;
    [Export(PropertyHint.Range, "-90,130,1")] public float RifleRightThumb2Degrees { get; set; } = 2.4f;
    [Export(PropertyHint.Range, "-90,130,1")] public float RifleRightThumb3Degrees { get; set; } = 72.3f;

    // Magazine finger flexion from each authored rest frame (proximal, middle, distal).
    [Export(PropertyHint.Range, "-90,130,1")] public float RifleMagazineIndex1Degrees { get; set; } = 2.0f;
    [Export(PropertyHint.Range, "-90,130,1")] public float RifleMagazineIndex2Degrees { get; set; } = 64.7f;
    [Export(PropertyHint.Range, "-90,130,1")] public float RifleMagazineIndex3Degrees { get; set; } = 45.2f;
    [Export(PropertyHint.Range, "-90,130,1")] public float RifleMagazineMiddle1Degrees { get; set; } = 26.9f;
    [Export(PropertyHint.Range, "-90,130,1")] public float RifleMagazineMiddle2Degrees { get; set; } = 34.7f;
    [Export(PropertyHint.Range, "-90,130,1")] public float RifleMagazineMiddle3Degrees { get; set; } = 48.0f;
    [Export(PropertyHint.Range, "-90,130,1")] public float RifleMagazineRing1Degrees { get; set; } = 25.6f;
    [Export(PropertyHint.Range, "-90,130,1")] public float RifleMagazineRing2Degrees { get; set; } = 20.2f;
    [Export(PropertyHint.Range, "-90,130,1")] public float RifleMagazineRing3Degrees { get; set; } = 62.2f;
    [Export(PropertyHint.Range, "-90,130,1")] public float RifleMagazinePinky1Degrees { get; set; } = 0.0f;
    [Export(PropertyHint.Range, "-90,130,1")] public float RifleMagazinePinky2Degrees { get; set; } = 44.8f;
    [Export(PropertyHint.Range, "-90,130,1")] public float RifleMagazinePinky3Degrees { get; set; } = 47.3f;
    [Export(PropertyHint.Range, "-90,130,1")] public float RifleMagazineThumb1Degrees { get; set; } = 14.5f;
    [Export(PropertyHint.Range, "-90,130,1")] public float RifleMagazineThumb2Degrees { get; set; } = 75.1f;
    [Export(PropertyHint.Range, "-90,130,1")] public float RifleMagazineThumb3Degrees { get; set; } = 36.0f;

    // Transit/bolt thumb and firing index curls.
    [Export(PropertyHint.Range, "-90,130,1")] public float RifleBoltThumb1Degrees { get; set; } = 45f;
    [Export(PropertyHint.Range, "-90,130,1")] public float RifleBoltThumb2Degrees { get; set; } = 60f;
    [Export(PropertyHint.Range, "-90,130,1")] public float RifleBoltThumb3Degrees { get; set; } = 30f;
    [Export(PropertyHint.Range, "0,90,1")] public float RifleTriggerIndex1Degrees { get; set; } = 5f;
    [Export(PropertyHint.Range, "0,90,1")] public float RifleTriggerIndex2Degrees { get; set; } = 20f;
    [Export(PropertyHint.Range, "0,90,1")] public float RifleTriggerIndex3Degrees { get; set; } = 45f;

    // ---------------------------------------------------------------- World
    [ExportGroup("World")]
    [Export(PropertyHint.Range, "1,80,1")] public int TankCount { get; set; } = 30;
    /// <summary>Tanks stand in groups of between these sizes, close enough to set each other off.</summary>
    [Export(PropertyHint.Range, "1,16,1")] public int ClusterSizeMin { get; set; } = 1;
    [Export(PropertyHint.Range, "1,16,1")] public int ClusterSizeMax { get; set; } = 10;
    [Export(PropertyHint.Range, "0,15,0.1")] public float ClearCelebrationTime { get; set; } = 3f;
    [Export(PropertyHint.Range, "0.3,10,0.1")] public float TransitionTime { get; set; } = 2.4f;
    [Export] public int FixedSeed { get; set; } = 0;

    [ExportGroup("Tank Gather")]
    [Export(PropertyHint.Range, "0.5,8,0.1")] public float GatherRadius { get; set; } = 3f;
    [Export(PropertyHint.Range, "2,64,1")] public int GatherMinimum { get; set; } = 2;
    [Export(PropertyHint.Range, "0,120,0.5")] public float GatherDelay { get; set; } = 20f;
    [Export(PropertyHint.Range, "0.05,2,0.05")] public float GatherScanInterval { get; set; } = 0.25f;
    [Export(PropertyHint.Range, "0.5,10,0.1")] public float GatherDuration { get; set; } = 3f;
    [Export(PropertyHint.Range, "0.1,4,0.1")] public float GatherLift { get; set; } = 1.5f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float GatherShimmer { get; set; } = 0.12f;
    [Export(PropertyHint.Range, "0,20,0.1")] public float GatherShimmerSpeed { get; set; } = 4f;
    [Export(PropertyHint.Range, "0,80,1")] public float GatherShimmerBands { get; set; } = 18f;
    [Export(PropertyHint.Range, "1,20,1")] public float GatherShimmerSharpness { get; set; } = 6f;
    [Export(PropertyHint.Range, "0.01,0.5,0.01")] public float GatherRestSpeed { get; set; } = 0.06f;
    [Export(PropertyHint.Range, "0.001,0.2,0.001")] public float GatherRestDistance { get; set; } = 0.015f;
    [Export(PropertyHint.Range, "0.1,10,0.1")] public float GatherRestAngle { get; set; } = 1f;
    [Export(PropertyHint.Range, "0.01,1,0.01")] public float GatherRestSpin { get; set; } = 0.1f;
    [Export(PropertyHint.Range, "0.01,1,0.01")] public float GatherPileTolerance { get; set; } = 0.15f;
    [Export(PropertyHint.Range, "1,90,1")] public float GatherPileAngle { get; set; } = 15f;
    [Export(PropertyHint.Range, "0,3,0.1")] public float GatherCenterSearch { get; set; } = 1f;
    [Export(PropertyHint.Range, "1,8,1")] public int GatherSearchRings { get; set; } = 2;
    [Export(PropertyHint.Range, "4,16,1")] public int GatherSearchDirections { get; set; } = 8;
    [Export(PropertyHint.Range, "0,0.2,0.005")] public float GatherClearance { get; set; } = 0.025f;
    [Export(PropertyHint.Range, "0,0.2,0.005")] public float GatherGroundTolerance { get; set; } = 0.025f;
    [Export(PropertyHint.Range, "0.9,1,0.005")] public float GatherGroundNormal { get; set; } = 0.99f;
    [Export(PropertyHint.Range, "1,100,1")] public float GatherGroundRay { get; set; } = 10f;

    [ExportGroup("Tank Piles")]
    [Export(PropertyHint.Range, "0.5,4,0.05")] public float PileSiteSpacing { get; set; } = 1f;
    [Export(PropertyHint.Range, "0,10,0.1")] public float PileRowsWeight { get; set; } = 1f;
    [Export(PropertyHint.Range, "0,10,0.1")] public float PileSquareWeight { get; set; } = 1f;
    [Export(PropertyHint.Range, "0,10,0.1")] public float PileTriangularWeight { get; set; } = 1f;
    [Export(PropertyHint.Range, "0,10,0.1")] public float PileClusterWeight { get; set; } = 1f;
    [Export(PropertyHint.Range, "0.32,0.5,0.001")] public float PileSpacing { get; set; } = 0.36f;
    /// <summary>Rise of the side-laid rows.</summary>
    [Export(PropertyHint.Range, "0.15,0.35,0.001")] public float PileLayerHeight { get; set; } = 0.249f;
    /// <summary>Upright pyramid rise: the authored collision collar is 0.462 m above the foot.</summary>
    [Export(PropertyHint.Range, "0.36,0.6,0.001")] public float PileUprightLayerHeight { get; set; } = 0.462f;
    [Export(PropertyHint.Range, "0.468,0.8,0.001")] public float PileDepthSpacing { get; set; } = 0.49f;

    // ---------------------------------------------------------------- HUD
    [ExportGroup("HUD")]
    /// <summary>Small arrows toward every group of tanks: on the screen edge, or over the group when in view.</summary>
    [Export] public bool TankArrows { get; set; } = true;

    // ---------------------------------------------------------------- Multiplayer: match
    [ExportGroup("Match")]
    /// <summary>Match length (seconds) a new lobby starts with.</summary>
    [Export(PropertyHint.Range, "30,900,5")] public float MatchLengthDefault { get; set; } = 150f;
    [Export(PropertyHint.Range, "30,900,5")] public float MatchLengthMin { get; set; } = 60f;
    [Export(PropertyHint.Range, "30,1800,5")] public float MatchLengthMax { get; set; } = 600f;
    [Export(PropertyHint.Range, "5,120,5")] public float MatchLengthStep { get; set; } = 30f;
    /// <summary>Initial actor capacity of a new lobby; the creator can select up to the tested ceiling.</summary>
    [Export(PropertyHint.Range, "2,16,1")] public int MaxPlayers { get; set; } = 5;
    /// <summary>Seconds between everyone having the suburb loaded and the match timer starting.</summary>
    [Export(PropertyHint.Range, "0,10,0.5")] public float CountdownTime { get; set; } = 4f;
    /// <summary>How long the results show before everyone returns to the lobby.</summary>
    [Export(PropertyHint.Range, "1,60,0.5")] public float ResultsTime { get; set; } = 10f;

    // ---------------------------------------------------------------- Multiplayer: tank bank
    [ExportGroup("Tank Bank")]
    [Export(PropertyHint.Range, "0,10,1")] public int HitDropCount { get; set; } = 1;
    /// <summary>After a hit drops tanks, further hits drop nothing for this long.</summary>
    [Export(PropertyHint.Range, "0,5,0.05")] public float HitGraceTime { get; set; } = 0.5f;
    [Export(PropertyHint.Range, "0,20,1")] public int RagdollDropCount { get; set; } = 5;
    /// <summary>Dropped tanks ignore bullets and chain reactions for this long.</summary>
    [Export(PropertyHint.Range, "0,5,0.05")] public float DropInvulnerableTime { get; set; } = 1f;
    /// <summary>How far apart dropped tanks land (meters).</summary>
    [Export(PropertyHint.Range, "0,4,0.05")] public float DropScatter { get; set; } = 0.9f;
    /// <summary>How hard dropped tanks are thrown out as they spawn (m/s).</summary>
    [Export(PropertyHint.Range, "0,15,0.1")] public float DropToss { get; set; } = 3f;
    /// <summary>How far behind the victim a bullet hit's tanks land (meters).</summary>
    [Export(PropertyHint.Range, "0,4,0.05")] public float HitDropDistance { get; set; } = 0.9f;

    // ---------------------------------------------------------------- Multiplayer: suburb
    [ExportGroup("Multiplayer Suburb")]
    /// <summary>Base supply, before adding the per-player supply.</summary>
    [Export(PropertyHint.Range, "0,120,1")] public int MpTankCount { get; set; } = 20;
    [Export(PropertyHint.Range, "0,20,1")] public int MpTanksPerPlayer { get; set; } = 4;
    [Export(PropertyHint.Range, "150,400,1")] public float MpMapSizeBase { get; set; } = 190f;
    [Export(PropertyHint.Range, "0,20,0.5")] public float MpMapSizePerPlayer { get; set; } = 4f;
    [Export(PropertyHint.Range, "0,2,0.05")] public float MpPileSizePerPlayer { get; set; } = 0.25f;
    [Export(PropertyHint.Range, "2,30,0.5")] public float MpStartSpacing { get; set; } = 12f;
    [Export(PropertyHint.Range, "55,100,1")] public float MpRoadSpacing { get; set; } = 70f;
    [Export(PropertyHint.Range, "1,16,1")] public int MpClusterSizeMin { get; set; } = 5;
    [Export(PropertyHint.Range, "1,16,1")] public int MpClusterSizeMax { get; set; } = 12;
    /// <summary>How strongly piles are pulled toward the suburb's center: 0 spreads them like single player.</summary>
    [Export(PropertyHint.Range, "0,6,0.05")] public float MpCenterBias { get; set; } = 1.5f;
    /// <summary>Radius (meters) of the central area that piles favor.</summary>
    [Export(PropertyHint.Range, "5,120,1")] public float MpCenterRadius { get; set; } = 40f;

    // ---------------------------------------------------------------- Multiplayer: ammo
    [ExportGroup("Ammo")]
    [Export(PropertyHint.Range, "1,200,1")] public int MagazineSize { get; set; } = 30;
    [Export(PropertyHint.Range, "0,600,1")] public int StartingReserve { get; set; } = 60;
    [Export(PropertyHint.Range, "0,999,1")] public int MaxReserve { get; set; } = 150;
    [Export(PropertyHint.Range, "0,6,0.05")] public float ReloadTime { get; set; } = 1.6f;
    /// <summary>Base ammo cans, before adding the per-player supply.</summary>
    [Export(PropertyHint.Range, "0,40,1")] public int PickupCount { get; set; } = 3;
    [Export(PropertyHint.Range, "0,10,1")] public int PickupsPerPlayer { get; set; } = 1;
    [Export(PropertyHint.Range, "1,300,1")] public int PickupAmount { get; set; } = 30;
    [Export(PropertyHint.Range, "0,120,0.5")] public float PickupRespawnTime { get; set; } = 20f;
    [Export(PropertyHint.Range, "0.05,5,0.05")] public float PickupRevealTime { get; set; } = 1.2f;

    // ---------------------------------------------------------------- Multiplayer: HUD
    [ExportGroup("Match HUD")]
    /// <summary>The farthest (meters) another player's score shows above them, when in line of sight.</summary>
    [Export(PropertyHint.Range, "5,300,1")] public float ScoreTagRange { get; set; } = 70f;
    [Export(PropertyHint.Range, "0.3,3,0.05")] public float ScoreTagScale { get; set; } = 1f;
    [Export(PropertyHint.Range, "0.02,2,0.01")] public float HitMarkerTime { get; set; } = 0.25f;
    [Export(PropertyHint.Range, "0.1,5,0.05")] public float ScorePopupTime { get; set; } = 1.4f;
    [Export(PropertyHint.Range, "0.1,5,0.05")] public float HitDirectionTime { get; set; } = 1.2f;

    // ---------------------------------------------------------------- Multiplayer: appearance
    [ExportGroup("Appearance")]
    /// <summary>Linear luminance limits; presets stay between them and keep their existing materials/glow.</summary>
    [Export(PropertyHint.Range, "0.001,0.15,0.001")] public float AppearanceDarkThreshold { get; set; } = 0.04f;
    [Export(PropertyHint.Range, "0.65,0.99,0.01")] public float AppearanceLightThreshold { get; set; } = 0.8f;
    [Export(PropertyHint.Range, "0.1,0.3,0.01")] public float AppearanceTagThreshold { get; set; } = 0.18f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float AppearanceBodyContrast { get; set; } = 0.45f;
    [Export(PropertyHint.Range, "1,8,0.1")] public float AppearanceRimPower { get; set; } = 3f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float AppearanceGlowContrast { get; set; } = 0.4f;
    [Export(PropertyHint.Range, "0,2,0.05")] public float AppearancePreviewAmbient { get; set; } = 0.35f;
    [Export(PropertyHint.Range, "0,2,0.05")] public float AppearancePreviewLight { get; set; } = 0.8f;

    // ---------------------------------------------------------------- Multiplayer: network
    [ExportGroup("Network")]
    [Export(PropertyHint.Range, "5,120,1")] public float PlayerSendRate { get; set; } = 30f;
    [Export(PropertyHint.Range, "1,60,1")] public float TankSendRate { get; set; } = 15f;
    [Export(PropertyHint.Range, "1,60,1")] public float CarSendRate { get; set; } = 6f;
    /// <summary>How far behind real time (seconds) remote players are shown, so their motion stays smooth.</summary>
    [Export(PropertyHint.Range, "0,0.5,0.005")] public float InterpolationDelay { get; set; } = 0.1f;
    /// <summary>How quickly (seconds) a drifted object eases back to where its owner has it.</summary>
    [Export(PropertyHint.Range, "0.02,2,0.01")] public float CorrectionBlendTime { get; set; } = 0.25f;
    /// <summary>Drift (meters) past which an object snaps to its owner's position instead of easing.</summary>
    [Export(PropertyHint.Range, "0.1,20,0.1")] public float SnapDistance { get; set; } = 3f;
}
