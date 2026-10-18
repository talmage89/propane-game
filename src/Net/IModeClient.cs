using System;
using System.Collections.Generic;
using Godot;
using Propane.Core;
using Propane.Modes;
using Propane.Player;
using Propane.Tank;

namespace Propane.Net;

/// <summary>
/// One physical session's mode policy. Match validates/sequences envelopes and calls these operations at its existing
/// callback boundaries. Views take explicit round time; no adapter advances a clock or owns a processing node.
/// Disposal clears decoded state and releases all borrowed collaborators, including on failed assembly.
/// </summary>
public interface IModeClient : IDisposable
{
    /// <summary>Stable live container for this session; values mutate synchronously, disposal empties it.</summary>
    IReadOnlyDictionary<int, int> Scores { get; }
    /// <summary>Immutable mode choices, sampled once when composition binds the adapter.</summary>
    ModeClientDisplay Display { get; }
    bool SuppressHud { get; }
    KingState? KingSnapshot { get; }
    IModeView? ViewAt(double elapsed);
    void ActorAdded(int id);
    void ActorRemoved(int id);
    void Apply(ModeEnvelope envelope);
    void HitByShot(PlayerCharacter victim);
    void Thrown(PlayerCharacter actor, Vector3 at);
    void PhysicsTick();
    bool LimitedAmmo(bool supplied);
}

/// <summary>Physical/wire/presentation operations borrowed synchronously until the session retires; no mode state.</summary>
public interface IClientModeReplica
{
    WorldContext World { get; }
    bool IsPlaying { get; }
    bool IsLoading { get; }
    PlayerCharacter? SelectedActor { get; }
    IReadOnlyList<ActorRuntime> Actors { get; }
    IEnumerable<ActorRuntime> OwnedActors { get; }
    PlayerCharacter? Actor(int id);
    PlayerProfile Profile(int id);
    ClientActorReplication Replication(ActorRuntime actor);
    PropaneTank? RestingIntactTank(BodyAuthority body);
    bool HasTank(uint id);
    bool Exploded(uint id);
    void AddTank(PropaneTank tank);
    void TrackTank(PropaneTank tank, int owner);
    void RemovePredictedTank(uint id);
    void SendMode(PlayerCharacter actor, uint action, ushort kind, object payload);
    void ScoreChanged(int delta);
    void MaterializePile(ClientPileView view, IReadOnlyList<PropaneTank> tanks, IReadOnlyList<BodyState> placements);
}

/// <summary>Lobby roster and view binding operations. The adapter owns reconciliation decisions, never bots or UI.</summary>
public interface IClientLobbyReplica
{
    void TitleChanged(LobbyInfo lobby);
    void Notice(string text, float seconds);
    void UpdateActor(PlayerCharacter actor, PlayerProfile profile);
    void AddActor(PlayerProfile profile, Vector3 position, float yaw);
    void RemoveBot(PlayerCharacter actor);
    void RemoveActor(ActorRuntime actor);
    void RefreshPeers();
    void StageChanged(bool staging, bool panelOpen);
}

public enum ClientPileView { Zone, Lobby }
public enum ClientLobbyInput { None, OpenPanel, Consume }

/// <summary>Plain mode choices for the existing client HUD and notices; rendering stays in composition.</summary>
public readonly record struct ModeClientDisplay(bool ShowGo, bool NameTags, string WaitingNotice, string Location);
