using System;
using Godot;
using Propane.Bots;
using Propane.Core;
using Propane.Fx;
using Propane.Modes;
using Propane.Player;
using Propane.UI;
using Propane.World;

namespace Propane.Net;

/// <summary>Lobby world view, input delivery and subscription binding; LobbyClientAdapter owns mode policy.</summary>
public partial class Match
{
    private LobbyFloor? lobbyFloor;
    private LobbyClientAdapter? LobbyAdapter => clientMode as LobbyClientAdapter;

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
        get => LobbyAdapter?.PanelOpen ?? false;
        set
        {
            if (LobbyAdapter is { } adapter) adapter.PanelOpen = value;
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
        var adapter = LobbyAdapter!;
        AuthorityResolved += adapter.MarkStock;
        Session.OnUnsubscribing(() =>
        {
            Net.LobbyChanged -= OnLobbyChanged;
            AuthorityResolved -= adapter.MarkStock;
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
        LobbyAdapter!.LobbyChanged(lobby, lobby.Phase == LobbyServer.LobbyPhase.Staging);
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
    /// The lobby world's keys: the panel opens on its own key; while it is open (or the stage counts down) the world
    /// leaves the mouse alone. True when the event is handled here.
    /// </summary>
    private bool LobbyInput(InputEvent @event)
    {
        if (LobbyAdapter is not { } adapter) return false;
        var action = adapter.Input(@event.IsActionPressed(InputSetup.LobbyPanel),
            @event is InputEventMouseButton || @event.IsActionPressed(InputSetup.Pause), menu.Visible, Staging);
        if (action == ClientLobbyInput.OpenPanel)
        {
            GetViewport().SetInputAsHandled();
            PanelRequested?.Invoke();
            return true;
        }
        return action == ClientLobbyInput.Consume;
    }
}
