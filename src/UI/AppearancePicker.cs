using System;
using Godot;
using Propane.Core;
using Propane.Net;
using Propane.Player;

namespace Propane.UI;

/// <summary>A local HSV editor; only the accepted sRGB bytes leave the picker.</summary>
public partial class AppearancePicker : CanvasLayer
{
    public required AppearanceRecord Initial { get; init; }
    public event Action<AppearanceRecord>? Applied;
    public event Action? Closed;
    public AppearanceRecord Selected { get; private set; } = AppearanceRecord.FromPalette(0);
    private WorldContext previewWorld = null!;
    public override void _ExitTree() => previewWorld.Dispose();

    private PlayerCharacter preview = null!;
    private Label colorText = null!;
    private LineEdit hex = null!;
    private readonly HsvColorSlider[] channels = new HsvColorSlider[3];
    private readonly Label[] values = new Label[3];
    private readonly Button[] presets = new Button[12];
    private ColorRect sample = null!;
    private bool refreshing;

    public override void _Ready()
    {
        Layer = 41;
        Selected = Initial;
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
        layout.AddChild(UiStyle.Title("YOUR COLOR", 34));
        layout.AddChild(UiStyle.Text("Pick a preset or adjust · Hold left / right for larger moves", 16, UiStyle.SoftInk, HorizontalAlignment.Center));
        var viewportContainer = new SubViewportContainer
        {
            CustomMinimumSize = new Vector2(0, 170),
            Stretch = true,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        layout.AddChild(viewportContainer);
        var viewport = new SubViewport { Size = new Vector2I(540, 170), TransparentBg = true };
        viewportContainer.AddChild(viewport);
        previewWorld = new WorldContext(viewport, Tuning.SavedTemplate, present: true);
        previewWorld.Root.AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.8f, 0.82f, 0.85f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = Colors.White,
                AmbientLightEnergy = previewWorld.Tuning.AppearancePreviewAmbient,
                TonemapMode = Godot.Environment.ToneMapper.Filmic,
            },
        });
        previewWorld.Root.AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-35, -25, 0), LightEnergy = previewWorld.Tuning.AppearancePreviewLight });
        preview = new PlayerCharacter { World = previewWorld, Name = "Preview", IsRemote = true, BodyColor = Selected.Color };
        previewWorld.Root.AddChild(preview);
        preview.SetPhysicsProcess(false);
        var camera = new Camera3D { Position = new Vector3(2, 1.5f, -4), Projection = Camera3D.ProjectionType.Orthogonal, Size = 2.4f };
        previewWorld.AddView(viewport, camera, followsPause: true);
        camera.LookAt(new Vector3(0, 1, 0));
        var current = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        current.AddThemeConstantOverride("separation", 12);
        var framedSample = UiStyle.ColorSample(Selected.Color, 32);
        sample = framedSample.GetChild<ColorRect>(0);
        current.AddChild(framedSample);
        colorText = UiStyle.Text("", 18);
        current.AddChild(colorText);
        layout.AddChild(current);
        var swatches = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        swatches.AddThemeConstantOverride("separation", 8);
        // Local suggestions, independent of the legacy network palette. Warm/cool hues plus two neutrals.
        var suggestions = new[]
        {
            ("Coral", "E85D65"), ("Orange", "E8873D"), ("Gold", "DDBB43"), ("Lime", "9FC74D"),
            ("Green", "45B878"), ("Teal", "38B7AC"), ("Sky", "48AEDD"), ("Blue", "557DDB"),
            ("Violet", "9969D4"), ("Pink", "D667B0"), ("Slate", "536174"), ("Pearl", "E5E8ED"),
        };
        for (var i = 0; i < suggestions.Length; i++)
        {
            var (name, html) = suggestions[i];
            var color = Color.FromHtml(html);
            var button = presets[i] = new Button
            {
                CustomMinimumSize = new Vector2(36, 36),
                FocusMode = Control.FocusModeEnum.All,
                TooltipText = name,
            };
            button.SetMeta("focus_key", $"preset_{name.ToLowerInvariant()}");
            button.SetMeta("preset_color", color);
            button.AddThemeStyleboxOverride("focus", UiStyle.Focus());
            button.Pressed += () => SetColor(color);
            swatches.AddChild(button);
        }
        layout.AddChild(swatches);
        var names = new[] { "Hue", "Saturation", "Brightness" };
        for (var i = 0; i < channels.Length; i++)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 12);
            var label = UiStyle.Text(names[i], 18);
            label.CustomMinimumSize = new Vector2(100, 0);
            row.AddChild(label);
            var slider = channels[i] = new HsvColorSlider { Hue = i == 0 };
            slider.SetMeta("focus_key", new[] { "hsv_hue", "hsv_saturation", "hsv_value" }[i]);
            slider.Changed += Changed;
            row.AddChild(slider);
            values[i] = UiStyle.Text("", 16, UiStyle.SoftInk, HorizontalAlignment.Right);
            values[i].CustomMinimumSize = new Vector2(44, 0);
            row.AddChild(values[i]);
            layout.AddChild(row);
        }
        hex = UiStyle.Field(Selected.Color.ToHtml(false).ToUpperInvariant(), "Hex color (RRGGBB)");
        hex.MaxLength = 7;
        hex.TextSubmitted += _ => AcceptHex();
        hex.FocusExited += () => { if (Visible) { AcceptHex(); } };
        layout.AddChild(hex);
        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        buttons.AddThemeConstantOverride("separation", 12);
        var apply = UiStyle.Button("Use color");
        apply.Pressed += () => { if (AcceptHex()) { Applied?.Invoke(Selected); Close(); } };
        buttons.AddChild(apply);
        var cancel = UiStyle.Button("Cancel");
        cancel.Pressed += Close;
        buttons.AddChild(cancel);
        layout.AddChild(buttons);
        AddChild(new MenuNavigation { Screen = this, Root = layout, Back = Close });
        SetColor(Initial.Color);
    }

    private void Changed()
    {
        if (refreshing) { return; }
        Selected = Selected.WithColor(Color.FromHsv(channels[0].Value, channels[1].Value, channels[2].Value));
        Refresh();
    }

    private bool AcceptHex()
    {
        var text = hex.Text.Trim().TrimStart('#');
        if (text.Length != 6 || !uint.TryParse(text, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var rgb))
        {
            colorText.Text = "Enter six hex digits, such as 12ABEF";
            return false;
        }
        // Reaccepting the displayed hex must not quantize the editing HSV state after every focus move.
        if (rgb != ((uint)Selected.Red << 16 | (uint)Selected.Green << 8 | Selected.Blue))
        {
            SetColor(new Color((byte)(rgb >> 16) / 255f, (byte)(rgb >> 8) / 255f, (byte)rgb / 255f));
        }
        else { Refresh(); }
        return true;
    }

    private void SetColor(Color color)
    {
        Selected = Selected.WithColor(color);
        refreshing = true;
        // Gray has no hue; black has neither hue nor saturation. Preserve the player's editing intent.
        if (color.S > 0) { channels[0].Value = color.H; }
        if (color.V > 0) { channels[1].Value = color.S; }
        channels[2].Value = color.V;
        refreshing = false;
        Refresh();
    }

    private void Refresh()
    {
        preview.BodyColor = Selected.Color;
        sample.Color = Selected.Color;
        channels[0].ColorAt = t => Color.FromHsv(t, 1, 1);
        channels[1].ColorAt = t => Color.FromHsv(channels[0].Value, t, channels[2].Value);
        channels[2].ColorAt = t => Color.FromHsv(channels[0].Value, channels[1].Value, t);
        for (var i = 0; i < channels.Length; i++)
        {
            values[i].Text = i == 0 ? $"{Mathf.RoundToInt(channels[i].Value * 360)}°" : $"{Mathf.RoundToInt(channels[i].Value * 100)}%";
            channels[i].QueueRedraw();
        }
        foreach (var button in presets)
        {
            var color = button.GetMeta("preset_color").AsColor();
            var selected = AppearanceRecord.FromColor(color).Color == Selected.Color;
            foreach (var state in new[] { "normal", "hover", "pressed" })
            {
                button.AddThemeStyleboxOverride(state, new StyleBoxFlat
                {
                    BgColor = state == "hover" ? color.Lightened(0.12f) : color,
                    BorderColor = UiStyle.Ink,
                    BorderWidthLeft = selected ? 3 : 1,
                    BorderWidthRight = selected ? 3 : 1,
                    BorderWidthTop = selected ? 3 : 1,
                    BorderWidthBottom = selected ? 3 : 1,
                    CornerRadiusTopLeft = 8,
                    CornerRadiusTopRight = 8,
                    CornerRadiusBottomLeft = 8,
                    CornerRadiusBottomRight = 8,
                });
            }
        }
        hex.Text = Selected.Color.ToHtml(false).ToUpperInvariant();
        colorText.Text = $"YOUR COLOR · #{hex.Text}";
    }

    public void Close()
    {
        if (!Visible) { return; }
        Visible = false;
        Closed?.Invoke();
        QueueFree();
    }

}
