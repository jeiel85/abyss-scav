namespace AbyssScav.Foundation;

/// <summary>Safe-mode policy (docs/09 §5): 1280x720 windowed, low graphics, no auto-reconnect.</summary>
public static class SafeModePolicy
{
    public const int Width = 1280;
    public const int Height = 720;

    public static AppSettings Apply(AppSettings source) =>
        source with
        {
            Graphics = new GraphicsSettings(Width, Height, "Windowed", "Low"),
            AutoReconnectLastSession = false,
        };
}
