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

    private static Tuning? playerTuning;

    /// <summary>
    /// Makes <paramref name="match"/> the live values for a multiplayer match, keeping the player's own aside until
    /// <see cref="EndMatch"/>. Everyone in a match plays on the same values.
    /// </summary>
    public static void UseForMatch(Tuning match)
    {
        playerTuning ??= Current;
        current = match;
    }

    /// <summary>Puts the player's own values back after a match.</summary>
    public static void EndMatch()
    {
        if (playerTuning != null)
        {
            current = playerTuning;
            playerTuning = null;
        }
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

    /// <summary>Sets the values in a snapshot that this build knows; unknown names are ignored.</summary>
    public void Apply(Godot.Collections.Dictionary values)
    {
        var known = new System.Collections.Generic.HashSet<string>(ValueNames(this));
        foreach (var (key, value) in values)
        {
            var name = key.AsString();
            if (known.Contains(name))
            {
                Set(name, value);
            }
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

    // ---------------------------------------------------------------- Multiplayer: match
    [ExportGroup("Match")]
    /// <summary>Match length (seconds) a new lobby starts with.</summary>
    [Export(PropertyHint.Range, "30,900,5")] public float MatchLengthDefault { get; set; } = 150f;
    [Export(PropertyHint.Range, "30,900,5")] public float MatchLengthMin { get; set; } = 60f;
    [Export(PropertyHint.Range, "30,1800,5")] public float MatchLengthMax { get; set; } = 600f;
    [Export(PropertyHint.Range, "5,120,5")] public float MatchLengthStep { get; set; } = 30f;
    [Export(PropertyHint.Range, "1,8,1")] public int MaxPlayers { get; set; } = 5;
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
    /// <summary>How far apart dropped tanks land (metres).</summary>
    [Export(PropertyHint.Range, "0,4,0.05")] public float DropScatter { get; set; } = 0.9f;
    /// <summary>How hard dropped tanks are thrown out as they spawn (m/s).</summary>
    [Export(PropertyHint.Range, "0,15,0.1")] public float DropToss { get; set; } = 3f;
    /// <summary>How far behind the victim a bullet hit's tanks land (metres).</summary>
    [Export(PropertyHint.Range, "0,4,0.05")] public float HitDropDistance { get; set; } = 0.9f;

    // ---------------------------------------------------------------- Multiplayer: suburb
    [ExportGroup("Multiplayer Suburb")]
    [Export(PropertyHint.Range, "1,120,1")] public int MpTankCount { get; set; } = 40;
    [Export(PropertyHint.Range, "1,16,1")] public int MpClusterSizeMin { get; set; } = 5;
    [Export(PropertyHint.Range, "1,16,1")] public int MpClusterSizeMax { get; set; } = 12;
    /// <summary>How strongly piles are pulled toward the suburb's centre: 0 spreads them like single player.</summary>
    [Export(PropertyHint.Range, "0,6,0.05")] public float MpCentreBias { get; set; } = 1.5f;
    /// <summary>Radius (metres) of the central area that piles favour.</summary>
    [Export(PropertyHint.Range, "5,120,1")] public float MpCentreRadius { get; set; } = 40f;

    // ---------------------------------------------------------------- Multiplayer: ammo
    [ExportGroup("Ammo")]
    [Export(PropertyHint.Range, "1,200,1")] public int MagazineSize { get; set; } = 30;
    [Export(PropertyHint.Range, "0,600,1")] public int StartingReserve { get; set; } = 60;
    [Export(PropertyHint.Range, "0,999,1")] public int MaxReserve { get; set; } = 150;
    [Export(PropertyHint.Range, "0,6,0.05")] public float ReloadTime { get; set; } = 1.6f;
    [Export(PropertyHint.Range, "0,40,1")] public int PickupCount { get; set; } = 8;
    [Export(PropertyHint.Range, "1,300,1")] public int PickupAmount { get; set; } = 30;
    [Export(PropertyHint.Range, "0,120,0.5")] public float PickupRespawnTime { get; set; } = 20f;
    [Export(PropertyHint.Range, "0.05,5,0.05")] public float PickupRevealTime { get; set; } = 1.2f;

    // ---------------------------------------------------------------- Multiplayer: HUD
    [ExportGroup("Match HUD")]
    /// <summary>The farthest (metres) another player's score shows above them, when in line of sight.</summary>
    [Export(PropertyHint.Range, "5,300,1")] public float ScoreTagRange { get; set; } = 70f;
    [Export(PropertyHint.Range, "0.3,3,0.05")] public float ScoreTagScale { get; set; } = 1f;
    [Export(PropertyHint.Range, "0.02,2,0.01")] public float HitMarkerTime { get; set; } = 0.25f;
    [Export(PropertyHint.Range, "0.1,5,0.05")] public float ScorePopupTime { get; set; } = 1.4f;
    [Export(PropertyHint.Range, "0.1,5,0.05")] public float HitDirectionTime { get; set; } = 1.2f;

    // ---------------------------------------------------------------- Multiplayer: network
    [ExportGroup("Network")]
    [Export(PropertyHint.Range, "5,120,1")] public float PlayerSendRate { get; set; } = 30f;
    [Export(PropertyHint.Range, "1,60,1")] public float TankSendRate { get; set; } = 15f;
    [Export(PropertyHint.Range, "1,60,1")] public float CarSendRate { get; set; } = 6f;
    /// <summary>How far behind real time (seconds) remote players are shown, so their motion stays smooth.</summary>
    [Export(PropertyHint.Range, "0,0.5,0.005")] public float InterpolationDelay { get; set; } = 0.1f;
    /// <summary>How quickly (seconds) a drifted object eases back to where its owner has it.</summary>
    [Export(PropertyHint.Range, "0.02,2,0.01")] public float CorrectionBlendTime { get; set; } = 0.25f;
    /// <summary>Drift (metres) past which an object snaps to its owner's position instead of easing.</summary>
    [Export(PropertyHint.Range, "0.1,20,0.1")] public float SnapDistance { get; set; } = 3f;
}
