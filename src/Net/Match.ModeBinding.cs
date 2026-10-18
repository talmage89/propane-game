using System.Collections.Generic;
using Godot;
using Propane.Core;
using Propane.Modes;
using Propane.Player;
using Propane.Tank;

namespace Propane.Net;

public partial class Match
{
    bool IClientModeReplica.IsLoading => phase == Phase.Loading;
    PlayerCharacter? IClientModeReplica.SelectedActor => player;
    IReadOnlyList<ActorRuntime> IClientModeReplica.Actors => Session.Actors;
    IEnumerable<ActorRuntime> IClientModeReplica.OwnedActors => Session.OwnedActors;
    PlayerCharacter? IClientModeReplica.Actor(int id) => Actor(id);
    PlayerProfile IClientModeReplica.Profile(int id) => profiles[id];
    ClientActorReplication IClientModeReplica.Replication(ActorRuntime actor) => actorReplication.Get(actor);
    PropaneTank? IClientModeReplica.RestingIntactTank(BodyAuthority body) => worldReplica.RestingIntactTank(body);
    bool IClientModeReplica.HasTank(uint id) => worldReplica.HasTank(id);
    bool IClientModeReplica.Exploded(uint id) => worldReplica.Exploded(id);
    void IClientModeReplica.AddTank(PropaneTank tank) => worldReplica.AddTank(tank);
    void IClientModeReplica.TrackTank(PropaneTank tank, int owner) => worldReplica.TrackTank(tank, owner);
    void IClientModeReplica.RemovePredictedTank(uint id) => worldReplica.RemovePredictedTank(id);
    void IClientModeReplica.SendMode(PlayerCharacter actor, uint action, ushort kind, object payload) =>
        Net.Send(MessageCodec.Encode(new NetMessage(Msg.ModeEvent, Setup.Id, 0, new ModeEnvelope(round, worldRevision, mode.Id,
            mode.SchemaVersion, actor.ActorId, actor.LifeId, action, kind, payload)), fromServer: false));
    void IClientModeReplica.ScoreChanged(int delta) => viewBinding.ScoreChanged(delta, ModePhase, timeLeft);
    void IClientModeReplica.MaterializePile(ClientPileView view, IReadOnlyList<PropaneTank> built, IReadOnlyList<BodyState> placements) =>
        viewBinding.MaterializePile(view, built, placements);
    void IClientLobbyReplica.TitleChanged(LobbyInfo lobby) => viewBinding.TitleChanged(lobby);
    void IClientLobbyReplica.Notice(string text, float seconds) => viewBinding.ShowNotice(text, seconds);
    void IClientLobbyReplica.UpdateActor(PlayerCharacter actor, PlayerProfile profile) => UpdateLobbyActor(actor, profile);
    void IClientLobbyReplica.AddActor(PlayerProfile profile, Vector3 position, float yaw)
    {
        var character = AddActor(profile, -1, position, yaw);
        if (!character.IsRemote)
        {
            SubscribeOwned(character);
            if (profile.Kind == ActorKind.Bot) ownedBots.AttachLobbyBot(character, profile);
        }
    }
    void IClientLobbyReplica.RemoveBot(PlayerCharacter actor) => ownedBots.RemoveBot(actor);
    void IClientLobbyReplica.RemoveActor(ActorRuntime actor) => Session.RemoveActor(actor);
    void IClientLobbyReplica.RefreshPeers() => ownedBots.RefreshBotPeers();
    void IClientLobbyReplica.StageChanged(bool staging, bool panelOpen) => viewBinding.StageChanged(staging, panelOpen);
    BodySync.Entry? IClientAuthorityReplica.FindBody(uint id) => worldReplica.FindBody(id);
    BodyState? IClientAuthorityReplica.CurrentProposal(uint id) => worldReplica.CurrentProposal(id);
    void IClientAuthorityReplica.CancelRoot(uint request) => worldReplica.CancelRoot(request);
    (BodyAuthority Authority, BodySync.Entry? Entry)? IClientAuthorityReplica.Adopt(BodyAuthority body, bool gatherGrant) => worldReplica.Adopt(body, gatherGrant);
    void IClientAuthorityReplica.Explode(BodyAuthority body) => worldReplica.Explode(body);
    void IClientAuthorityReplica.Restore(BodyAuthority body, BodySync.Entry? entry, ClaimRefusal reason, bool finishedPuncture) => worldReplica.Restore(body, entry, reason, finishedPuncture);
    void IClientAuthorityReplica.RemoveUnknown(uint id) => worldReplica.RemoveUnknown(id);
    void IClientAuthorityReplica.Notify(AuthorityMessage result) => AuthorityResolved?.Invoke(result);
}
