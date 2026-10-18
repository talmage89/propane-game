using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Propane.Bots;
using Propane.Core;
using Propane.Fx;
using Propane.Modes;
using Propane.Player;
using Propane.UI;
using Propane.World;

namespace Propane.Net;

/// <summary>
/// The lobby world: a <see cref="Match"/> of the Lobby mode, which everyone in a waiting lobby roams together. It
/// builds and replicates like any match; what differs lives here. Its actors follow the lobby's roster live: whoever
/// joins arrives at their slot's spot on the arrival ring, whoever leaves (or is removed, for a bot) goes, and names and
/// colors follow their changes. Owned bots get their brains as they arrive. Its piles stand on marked pads and build
/// themselves again when refilled. The lobby panel opens over it, and the stage locks everyone's input.
/// </summary>
public partial class Match
{
    private readonly HashSet<int> lobbyDeparted = new();
    private LobbyFloor? lobbyFloor;
    private bool panelOpen;
    private bool lobbyStaged;

    /// <summary>This lobby world follows a match the same peers just played (its arrival says so).</summary>
    public bool Returning { get; init; }

    /// <summary>A lobby world rather than a match.</summary>
    public bool IsLobby => Setup.ModeId == ModeRegistry.LobbyWorldId;

    /// <summary>The player asked for the lobby panel (Tab, or View / Create on a controller).</summary>
    public event Action? PanelRequested;

    /// <summary>Lobby piles that refilled and built themselves here.</summary>
    public int LobbyRefills => lobbyFloor?.Materialized ?? 0;

    /// <summary>
    /// The lobby panel is open over this world: the mouse is free and the player's character stands still until it
    /// closes. Set by whoever shows the panel.
    /// </summary>
    public bool PanelOpen
    {
        get => panelOpen;
        set
        {
            panelOpen = value;
            if (World is { IsAlive: true } && !menu.Visible) CaptureMouse(!value && !Staging);
        }
    }

    /// <summary>The lobby's start is staged: everyone watches the countdown, standing still.</summary>
    private bool Staging => Net.Lobby is { Phase: LobbyServer.LobbyPhase.Staging };

    /// <summary>The pads, ring and title of the lobby world's floor, and the builder of its refilled piles.</summary>
    private void BuildLobbyContent()
    {
        var tuning = World.Tuning;
        lobbyFloor = new LobbyFloor
        {
            World = World,
            Name = "LobbyFloor",
            Spots = LobbyLayout.Spots(tuning.LobbyPiles, tuning.LobbyPileRing),
            Title = Net.Lobby is { } lobby ? LobbyText.Title(lobby) : "",
        };
        suburb.AddChild(lobbyFloor);
    }

    /// <summary>The lobby world's own views and subscriptions, after the match's.</summary>
    private void AttachLobby()
    {
        if (!IsLobby) return;
        if (CreateLocalView)
        {
            Session.AddView(this, new LobbyCard
            {
                Net = Net,
                Name = "LobbyCard",
                Arrival = Returning ? "Back in the lobby, ready for another" : "Roam, shoot and throw tanks until the match starts",
            });
        }
        Net.LobbyChanged += OnLobbyChanged;
        AuthorityResolved += MarkStock;
        Session.OnUnsubscribing(() =>
        {
            Net.LobbyChanged -= OnLobbyChanged;
            AuthorityResolved -= MarkStock;
        });
    }

    /// <summary>The roster as it is now, once the world is ready (it may have changed since the server sent the start).</summary>
    private void ReadyLobby()
    {
        if (!IsLobby) return;
        if (lobbyFloor != null && player != null) lobbyFloor.Facing = player.FacingYaw;
        OnLobbyChanged();
    }

    private void OnLobbyChanged()
    {
        if (World is not { IsAlive: true } || Net.Lobby is not { } lobby) return;
        if (lobbyFloor != null) lobbyFloor.Title = LobbyText.Title(lobby);
        ReconcileRoster(lobby);
        // The stage holds everyone where they stand; play resumes if the creator takes the start back.
        var staging = lobby.Phase == LobbyServer.LobbyPhase.Staging;
        foreach (var owned in Session.OwnedActors) owned.Character.InputLocked = phase != Phase.Playing || staging;
        if (staging == lobbyStaged) return;
        lobbyStaged = staging;
        if (staging) menu.Visible = false;
        if (CreateLocalView) CaptureMouse(!staging && !panelOpen);
    }

    /// <summary>
    /// Makes this world's actors the roster's present actors: arrivals at their slot's spot, departures gone, and
    /// changed names, colors and bot difficulties applied. This peer's own player never leaves its world here: leaving
    /// the lobby tears the world down instead.
    /// </summary>
    private void ReconcileRoster(LobbyInfo lobby)
    {
        var present = lobby.Members.Where(m => m.Present).ToDictionary(m => m.Id);
        var changed = false;
        foreach (var actor in Session.Actors.ToArray())
        {
            var id = actor.Character.ActorId;
            if (present.ContainsKey(id) || actor.Character == player) continue;
            ShowNotice($"{profiles[id].Name} left", 3f);
            RemoveLobbyActor(actor);
            changed = true;
        }
        foreach (var member in present.Values)
        {
            if (Session.Actor(member.Id) is { } known)
            {
                UpdateLobbyActor(known.Character, member);
                continue;
            }
            if (lobbyDeparted.Contains(member.Id)) continue;
            var (at, yaw) = LobbyLayout.Arrival(member.AllocationSlot, World.Tuning.LobbySpawnRing);
            var character = AddActor(member, -1, Suburb.ToWorld(new Vector2((float)at.X, (float)at.Z), 0.05f), (float)yaw);
            if (!character.IsRemote)
            {
                SubscribeOwned(character);
                if (member.Kind == ActorKind.Bot) AttachLobbyBot(character, member);
            }
            ShowNotice($"{member.Name} joined", 3f);
            changed = true;
        }
        if (changed) RefreshBotPeers();
    }

    private void UpdateLobbyActor(PlayerCharacter character, PlayerProfile member)
    {
        var prior = profiles[member.Id];
        profiles[member.Id] = member;
        if (prior.Kind != member.Kind) RefreshPlayer();
        if (prior.Appearance.Color != member.Appearance.Color) character.BodyColor = member.Appearance.Color;
        if (prior.ControllerConfig.Difficulty != member.ControllerConfig.Difficulty && BotBrain(member.Id) is { } brain)
            brain.Skill = BotProfile.For(member.ControllerConfig.Difficulty, World.Tuning).Skill;
    }

    private void RemoveLobbyActor(ActorRuntime actor)
    {
        var id = actor.Character.ActorId;
        foreach (var bot in bots.Where(b => b.Actor == actor.Character).ToArray())
        {
            bot.Brain.Dispose();
            bots.Remove(bot);
        }
        lobbyDeparted.Add(id);
        Session.RemoveActor(actor);
    }

    /// <summary>A lobby bot this peer added while the world ran: its brain, as <see cref="AttachBots"/> gives the first ones theirs.</summary>
    private void AttachLobbyBot(PlayerCharacter actor, PlayerProfile profile)
    {
        if (mode.CreateBot is not { } create) return;
        botServices ??= new BotServices(World, plan.Bounds);
        var brain = create((ulong)(uint)Setup.Id << 32 | (uint)profile.Id);
        brain.Skill = BotProfile.For(profile.ControllerConfig.Difficulty, World.Tuning).Skill;
        actor.InputOverride = brain;
        var view = new OwnedBot(this, actor, brain, botServices);
        brain.Prepare(view);
        bots.Add(view);
    }

    /// <summary>
    /// Bots shoot, reload and fetch by their magazine, so where rifles never run dry each bot this peer runs carries one
    /// whose reserve stays full. People carry none.
    /// </summary>
    private void RefillLobbyRifles()
    {
        if (LimitedAmmo) return;
        foreach (var owned in Session.OwnedActors)
        {
            if (profiles[owned.Character.ActorId].Kind != ActorKind.Bot) continue;
            var ammo = owned.Character.Ammo ??= AmmoState.Starting(World.Tuning);
            ammo.Reserve = World.Tuning.MaxReserve;
        }
    }

    /// <summary>The arrival's view of the world marks the piles standing in it as piles, as a built plan's are.</summary>
    private void MarkStock(AuthorityMessage result)
    {
        if (phase != Phase.Loading || result.Request != 0 || result.Reason != ClaimRefusal.None) return;
        foreach (var body in result.Bodies)
        {
            if (body.State == TankLedger.State.Intact && body.Rest && tanks.TryGetValue(body.Body.Id, out var tank) && IsInstanceValid(tank) &&
                tank.PilePose == null)
            {
                tank.MarkPile(body.Body.Transform);
            }
        }
    }

    /// <summary>A refilled pile builds itself on its pad.</summary>
    private void MaterializeLobbyPile(IReadOnlyList<Tank.PropaneTank> built, KingPileSpawned spawned) =>
        lobbyFloor?.Materialize(built, Tank.PileSite.Center(spawned.Tanks.Select(t => t.Transform).ToArray()));

    /// <summary>
    /// The lobby world's keys: the panel opens on its own key; while it is open (or the stage counts down) the world
    /// leaves the mouse alone. True when the event is handled here.
    /// </summary>
    private bool LobbyInput(InputEvent @event)
    {
        if (!IsLobby) return false;
        if (@event.IsActionPressed(InputSetup.LobbyPanel) && !panelOpen && !menu.Visible && !Staging)
        {
            GetViewport().SetInputAsHandled();
            PanelRequested?.Invoke();
            return true;
        }
        return (panelOpen || Staging) && (@event is InputEventMouseButton || @event.IsActionPressed(InputSetup.Pause));
    }
}
