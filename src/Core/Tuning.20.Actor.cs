using Godot;

namespace Propane.Core;

public partial class Tuning
{
    // ---------------------------------------------------------------- Actor presentation
    [ExportGroup("Actor Detail")]
    [Export] public bool ActorPoseLod { get; set; } = true;
    [Export(PropertyHint.Range, "0.01,1,0.01")] public float ActorMeshLodBias { get; set; } = 0.1f;
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
}
