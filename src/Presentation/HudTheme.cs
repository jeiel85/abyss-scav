using AbyssScav.App;
using AbyssScav.Foundation;
using Godot;

namespace AbyssScav.Presentation;

/// <summary>
/// In-run HUD look resolved from settings: the sonar palette (colour-vision
/// option) and the high-contrast switch. High contrast gives opaque black
/// panels, 2 px bright borders, white text with a dark outline, and +2 pt type
/// so text stays legible over bright water and particles. Resolved once per
/// HUD build (settings only change from the main menu).
/// </summary>
public sealed class HudTheme
{
    public bool HighContrast { get; }
    public SonarPalette Palette { get; }

    public Color Text { get; }
    public Color TextDim { get; }
    public Color Accent { get; }
    public Color Alert { get; }
    public Color PanelBg { get; }
    public Color PanelBorder { get; }
    public int BorderWidth { get; }
    public int OutlineSize { get; }
    public int FontBump { get; }

    public HudTheme(SonarPalette palette, bool highContrast)
    {
        Palette = palette;
        HighContrast = highContrast;
        Accent = ToColor(palette.Accent);
        Alert = ToColor(palette.Alert);
        if (highContrast)
        {
            Text = Colors.White;
            TextDim = new Color(0.88f, 0.90f, 0.90f);
            PanelBg = new Color(0f, 0f, 0f, 1f);
            PanelBorder = new Color(0.92f, 0.95f, 0.95f);
            BorderWidth = 2;
            OutlineSize = 4;
            FontBump = 2;
        }
        else
        {
            Text = ToColor(palette.Text);
            TextDim = new Color(0.62f, 0.72f, 0.70f);
            PanelBg = new Color(0.03f, 0.09f, 0.12f, 0.88f);
            PanelBorder = ToColor(palette.Rim);
            BorderWidth = 1;
            OutlineSize = 0;
            FontBump = 0;
        }
    }

    /// <summary>Theme for the current settings; standard look before boot (headless tests).</summary>
    public static HudTheme Current()
    {
        if (GameServices.IsInitialized
            && GameServices.Registry.TryResolve<AppSettingsHolder>(out var holder)
            && holder is not null)
        {
            return new HudTheme(SonarPalette.Resolve(holder.Current.SonarPaletteId), holder.Current.HighContrastHud);
        }

        return new HudTheme(SonarPalette.Standard, false);
    }

    public static Color ToColor(Rgb c) => Color.Color8(c.R, c.G, c.B);

    public StyleBoxFlat PanelStyle() => new()
    {
        BgColor = PanelBg,
        BorderColor = PanelBorder,
        BorderWidthLeft = BorderWidth,
        BorderWidthRight = BorderWidth,
        BorderWidthTop = BorderWidth,
        BorderWidthBottom = BorderWidth,
        CornerRadiusTopLeft = 3,
        CornerRadiusTopRight = 3,
        CornerRadiusBottomLeft = 3,
        CornerRadiusBottomRight = 3,
        ContentMarginLeft = 10,
        ContentMarginRight = 10,
        ContentMarginTop = 8,
        ContentMarginBottom = 8,
    };

    /// <summary>Font size, colour and (high contrast) outline for a HUD label.</summary>
    public void StyleLabel(Label label, int baseSize, Color color)
    {
        label.AddThemeFontSizeOverride("font_size", baseSize + FontBump);
        label.AddThemeColorOverride("font_color", color);
        if (OutlineSize > 0)
        {
            label.AddThemeConstantOverride("outline_size", OutlineSize);
            label.AddThemeColorOverride("font_outline_color", Colors.Black);
        }
    }
}
