namespace AbyssScav.Foundation;

/// <summary>
/// Static facade over the boot-owned <see cref="LocalizationService"/>.
/// Initialized once by <c>GameBootstrap</c>; before that every <c>T</c>
/// echoes the key (no-op state), so static UI code never nulls.
/// </summary>
public static class Localization
{
    private static LocalizationService? _service;

    /// <summary>Sets the active service. Null restores the key-echo no-op state.</summary>
    public static void Initialize(LocalizationService? service)
    {
        _service = service;
    }

    /// <summary>Active language code ("en" before initialization).</summary>
    public static string CurrentLanguage => _service?.CurrentLanguage ?? "en";

    /// <summary>Supported language codes in display order.</summary>
    public static IReadOnlyList<string> AvailableLanguages =>
        _service?.AvailableLanguages ?? new[] { "en", "ko" };

    /// <summary>Native-language display name ("English" / "한국어"); unknown codes echo back.</summary>
    public static string LanguageName(string code) => LocalizationService.LanguageName(code);

    /// <summary>Switches language; false when uninitialized or the code is unknown.</summary>
    public static bool SetLanguage(string code) => _service?.SetLanguage(code) ?? false;

    /// <summary>Translates a key: current table, then English, then the key itself.</summary>
    public static string T(string key) => _service?.T(key) ?? key;

    /// <summary>Translates a key, returning <paramref name="fallback"/> when no table has it.</summary>
    public static string T(string key, string fallback) => _service?.T(key, fallback) ?? fallback;

    /// <summary>Translates a key and formats it with <see cref="string.Format"/>.</summary>
    public static string T(string key, params object[] args) => _service?.T(key, args) ?? string.Format(key, args);
}
