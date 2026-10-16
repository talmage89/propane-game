using Godot;

namespace Propane.Core;

public partial class Tuning
{
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
}
