using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Modes;
using Propane.Player;
using Propane.Tank;

namespace Propane.Net;

/// <summary>Session composition's synchronous physical and view operations. Mode policy lives in the adapters.</summary>
public partial class Match
{
    bool IClientModeReplica.IsLoading => phase == Phase.Loading;
    PlayerCharacter? IClientModeReplica.SelectedActor => player;
    IReadOnlyList<ActorRuntime> IClientModeReplica.Actors => Session.Actors;
    IEnumerable<ActorRuntime> IClientModeReplica.OwnedActors => Session.OwnedActors;
    PlayerCharacter? IClientModeReplica.Actor(int id) => Actor(id);
    PlayerProfile IClientModeReplica.Profile(int id) => profiles[id];
    PropaneTank? IClientModeReplica.RestingIntactTank(BodyAuthority body) =>
        body.State == TankLedger.State.Intact && body.Rest && tanks.TryGetValue(body.Body.Id, out var tank) && IsInstanceValid(tank) ? tank : null;
    bool IClientModeReplica.HasTank(uint id) => tanks.ContainsKey(id);
    bool IClientModeReplica.Exploded(uint id) => exploded.Contains(id);
    void IClientModeReplica.AddTank(PropaneTank tank) => suburb.AddTank(tank);
    void IClientModeReplica.TrackTank(PropaneTank tank, int owner) => Track(tank, owner);

    void IClientModeReplica.RemovePredictedTank(uint id)
    {
        if (!tanks.Remove(id, out var refused)) return;
        bodies.Remove(id);
        refused.Punctured -= OnTankPunctured;
        refused.Detonated -= OnTankDetonated;
        refused.QueueFree();
    }

    void IClientModeReplica.SendMode(PlayerCharacter actor, uint action, ushort kind, object payload) =>
        Net.Send(MessageCodec.Encode(new NetMessage(Msg.ModeEvent, Setup.Id, 0, new ModeEnvelope(round, worldRevision, mode.Id,
            mode.SchemaVersion, actor.ActorId, actor.LifeId, action, kind, payload)), fromServer: false));

    void IClientModeReplica.ScoreChanged(int delta)
    {
        matchHud.Show(PresentHud(tankGroups, World.Tuning.TankArrows), delta > 0);
        if (delta > 0) matchHud.AddScorePopup(delta);
    }

    void IClientModeReplica.MaterializePile(ClientPileView view, IReadOnlyList<PropaneTank> built, IReadOnlyList<BodyState> placements)
    {
        // Keep the original optional-presenter short circuit: headless King never samples/allocates the center.
        if (view == ClientPileView.Zone) kingZone?.Materialize(built, PileSite.Center(placements.Select(t => t.Transform).ToArray()));
        else lobbyFloor?.Materialize(built, PileSite.Center(placements.Select(t => t.Transform).ToArray()));
    }

    void IClientLobbyReplica.TitleChanged(LobbyInfo lobby)
    {
        if (lobbyFloor != null) lobbyFloor.Title = UI.LobbyText.Title(lobby);
    }
    void IClientLobbyReplica.Notice(string text, float seconds) => ShowNotice(text, seconds);
    void IClientLobbyReplica.UpdateActor(PlayerCharacter actor, PlayerProfile profile) => UpdateLobbyActor(actor, profile);
    void IClientLobbyReplica.AddActor(PlayerProfile profile, Vector3 position, float yaw)
    {
        var character = AddActor(profile, -1, position, yaw);
        if (!character.IsRemote)
        {
            SubscribeOwned(character);
            if (profile.Kind == ActorKind.Bot) AttachLobbyBot(character, profile);
        }
    }
    void IClientLobbyReplica.RemoveBot(PlayerCharacter actor) => RemoveBot(actor);
    void IClientLobbyReplica.RemoveActor(ActorRuntime actor) => Session.RemoveActor(actor);
    void IClientLobbyReplica.RefreshPeers() => RefreshBotPeers();
    void IClientLobbyReplica.StageChanged(bool staging, bool panelOpen)
    {
        if (staging) menu.Visible = false;
        if (CreateLocalView) CaptureMouse(!staging && !panelOpen);
    }
}
