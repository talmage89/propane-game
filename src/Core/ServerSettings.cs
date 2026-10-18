using System;
using System.Collections.Generic;
using Propane.Modes;
using Propane.Net;
using Propane.World.Plan;

namespace Propane.Core;

/// <summary>
/// Application composition's tuning adapter. Borrows immutable supply bindings and the application template accessor;
/// each factory result exclusively owns its native copy. No asset, world, transport or run is retained here.
/// </summary>
public sealed class ServerSettings(IReadOnlyDictionary<ServerMode, Func<Tuning, int, SuburbSettings>> supplies,
    Func<Tuning>? template = null) : IServerSettings
{
    private Tuning Template => template?.Invoke() ?? Tuning.SavedTemplate;
    public float MatchLengthDefault => Template.MatchLengthDefault;
    public int MaxPlayers => Template.MaxPlayers;
    public IServerRunSettings CopyTemplate(ServerMode mode) => new RunSettings(Template.Copy(), mode, supplies);
    public IServerRunSettings FromSnapshot(ServerMode mode, Godot.Collections.Dictionary snapshot) =>
        new RunSettings(Tuning.FromSnapshot(snapshot), mode, supplies);

    /// <summary>The caller transfers this owner to a run, or disposes it on failure before transfer.</summary>
    internal sealed class RunSettings(Tuning tuning, ServerMode mode, IReadOnlyDictionary<ServerMode, Func<Tuning, int, SuburbSettings>> supplies) : IServerRunSettings
    {
        // Native lifetime witness for Dev; Server only sees IServerRunSettings.
        internal Tuning Resource => tuning;
        private bool disposed;
        public float CountdownTime => tuning.CountdownTime;
        public float ResultsTime => tuning.ResultsTime;
        public float LobbyStageTime => tuning.LobbyStageTime;
        public int LobbyPiles => tuning.LobbyPiles;
        public float LobbyPileRing => tuning.LobbyPileRing;
        public float LobbySpawnRing => tuning.LobbySpawnRing;
        public float PlayerSendRate => tuning.PlayerSendRate;
        public float PickupRespawnTime => tuning.PickupRespawnTime;
        public float MeleeWindup => tuning.MeleeWindup;
        public float MeleeActive => tuning.MeleeActive;
        public float MeleeRecovery => tuning.MeleeRecovery;
        public float MeleeCooldown => tuning.MeleeCooldown;
        public float MeleeRelayGrace => tuning.MeleeRelayGrace;
        public int MeleeMaxContacts => tuning.MeleeMaxContacts;
        public int KingPileCount => tuning.KingPileCount;
        public float KingPileReach => tuning.KingPileReach;
        public float KingPileClearance => tuning.KingPileClearance;
        public int LobbyPileCount => tuning.LobbyPileCount;
        public float LobbyPileReach => tuning.LobbyPileReach;
        public float LobbyPileClearance => tuning.LobbyPileClearance;
        public float PileSpacing => tuning.PileSpacing;
        public float PileLayerHeight => tuning.PileLayerHeight;
        public float PileDepthSpacing => tuning.PileDepthSpacing;
        public float PileUprightLayerHeight => tuning.PileUprightLayerHeight;

        public void ConfigureLobby(float mintSeconds, int tankSupply)
        {
            tuning.KingMintSeconds = mintSeconds;
            tuning.TankCountMultiplier = tankSupply;
        }
        public bool IsLobbyLocked(string name) => name == nameof(Tuning.TankCountMultiplier);
        public Godot.Collections.Dictionary Snapshot() => tuning.Snapshot();
        public void Apply(Godot.Collections.Dictionary values) => tuning.Apply(values);
        public SuburbSettings Supply(int players) => supplies[mode](tuning, players);
        public ModeTuners ReadModeTuners() => ModeDefinition.CopyTuners(tuning, mode.Tuners);
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            tuning.Dispose();
        }
    }
}
