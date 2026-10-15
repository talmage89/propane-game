using System;
using System.Linq;
using Godot;
using Propane.Core;
using Propane.Net;

namespace Propane.UI;

/// <summary>
/// The moment between the lobby and a match. When the creator starts, the lobby world washes out behind the next
/// match's mode, its settings and everyone's card, and the count runs down in big numbers; the creator can still take
/// it back. Then the world goes, the void fills in white and the same lineup waits while the suburb builds.
/// </summary>
public partial class StageScreen : CanvasLayer
{
    public enum Moment { Countdown, Loading }

    public required NetClient Net { get; init; }

    /// <summary>The creator takes the start back (its button, Esc, or B on a controller).</summary>
    public event Action? CancelRequested;

    private ColorRect wash = null!;
    private ColorRect band = null!;
    private Label mode = null!;
    private Label settings = null!;
    private HFlowContainer lineup = null!;
    private Label count = null!;
    private Label status = null!;
    private Button cancel = null!;
    private VBoxContainer layout = null!;
    private Moment moment;
    private int shown = -1;
    private float pop;
    private float age;
    private uint built;

    public Moment Current => moment;

    public override void _Ready()
    {
        Layer = 45;
        Visible = false;
        wash = new ColorRect { MouseFilter = Control.MouseFilterEnum.Stop };
        wash.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(wash);
        band = new ColorRect { Color = new Color(UiStyle.Accent, 0.9f), MouseFilter = Control.MouseFilterEnum.Ignore };
        band.SetAnchorsPreset(Control.LayoutPreset.CenterLeft);
        AddChild(band);

        var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(center);
        layout = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(980, 0) };
        layout.AddThemeConstantOverride("separation", 6);
        center.AddChild(layout);
        var next = Label(18, new Color(UiStyle.Ink, 0.6f));
        next.Text = "NEXT MATCH";
        layout.AddChild(next);
        mode = Label(88, UiStyle.Ink);
        layout.AddChild(mode);
        settings = Label(22, new Color(UiStyle.Ink, 0.7f));
        layout.AddChild(settings);
        layout.AddChild(new Control { CustomMinimumSize = new Vector2(0, 18), MouseFilter = Control.MouseFilterEnum.Ignore });
        lineup = new HFlowContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Alignment = FlowContainer.AlignmentMode.Center };
        lineup.AddThemeConstantOverride("h_separation", 12);
        lineup.AddThemeConstantOverride("v_separation", 10);
        layout.AddChild(lineup);
        layout.AddChild(new Control { CustomMinimumSize = new Vector2(0, 14), MouseFilter = Control.MouseFilterEnum.Ignore });
        count = Label(150, UiStyle.Accent);
        count.CustomMinimumSize = new Vector2(0, 170);
        count.PivotOffset = new Vector2(490, 85);
        layout.AddChild(count);
        status = Label(22, new Color(UiStyle.Ink, 0.75f));
        layout.AddChild(status);
        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        cancel = UiStyle.Button("Cancel start");
        cancel.SetMeta("focus_key", "cancel_start");
        cancel.Pressed += () => CancelRequested?.Invoke();
        buttons.AddChild(cancel);
        layout.AddChild(buttons);
        AddChild(new MenuNavigation { Screen = this, Root = layout, Back = () => { if (CanCancel) CancelRequested?.Invoke(); } });
        Net.LobbyChanged += Rebuild;
    }

    public override void _ExitTree() => Net.LobbyChanged -= Rebuild;

    private bool CanCancel => moment == Moment.Countdown && Net.IsCreator && Visible;

    /// <summary>The countdown over the lobby world.</summary>
    public void ShowCountdown()
    {
        if (Visible && moment == Moment.Countdown) return;
        moment = Moment.Countdown;
        shown = -1;
        age = 0;
        Visible = true;
        Rebuild();
    }

    /// <summary>The world has gone; the same lineup waits on white while the match builds.</summary>
    public void ShowLoading()
    {
        moment = Moment.Loading;
        Visible = true;
        Rebuild();
    }

    public void Close() => Visible = false;

    public override void _Input(InputEvent e)
    {
        if (CanCancel && e is InputEventKey && e.IsActionPressed(InputSetup.Pause))
        {
            GetViewport().SetInputAsHandled();
            CancelRequested?.Invoke();
        }
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        var dt = (float)delta;
        age += dt;
        var size = GetViewport().GetVisibleRect().Size;
        // The world washes out as the stage arrives; once it has gone, the void is solid white behind the lineup.
        var target = moment == Moment.Loading ? 1f : 0.62f;
        wash.Color = new Color(0.96f, 0.96f, 0.97f, Mathf.Lerp(0, target, Mathf.Clamp(age / 0.35f, 0, 1)));
        band.Size = new Vector2(size.X * Mathf.Clamp(age / 0.5f, 0, 1), 6);
        // The accent sweeps in just above the lineup's heading, however tall the lineup grows.
        band.Position = new Vector2(0, layout.GetGlobalRect().Position.Y - 24);
        pop = Mathf.MoveToward(pop, 0, dt * 2.5f);
        count.Scale = Vector2.One * (1f + pop * pop * 0.35f);
        if (moment == Moment.Countdown)
        {
            var left = Mathf.CeilToInt((float)Net.StageLeft);
            if (left != shown)
            {
                shown = left;
                pop = 1;
                count.Text = left > 0 ? left.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
            }
            status.Text = left > 0 ? Net.IsCreator ? "Starting. Changed your mind?" : $"{CreatorName()} started the match" : "Here we go";
        }
        else
        {
            count.Text = "";
            status.Text = "Building the suburb" + new string('.', 1 + (int)(age * 2.5f) % 3);
        }
        cancel.Visible = CanCancel;
    }

    private string CreatorName() => Net.Lobby is { } lobby ? LobbyText.Creator(lobby) : "The creator";

    private void Rebuild()
    {
        if (Net.Lobby is not { } lobby || !IsInsideTree()) return;
        mode.Text = LobbyText.Mode(lobby).Name.ToUpperInvariant();
        settings.Text = LobbyText.Details(lobby);
        // The roster only changes here when someone leaves; rebuild the cards only then.
        if (built == lobby.RosterRevision && lineup.GetChildCount() > 0) return;
        built = lobby.RosterRevision;
        foreach (var child in lineup.GetChildren())
        {
            lineup.RemoveChild(child);
            child.QueueFree();
        }
        foreach (var member in lobby.Members) lineup.AddChild(Card(member));
        cancel.Visible = CanCancel;
    }

    /// <summary>One player's card: their color down the left edge, their name, and what they are.</summary>
    private Control Card(PlayerProfile member)
    {
        var card = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(220, 0) };
        card.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(1, 1, 1, 0.92f),
            BorderColor = member.Appearance.Color,
            BorderWidthLeft = 10,
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8,
            ContentMarginLeft = 16,
            ContentMarginRight = 14,
            ContentMarginTop = 8,
            ContentMarginBottom = 8,
            ShadowColor = new Color(0, 0, 0, 0.12f),
            ShadowSize = 8,
        });
        var text = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        text.AddThemeConstantOverride("separation", -2);
        var name = UiStyle.Text(member.Name, 22, UiStyle.Ink);
        name.AddThemeFontOverride("font", UiStyle.BoldFont);
        name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        text.AddChild(name);
        var bot = member.Kind == ActorKind.Bot;
        var what = bot ? $"{(Bots.BotDifficulty)Math.Min(member.ControllerConfig.Difficulty, (byte)Bots.BotDifficulty.Hard)} bot"
            : member.OwnerPeerId == Net.MyId ? "You" : member.OwnerPeerId == Net.Lobby?.CreatorId ? "Creator" : "Player";
        text.AddChild(UiStyle.Text(what, 15, UiStyle.SoftInk));
        card.AddChild(text);
        return card;
    }

    private static Label Label(int size, Color color)
    {
        var label = new Label { HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
        label.AddThemeFontOverride("font", UiStyle.BoldFont);
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color);
        label.AddThemeColorOverride("font_outline_color", new Color(1, 1, 1, 0.8f));
        label.AddThemeConstantOverride("outline_size", Math.Max(4, size / 10));
        return label;
    }
}
