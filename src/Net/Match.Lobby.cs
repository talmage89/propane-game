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
    private LobbyClientAdapter? LobbyAdapter => clientMode as LobbyClientAdapter;

    /// <summary>This lobby world follows a match the same peers just played (its arrival says so).</summary>
    public bool Returning { get; init; }

    /// <summary>A lobby world rather than a match.</summary>
    public bool IsLobby => Setup.ModeId == ModeRegistry.LobbyWorldId;

    /// <summary>The player asked for the lobby panel (Tab, or View / Create on a controller).</summary>
    public event Action? PanelRequested;

    /// <summary>Lobby piles that refilled and built themselves here.</summary>
    public int LobbyRefills => viewBinding.LobbyRefills;

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
            viewBinding.PanelChanged(value);
        }
    }

    /// <summary>The lobby's start is staged: everyone watches the countdown, standing still.</summary>
    private bool Staging => Net.Lobby is { Phase: LobbyPhase.Staging };

    private void AttachLobby()
    {
        if (!IsLobby) return;
        viewBinding.AddLobbyCard();
        Net.LobbyChanged += OnLobbyChanged;
        var adapter = LobbyAdapter!;
        AuthorityResolved += adapter.MarkStock;
        Session.OnUnsubscribing(() =>
        {
            Net.LobbyChanged -= OnLobbyChanged;
            AuthorityResolved -= adapter.MarkStock;
        });
    }

    private void ReadyLobby()
    {
        if (!IsLobby) return;
        viewBinding.FaceLobby();
        OnLobbyChanged();
    }

    private void OnLobbyChanged()
    {
        if (World is not { IsAlive: true } || Net.Lobby is not { } lobby) return;
        LobbyAdapter!.LobbyChanged(lobby, lobby.Phase == LobbyPhase.Staging);
    }

    private void UpdateLobbyActor(PlayerCharacter character, PlayerProfile member)
    {
        var prior = profiles[member.Id];
        profiles[member.Id] = member;
        if (prior.Kind != member.Kind) viewBinding.RefreshPlayer();
        if (prior.Appearance.Color != member.Appearance.Color) character.BodyColor = member.Appearance.Color;
        if (prior.ControllerConfig.Difficulty != member.ControllerConfig.Difficulty && ownedBots.BotBrain(member.Id) is { } brain)
            brain.Skill = BotProfile.For(member.ControllerConfig.Difficulty, World.Tuning).Skill;
    }

    private bool LobbyInput(InputEvent @event)
    {
        if (LobbyAdapter is not { } adapter) return false;
        var action = adapter.Input(@event.IsActionPressed(InputSetup.LobbyPanel),
            @event is InputEventMouseButton || @event.IsActionPressed(InputSetup.Pause), viewBinding.MenuVisible, Staging);
        if (action == ClientLobbyInput.OpenPanel)
        {
            GetViewport().SetInputAsHandled();
            PanelRequested?.Invoke();
            return true;
        }
        return action == ClientLobbyInput.Consume;
    }
}
