using System.Linq;
using Godot;
using Propane.Bots;
using Propane.Core;
using Propane.Net;

namespace Propane.UI;

/// <summary>The lobby's name, mode and settings as one line of text each, as the lobby card, panel and stage show them.</summary>
public static class LobbyText
{
    public static string Title(LobbyInfo lobby) => $"{Creator(lobby).ToUpperInvariant()}'S LOBBY";

    /// <summary>The creator's name, who starts the match.</summary>
    public static string Creator(LobbyInfo lobby) =>
        lobby.Members.FirstOrDefault(m => m.OwnerPeerId == lobby.CreatorId && m.Kind == ActorKind.Human)?.Name ?? "?";

    /// <summary>"Tank Bank · 2:30 · 2× tanks", or King's round, pace and supply.</summary>
    public static string Settings(LobbyInfo lobby) => $"{Mode(lobby).Name} · {Details(lobby)}";

    /// <summary>The settings without the mode: "2:30 · 2× tanks".</summary>
    public static string Details(LobbyInfo lobby)
    {
        var king = Mode(lobby).Id == Modes.ModeRegistry.KingId;
        var pace = king ? $" · a tank every {lobby.Mode.MintSeconds:0.0} s" : "";
        return $"{UiStyle.FormatTime(lobby.MatchLength)}{(king ? " rounds" : "")}{pace} · {lobby.Mode.TankSupply}× tanks";
    }

    public static Modes.ModeDefinition Mode(LobbyInfo lobby) => Modes.ModeRegistry.ForLobby(lobby.Mode.ModeId) ?? Modes.ModeRegistry.TankBank;

    /// <summary>A member's name with what they are: you, a bot of some difficulty.</summary>
    public static string Member(PlayerProfile member, int me) => member.Name + (member.Kind == ActorKind.Bot
        ? $"  ·  {(BotDifficulty)System.Math.Min(member.ControllerConfig.Difficulty, (byte)BotDifficulty.Hard)} bot"
        : member.OwnerPeerId == me ? "  ·  you" : "");
}

/// <summary>
/// The lobby world's own HUD: a card in the corner with the lobby, its next match and who is here, and the key that
/// opens the lobby panel; and, on arrival, the lobby's name across the middle of the screen for a moment.
/// </summary>
public partial class LobbyCard : CanvasLayer
{
    private const float BannerSeconds = 4.5f;
    private const int RowsShown = 8;

    public required NetClient Net { get; init; }
    /// <summary>The arrival banner's subtitle: a first arrival, or back after a match.</summary>
    public string Arrival { get; init; } = "Roam, shoot and throw tanks until the match starts";

    private VBoxContainer card = null!;
    private Label title = null!;
    private Label settings = null!;
    private VBoxContainer roster = null!;
    private Label hint = null!;
    private Label key = null!;
    private Control banner = null!;
    private Label bannerTitle = null!;
    private float bannerAge;
    private bool gamepad;

    public override void _Ready()
    {
        Layer = 12;
        var root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(root);

        var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Position = new Vector2(20, 20) };
        var style = UiStyle.Panel(0.78f);
        style.ContentMarginLeft = style.ContentMarginRight = 18;
        style.ContentMarginTop = style.ContentMarginBottom = 14;
        panel.AddThemeStyleboxOverride("panel", style);
        root.AddChild(panel);
        card = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(300, 0) };
        card.AddThemeConstantOverride("separation", 4);
        panel.AddChild(card);
        title = UiStyle.Title("", 22);
        title.HorizontalAlignment = HorizontalAlignment.Left;
        card.AddChild(title);
        settings = UiStyle.Text("", 15, UiStyle.SoftInk);
        card.AddChild(settings);
        card.AddChild(new HSeparator());
        roster = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        roster.AddThemeConstantOverride("separation", 2);
        card.AddChild(roster);
        card.AddChild(new HSeparator());
        var keys = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        keys.AddThemeConstantOverride("separation", 10);
        key = KeyChip();
        keys.AddChild(key);
        hint = UiStyle.Text("", 16, UiStyle.Ink);
        keys.AddChild(hint);
        card.AddChild(keys);

        banner = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.Center };
        banner.SetAnchorsPreset(Control.LayoutPreset.Center);
        banner.Position = new Vector2(-600, -230);
        banner.Size = new Vector2(1200, 150);
        root.AddChild(banner);
        bannerTitle = Outlined(64);
        banner.AddChild(bannerTitle);
        var subtitle = Outlined(22);
        subtitle.Text = Arrival;
        subtitle.AddThemeColorOverride("font_color", new Color(UiStyle.Ink, 0.75f));
        banner.AddChild(subtitle);

        Net.LobbyChanged += Refresh;
        Refresh();
    }

    public override void _ExitTree() => Net.LobbyChanged -= Refresh;

    public override void _Process(double delta)
    {
        bannerAge += (float)delta;
        var fade = Mathf.Clamp((bannerAge - (BannerSeconds - 1.2f)) / 1.2f, 0, 1);
        banner.Visible = bannerAge < BannerSeconds;
        banner.Modulate = new Color(1, 1, 1, Mathf.SmoothStep(0, 1, Mathf.Clamp(bannerAge / 0.4f, 0, 1)) * (1 - fade));
        if (gamepad != MenuNavigation.Gamepad) Refresh();
    }

    private void Refresh()
    {
        if (Net.Lobby is not { } lobby) return;
        gamepad = MenuNavigation.Gamepad;
        title.Text = LobbyText.Title(lobby);
        bannerTitle.Text = title.Text;
        settings.Text = LobbyText.Settings(lobby);
        foreach (var child in roster.GetChildren())
        {
            roster.RemoveChild(child);
            child.QueueFree();
        }
        foreach (var member in lobby.Members.Take(lobby.Members.Count > RowsShown ? RowsShown - 1 : RowsShown))
        {
            var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            row.AddThemeConstantOverride("separation", 10);
            row.AddChild(UiStyle.ColorSample(member.Appearance.Color, 14));
            var name = UiStyle.Text(LobbyText.Member(member, Net.MyId), 16, member.Kind == ActorKind.Bot ? UiStyle.SoftInk : UiStyle.Ink);
            name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            name.CustomMinimumSize = new Vector2(240, 0);
            row.AddChild(name);
            roster.AddChild(row);
        }
        if (lobby.Members.Count > RowsShown)
            roster.AddChild(UiStyle.Text($"and {lobby.Members.Count - RowsShown + 1} more", 15, UiStyle.SoftInk));
        key.Text = gamepad ? "View" : "Tab";
        hint.Text = Net.IsCreator ? "Lobby and match settings" : "Lobby: name, color, bots";
    }

    /// <summary>A small dark key cap, so the panel's key reads at a glance.</summary>
    private static Label KeyChip()
    {
        var chip = UiStyle.Text("", 14, Colors.White, HorizontalAlignment.Center);
        chip.AddThemeFontOverride("font", UiStyle.BoldFont);
        chip.CustomMinimumSize = new Vector2(48, 0);
        chip.AddThemeStyleboxOverride("normal", new StyleBoxFlat
        {
            BgColor = UiStyle.Ink,
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
            ContentMarginLeft = 8,
            ContentMarginRight = 8,
            ContentMarginTop = 2,
            ContentMarginBottom = 2,
        });
        return chip;
    }

    private static Label Outlined(int size)
    {
        var label = new Label { HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
        label.AddThemeFontOverride("font", UiStyle.BoldFont);
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", UiStyle.Ink);
        label.AddThemeColorOverride("font_outline_color", new Color(1, 1, 1, 0.85f));
        label.AddThemeConstantOverride("outline_size", size / 6);
        return label;
    }
}
