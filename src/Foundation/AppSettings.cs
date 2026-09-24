using System.Text.Json;
using System.Text.Json.Serialization;

namespace AbyssScav.Foundation;

public sealed record GraphicsSettings(
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("window_mode")] string WindowMode,
    [property: JsonPropertyName("quality")] string Quality)
{
    public static GraphicsSettings Default() => new(1280, 720, "Windowed", "Medium");
}

/// <summary>
/// Foundation settings: window/graphics/volume/language (A-01) plus
/// accessibility (sonar palette, high-contrast HUD) and keyboard bindings.
/// Serialized as JSON inside config/settings.cfg.
/// Schema history: v1 base; v2 adds language; v3 adds sonar_palette,
/// high_contrast_hud, key_bindings. Older files load with the new fields at
/// defaults (missing JSON members arrive as null/false and Normalized() fills them).
/// </summary>
public sealed record AppSettings(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("graphics")] GraphicsSettings Graphics,
    [property: JsonPropertyName("master_volume_percent")] int MasterVolumePercent,
    [property: JsonPropertyName("auto_reconnect_last_session")] bool AutoReconnectLastSession,
    [property: JsonPropertyName("language")] string Language,
    [property: JsonPropertyName("sonar_palette")] string? SonarPaletteId = null,
    [property: JsonPropertyName("high_contrast_hud")] bool HighContrastHud = false,
    [property: JsonPropertyName("key_bindings")][property: JsonConverter(typeof(LenientStringMapConverter))] IReadOnlyDictionary<string, string>? KeyBindings = null)
{
    public const int CurrentSchemaVersion = 3;

    public static AppSettings Default() => new(
        CurrentSchemaVersion,
        GraphicsSettings.Default(),
        80,
        false,
        "en",
        SonarPalette.StandardId,
        false,
        KeyBindingCatalog.Defaults());

    /// <summary>Clamp/whitelist raw values so a hand-edited file cannot crash boot.</summary>
    public AppSettings Normalized()
    {
        // A hand-edited file may omit "graphics" entirely (deserializes as null).
        var g = Graphics ?? GraphicsSettings.Default();
        var width = g.Width is >= 640 and <= 7680 ? g.Width : 1280;
        var height = g.Height is >= 360 and <= 4320 ? g.Height : 720;
        var mode = g.WindowMode is "Windowed" or "Fullscreen" or "Maximized" ? g.WindowMode : "Windowed";
        var quality = g.Quality is "Low" or "Medium" or "High" ? g.Quality : "Medium";
        var volume = Math.Clamp(MasterVolumePercent, 0, 100);
        // v1 files carry no language (deserializes as null): whitelist to "en".
        var lang = Language is "en" or "ko" ? Language : "en";
        // v1/v2 files carry no accessibility/bindings fields: palette -> standard,
        // bindings -> complete, whitelisted, duplicate-free map (defaults for gaps).
        return this with
        {
            Graphics = new GraphicsSettings(width, height, mode, quality),
            MasterVolumePercent = volume,
            Language = lang,
            SonarPaletteId = SonarPalette.CanonicalId(SonarPaletteId),
            KeyBindings = KeyBindingCatalog.Normalize(KeyBindings),
        };
    }
}

/// <summary>
/// Reads a JSON object of string values leniently: non-object input becomes null
/// and non-string members are skipped, so a hand-edited key_bindings section can
/// only lose the malformed entries (then normalized to defaults), never make the
/// whole settings file look corrupt.
/// </summary>
public sealed class LenientStringMapConverter : JsonConverter<IReadOnlyDictionary<string, string>?>
{
    public override IReadOnlyDictionary<string, string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return null;
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return map;
            }

            var name = reader.GetString() ?? string.Empty;
            reader.Read();
            if (reader.TokenType == JsonTokenType.String)
            {
                map[name] = reader.GetString() ?? string.Empty;
            }
            else
            {
                reader.Skip();
            }
        }

        throw new JsonException("Unterminated key_bindings object.");
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyDictionary<string, string>? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        foreach (var kv in value)
        {
            writer.WriteString(kv.Key, kv.Value);
        }

        writer.WriteEndObject();
    }
}
