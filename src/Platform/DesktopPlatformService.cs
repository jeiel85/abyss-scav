using AbyssScav.Foundation;
using Godot;

namespace AbyssScav.Platform;

/// <summary>Desktop (Windows) implementation of <see cref="IPlatformService"/>.</summary>
public sealed class DesktopPlatformService : IPlatformService
{
    private readonly AppPaths _paths;

    public DesktopPlatformService(AppPaths paths)
    {
        _paths = paths;
    }

    public void OpenSaveFolder() => OpenFolder(_paths.SavesDir);

    public void OpenLogFolder() => OpenFolder(_paths.LogsDir);

    public void CopyTextToClipboard(string text) => DisplayServer.ClipboardSet(text);

    public void OpenUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
        {
            OS.ShellOpen(url);
        }
        else
        {
            GD.PushError($"Refused to open non-http(s) URL: {url}");
        }
    }

    public string DescribeOs() =>
        $"{OS.GetName()} {OS.GetVersion()} ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})";

    public string SupportBundlePath() => Path.Combine(_paths.DiagnosticsDir, "support-bundle.zip");

    private static void OpenFolder(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            OS.ShellOpen(ProjectSettings.GlobalizePath(dir));
        }
        catch (Exception ex)
        {
            GD.PushError($"Cannot open folder {dir}: {ex.Message}");
        }
    }
}
