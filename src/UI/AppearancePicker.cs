using System;
using Godot;
using Propane.Core;
using Propane.Net;
using Propane.Player;

namespace Propane.UI;

/// <summary>A byte-accurate color editor. Up/down selects a channel; left/right (or stick) adjusts it.</summary>
public partial class AppearancePicker : CanvasLayer
{
    public required AppearanceRecord Initial { get; init; }
    public event Action<AppearanceRecord>? Applied;
    public event Action? Closed;
    public AppearanceRecord Selected { get; private set; } = AppearanceRecord.FromPalette(0);
    private PlayerCharacter preview = null!;
    private Label colorText = null!;
    private LineEdit hex = null!;
    private readonly HSlider[] channels = new HSlider[3];
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
        var layout = new VBoxContainer { CustomMinimumSize = new Vector2(480, 0) };
        layout.AddThemeConstantOverride("separation", 12);
        panel.AddChild(layout);
        layout.AddChild(UiStyle.Title("YOUR COLOR", 34));
        layout.AddChild(UiStyle.Text("Up / down to select · Left / right to adjust", 16, UiStyle.SoftInk, HorizontalAlignment.Center));
        var viewportContainer = new SubViewportContainer
        {
            CustomMinimumSize = new Vector2(0, 210),
            Stretch = true,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        layout.AddChild(viewportContainer);
        var viewport = new SubViewport { Size = new Vector2I(480, 210), OwnWorld3D = true, TransparentBg = true };
        viewportContainer.AddChild(viewport);
        viewport.AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.8f, 0.82f, 0.85f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = Colors.White,
                AmbientLightEnergy = Tuning.Current.AppearancePreviewAmbient,
                TonemapMode = Godot.Environment.ToneMapper.Filmic,
            },
        });
        viewport.AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-35, -25, 0), LightEnergy = Tuning.Current.AppearancePreviewLight });
        preview = new PlayerCharacter { Name = "Preview", IsRemote = true, BodyColor = Selected.Color };
        viewport.AddChild(preview);
        preview.SetPhysicsProcess(false);
        var camera = new Camera3D { Position = new Vector3(2, 1.5f, -4), Projection = Camera3D.ProjectionType.Orthogonal, Size = 2.4f };
        viewport.AddChild(camera);
        camera.LookAt(new Vector3(0, 1, 0));
        colorText = UiStyle.Text("", 18, UiStyle.Ink, HorizontalAlignment.Center);
        layout.AddChild(colorText);
        var names = new[] { "Red", "Green", "Blue" };
        var values = new[] { Selected.Red, Selected.Green, Selected.Blue };
        for (var i = 0; i < channels.Length; i++)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 12);
            var label = UiStyle.Text(names[i], 18);
            label.CustomMinimumSize = new Vector2(70, 0);
            row.AddChild(label);
            var slider = channels[i] = new ChannelSlider
            {
                MinValue = 0,
                MaxValue = 255,
                Step = 1,
                Value = values[i],
                FocusMode = Control.FocusModeEnum.All,
                CustomMinimumSize = new Vector2(330, 34),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            };
            slider.SetMeta("focus_key", $"rgb_{names[i].ToLowerInvariant()}");
            slider.ValueChanged += _ => Changed();
            row.AddChild(slider);
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
        Refresh();
    }

    private void Changed()
    {
        if (refreshing) { return; }
        Selected = Selected with { Red = (byte)channels[0].Value, Green = (byte)channels[1].Value, Blue = (byte)channels[2].Value };
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
        Selected = Selected with { Red = (byte)(rgb >> 16), Green = (byte)(rgb >> 8), Blue = (byte)rgb };
        refreshing = true;
        channels[0].Value = Selected.Red; channels[1].Value = Selected.Green; channels[2].Value = Selected.Blue;
        refreshing = false;
        Refresh();
        return true;
    }

    private void Refresh()
    {
        preview.BodyColor = Selected.Color;
        hex.Text = Selected.Color.ToHtml(false).ToUpperInvariant();
        colorText.Text = $"#{hex.Text}   ·   {Selected.Red}, {Selected.Green}, {Selected.Blue}";
    }

    public void Close()
    {
        if (!Visible) { return; }
        Visible = false;
        Closed?.Invoke();
        QueueFree();
    }

    // HSlider highlights only its grabber by default. Give controller/keyboard focus the same outline as buttons.
    private partial class ChannelSlider : HSlider
    {
        private readonly StyleBoxFlat focus = UiStyle.Focus();

        public override void _Ready()
        {
            FocusEntered += QueueRedraw;
            FocusExited += QueueRedraw;
        }

        public override void _Draw()
        {
            if (HasFocus()) { DrawStyleBox(focus, new Rect2(Vector2.Zero, Size)); }
        }
    }

}
