namespace AbyssScav.Foundation;

/// <summary>Error domains from docs/16_CODE_CONTRACTS.md §11.</summary>
public static class ErrorCodes
{
    public const string BootConfigCorrupt = "BOOT-001";
    public const string BootConfigFutureVersion = "BOOT-002";
    public const string BootSessionLock = "BOOT-003";
    public const string BootLogInit = "BOOT-004";
    public const string SaveWrite = "SAVE-001";
    public const string SaveBackupRestore = "SAVE-002";
    public const string ContentSceneMissing = "CONTENT-001";
}

/// <summary>Structured error: code + localization key + diagnostic context.</summary>
public sealed record GameError(string Code, string LocalizationKey, string Detail);
