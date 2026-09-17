namespace AbyssScav.Platform;

/// <summary>Local platform capabilities only (docs/01 §9). No store SDK in v2.1 scope.</summary>
public interface IPlatformService
{
    void OpenSaveFolder();
    void OpenLogFolder();
    void CopyTextToClipboard(string text);
    void OpenUrl(string url);
    string DescribeOs();
    string SupportBundlePath();
}
