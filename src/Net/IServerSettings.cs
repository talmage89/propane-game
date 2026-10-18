using System;
using Godot;
using Propane.Modes;
using Propane.World.Plan;

namespace Propane.Net;

/// <summary>Borrowed application configuration factory. Samples the saved template at the original call sites.</summary>
public interface IServerSettings
{
    float MatchLengthDefault { get; }
    int MaxPlayers { get; }
    IServerRunSettings CopyTemplate(ServerMode mode);
    IServerRunSettings FromSnapshot(ServerMode mode, Godot.Collections.Dictionary snapshot);
}

/// <summary>
/// Run-owned live configuration. Reads and edits are synchronous; retirement releases the underlying resource once.
/// No cached value projection: authorized debug edits remain observable at each original read site.
/// </summary>
public interface IServerRunSettings : IDisposable
{
    float CountdownTime { get; }
    float ResultsTime { get; }
    float LobbyStageTime { get; }
    int LobbyPiles { get; }
    float LobbyPileRing { get; }
    float LobbySpawnRing { get; }
    float PlayerSendRate { get; }
    float PickupRespawnTime { get; }
    float MeleeWindup { get; }
    float MeleeActive { get; }
    float MeleeRecovery { get; }
    float MeleeCooldown { get; }
    float MeleeRelayGrace { get; }
    int MeleeMaxContacts { get; }
    int KingPileCount { get; }
    float KingPileReach { get; }
    float KingPileClearance { get; }
    int LobbyPileCount { get; }
    float LobbyPileReach { get; }
    float LobbyPileClearance { get; }
    float PileSpacing { get; }
    float PileLayerHeight { get; }
    float PileDepthSpacing { get; }
    float PileUprightLayerHeight { get; }
    void ConfigureLobby(float mintSeconds, int tankSupply);
    Godot.Collections.Dictionary Snapshot();
    void Apply(Godot.Collections.Dictionary values);
    SuburbSettings Supply(int players);
    ModeTuners ReadModeTuners();
}
