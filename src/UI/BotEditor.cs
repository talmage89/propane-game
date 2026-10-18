using System;
using System.Linq;
using Godot;
using Propane.Bots;
using Propane.Core;
using Propane.Net;

namespace Propane.UI;

/// <summary>
/// The lobby's add/edit bot dialog: a name, a color (presets or the appearance picker) and a difficulty. It sends one
/// roster request and closes when the server accepts it; a refusal stays open with the server's reason.
/// </summary>
public partial class BotEditor : CanvasLayer
{
    public required NetClient Net { get; init; }
    /// <summary>The owned bot to edit, or null to add one.</summary>
    public PlayerProfile? Editing { get; init; }
    public required string InitialName { get; init; }
    public required AppearanceRecord InitialAppearance { get; init; }
    public BotDifficulty InitialDifficulty { get; init; } = BotDifficulty.Normal;
    public event Action? Closed;

    public AppearanceRecord Appearance { get; private set; } = AppearanceRecord.FromPalette(0);
    public BotDifficulty Difficulty { get; private set; }
    public string Message => message.Text;

    private LineEdit name = null!;
    private Label message = null!;
    private Label difficultyNote = null!;
    private Button custom = null!;
    private Button save = null!;
    private Button? remove;
    private readonly Button[] swatches = new Button[AppearancePalette.PlayerColors.Length];
    private readonly Button[] difficulties = new Button[BotProfile.All.Length];
    private HBoxContainer colors = null!;
    private AppearancePicker? picker;
    private MenuNavigation navigation = null!;
    private uint pending;

    public override void _Ready()
    {
        Layer = 41;
        Appearance = InitialAppearance;
        Difficulty = InitialDifficulty;
        var shade = new ColorRect { Color = new Color(0.1f, 0.11f, 0.13f, 0.45f) };
        shade.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(shade);
        var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(center);
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", UiStyle.Panel(1));
        center.AddChild(panel);
        var layout = new VBoxContainer { CustomMinimumSize = new Vector2(540, 0) };
        layout.AddThemeConstantOverride("separation", 10);
        panel.AddChild(layout);
        layout.AddChild(UiStyle.Title(Editing == null ? "ADD BOT" : "EDIT BOT", 34));
        layout.AddChild(UiStyle.Text("Your bots play on this computer and leave with you.", 16, UiStyle.SoftInk, HorizontalAlignment.Center));
        layout.AddChild(new HSeparator());

        layout.AddChild(UiStyle.Text("Name", 16, UiStyle.SoftInk));
        name = UiStyle.Field(InitialName, "Bot name");
        name.MaxLength = Protocol.MaxNameLength;
        name.SetMeta("focus_key", "bot_name");
        name.TextChanged += _ => SetMessage("");
        layout.AddChild(name);

        layout.AddChild(UiStyle.Text("Color", 16, UiStyle.SoftInk));
        colors = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        colors.AddThemeConstantOverride("separation", 8);
        layout.AddChild(colors);
        custom = UiStyle.Button("");
        custom.SetMeta("focus_key", "bot_custom_color");
        custom.Pressed += OpenPicker;
        layout.AddChild(custom);
        RefreshColors();

        layout.AddChild(UiStyle.Text("Difficulty", 16, UiStyle.SoftInk));
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        row.AddThemeConstantOverride("separation", 10);
        foreach (var difficulty in BotProfile.All)
        {
            var button = difficulties[(int)difficulty] = UiStyle.Button(difficulty.ToString());
            button.CustomMinimumSize = new Vector2(120, 42);
            button.SetMeta("focus_key", $"difficulty_{difficulty.ToString().ToLowerInvariant()}");
            button.Pressed += () => SetDifficulty(difficulty);
            row.AddChild(button);
        }
        layout.AddChild(row);
        difficultyNote = UiStyle.Text("", 15, UiStyle.SoftInk, HorizontalAlignment.Center);
        layout.AddChild(difficultyNote);
        SetDifficulty(Difficulty);

        message = UiStyle.Text("", 16, new Color(0.75f, 0.2f, 0.12f), HorizontalAlignment.Center);
        message.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        message.CustomMinimumSize = new Vector2(480, 0);
        message.Visible = false;
        layout.AddChild(message);
        layout.AddChild(new HSeparator());
        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        buttons.AddThemeConstantOverride("separation", 10);
        save = UiStyle.Button(Editing == null ? "Add bot" : "Save");
        save.SetMeta("focus_key", "bot_save");
        save.Pressed += Submit;
        buttons.AddChild(save);
        if (Editing != null)
        {
            remove = UiStyle.Button("Remove bot");
            remove.SetMeta("focus_key", "bot_remove");
            remove.Pressed += Remove;
            buttons.AddChild(remove);
        }
        var cancel = UiStyle.Button("Cancel");
        cancel.SetMeta("focus_key", "bot_cancel");
        cancel.Pressed += Close;
        buttons.AddChild(cancel);
        layout.AddChild(buttons);
        navigation = new MenuNavigation { Screen = this, Root = layout, Back = Close };
        AddChild(navigation);
        Net.ActorRequestCompleted += OnResult;
        Net.LobbyChanged += OnLobbyChanged;
    }

    public override void _ExitTree()
    {
        Net.ActorRequestCompleted -= OnResult;
        Net.LobbyChanged -= OnLobbyChanged;
    }

    public override void _Input(InputEvent @event)
    {
        // Esc on a keyboard matches B on a controller, even while typing a name; a picker above it stays in charge.
        if (picker == null && Visible && @event is InputEventKey && @event.IsActionPressed("ui_cancel"))
        {
            GetViewport().SetInputAsHandled();
            Close();
        }
    }

    private void RefreshColors()
    {
        var focus = navigation?.FocusKey();
        foreach (var child in colors.GetChildren())
        {
            colors.RemoveChild(child);
            child.QueueFree();
        }
        for (var i = 0; i < swatches.Length; i++)
        {
            var color = AppearancePalette.PlayerColor(i);
            var swatch = swatches[i] = UiStyle.Swatch(color, Appearance.Color == AppearanceRecord.FromPalette(i).Color, $"bot_color_{i}",
                AppearancePalette.ColorNames[i]);
            swatch.Pressed += () => SetAppearance(Appearance.WithColor(color));
            colors.AddChild(swatch);
        }
        custom.Text = $"Custom color · #{Appearance.Color.ToHtml(false).ToUpperInvariant()}";
        // Rebuilt swatches need fresh neighbors; the picker reconfigures when it closes.
        if (navigation != null && picker == null) navigation.Configure(focus);
    }

    private void SetAppearance(AppearanceRecord appearance)
    {
        Appearance = appearance;
        SetMessage("");
        RefreshColors();
    }

    private void SetDifficulty(BotDifficulty difficulty)
    {
        Difficulty = difficulty;
        for (var i = 0; i < difficulties.Length; i++) UiStyle.Choose(difficulties[i], i == (int)difficulty);
        difficultyNote.Text = difficulty switch
        {
            BotDifficulty.Easy => "Slower to react, with looser aim.",
            BotDifficulty.Hard => "Quick to react, with tight aim.",
            _ => "Steady reactions and aim.",
        };
    }

    private void OpenPicker()
    {
        if (picker != null) return;
        picker = new AppearancePicker { Initial = Appearance, Title = "BOT COLOR" };
        picker.Applied += SetAppearance;
        picker.Closed += () =>
        {
            picker = null;
            if (Visible) navigation.Configure("bot_custom_color");
        };
        AddChild(picker);
        picker.Layer = Layer + 1;
    }

    private void Submit()
    {
        if (pending != 0) return;
        var text = name.Text.Trim();
        name.Text = text;
        if (!Protocol.ValidActorName(text))
        {
            SetMessage($"Names need 1 to {Protocol.MaxNameLength} letters, digits, spaces or symbols.");
            return;
        }
        if (Net.Roster.Any(a => a.Id != Editing?.Id && Protocol.SameName(a.Name, text)))
        {
            SetMessage($"Someone in the lobby is already called {text}.");
            return;
        }
        var config = ControllerConfig.ForBot((byte)Difficulty);
        pending = Editing == null ? Net.AddActor(ActorKind.Bot, text, Appearance, config) : Net.UpdateActor(Editing.Id, text, Appearance, config);
        SetMessage("");
    }

    private void Remove()
    {
        if (pending != 0 || Editing == null) return;
        pending = Net.RemoveActor(Editing.Id);
    }

    private void OnResult(ActorRequestResultMessage result)
    {
        if (result.RequestId != pending) return;
        pending = 0;
        if (result.Accepted) Close();
        else SetMessage(result.Reason);
    }

    private void OnLobbyChanged()
    {
        // A match starting, leaving the lobby, or the edited bot disappearing ends the edit.
        if (Net.Lobby is not { Phase: LobbyPhase.Waiting } || Editing != null && pending == 0 && Net.Roster.All(a => a.Id != Editing.Id))
            Close();
    }

    private void SetMessage(string text)
    {
        if (!IsInstanceValid(message)) return;
        message.Text = text;
        message.Visible = text.Length > 0;
    }

    public void Close()
    {
        if (!Visible) return;
        Visible = false;
        picker?.Close();
        Closed?.Invoke();
        QueueFree();
    }
}
