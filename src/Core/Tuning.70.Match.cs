using Godot;

namespace Propane.Core;

public partial class Tuning
{
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
    /// <summary>Top rows on the match leaderboard (the local player's own row shows below them when outside); 0 hides it.</summary>
    [Export(PropertyHint.Range, "0,16,1")] public int LeaderboardRows { get; set; } = 5;
    /// <summary>The leaderboard's offset from the top-left corner, in 1080p pixels (the HUD scales with the window).</summary>
    [Export(PropertyHint.Range, "0,400,1")] public float LeaderboardMarginX { get; set; } = 24f;
    [Export(PropertyHint.Range, "0,400,1")] public float LeaderboardMarginY { get; set; } = 24f;
    /// <summary>Seconds a leaderboard row takes to slide to a new rank.</summary>
    [Export(PropertyHint.Range, "0.05,2,0.05")] public float LeaderboardSlideTime { get; set; } = 0.35f;
    /// <summary>Seconds a row's highlight lasts after it climbs a rank (and its tint after its score drops).</summary>
    [Export(PropertyHint.Range, "0.1,3,0.05")] public float LeaderboardFlashTime { get; set; } = 0.9f;

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
