using System.Globalization;

namespace AbyssScav.Foundation;

/// <summary>8-bit sRGB colour, engine-independent (Presentation converts to Godot.Color).</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    /// <summary>Parses "#rrggbb" (leading '#' optional). Throws on malformed input: palettes are code constants.</summary>
    public static Rgb FromHex(string hex)
    {
        var s = hex.StartsWith('#') ? hex[1..] : hex;
        if (s.Length != 6)
        {
            throw new FormatException($"Expected #rrggbb, got '{hex}'.");
        }

        return new Rgb(
            byte.Parse(s[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(s[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(s[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    public string ToHex() => $"#{R:x2}{G:x2}{B:x2}";
}

/// <summary>Sonar contact kinds as drawn on the scope (mirrors Domain.SonarClass without a dependency).</summary>
public enum SonarContactKind
{
    Terrain,
    Structure,
    Salvage,
    Biological,
    Unknown,
}

/// <summary>
/// Sonar/HUD colour set (docs/07 §3, §6 "colorblind-safe sonar palettes").
/// Colour is never the only cue — every contact kind also has its own glyph —
/// but the CVD palettes keep the five contact colours mutually distinguishable
/// under simulated dichromacy (verified by Foundation tests with Machado 2009
/// matrices and CIELAB distance), and every contact colour keeps at least 3:1
/// contrast against the scope background.
/// </summary>
public sealed record SonarPalette(
    string Id,
    Rgb ScopeBackground,
    Rgb Rim,
    Rgb Accent,
    Rgb Alert,
    Rgb Text,
    Rgb Salvage,
    Rgb Biological,
    Rgb Structure,
    Rgb Unknown,
    Rgb Terrain)
{
    public const string StandardId = "standard";
    /// <summary>Okabe–Ito based: safe for deuteranopia and protanopia (red–green).</summary>
    public const string RedGreenSafeId = "deutan_protan";
    /// <summary>Red/cyan/white based: safe for tritanopia (blue–yellow).</summary>
    public const string BlueYellowSafeId = "tritan";

    /// <summary>Selectable ids in display order.</summary>
    public static readonly IReadOnlyList<string> Ids = new[] { StandardId, RedGreenSafeId, BlueYellowSafeId };

    public static readonly SonarPalette Standard = new(
        StandardId,
        ScopeBackground: Rgb.FromHex("#071622"),
        Rim: Rgb.FromHex("#29414b"),
        Accent: Rgb.FromHex("#71d9d1"),
        Alert: Rgb.FromHex("#dfa44d"),
        Text: Rgb.FromHex("#dae4df"),
        Salvage: Rgb.FromHex("#dae4df"),
        Biological: Rgb.FromHex("#dfa44d"),
        Structure: Rgb.FromHex("#71d9d1"),
        Unknown: Rgb.FromHex("#999aa6"),
        Terrain: Rgb.FromHex("#738c8c"));

    public static readonly SonarPalette RedGreenSafe = new(
        RedGreenSafeId,
        ScopeBackground: Rgb.FromHex("#071622"),
        Rim: Rgb.FromHex("#3a4f59"),
        Accent: Rgb.FromHex("#56b4e9"),
        Alert: Rgb.FromHex("#e69f00"),
        Text: Rgb.FromHex("#f5f5f5"),
        Salvage: Rgb.FromHex("#f5f5f5"),
        Biological: Rgb.FromHex("#e69f00"),
        Structure: Rgb.FromHex("#56b4e9"),
        Unknown: Rgb.FromHex("#cc79a7"),
        Terrain: Rgb.FromHex("#5f6b6b"));

    public static readonly SonarPalette BlueYellowSafe = new(
        BlueYellowSafeId,
        ScopeBackground: Rgb.FromHex("#071622"),
        Rim: Rgb.FromHex("#3a4f59"),
        Accent: Rgb.FromHex("#00c2c7"),
        Alert: Rgb.FromHex("#e8384f"),
        Text: Rgb.FromHex("#f5f5f5"),
        Salvage: Rgb.FromHex("#f5f5f5"),
        Biological: Rgb.FromHex("#e8384f"),
        Structure: Rgb.FromHex("#00c2c7"),
        Unknown: Rgb.FromHex("#ff99cc"),
        Terrain: Rgb.FromHex("#5f6b6b"));

    public Rgb ContactColor(SonarContactKind kind) => kind switch
    {
        SonarContactKind.Salvage => Salvage,
        SonarContactKind.Biological => Biological,
        SonarContactKind.Structure => Structure,
        SonarContactKind.Unknown => Unknown,
        _ => Terrain,
    };

    /// <summary>Canonical id for a raw settings value (case-insensitive); unknown/blank -&gt; standard.</summary>
    public static string CanonicalId(string? raw)
    {
        if (!string.IsNullOrWhiteSpace(raw))
        {
            foreach (var id in Ids)
            {
                if (string.Equals(id, raw.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return id;
                }
            }
        }

        return StandardId;
    }

    /// <summary>Palette for a raw settings value; never null (unknown -&gt; standard).</summary>
    public static SonarPalette Resolve(string? id) => CanonicalId(id) switch
    {
        RedGreenSafeId => RedGreenSafe,
        BlueYellowSafeId => BlueYellowSafe,
        _ => Standard,
    };
}
