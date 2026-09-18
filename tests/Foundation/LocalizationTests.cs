using System.Reflection;
using System.Text.Json;
using AbyssScav.Foundation;

namespace AbyssScav.Foundation.Tests;

internal static class LocalizationTests
{
    public static int Run()
    {
        var cases = new (string Name, Action Test)[]
        {
            ("embedded_tables_load_and_translate", EmbeddedTablesLoadAndTranslate),
            ("missing_key_falls_back_to_key", MissingKeyFallsBack),
            ("fallback_overload_returns_fallback", FallbackOverload),
            ("format_overload_applies_placeholders", FormatOverload),
            ("unknown_language_rejected_without_change", UnknownLanguageRejected),
            ("language_names_are_native", LanguageNames),
            ("available_languages_cover_en_and_ko", AvailableLanguages),
            ("ko_table_has_no_empty_keys_or_values", KoTableIntegrity),
            ("appsettings_defaults_to_english_schema2", AppSettingsDefaults),
            ("appsettings_normalized_whitelists_language", AppSettingsNormalized),
            ("config_v1_without_language_migrates_to_v2_english", ConfigV1Migration),
            ("config_v2_korean_roundtrips", ConfigKoreanRoundtrip),
        };

        var fail = 0;
        foreach (var (name, test) in cases)
        {
            try
            {
                test();
                Console.WriteLine($"  ok: {name}");
            }
            catch (Exception ex)
            {
                fail++;
                Console.WriteLine($"  FAIL: {name}: {ex.Message}");
            }
        }

        return fail;
    }

    private static void EmbeddedTablesLoadAndTranslate()
    {
        var service = new LocalizationService();
        // English is the key-fallback language: unknown keys echo.
        TestAssert.Equal("Solo Dive — Contract & Loadout", service.T("Solo Dive — Contract & Loadout"), "en passthrough");
        TestAssert.True(service.SetLanguage("ko"), "set ko");
        TestAssert.Equal("ko", service.CurrentLanguage, "current ko");
        TestAssert.Equal("솔로 잠수 — 계약 및 장비 구성", service.T("Solo Dive — Contract & Loadout"), "ko translation");
        TestAssert.Equal("설정", service.T("Settings"), "ko settings");
    }

    private static void MissingKeyFallsBack()
    {
        var service = new LocalizationService();
        service.SetLanguage("ko");
        TestAssert.Equal("not.a.real.key", service.T("not.a.real.key"), "ko missing falls back to key");
        // A key present only in a caller-supplied English table resolves from en.
        var tables = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["en"] = new Dictionary<string, string> { ["en.only"] = "English Only" },
            ["ko"] = new Dictionary<string, string>(),
        };
        var custom = new LocalizationService(tables, "ko");
        TestAssert.Equal("English Only", custom.T("en.only"), "en table fallback");
    }

    private static void FallbackOverload()
    {
        var service = new LocalizationService();
        service.SetLanguage("ko");
        TestAssert.Equal("대신 표시", service.T("missing.key.here", "대신 표시"), "explicit fallback");
        TestAssert.Equal("솔로 잠수 — 계약 및 장비 구성", service.T("Solo Dive — Contract & Loadout", "대신 표시"), "hit ignores fallback");
    }

    private static void FormatOverload()
    {
        var service = new LocalizationService();
        service.SetLanguage("ko");
        TestAssert.Equal("마스터 볼륨: 80%", service.T("Master volume: {0}%", 80), "ko format");
        // Formatting still applies when the key is entirely missing (no placeholder leak).
        TestAssert.Equal("oxygen 42", service.T("oxygen {0}", 42), "missing key still formats");
    }

    private static void UnknownLanguageRejected()
    {
        var service = new LocalizationService();
        service.SetLanguage("ko");
        TestAssert.True(!service.SetLanguage("ja"), "unknown rejected");
        TestAssert.Equal("ko", service.CurrentLanguage, "language unchanged");
    }

    private static void LanguageNames()
    {
        TestAssert.Equal("English", LocalizationService.LanguageName("en"), "en name");
        TestAssert.Equal("한국어", LocalizationService.LanguageName("ko"), "ko name");
        TestAssert.Equal("ja", LocalizationService.LanguageName("ja"), "unknown echoes");
    }

    private static void AvailableLanguages()
    {
        var langs = new LocalizationService().AvailableLanguages;
        TestAssert.True(langs.Contains("en") && langs.Contains("ko"), "en+ko present");
    }

    private static void KoTableIntegrity()
    {
        var assembly = typeof(LocalizationService).Assembly;
        using var stream = assembly.GetManifestResourceStream("AbyssScav.Foundation.lang.ko.json");
        TestAssert.True(stream is not null, "ko resource embedded");
        var table = JsonSerializer.Deserialize<Dictionary<string, string>>(stream!);
        TestAssert.True(table is not null && table.Count > 150, $"ko entry count (got {table?.Count ?? 0})");
        foreach (var kv in table!)
        {
            TestAssert.True(!string.IsNullOrWhiteSpace(kv.Key), "key non-empty");
            TestAssert.True(!string.IsNullOrWhiteSpace(kv.Value), $"value non-empty for '{kv.Key}'");
        }
    }

    private static void AppSettingsDefaults()
    {
        TestAssert.Equal(2, AppSettings.CurrentSchemaVersion, "schema 2");
        var defaults = AppSettings.Default();
        TestAssert.Equal("en", defaults.Language, "default language");
        TestAssert.Equal(2, defaults.SchemaVersion, "default schema");
    }

    private static void AppSettingsNormalized()
    {
        var nullLang = AppSettings.Default() with { Language = null! };
        TestAssert.Equal("en", nullLang.Normalized().Language, "null -> en (v1 files)");
        var unknown = AppSettings.Default() with { Language = "ja" };
        TestAssert.Equal("en", unknown.Normalized().Language, "unknown -> en");
        var korean = AppSettings.Default() with { Language = "ko" };
        TestAssert.Equal("ko", korean.Normalized().Language, "ko kept");
    }

    private static void ConfigV1Migration()
    {
        var paths = AppPaths.FromRoot(TestTemp.NewRoot());
        paths.EnsureDirectories();
        const string v1 = """{"schema_version":1,"graphics":{"width":1920,"height":1080,"window_mode":"Fullscreen","quality":"High"},"master_volume_percent":42,"auto_reconnect_last_session":false}""";
        File.WriteAllText(paths.ConfigPath, v1);
        var result = ConfigLoader.Load(paths);
        TestAssert.Equal(ConfigLoadStatus.Ok, result.Status, "status");
        TestAssert.Equal(2, result.Settings.SchemaVersion, "promoted to v2");
        TestAssert.Equal("en", result.Settings.Language, "missing language -> en");
        TestAssert.Equal(42, result.Settings.MasterVolumePercent, "volume preserved");
    }

    private static void ConfigKoreanRoundtrip()
    {
        var paths = AppPaths.FromRoot(TestTemp.NewRoot());
        ConfigLoader.Load(paths);
        var korean = AppSettings.Default() with { Language = "ko", MasterVolumePercent = 55 };
        TestAssert.True(ConfigLoader.TrySave(paths, korean, isSafeMode: false, userInitiated: true, out var err), "save " + err);
        var reloaded = ConfigLoader.Load(paths);
        TestAssert.Equal("ko", reloaded.Settings.Language, "ko persisted");
        TestAssert.Equal(55, reloaded.Settings.MasterVolumePercent, "volume persisted");

        // The facade honors the persisted language end to end.
        Localization.Initialize(new LocalizationService());
        TestAssert.True(Localization.SetLanguage(reloaded.Settings.Language), "facade set ko");
        TestAssert.Equal("설정", Localization.T("Settings"), "facade ko");
        TestAssert.Equal("한국어", Localization.LanguageName("ko"), "facade name");
    }
}
