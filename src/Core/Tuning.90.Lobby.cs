using Godot;

namespace Propane.Core;

public partial class Tuning
{
    // ---------------------------------------------------------------- Multiplayer: lobby world
    // The void world a waiting lobby roams: its piles and how they refill, its ammo, where players arrive, and the
    // stage that counts down once the creator starts. The server's own dials build each world; the creator's start
    // snapshot supplies the stage time.
    [ExportGroup("Lobby")]
    /// <summary>Tank piles in a lobby world, evenly around a ring.</summary>
    [Export(PropertyHint.Range, "1,12,1")] public int LobbyPiles { get; set; } = 6;
    /// <summary>Tanks in each lobby pile.</summary>
    [Export(PropertyHint.Range, "1,30,1")] public int LobbyPileCount { get; set; } = 10;
    /// <summary>Radius (meters) of the ring the piles stand on.</summary>
    [Export(PropertyHint.Range, "6,60,1")] public float LobbyPileRing { get; set; } = 13f;
    /// <summary>Radius (meters) of the ring players arrive on, each at a spot of their own.</summary>
    [Export(PropertyHint.Range, "2,40,0.5")] public float LobbySpawnRing { get; set; } = 6f;
    /// <summary>Seconds after a pile's last intact tank is gone before a fresh pile materializes there.</summary>
    [Export(PropertyHint.Range, "0,120,0.5")] public float LobbyRefillDelay { get; set; } = 5f;
    /// <summary>A pile's tank still counts as standing while it is intact within this many meters of its spot.</summary>
    [Export(PropertyHint.Range, "1,20,0.5")] public float LobbyPileReach { get; set; } = 4f;
    /// <summary>Open ground kept around a refilled pile; players stay twice this far from it.</summary>
    [Export(PropertyHint.Range, "0.2,3,0.1")] public float LobbyPileClearance { get; set; } = 1f;
    /// <summary>No pile refills when its tanks would take the world past this many (venting strays included).</summary>
    [Export(PropertyHint.Range, "10,400,5")] public int LobbyTankLimit { get; set; } = 120;
    /// <summary>How long a refilled pile takes to materialize.</summary>
    [Export(PropertyHint.Range, "0.2,4,0.1")] public float LobbyMaterializeSeconds { get; set; } = 1.2f;
    /// <summary>Rifles never run dry in the lobby; off, they carry a match's starting ammo and no cans.</summary>
    [Export] public bool LobbyInfiniteAmmo { get; set; } = true;
    /// <summary>Seconds the stage screen counts down once the creator starts, before the match loads (0 starts at once).</summary>
    [Export(PropertyHint.Range, "0,10,0.5")] public float LobbyStageTime { get; set; } = 5f;
}
