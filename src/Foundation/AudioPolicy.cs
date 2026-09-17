namespace AbyssScav.Foundation;

/// <summary>
/// Engine-independent audio mapping for the master volume setting.
/// The Godot layer applies the result to the Master bus (see SettingsAppliance).
/// </summary>
public static class AudioPolicy
{
    public const float SilenceFloorDb = -80f;

    public static bool IsMuted(int volumePercent) => volumePercent <= 0;

    /// <summary>Maps 0..100 to bus dB: 100 -&gt; 0 dB, 0 -&gt; silence floor (mute).</summary>
    public static float ToMasterVolumeDb(int volumePercent)
    {
        var clamped = Math.Clamp(volumePercent, 0, 100);
        if (clamped <= 0)
        {
            return SilenceFloorDb;
        }

        return 20f * (float)Math.Log10(clamped / 100.0);
    }
}
