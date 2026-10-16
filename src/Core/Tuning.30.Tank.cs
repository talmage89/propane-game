using Godot;

namespace Propane.Core;

public partial class Tuning
{
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
    /// <summary>Overlapping full explosions; excess blasts retain their core flash and particle fireball.</summary>
    [Export(PropertyHint.Range, "1,128,1")] public int ExplosionFullEffectBudget { get; set; } = 16;
    /// <summary>Pending fuses needed to bound an offline dense chain; ordinary play and network worlds keep engine catch-up.</summary>
    [Export(PropertyHint.Range, "1,512,1")] public int DenseChainFuseThreshold { get; set; } = 64;
    /// <summary>Offline catch-up steps during that chain; 0 disables the transient limit.</summary>
    [Export(PropertyHint.Range, "0,12,1")] public int DenseChainPhysicsSteps { get; set; } = 4;
    /// <summary>Real seconds to retain the transient limit after fuse pressure drops.</summary>
    [Export(PropertyHint.Range, "0,2,0.01")] public float DenseChainCatchupHold { get; set; } = 0.5f;
    [Export(PropertyHint.Range, "0,4,0.05")] public float SmokeAmount { get; set; } = 1f;
    [Export(PropertyHint.Range, "0,4,0.05")] public float EmberAmount { get; set; } = 1f;
    [Export(PropertyHint.Range, "0,200,1")] public float FlashEnergy { get; set; } = 40f;
    [Export(PropertyHint.Range, "1,80,0.5")] public float FlashRange { get; set; } = 22f;
    [Export(PropertyHint.Range, "0.05,3,0.01")] public float FlashDuration { get; set; } = 0.55f;
    [Export(PropertyHint.Range, "0,60,0.5")] public float DebrisSpeed { get; set; } = 9f;
    [Export(PropertyHint.Range, "0,60,0.5")] public float DebrisSpin { get; set; } = 22f;
    /// <summary>Well-spread pieces of the authored 25-piece shell; the remaining fragments are cosmetic detail.</summary>
    [Export(PropertyHint.Range, "0,25,1")] public int DebrisPiecesPerTank { get; set; } = 6;
    /// <summary>Deferred rigid-body registrations per presentation frame; tank/blast rules never wait on this queue.</summary>
    [Export(PropertyHint.Range, "1,256,1")] public int DebrisSpawnPerFrame { get; set; } = 8;
    [Export(PropertyHint.Range, "0,10,0.1")] public float ScorchSize { get; set; } = 4.2f;
    [Export(PropertyHint.Range, "0,4,0.05")] public float ShockwaveScale { get; set; } = 1f;

    // ---------------------------------------------------------------- Feedback
    [ExportGroup("Feedback")]
    [Export(PropertyHint.Range, "0,4,0.05")] public float CameraShake { get; set; } = 1f;
    [Export(PropertyHint.Range, "1,120,1")] public float CameraShakeDistance { get; set; } = 45f;
    [Export(PropertyHint.Range, "0,0.3,0.005")] public float HitstopDuration { get; set; } = 0.07f;
    [Export(PropertyHint.Range, "0,40,0.5")] public float HitstopRadius { get; set; } = 9f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float HitstopTimeScale { get; set; } = 0.05f;
}
