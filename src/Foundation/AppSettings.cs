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
/// Foundation settings (EPIC A-01 scope only: window/graphics/volume).
/// Serialized as JSON inside config/settings.cfg.
/// </summary>
public sealed record AppSettings(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("graphics")] GraphicsSettings Graphics,
    [property: JsonPropertyName("master_volume_percent")] int MasterVolumePercent,
    [property: JsonPropertyName("auto_reconnect_last_session")] bool AutoReconnectLastSession)
{
    public const int CurrentSchemaVersion = 1;

    public static AppSettings Default() => new(
        CurrentSchemaVersion,
        GraphicsSettings.Default(),
        80,
        false);

    /// <summary>Clamp/whitelist raw values so a hand-edited file cannot crash boot.</summary>
    public AppSettings Normalized()
    {
        var width = Graphics.Width is >= 640 and <= 7680 ? Graphics.Width : 1280;
        var height = Graphics.Height is >= 360 and <= 4320 ? Graphics.Height : 720;
        var mode = Graphics.WindowMode is "Windowed" or "Fullscreen" or "Maximized" ? Graphics.WindowMode : "Windowed";
        var quality = Graphics.Quality is "Low" or "Medium" or "High" ? Graphics.Quality : "Medium";
        var volume = Math.Clamp(MasterVolumePercent, 0, 100);
        return this with { Graphics = new GraphicsSettings(width, height, mode, quality), MasterVolumePercent = volume };
    }
}
