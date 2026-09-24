using AbyssScav.Foundation;

namespace AbyssScav.Foundation.Tests;

/// <summary>
/// Accessibility settings: schema v3 migration/normalization, key-binding
/// whitelist + conflict handling, hint rewriting, and colour-vision safety of
/// the sonar palettes (Machado 2009 dichromacy simulation + CIELAB distance).
/// </summary>
internal static class AccessibilityTests
{
    public static int Run()
    {
        var cases = new (string Name, Action Test)[]
        {
            ("v2_file_migrates_with_a11y_defaults", V2Migrates),
            ("v1_file_migrates_with_a11y_defaults", V1Migrates),
            ("a11y_fields_roundtrip", A11yRoundtrip),
            ("unknown_palette_falls_back_to_standard", UnknownPaletteFallsBack),
            ("palette_id_is_case_insensitive", PaletteCaseInsensitive),
            ("malformed_bindings_section_is_not_corruption", MalformedBindingsNotCorrupt),
            ("missing_graphics_section_does_not_crash", MissingGraphicsNoCrash),
            ("bindings_default_complete_and_unique", BindingsDefaultComplete),
            ("bindings_normalize_drops_unknown_and_invalid", BindingsDropUnknownInvalid),
            ("bindings_normalize_rejects_reserved_escape", BindingsRejectEscape),
            ("bindings_normalize_resolves_duplicates", BindingsResolveDuplicates),
            ("bindings_normalize_all_same_key_stays_unique", BindingsAllSameKey),
            ("bindings_normalize_canonicalizes_case", BindingsCanonicalCase),
            ("rebind_free_key_binds", RebindFreeKey),
            ("rebind_conflict_swaps", RebindSwaps),
            ("rebind_refuses_reserved_and_unknown", RebindRefuses),
            ("rebind_same_key_unchanged", RebindUnchanged),
            ("display_labels", DisplayLabels),
            ("hint_rewrite_swaps_in_single_pass", HintRewriteSwap),
            ("hint_rewrite_skips_pad_buttons_and_specs", HintRewriteSkips),
            ("hint_rewrite_korean_particles", HintRewriteKorean),
            ("hint_rewrite_empty_map_is_identity", HintRewriteIdentity),
            ("palette_resolve_never_null", PaletteResolve),
            ("palette_contact_contrast_at_least_3_to_1", PaletteContrast),
            ("red_green_palette_distinguishable_for_deutan_protan", RedGreenSafe),
            ("blue_yellow_palette_distinguishable_for_tritan", BlueYellowSafe),
            ("cvd_metric_flags_standard_palette_for_protan", MetricFlagsStandard),
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

    // ------------------------------------------------------------ settings

    private static ConfigLoadResult LoadRaw(string raw)
    {
        var paths = AppPaths.FromRoot(TestTemp.NewRoot());
        paths.EnsureDirectories();
        File.WriteAllText(paths.ConfigPath, raw);
        return ConfigLoader.Load(paths);
    }

    private static void AssertA11yDefaults(AppSettings s)
    {
        TestAssert.Equal(SonarPalette.StandardId, s.SonarPaletteId, "palette default");
        TestAssert.True(!s.HighContrastHud, "high contrast default off");
        TestAssert.True(s.KeyBindings is not null, "bindings filled");
        foreach (var a in KeyBindingCatalog.Actions)
        {
            TestAssert.Equal(a.DefaultKey, s.KeyBindings![a.Id], "default binding " + a.Id);
        }
    }

    private static void V2Migrates()
    {
        var result = LoadRaw("""{"schema_version":2,"graphics":{"width":1600,"height":900,"window_mode":"Maximized","quality":"High"},"master_volume_percent":55,"auto_reconnect_last_session":false,"language":"ko"}""");
        TestAssert.Equal(ConfigLoadStatus.Ok, result.Status, "status");
        TestAssert.Equal(2, result.FoundVersion, "found version");
        TestAssert.Equal(AppSettings.CurrentSchemaVersion, result.Settings.SchemaVersion, "migrated version");
        TestAssert.Equal("ko", result.Settings.Language, "language kept");
        TestAssert.Equal(1600, result.Settings.Graphics.Width, "graphics kept");
        TestAssert.Equal(55, result.Settings.MasterVolumePercent, "volume kept");
        AssertA11yDefaults(result.Settings);
    }

    private static void V1Migrates()
    {
        var result = LoadRaw("""{"schema_version":1,"graphics":{"width":1280,"height":720,"window_mode":"Windowed","quality":"Medium"},"master_volume_percent":80,"auto_reconnect_last_session":false}""");
        TestAssert.Equal(ConfigLoadStatus.Ok, result.Status, "status");
        TestAssert.Equal("en", result.Settings.Language, "language default");
        AssertA11yDefaults(result.Settings);
    }

    private static void A11yRoundtrip()
    {
        var paths = AppPaths.FromRoot(TestTemp.NewRoot());
        ConfigLoader.Load(paths);
        var rebound = KeyBindingCatalog.Rebind(KeyBindingCatalog.Defaults(), "abyss_ping", "Q").Bindings;
        var custom = AppSettings.Default() with
        {
            SonarPaletteId = SonarPalette.BlueYellowSafeId,
            HighContrastHud = true,
            KeyBindings = rebound,
        };
        TestAssert.True(ConfigLoader.TrySave(paths, custom, isSafeMode: false, userInitiated: true, out var err), "save " + err);
        var reloaded = ConfigLoader.Load(paths);
        TestAssert.Equal(ConfigLoadStatus.Ok, reloaded.Status, "reload status");
        TestAssert.Equal(SonarPalette.BlueYellowSafeId, reloaded.Settings.SonarPaletteId, "palette persisted");
        TestAssert.True(reloaded.Settings.HighContrastHud, "high contrast persisted");
        TestAssert.Equal("Q", reloaded.Settings.KeyBindings!["abyss_ping"], "rebind persisted");
        TestAssert.Equal("E", reloaded.Settings.KeyBindings!["abyss_interact"], "others untouched");
        TestAssert.True(File.ReadAllText(paths.ConfigPath).Contains("\"key_bindings\""), "bindings serialized under key_bindings");
    }

    private static void UnknownPaletteFallsBack()
    {
        var s = (AppSettings.Default() with { SonarPaletteId = "rainbow" }).Normalized();
        TestAssert.Equal(SonarPalette.StandardId, s.SonarPaletteId, "fallback");
        var n = (AppSettings.Default() with { SonarPaletteId = null }).Normalized();
        TestAssert.Equal(SonarPalette.StandardId, n.SonarPaletteId, "null fallback");
    }

    private static void PaletteCaseInsensitive()
    {
        var s = (AppSettings.Default() with { SonarPaletteId = "  Deutan_Protan " }).Normalized();
        TestAssert.Equal(SonarPalette.RedGreenSafeId, s.SonarPaletteId, "canonical id");
    }

    private static void MalformedBindingsNotCorrupt()
    {
        var arrayForm = LoadRaw("""{"schema_version":3,"graphics":{"width":1280,"height":720,"window_mode":"Windowed","quality":"Low"},"master_volume_percent":80,"auto_reconnect_last_session":false,"language":"en","key_bindings":["F","E"]}""");
        TestAssert.Equal(ConfigLoadStatus.Ok, arrayForm.Status, "array bindings must not quarantine the file");
        TestAssert.Equal("Low", arrayForm.Settings.Graphics.Quality, "other settings kept");
        AssertA11yDefaults(arrayForm.Settings);

        var mixed = LoadRaw("""{"schema_version":3,"graphics":{"width":1280,"height":720,"window_mode":"Windowed","quality":"Medium"},"master_volume_percent":80,"auto_reconnect_last_session":false,"language":"en","key_bindings":{"abyss_ping":7,"abyss_interact":{"x":1},"abyss_repair":"Q","abyss_dock":null}}""");
        TestAssert.Equal(ConfigLoadStatus.Ok, mixed.Status, "mixed-type bindings must not quarantine the file");
        TestAssert.Equal("F", mixed.Settings.KeyBindings!["abyss_ping"], "numeric value skipped -> default");
        TestAssert.Equal("E", mixed.Settings.KeyBindings!["abyss_interact"], "object value skipped -> default");
        TestAssert.Equal("Q", mixed.Settings.KeyBindings!["abyss_repair"], "valid string kept");
        TestAssert.Equal("J", mixed.Settings.KeyBindings!["abyss_dock"], "null value skipped -> default");
    }

    private static void MissingGraphicsNoCrash()
    {
        var result = LoadRaw("""{"schema_version":3,"master_volume_percent":80}""");
        TestAssert.Equal(ConfigLoadStatus.Ok, result.Status, "status");
        TestAssert.Equal(1280, result.Settings.Graphics.Width, "graphics defaulted");
        TestAssert.Equal("en", result.Settings.Language, "language defaulted");
        AssertA11yDefaults(result.Settings);
    }

    // ------------------------------------------------------------ bindings

    private static void AssertCompleteUnique(IReadOnlyDictionary<string, string> map, string context)
    {
        TestAssert.Equal(KeyBindingCatalog.Actions.Count, map.Count, context + ": one entry per action");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in KeyBindingCatalog.Actions)
        {
            TestAssert.True(map.TryGetValue(a.Id, out var key), context + ": missing " + a.Id);
            TestAssert.True(KeyBindingCatalog.TryCanonicalKey(key, out var canon) && canon == key, context + ": key whitelisted " + key);
            TestAssert.True(key != KeyBindingCatalog.ReservedKey, context + ": escape never bound");
            TestAssert.True(seen.Add(key!), context + ": duplicate key " + key);
        }
    }

    private static void BindingsDefaultComplete()
    {
        AssertCompleteUnique(KeyBindingCatalog.Defaults(), "defaults");
        AssertCompleteUnique(KeyBindingCatalog.Normalize(null), "normalize(null)");
        foreach (var a in KeyBindingCatalog.Actions)
        {
            TestAssert.True(KeyBindingCatalog.TryCanonicalKey(a.DefaultKey, out _), "default key allowed: " + a.DefaultKey);
        }

        TestAssert.True(!KeyBindingCatalog.IsKnownAction("abyss_pause"), "pause is not remappable (Escape reserved)");
    }

    private static void BindingsDropUnknownInvalid()
    {
        var raw = new Dictionary<string, string>
        {
            ["abyss_ping"] = "Q",
            ["abyss_interact"] = "NotAKey",
            ["abyss_repair"] = "",
            ["abyss_teleport"] = "P",
            ["abyss_survey"] = "Meta",
        };
        var map = KeyBindingCatalog.Normalize(raw);
        AssertCompleteUnique(map, "normalized");
        TestAssert.Equal("Q", map["abyss_ping"], "valid kept");
        TestAssert.Equal("E", map["abyss_interact"], "invalid -> default");
        TestAssert.Equal("R", map["abyss_repair"], "blank -> default");
        TestAssert.Equal("V", map["abyss_survey"], "non-whitelisted OS key -> default");
        TestAssert.True(!map.ContainsKey("abyss_teleport"), "unknown action dropped");
    }

    private static void BindingsRejectEscape()
    {
        var map = KeyBindingCatalog.Normalize(new Dictionary<string, string> { ["abyss_extract"] = "escape" });
        TestAssert.Equal("T", map["abyss_extract"], "escape -> default");
    }

    private static void BindingsResolveDuplicates()
    {
        // Explicit F on both ping and interact; interact's default E is free.
        var map = KeyBindingCatalog.Normalize(new Dictionary<string, string> { ["abyss_ping"] = "F", ["abyss_interact"] = "F" });
        AssertCompleteUnique(map, "dup");
        TestAssert.Equal("F", map["abyss_ping"], "earlier action keeps key");
        TestAssert.Equal("E", map["abyss_interact"], "later action takes default");

        // An explicit choice beats a missing entry's default: ping missing, repair explicitly on F.
        var explicitWins = KeyBindingCatalog.Normalize(new Dictionary<string, string> { ["abyss_repair"] = "F" });
        AssertCompleteUnique(explicitWins, "explicit wins");
        TestAssert.Equal("F", explicitWins["abyss_repair"], "explicit choice kept");
        TestAssert.True(explicitWins["abyss_ping"] != "F", "missing ping yields F to the explicit choice");
    }

    private static void BindingsAllSameKey()
    {
        var raw = KeyBindingCatalog.Actions.ToDictionary(a => a.Id, _ => "W");
        AssertCompleteUnique(KeyBindingCatalog.Normalize(raw), "all W");
    }

    private static void BindingsCanonicalCase()
    {
        var map = KeyBindingCatalog.Normalize(new Dictionary<string, string> { ["abyss_ping"] = "q", ["abyss_boost"] = "ALT", ["abyss_winch"] = " kp5 " });
        TestAssert.Equal("Q", map["abyss_ping"], "letter canonical");
        TestAssert.Equal("Alt", map["abyss_boost"], "named key canonical");
        TestAssert.Equal("Kp5", map["abyss_winch"], "trimmed + canonical");
    }

    private static void RebindFreeKey()
    {
        var r = KeyBindingCatalog.Rebind(KeyBindingCatalog.Defaults(), "abyss_ping", "q");
        TestAssert.Equal(RebindOutcome.Bound, r.Outcome, "outcome");
        TestAssert.Equal("Q", r.Bindings["abyss_ping"], "bound");
        AssertCompleteUnique(r.Bindings, "after bind");
    }

    private static void RebindSwaps()
    {
        var r = KeyBindingCatalog.Rebind(KeyBindingCatalog.Defaults(), "abyss_ping", "E");
        TestAssert.Equal(RebindOutcome.Swapped, r.Outcome, "outcome");
        TestAssert.Equal("E", r.Bindings["abyss_ping"], "ping took E");
        TestAssert.Equal("F", r.Bindings["abyss_interact"], "interact received ping's old key");
        TestAssert.Equal("abyss_interact", r.SwappedActionId, "swapped action reported");
        TestAssert.Equal("F", r.SwappedToKey, "swapped key reported");
        AssertCompleteUnique(r.Bindings, "after swap");
    }

    private static void RebindRefuses()
    {
        var defaults = KeyBindingCatalog.Defaults();
        var esc = KeyBindingCatalog.Rebind(defaults, "abyss_ping", "Escape");
        TestAssert.Equal(RebindOutcome.KeyReserved, esc.Outcome, "escape reserved");
        TestAssert.Equal("F", esc.Bindings["abyss_ping"], "unchanged on refusal");
        TestAssert.Equal(RebindOutcome.KeyNotAllowed, KeyBindingCatalog.Rebind(defaults, "abyss_ping", "Meta").Outcome, "meta not allowed");
        TestAssert.Equal(RebindOutcome.KeyNotAllowed, KeyBindingCatalog.Rebind(defaults, "abyss_ping", null).Outcome, "null not allowed");
        TestAssert.Equal(RebindOutcome.UnknownAction, KeyBindingCatalog.Rebind(defaults, "abyss_pause", "P").Outcome, "pause not remappable");
        TestAssert.True(!esc.Success, "refusal is not success");
    }

    private static void RebindUnchanged()
    {
        var r = KeyBindingCatalog.Rebind(KeyBindingCatalog.Defaults(), "abyss_ping", "F");
        TestAssert.Equal(RebindOutcome.Unchanged, r.Outcome, "outcome");
        TestAssert.True(r.Success, "unchanged is success");
    }

    private static void DisplayLabels()
    {
        TestAssert.Equal("F", KeyBindingCatalog.DisplayLabel("F"), "letter");
        TestAssert.Equal("1", KeyBindingCatalog.DisplayLabel("Key1"), "digit");
        TestAssert.Equal("Num 5", KeyBindingCatalog.DisplayLabel("Kp5"), "keypad");
        TestAssert.Equal("[", KeyBindingCatalog.DisplayLabel("Bracketleft"), "punctuation");
        TestAssert.Equal("Space", KeyBindingCatalog.DisplayLabel("Space"), "named");
        TestAssert.Equal("F10", KeyBindingCatalog.DisplayLabel("F10"), "function key");
    }

    // ------------------------------------------------------------ hint rewriting

    private static void HintRewriteSwap()
    {
        var map = new Dictionary<string, string> { ["F"] = "E", ["E"] = "F" };
        TestAssert.Equal("No salvage within 30 m — ping (E), close in, then F.",
            KeyHintRewriter.Rewrite("No salvage within 30 m — ping (F), close in, then E.", map), "swap rewritten once");
        var toNamed = new Dictionary<string, string> { ["J"] = "Space" };
        TestAssert.Equal("Docked — drill (H) or undock (Space)",
            KeyHintRewriter.Rewrite("Docked — drill (H) or undock (J)", toNamed), "multi-char label");
    }

    private static void HintRewriteSkips()
    {
        var map = new Dictionary<string, string> { ["B"] = "K", ["A"] = "Q", ["D"] = "L", ["F"] = "P", ["R"] = "Y" };
        TestAssert.Equal("press Y (pad B) to weld",
            KeyHintRewriter.Rewrite("press R (pad B) to weld", map), "pad button untouched");
        TestAssert.Equal("press J (pad D-pad Left)",
            KeyHintRewriter.Rewrite("press J (pad D-pad Left)", map), "D-pad untouched");
        TestAssert.Equal("{0:F0} m zone_A F1",
            KeyHintRewriter.Rewrite("{0:F0} m zone_A F1", map), "format spec / id / function key untouched");
        TestAssert.Equal("R(패드 B)을 눌러",
            KeyHintRewriter.Rewrite("R(패드 B)을 눌러", new Dictionary<string, string> { ["B"] = "K" }), "korean pad button untouched");
    }

    private static void HintRewriteKorean()
    {
        var map = new Dictionary<string, string> { ["J"] = "K", ["H"] = "U" };
        TestAssert.Equal("드릴(U) 가능, K로 도킹 해제.",
            KeyHintRewriter.Rewrite("드릴(H) 가능, J로 도킹 해제.", map), "hangul neighbours are not letters");
    }

    private static void HintRewriteIdentity()
    {
        const string text = "Press F to ping.";
        TestAssert.Equal(text, KeyHintRewriter.Rewrite(text, new Dictionary<string, string>()), "empty map");
        TestAssert.Equal(text, KeyHintRewriter.Rewrite(text, null), "null map");
        TestAssert.Equal(string.Empty, KeyHintRewriter.Rewrite(null, new Dictionary<string, string> { ["F"] = "Q" }), "null text");
    }

    // ------------------------------------------------------------ palettes

    private static readonly SonarContactKind[] Kinds =
    {
        SonarContactKind.Terrain, SonarContactKind.Structure, SonarContactKind.Salvage,
        SonarContactKind.Biological, SonarContactKind.Unknown,
    };

    private static void PaletteResolve()
    {
        TestAssert.Equal(SonarPalette.Standard, SonarPalette.Resolve(null), "null -> standard");
        TestAssert.Equal(SonarPalette.Standard, SonarPalette.Resolve("bogus"), "unknown -> standard");
        TestAssert.Equal(SonarPalette.RedGreenSafe, SonarPalette.Resolve(SonarPalette.RedGreenSafeId), "red-green");
        TestAssert.Equal(SonarPalette.BlueYellowSafe, SonarPalette.Resolve("TRITAN"), "blue-yellow");
        foreach (var id in SonarPalette.Ids)
        {
            TestAssert.Equal(id, SonarPalette.Resolve(id).Id, "id roundtrip " + id);
        }
    }

    private static void PaletteContrast()
    {
        foreach (var id in SonarPalette.Ids)
        {
            var p = SonarPalette.Resolve(id);
            foreach (var k in Kinds)
            {
                var ratio = Cvd.ContrastRatio(p.ContactColor(k), p.ScopeBackground, Cvd.Normal);
                TestAssert.True(ratio >= 3.0, $"{id}/{k} contrast {ratio:F2} < 3:1");
            }

            TestAssert.True(Cvd.ContrastRatio(p.Alert, p.ScopeBackground, Cvd.Normal) >= 3.0, id + " alert contrast");
            TestAssert.True(Cvd.ContrastRatio(p.Accent, p.ScopeBackground, Cvd.Normal) >= 3.0, id + " accent contrast");
        }
    }

    private const double MinDeltaE = 15.0;

    private static void AssertDistinguishable(SonarPalette p, double[,] vision, string visionName)
    {
        for (var i = 0; i < Kinds.Length; i++)
        {
            for (var j = i + 1; j < Kinds.Length; j++)
            {
                var d = Cvd.DeltaE(p.ContactColor(Kinds[i]), p.ContactColor(Kinds[j]), vision);
                TestAssert.True(d >= MinDeltaE, $"{p.Id} under {visionName}: {Kinds[i]} vs {Kinds[j]} dE={d:F1} < {MinDeltaE}");
            }

            var contrast = Cvd.ContrastRatio(p.ContactColor(Kinds[i]), p.ScopeBackground, vision);
            TestAssert.True(contrast >= 3.0, $"{p.Id} under {visionName}: {Kinds[i]} contrast {contrast:F2} < 3:1");
        }
    }

    private static void RedGreenSafe()
    {
        AssertDistinguishable(SonarPalette.RedGreenSafe, Cvd.Normal, "normal");
        AssertDistinguishable(SonarPalette.RedGreenSafe, Cvd.Protanopia, "protanopia");
        AssertDistinguishable(SonarPalette.RedGreenSafe, Cvd.Deuteranopia, "deuteranopia");
    }

    private static void BlueYellowSafe()
    {
        AssertDistinguishable(SonarPalette.BlueYellowSafe, Cvd.Normal, "normal");
        AssertDistinguishable(SonarPalette.BlueYellowSafe, Cvd.Tritanopia, "tritanopia");
    }

    private static void MetricFlagsStandard()
    {
        // Sanity check of the metric itself: the legacy palette's cyan/parchment
        // pair collapses under protanopia, which is why the CVD palettes exist.
        var d = Cvd.DeltaE(SonarPalette.Standard.Structure, SonarPalette.Standard.Salvage, Cvd.Protanopia);
        TestAssert.True(d < MinDeltaE, $"expected standard structure/salvage to be confusable under protanopia, dE={d:F1}");
    }

    /// <summary>Dichromacy simulation (Machado, Oliveira &amp; Fernandes 2009, severity 1.0) on linear sRGB.</summary>
    private static class Cvd
    {
        public static readonly double[,] Normal = { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
        public static readonly double[,] Protanopia =
        {
            { 0.152286, 1.052583, -0.204868 }, { 0.114503, 0.786281, 0.099216 }, { -0.003882, -0.048116, 1.051998 },
        };
        public static readonly double[,] Deuteranopia =
        {
            { 0.367322, 0.860646, -0.227968 }, { 0.280085, 0.672501, 0.047413 }, { -0.011820, 0.042940, 0.968881 },
        };
        public static readonly double[,] Tritanopia =
        {
            { 1.255528, -0.076749, -0.178779 }, { -0.078411, 0.930809, 0.147602 }, { 0.004733, 0.691367, 0.303900 },
        };

        private static double Linear(byte c)
        {
            var v = c / 255.0;
            return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        private static double[] Simulate(Rgb c, double[,] m)
        {
            var r = Linear(c.R);
            var g = Linear(c.G);
            var b = Linear(c.B);
            var o = new double[3];
            for (var i = 0; i < 3; i++)
            {
                o[i] = Math.Clamp(m[i, 0] * r + m[i, 1] * g + m[i, 2] * b, 0.0, 1.0);
            }

            return o;
        }

        private static double Luminance(double[] l) => 0.2126 * l[0] + 0.7152 * l[1] + 0.0722 * l[2];

        public static double ContrastRatio(Rgb a, Rgb b, double[,] m)
        {
            var la = Luminance(Simulate(a, m));
            var lb = Luminance(Simulate(b, m));
            return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        }

        private static (double L, double A, double B) Lab(double[] l)
        {
            var x = (0.4124 * l[0] + 0.3576 * l[1] + 0.1805 * l[2]) / 0.95047;
            var y = 0.2126 * l[0] + 0.7152 * l[1] + 0.0722 * l[2];
            var z = (0.0193 * l[0] + 0.1192 * l[1] + 0.9505 * l[2]) / 1.08883;
            static double F(double t) => t > 0.008856 ? Math.Cbrt(t) : 7.787 * t + 16.0 / 116.0;
            return (116 * F(y) - 16, 500 * (F(x) - F(y)), 200 * (F(y) - F(z)));
        }

        /// <summary>CIE76 distance between two colours as seen with the given vision matrix.</summary>
        public static double DeltaE(Rgb a, Rgb b, double[,] m)
        {
            var la = Lab(Simulate(a, m));
            var lb = Lab(Simulate(b, m));
            return Math.Sqrt(Math.Pow(la.L - lb.L, 2) + Math.Pow(la.A - lb.A, 2) + Math.Pow(la.B - lb.B, 2));
        }
    }
}
