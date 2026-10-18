using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Modes;
using Propane.Player;
using Propane.Tank;

namespace Propane.Net;

/// <summary>
/// One roaming session's roster reconciliation, staging, stock and refill policy. Presentation/bot bindings remain
/// synchronous composition operations. No timers: refill retries are server-clock policy, magazine refill is called
/// at Match's original physics boundary. Retirement releases both bindings, pile state and departure tombstones.
/// </summary>
public sealed class LobbyClientAdapter : SilentClientAdapter
{
    private IClientLobbyReplica lobbyReplica;
    private readonly ClientModePiles piles;
    private readonly HashSet<int> lobbyDeparted = new();
    public override ModeClientDisplay Display => new(false, true, "", "lobby");
    public override bool SuppressHud => Staged;
    public bool PanelOpen { get; set; }
    public bool Staged { get; private set; }

    public LobbyClientAdapter(IClientModeReplica replica, IClientLobbyReplica lobbyReplica) : base(replica)
    {
        this.lobbyReplica = lobbyReplica;
        piles = new ClientModePiles(replica, ClientPileView.Lobby);
    }

    public override bool LimitedAmmo(bool supplied) => supplied || !replica.World.Tuning.LobbyInfiniteAmmo;
    public override void ActorRemoved(int id) => scores.Remove(id);

    public override void Apply(ModeEnvelope envelope)
    {
        switch (envelope.Payload)
        {
            case KingPileWanted wanted: piles.PlacePile(wanted); break;
            case KingPileSpawned spawned: piles.SpawnPile(spawned); break;
        }
    }

    public void LobbyChanged(LobbyInfo lobby, bool staging)
    {
        lobbyReplica.TitleChanged(lobby);
        ReconcileRoster(lobby);
        foreach (var owned in replica.OwnedActors) owned.Character.InputLocked = !replica.IsPlaying || staging;
        if (staging == Staged) return;
        Staged = staging;
        lobbyReplica.StageChanged(staging, PanelOpen);
    }

    private void ReconcileRoster(LobbyInfo lobby)
    {
        var present = lobby.Members.Where(m => m.Present).ToDictionary(m => m.Id);
        var changed = false;
        foreach (var actor in replica.Actors.ToArray())
        {
            var id = actor.Character.ActorId;
            if (present.ContainsKey(id) || actor.Character == replica.SelectedActor) continue;
            lobbyReplica.Notice($"{replica.Profile(id).Name} left", 3f);
            lobbyReplica.RemoveBot(actor.Character);
            lobbyDeparted.Add(id);
            lobbyReplica.RemoveActor(actor);
            changed = true;
        }
        foreach (var member in present.Values)
        {
            if (replica.Actor(member.Id) is { } known)
            {
                lobbyReplica.UpdateActor(known, member);
                continue;
            }
            if (lobbyDeparted.Contains(member.Id)) continue;
            var (at, yaw) = LobbyLayout.Arrival(member.AllocationSlot, replica.World.Tuning.LobbySpawnRing);
            lobbyReplica.AddActor(member, World.Suburb.ToWorld(new Vector2((float)at.X, (float)at.Z), 0.05f), (float)yaw);
            lobbyReplica.Notice($"{member.Name} joined", 3f);
            changed = true;
        }
        if (changed) lobbyReplica.RefreshPeers();
    }

    public override void PhysicsTick()
    {
        if (LimitedAmmo(false)) return;
        foreach (var owned in replica.OwnedActors)
        {
            if (replica.Profile(owned.Character.ActorId).Kind != ActorKind.Bot) continue;
            var ammo = owned.Character.Ammo ??= AmmoState.Starting(replica.World.Tuning);
            ammo.Reserve = replica.World.Tuning.MaxReserve;
        }
    }

    public void MarkStock(AuthorityMessage result)
    {
        // An event invocation snapshot may outlive unsubscription during synchronous session retirement.
        if (replica == null || !replica.IsLoading || result.Request != 0 || result.Reason != ClaimRefusal.None) return;
        foreach (var body in result.Bodies)
        {
            if (replica.RestingIntactTank(body) is { PilePose: null } tank)
            {
                tank.MarkPile(body.Body.Transform);
            }
        }
    }

    public ClientLobbyInput Input(bool panelPressed, bool pointerOrPause, bool menuOpen, bool staging)
    {
        if (panelPressed && !PanelOpen && !menuOpen && !staging) return ClientLobbyInput.OpenPanel;
        return (PanelOpen || staging) && pointerOrPause ? ClientLobbyInput.Consume : ClientLobbyInput.None;
    }

    public override void Dispose()
    {
        base.Dispose();
        piles.Dispose();
        lobbyReplica = null!;
        lobbyDeparted.Clear();
        PanelOpen = Staged = false;
    }
}
