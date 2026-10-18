using System;
using Godot;
using Propane.Core;
using Propane.Net;

namespace Propane.UI;

/// <summary>Mode, supply, mint pace, duration and capacity controls and adjustments.</summary>
internal sealed class LobbySettingsPanel
{
    private readonly NetClient net;

    public LobbySettingsPanel(NetClient net) => this.net = net;

    public void Build(VBoxContainer layout, LobbyInfo lobby)
    {
        var tuning = net.Template;
        BuildModeChoice(layout, lobby);

        // Match length (a King round's length): the creator sets it.
        var king = lobby.Mode.ModeId == Modes.ModeRegistry.KingId;
        var length = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        length.AddThemeConstantOverride("separation", 12);
        length.AddChild(UiStyle.Text(king ? "Round length" : "Match length", 20));
        var step = Mathf.Max(tuning.MatchLengthStep, 1f);
        if (net.IsCreator)
        {
            var shorter = UiStyle.Button("−");
            shorter.CustomMinimumSize = new Vector2(44, 40);
            shorter.Disabled = lobby.MatchLength <= tuning.MatchLengthMin;
            shorter.Pressed += () => net.SetMatchLength(Mathf.Max(tuning.MatchLengthMin, lobby.MatchLength - step));
            length.AddChild(shorter);
        }
        if (net.IsCreator)
        {
            var minutes = UiStyle.Button(UiStyle.FormatTime(lobby.MatchLength));
            minutes.SetMeta("focus_key", "match_length");
            minutes.TooltipText = $"Left / right to change {(king ? "round" : "match")} length";
            minutes.CustomMinimumSize = new Vector2(80, 40);
            // Keep the existing mouse −/+ buttons; the time itself is a controller adjustment target.
            minutes.GuiInput += e =>
            {
                if (e is InputEventKey && (e.IsActionPressed("ui_left") || e.IsActionPressed("ui_right")))
                {
                    AdjustMatchLength(e.IsActionPressed("ui_left") ? -1 : 1);
                    minutes.AcceptEvent();
                }
            };
            length.AddChild(minutes);
        }
        else
        {
            var minutes = UiStyle.Title(UiStyle.FormatTime(lobby.MatchLength), 26);
            minutes.CustomMinimumSize = new Vector2(80, 0);
            length.AddChild(minutes);
        }
        if (net.IsCreator)
        {
            var longer = UiStyle.Button("+");
            longer.CustomMinimumSize = new Vector2(44, 40);
            longer.Disabled = lobby.MatchLength >= tuning.MatchLengthMax;
            longer.Pressed += () => net.SetMatchLength(Mathf.Min(tuning.MatchLengthMax, lobby.MatchLength + step));
            length.AddChild(longer);
        }
        var settings = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        settings.AddThemeConstantOverride("separation", 24);
        settings.AddChild(length);
        var capacity = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        capacity.AddThemeConstantOverride("separation", 8);
        capacity.AddChild(UiStyle.Text("Capacity", 20));
        if (net.IsCreator)
        {
            var fewer = UiStyle.Button("−");
            fewer.SetMeta("focus_key", "capacity_down");
            fewer.Disabled = lobby.Phase != LobbyServer.LobbyPhase.Waiting || lobby.Capacity <= Mathf.Max(2, lobby.Members.Count);
            fewer.Pressed += () => net.SetCapacity(lobby.Capacity - 1);
            capacity.AddChild(fewer);
        }
        capacity.AddChild(UiStyle.Text($"{lobby.Capacity} / {net.LobbyCapacity}", 20, UiStyle.Ink));
        if (net.IsCreator)
        {
            var more = UiStyle.Button("+");
            more.SetMeta("focus_key", "capacity_up");
            more.Disabled = lobby.Phase != LobbyServer.LobbyPhase.Waiting || lobby.Capacity >= net.LobbyCapacity;
            more.Pressed += () => net.SetCapacity(lobby.Capacity + 1);
            capacity.AddChild(more);
        }
        settings.AddChild(capacity);
        layout.AddChild(settings);
    }

    // The same mode/supply controls serve classic and docked lobbies.
    private void BuildModeChoice(VBoxContainer layout, LobbyInfo lobby)
    {
        var settings = lobby.Mode;
        var chosen = Modes.ModeRegistry.ForLobby(settings.ModeId) ?? Modes.ModeRegistry.TankBank;
        var waiting = lobby.Phase == LobbyServer.LobbyPhase.Waiting;
        var modes = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        modes.AddThemeConstantOverride("separation", 10);
        modes.AddChild(UiStyle.Text("Mode", 20));
        if (net.IsCreator)
        {
            foreach (var mode in Modes.ModeRegistry.Lobby)
            {
                var button = UiStyle.Button(mode.Name);
                button.SetMeta("focus_key", $"mode_{mode.Id}");
                button.Disabled = !waiting;
                UiStyle.Choose(button, mode == chosen);
                var id = mode.Id;
                button.Pressed += () => { if (id != net.Lobby?.Mode.ModeId) net.SetMode(settings with { ModeId = id }); };
                modes.AddChild(button);
            }
        }
        else
        {
            modes.AddChild(UiStyle.Title(chosen.Name, 24));
        }
        layout.AddChild(modes);
        layout.AddChild(UiStyle.Text(chosen.Id == Modes.ModeRegistry.KingId
            ? "Hold the beam alone to mint banked tanks; it moves between tank piles. Hits and throws spill."
            : "Every tank you blow up is banked; getting hit or thrown spills them. Most banked wins.", 15, UiStyle.SoftInk, HorizontalAlignment.Center));
        BuildLobbyDial(layout, "Tank supply", $"{settings.TankSupply}×", "tank_supply", "tank_less", "tank_more",
            waiting, settings.TankSupply <= Tuning.TankSupplyMinimum, settings.TankSupply >= Tuning.TankSupplyMaximum, AdjustTankSupply);
        // Guests do not have the creator's debug tuning until match start. Label the default-tuning preview rather
        // than pretending their local template describes this match; generation remains the server's job.
        using var defaults = new Tuning();
        defaults.KingMintSeconds = settings.MintSeconds;
        defaults.TankCountMultiplier = settings.TankSupply;
        var count = chosen.Supply.Suburb(defaults, lobby.Members.Count).TankCount;
        layout.AddChild(UiStyle.Text($"{count} tanks at default tuning · {lobby.Members.Count} players", 15, UiStyle.SoftInk, HorizontalAlignment.Center));
        if (chosen.Id == Modes.ModeRegistry.KingId)
        {
            BuildLobbyDial(layout, "Mint pace", $"A tank every {settings.MintSeconds:0.0} s", "mint_pace", "mint_faster", "mint_slower",
                waiting, settings.MintSeconds <= Modes.KingRules.MintPaceMin, settings.MintSeconds >= Modes.KingRules.MintPaceMax, AdjustMintPace);
        }
    }

    private void BuildLobbyDial(VBoxContainer layout, string label, string text, string key, string lessKey, string moreKey,
        bool waiting, bool atMinimum, bool atMaximum, Action<int> adjust)
    {
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        row.AddThemeConstantOverride("separation", 12);
        row.AddChild(UiStyle.Text(label, 20));
        if (net.IsCreator)
        {
            var less = UiStyle.Button("−");
            less.SetMeta("focus_key", lessKey);
            less.CustomMinimumSize = new Vector2(44, 40);
            less.Disabled = !waiting || atMinimum;
            less.Pressed += () => adjust(-1);
            row.AddChild(less);
            var value = UiStyle.Button(text);
            value.SetMeta("focus_key", key);
            value.TooltipText = $"Left / right to change {label.ToLowerInvariant()}";
            value.Disabled = !waiting;
            value.GuiInput += e =>
            {
                if (e is InputEventKey && (e.IsActionPressed("ui_left") || e.IsActionPressed("ui_right")))
                {
                    adjust(e.IsActionPressed("ui_left") ? -1 : 1);
                    value.AcceptEvent();
                }
            };
            row.AddChild(value);
            var more = UiStyle.Button("+");
            more.SetMeta("focus_key", moreKey);
            more.CustomMinimumSize = new Vector2(44, 40);
            more.Disabled = !waiting || atMaximum;
            more.Pressed += () => adjust(1);
            row.AddChild(more);
        }
        else
        {
            row.AddChild(UiStyle.Title(text, 24));
        }

        layout.AddChild(row);
    }

    public void AdjustTankSupply(int direction)
    {
        if (net.Lobby is not { Phase: LobbyServer.LobbyPhase.Waiting } lobby || !net.IsCreator) return;
        var supply = Math.Clamp(lobby.Mode.TankSupply + direction, Tuning.TankSupplyMinimum, Tuning.TankSupplyMaximum);
        if (supply != lobby.Mode.TankSupply) net.SetMode(lobby.Mode with { TankSupply = supply });
    }

    /// <summary>Left is faster (fewer seconds per tank), right slower.</summary>
    public void AdjustMintPace(int direction)
    {
        if (net.Lobby is not { Phase: LobbyServer.LobbyPhase.Waiting } lobby || !net.IsCreator) return;
        var seconds = (float)Math.Clamp(lobby.Mode.MintSeconds + direction * Modes.KingRules.MintPaceStep, Modes.KingRules.MintPaceMin, Modes.KingRules.MintPaceMax);
        if (seconds != lobby.Mode.MintSeconds) net.SetMode(lobby.Mode with { MintSeconds = seconds });
    }

    public void AdjustMatchLength(int direction)
    {
        if (net.Lobby is not { } lobby || !net.IsCreator)
        {
            return;
        }
        var tuning = net.Template;
        var seconds = Mathf.Clamp(lobby.MatchLength + direction * Mathf.Max(tuning.MatchLengthStep, 1f),
            tuning.MatchLengthMin, tuning.MatchLengthMax);
        if (seconds != lobby.MatchLength)
        {
            net.SetMatchLength(seconds);
        }
    }
}
