using System.Linq;
using Godot;
using Propane.UI;

namespace Propane.Lab;

/// <summary>Persistent scenario reference and a controller-accessible pause/reset/tuning menu.</summary>
public partial class LabOverlay : CanvasLayer
{
    public required LabSession Lab { get; init; }
    private Label status = null!;
    private Label hint = null!;
    private Label title = null!;
    private Label result = null!;
    private Label ammo = null!;
    private Button resume = null!;
    private string fixtureSummary = "";
    private MenuNavigation navigation = null!;
    private CanvasLayer menuLayer = null!;
    public CanvasLayer MenuLayer => menuLayer;
    public bool MenuVisible => menuLayer.Visible;

    public override void _Ready()
    {
        fixtureSummary = Lab.Scenario.Id == "stack" ? $"{Lab.World.Tuning.LabStackCount} tanks · upright pyramid" :
            Lab.Scenario.Id == "range" ? $"{Lab.World.Tuning.LabRangeStep:0}–{Lab.World.Tuning.LabRangeStep * Lab.World.Tuning.LabRangeLanes:0} m" : "Your session · your dials";
        Layer = 12;
        var inset = new MarginContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        inset.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        inset.AddThemeConstantOverride("margin_left", 28);
        inset.AddThemeConstantOverride("margin_top", 22);
        inset.AddThemeConstantOverride("margin_right", 28);
        AddChild(inset);
        var text = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        inset.AddChild(text);
        text.AddChild(UiStyle.Text($"LAB / {Lab.Scenario.Name.ToUpperInvariant()} / {Lab.EnvironmentKind.ToString().ToUpperInvariant()}", 18));
        status = UiStyle.Text("", 26);
        status.AddThemeFontOverride("font", UiStyle.BoldFont);
        text.AddChild(status);
        hint = UiStyle.Text("", 16, UiStyle.SoftInk);
        text.AddChild(hint);

        ammo = UiStyle.Text("", 24, UiStyle.Ink, HorizontalAlignment.Right);
        ammo.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
        ammo.OffsetLeft = -360;
        ammo.OffsetRight = -28;
        ammo.OffsetTop = -74;
        ammo.OffsetBottom = -28;
        AddChild(ammo);
        menuLayer = new CanvasLayer { Layer = 30, Visible = false };
        AddChild(menuLayer);
        var shade = new ColorRect { Color = new Color(0.95f, 0.95f, 0.96f, 0.55f) };
        shade.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        menuLayer.AddChild(shade);
        var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        menuLayer.AddChild(center);
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", UiStyle.Panel(0.96f));
        center.AddChild(panel);
        var layout = new VBoxContainer { CustomMinimumSize = new Vector2(520, 0) };
        layout.AddThemeConstantOverride("separation", 10);
        panel.AddChild(layout);
        title = UiStyle.Title(Lab.Scenario.Name.ToUpperInvariant(), 32);
        layout.AddChild(title);
        result = UiStyle.Text("Paused", 18, UiStyle.SoftInk, HorizontalAlignment.Center);
        layout.AddChild(result);
        layout.AddChild(new HSeparator());
        layout.AddChild(new ControlsReference { Compact = true, ResetAction = Lab.Scenario.Banking ? "Reload" : "Reset scenario" });
        layout.AddChild(UiStyle.Text(Lab.Scenario.Banking ? "R / X reloads · Backspace resets the duel" :
            "R / X or Backspace resets this scenario", 16, UiStyle.SoftInk, HorizontalAlignment.Center));
        resume = AddButton(layout, "Resume Lab", () => Lab.SetMenu(false));
        AddButton(layout, Lab.Scenario.Id == "range" ? "Respawn targets" : "Reset scenario", Lab.RequestReset);
        AddButton(layout, "Free tuning", Lab.OpenTuning);
        AddButton(layout, "Lab menu", Lab.RequestExit);
        navigation = new MenuNavigation { Screen = menuLayer, Root = layout, Back = () => Lab.SetMenu(false) };
        menuLayer.AddChild(navigation);
        UpdateStatus();
    }

    private static Button AddButton(Node parent, string text, System.Action action)
    {
        var button = UiStyle.Button(text);
        button.Pressed += action;
        parent.AddChild(button);
        return button;
    }

    public void ShowMenu(bool visible)
    {
        UpdateStatus();
        menuLayer.Visible = visible;
        if (visible) navigation.Configure();
    }

    public void UpdateStatus()
    {
        if (Lab.Player.Ammo is { } rounds)
        {
            ammo.Text = $"{rounds.Magazine} / {rounds.Reserve}  ·  " + (rounds.Reloading ? "Reloading" :
                MenuNavigation.Gamepad ? "X: reload" : "R: reload");
        }

        if (Lab.Bank is { } bank)
        {
            resume.Disabled = !bank.IsPlaying;
            status.Text = $"You  {bank.Scores[1]}    ·    Bot  {bank.Scores[2]}    ·    {Mathf.CeilToInt(bank.TimeLeft) / 60}:{Mathf.CeilToInt(bank.TimeLeft) % 60:00}";
            // The round's result takes the panel's title; the controls below stay for resetting or leaving.
            title.Text = bank.IsPlaying ? Lab.Scenario.Name.ToUpperInvariant() : bank.Scores[1] == bank.Scores[2] ? "TIED" :
                bank.Scores[1] > bank.Scores[2] ? "YOU WIN" : "BOT WINS";
            result.Text = bank.IsPlaying ? "Paused · the clock is stopped" : $"Time’s up · you banked {bank.Scores[1]}, the bot {bank.Scores[2]}";
        }
        else
        {
            status.Text = Lab.Scenario.Id == "range" ? $"{Lab.LiveTanks.Count()} targets · {fixtureSummary}" : fixtureSummary;
        }

        hint.Text = MenuNavigation.Gamepad ? (Lab.Scenario.Banking ? "Start: Lab controls / reset / tuning   ·   X: reload" :
            "X: reset   ·   Start: Lab controls / tuning") : (Lab.Scenario.Banking ?
            "Esc: Lab controls   ·   Backspace: reset   ·   R: reload   ·   F1: tuning" :
            "R / Backspace: reset   ·   Esc: Lab controls   ·   F1: tuning");
    }
}
