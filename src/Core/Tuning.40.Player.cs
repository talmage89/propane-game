using Godot;

namespace Propane.Core;

public partial class Tuning
{
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
}
