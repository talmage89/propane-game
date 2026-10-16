using System;
using Godot;
using Propane.Bots;
using Propane.Net;

namespace Propane.UI;

/// <summary>Roster rows and owner-only bot edit affordances.</summary>
internal static class LobbyRoster
{
    public static void Build(VBoxContainer layout, NetClient net, LobbyInfo lobby, bool compact, Action<PlayerProfile> editBot)
    {
        var roster = new GridContainer { Columns = compact ? 2 : 1 };
        roster.AddThemeConstantOverride("h_separation", 24);
        roster.AddThemeConstantOverride("v_separation", 2);
        layout.AddChild(roster);
        foreach (var member in lobby.Members)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 12);
            row.AddChild(UiStyle.ColorSample(member.Appearance.Color, 20, member.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            var bot = member.Kind == ActorKind.Bot;
            var difficulty = (BotDifficulty)Math.Min(member.ControllerConfig.Difficulty, (byte)BotDifficulty.Hard);
            var label = UiStyle.Text(member.Name + (bot ? $"  ({difficulty} bot)" : member.OwnerPeerId == net.MyId ? "  (you)" : ""), compact ? 18 : 20);
            label.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            label.CustomMinimumSize = new Vector2(compact ? 200 : 260, 0);
            label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            row.AddChild(label);
            if (member.OwnerPeerId == lobby.CreatorId && member.Kind == ActorKind.Human)
            {
                row.AddChild(UiStyle.Text(compact ? "creator" : "starts the match", 15, UiStyle.SoftInk));
            }
            if (bot && member.OwnerPeerId == net.MyId)
            {
                // Only a bot's owner may change it; the server enforces the same rule.
                var edit = UiStyle.Button("Edit");
                // As short as a name row, so a full lobby of bots still fits on the screen.
                edit.CustomMinimumSize = new Vector2(0, compact ? 26 : 30);
                edit.AddThemeFontSizeOverride("font_size", 15);
                edit.SetMeta("focus_key", $"bot_{member.Id}");
                edit.Disabled = lobby.Phase != LobbyServer.LobbyPhase.Waiting;
                var editing = member;
                edit.Pressed += () => editBot(editing);
                row.AddChild(edit);
            }
            roster.AddChild(row);
        }
    }
}
