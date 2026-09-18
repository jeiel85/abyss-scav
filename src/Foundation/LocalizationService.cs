using System.Reflection;
using System.Text.Json;

namespace AbyssScav.Foundation;

/// <summary>
/// Engine-independent string localization backed by embedded JSON tables.
/// Falls back through the current language table, then the English table,
/// then the key itself, so a missing entry is never a crash or a blank label.
/// Tables are immutable after construction; only <see cref="CurrentLanguage"/>
/// mutates, guarded by a lock for thread safety.
/// </summary>
public sealed class LocalizationService
{
    private static readonly string[] KnownLanguages = ["en", "ko"];

    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> _tables;
    private readonly object _gate = new();
    private string _currentLanguage;

    /// <summary>Loads the embedded tables (lang/en.json, lang/ko.json). Defaults to English.</summary>
    public LocalizationService()
        : this(LoadEmbeddedTables(), "en")
    {
    }

    /// <summary>Test seam: explicit tables with an optional default language.</summary>
    /// <param name="tables">Language code to flat key/value table.</param>
    /// <param name="defaultLanguage">Initial language; unknown codes fall back to "en".</param>
    public LocalizationService(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> tables, string defaultLanguage = "en")
    {
        ArgumentNullException.ThrowIfNull(tables);
        var copy = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var kv in tables)
        {
            copy[kv.Key] = new Dictionary<string, string>(kv.Value, StringComparer.Ordinal);
        }

        _tables = copy;
        _currentLanguage = copy.ContainsKey(defaultLanguage) ? defaultLanguage : "en";
    }

    /// <summary>Active language code ("en" or "ko").</summary>
    public string CurrentLanguage
    {
        get
        {
            lock (_gate)
            {
                return _currentLanguage;
            }
        }
    }

    /// <summary>Supported language codes in display order.</summary>
    public IReadOnlyList<string> AvailableLanguages => KnownLanguages;

    /// <summary>Native-language display name ("English" / "한국어"); unknown codes echo back.</summary>
    public static string LanguageName(string code) => code switch
    {
        "en" => "English",
        "ko" => "한국어",
        _ => code,
    };

    /// <summary>Switches language; returns false and changes nothing for unknown codes.</summary>
    public bool SetLanguage(string code)
    {
        if (Array.IndexOf(KnownLanguages, code) < 0)
        {
            return false;
        }

        lock (_gate)
        {
            _currentLanguage = code;
        }

        return true;
    }

    /// <summary>Translates a key: current table, then English, then the key itself.</summary>
    public string T(string key)
    {
        string current;
        lock (_gate)
        {
            current = _currentLanguage;
        }

        if (_tables.TryGetValue(current, out var table) && table.TryGetValue(key, out var hit))
        {
            return hit;
        }

        if (!string.Equals(current, "en", StringComparison.Ordinal)
            && _tables.TryGetValue("en", out var en) && en.TryGetValue(key, out var enHit))
        {
            return enHit;
        }

        return key;
    }

    /// <summary>Translates a key, returning <paramref name="fallback"/> when no table has it.</summary>
    public string T(string key, string fallback)
    {
        string current;
        lock (_gate)
        {
            current = _currentLanguage;
        }

        if (_tables.TryGetValue(current, out var table) && table.TryGetValue(key, out var hit))
        {
            return hit;
        }

        if (!string.Equals(current, "en", StringComparison.Ordinal)
            && _tables.TryGetValue("en", out var en) && en.TryGetValue(key, out var enHit))
        {
            return enHit;
        }

        return fallback;
    }

    /// <summary>Translates a key and formats it with <see cref="string.Format"/>; formatting applies even when the key is missing.</summary>
    public string T(string key, params object[] args) => string.Format(T(key), args);

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> LoadEmbeddedTables()
    {
        var tables = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var code in KnownLanguages)
        {
            tables[code] = LoadEmbeddedTable($"AbyssScav.Foundation.lang.{code}.json");
        }

        return tables;
    }

    private static IReadOnlyDictionary<string, string> LoadEmbeddedTable(string resourceName)
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
            return parsed is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(parsed, StringComparer.Ordinal);
        }
        catch
        {
            // A damaged table must never break boot: callers fall back to the key.
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }
}
