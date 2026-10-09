using Godot;

namespace Propane.UI;

/// <summary>Shared look for the menus: light glassy panels with dark text, matching the white void.</summary>
public static class UiStyle
{
    public static readonly Color Ink = new(0.1f, 0.11f, 0.13f);
    public static readonly Color SoftInk = new(0.1f, 0.11f, 0.13f, 0.6f);
    public static readonly Color Accent = new(0.92f, 0.44f, 0.16f);

    private static FontVariation? boldFont;

    /// <summary>
    /// A heavy sans for the HUD: the platform's system sans (Helvetica Neue, Segoe UI, Roboto...) with synthetic
    /// emboldening, since system font weight matching is unreliable across platforms.
    /// </summary>
    public static FontVariation BoldFont => boldFont ??= new FontVariation
    {
        BaseFont = new SystemFont
        {
            FontNames = new[] { "Helvetica Neue", "Segoe UI", "Roboto", "Arial", "sans-serif" },
            FontWeight = 700,
        },
        VariationEmbolden = 0.6f,
    };

    public static StyleBoxFlat Panel(float alpha = 0.9f) => new()
    {
        BgColor = new Color(0.97f, 0.97f, 0.98f, alpha),
        CornerRadiusTopLeft = 14,
        CornerRadiusTopRight = 14,
        CornerRadiusBottomLeft = 14,
        CornerRadiusBottomRight = 14,
        ContentMarginLeft = 22,
        ContentMarginRight = 22,
        ContentMarginTop = 18,
        ContentMarginBottom = 18,
        ShadowColor = new Color(0, 0, 0, 0.18f),
        ShadowSize = 18,
    };

    public static Label Text(string text, int size, Color? color = null, HorizontalAlignment align = HorizontalAlignment.Left)
    {
        var label = new Label { Text = text, HorizontalAlignment = align };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color ?? Ink);
        return label;
    }

    public static LineEdit Field(string text, string placeholder)
    {
        var field = new LineEdit { Text = text, PlaceholderText = placeholder, CustomMinimumSize = new Vector2(0, 40) };
        field.AddThemeFontSizeOverride("font_size", 18);
        field.AddThemeColorOverride("font_color", Ink);
        field.AddThemeColorOverride("font_placeholder_color", SoftInk);
        field.AddThemeColorOverride("caret_color", Ink);
        foreach (var (state, color) in new[] { ("normal", new Color(1f, 1f, 1f, 0.9f)), ("focus", new Color(1f, 1f, 1f, 1f)) })
        {
            field.AddThemeStyleboxOverride(state, new StyleBoxFlat
            {
                BgColor = color,
                BorderColor = state == "focus" ? Accent : new Color(0.8f, 0.81f, 0.84f),
                BorderWidthBottom = 2,
                BorderWidthTop = 2,
                BorderWidthLeft = 2,
                BorderWidthRight = 2,
                CornerRadiusTopLeft = 8,
                CornerRadiusTopRight = 8,
                CornerRadiusBottomLeft = 8,
                CornerRadiusBottomRight = 8,
                ContentMarginLeft = 12,
                ContentMarginRight = 12,
            });
        }
        return field;
    }

    /// <summary>A title in the heavy HUD face.</summary>
    public static Label Title(string text, int size)
    {
        var label = Text(text, size, Ink, HorizontalAlignment.Center);
        label.AddThemeFontOverride("font", BoldFont);
        return label;
    }

    public static Button Button(string text)
    {
        var button = new Button { Text = text, FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(0, 42) };
        button.AddThemeFontSizeOverride("font_size", 18);
        foreach (var (state, color) in new[] { ("normal", new Color(0.88f, 0.89f, 0.91f)), ("hover", new Color(0.82f, 0.84f, 0.87f)), ("pressed", new Color(0.75f, 0.77f, 0.8f)), ("disabled", new Color(0.93f, 0.93f, 0.94f)) })
        {
            button.AddThemeStyleboxOverride(state, new StyleBoxFlat
            {
                BgColor = color,
                CornerRadiusTopLeft = 8,
                CornerRadiusTopRight = 8,
                CornerRadiusBottomLeft = 8,
                CornerRadiusBottomRight = 8,
                ContentMarginLeft = 14,
                ContentMarginRight = 14,
            });
        }
        foreach (var name in new[] { "font_color", "font_hover_color", "font_pressed_color" })
        {
            button.AddThemeColorOverride(name, Ink);
        }
        button.AddThemeColorOverride("font_disabled_color", new Color(Ink, 0.35f));
        return button;
    }
}
