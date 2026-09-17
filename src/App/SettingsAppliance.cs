using AbyssScav.Foundation;
using Godot;

namespace AbyssScav.App;

/// <summary>
/// Single applicator for window, master volume, and render quality.
/// Used by both boot (GameBootstrap) and explicit user Apply (SettingsPanel)
/// so volume/quality are actually applied everywhere, not just stored.
/// </summary>
public static class SettingsAppliance
{
    public static void Apply(AppSettings settings, Viewport? viewport)
    {
        ApplyWindow(settings);
        ApplyVolume(settings.MasterVolumePercent);
        if (viewport is not null)
        {
            ApplyQuality(viewport, settings.Graphics.Quality);
        }
    }

    public static void ApplyWindow(AppSettings settings)
    {
        DisplayServer.WindowSetSize(new Vector2I(settings.Graphics.Width, settings.Graphics.Height));
        DisplayServer.WindowSetMode(settings.Graphics.WindowMode switch
        {
            "Fullscreen" => DisplayServer.WindowMode.Fullscreen,
            "Maximized" => DisplayServer.WindowMode.Maximized,
            _ => DisplayServer.WindowMode.Windowed,
        });
    }

    public static void ApplyVolume(int volumePercent)
    {
        var bus = AudioServer.GetBusIndex("Master");
        if (bus < 0)
        {
            return;
        }

        if (AudioPolicy.IsMuted(volumePercent))
        {
            AudioServer.SetBusMute(bus, true);
            return;
        }

        AudioServer.SetBusMute(bus, false);
        AudioServer.SetBusVolumeDb(bus, AudioPolicy.ToMasterVolumeDb(volumePercent));
    }

    public static void ApplyQuality(Viewport viewport, string quality)
    {
        switch (quality)
        {
            case "Low":
                viewport.Scaling3DMode = Viewport.Scaling3DModeEnum.Bilinear;
                viewport.Scaling3DScale = 0.75f;
                viewport.Msaa3D = Viewport.Msaa.Disabled;
                RenderingServer.DirectionalShadowAtlasSetSize(1024, false);
                break;
            case "High":
                viewport.Scaling3DMode = Viewport.Scaling3DModeEnum.Bilinear;
                viewport.Scaling3DScale = 1.0f;
                viewport.Msaa3D = Viewport.Msaa.Msaa4X;
                RenderingServer.DirectionalShadowAtlasSetSize(4096, false);
                break;
            default: // Medium and any normalized fallback.
                viewport.Scaling3DMode = Viewport.Scaling3DModeEnum.Bilinear;
                viewport.Scaling3DScale = 1.0f;
                viewport.Msaa3D = Viewport.Msaa.Msaa2X;
                RenderingServer.DirectionalShadowAtlasSetSize(2048, false);
                break;
        }
    }
}
