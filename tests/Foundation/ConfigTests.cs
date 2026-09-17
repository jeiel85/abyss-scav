using AbyssScav.Foundation;

namespace AbyssScav.Foundation.Tests;

internal static class ConfigTests
{
    public static int Run()
    {
        var cases = new (string Name, Action Test)[]
        {
            ("missing_file_creates_defaults", MissingCreatesDefaults),
            ("corrupt_file_quarantined_and_defaults_restored", CorruptRecovered),
            ("quarantine_failure_preserves_original_in_memory", QuarantineFailurePreservesOriginal),
            ("unreadable_file_is_io_error_not_corruption", UnreadableIsIoError),
            ("storage_unavailable_is_io_error", StorageUnavailableIsIoError),
            ("future_version_never_overwritten", FutureVersionReadOnly),
            ("future_with_changed_fields_still_protected", FutureChangedFieldsProtected),
            ("trysave_refuses_ondisk_future_version", TrySaveRefusesOnDiskFuture),
            ("persisted_settings_roundtrip", PersistedRoundtrip),
            ("safe_mode_boot_does_not_overwrite_prefs", SafeModeNoOverwrite),
            ("safe_mode_effective_values", SafeModeEffective),
            ("notices_carry_no_paths_or_usernames", NoticesCarryNoPaths),
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

    private static void MissingCreatesDefaults()
    {
        var paths = AppPaths.FromRoot(TestTemp.NewRoot());
        var result = ConfigLoader.Load(paths);
        TestAssert.Equal(ConfigLoadStatus.CreatedDefaults, result.Status, "status");
        TestAssert.True(File.Exists(paths.ConfigPath), "defaults file written");
        TestAssert.Equal(AppSettings.CurrentSchemaVersion, result.Settings.SchemaVersion, "schema");
    }

    private static void CorruptRecovered()
    {
        var paths = AppPaths.FromRoot(TestTemp.NewRoot());
        paths.EnsureDirectories();
        File.WriteAllText(paths.ConfigPath, "{ this is not json {{{");
        var result = ConfigLoader.Load(paths);
        TestAssert.Equal(ConfigLoadStatus.RecoveredFromCorruption, result.Status, "status");
        TestAssert.True(result.CorruptBackupPath is not null, "backup named");
        TestAssert.True(!Path.IsPathRooted(result.CorruptBackupPath!), "backup is a file name, not a full path");
        TestAssert.True(File.Exists(Path.Combine(paths.ConfigDir, result.CorruptBackupPath!)), "corrupt backup kept");
        TestAssert.True(File.Exists(paths.ConfigPath), "defaults rewritten");
        // Reloading the rewritten file must be clean.
        var again = ConfigLoader.Load(paths);
        TestAssert.Equal(ConfigLoadStatus.Ok, again.Status, "reload status");
    }

    private static void QuarantineFailurePreservesOriginal()
    {
        var paths = AppPaths.FromRoot(TestTemp.NewRoot());
        paths.EnsureDirectories();
        const string corrupt = "{ this is not json {{{";
        File.WriteAllText(paths.ConfigPath, corrupt);
        // Held open for read (no delete share): reads succeed but quarantine (File.Move) fails.
        using var held = new FileStream(paths.ConfigPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var result = ConfigLoader.Load(paths);
        TestAssert.Equal(ConfigLoadStatus.RecoveredInMemoryOnly, result.Status, "status");
        TestAssert.Equal(corrupt, File.ReadAllText(paths.ConfigPath), "original preserved, not overwritten");
        held.Dispose();
        // After releasing, recovery proceeds normally.
        var again = ConfigLoader.Load(paths);
        TestAssert.Equal(ConfigLoadStatus.RecoveredFromCorruption, again.Status, "recovery after unlock");
    }

    private static void UnreadableIsIoError()
    {
        var paths = AppPaths.FromRoot(TestTemp.NewRoot());
        paths.EnsureDirectories();
        const string original = """{"schema_version":1,"graphics":{"width":1280,"height":720,"window_mode":"Windowed","quality":"Medium"},"master_volume_percent":80,"auto_reconnect_last_session":false}""";
        File.WriteAllText(paths.ConfigPath, original);
        using var held = new FileStream(paths.ConfigPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = ConfigLoader.Load(paths);
        TestAssert.Equal(ConfigLoadStatus.IoErrorInMemoryDefaults, result.Status, "I/O error is not corruption");
        held.Dispose();
        TestAssert.Equal(original, File.ReadAllText(paths.ConfigPath), "unreadable original left untouched");
    }

    private static void StorageUnavailableIsIoError()
    {
        var root = TestTemp.NewRoot();
        // A regular file where the config directory must go makes storage unavailable.
        File.WriteAllText(Path.Combine(root, "config"), "blocker");
        var paths = AppPaths.FromRoot(root);
        var result = ConfigLoader.Load(paths);
        TestAssert.Equal(ConfigLoadStatus.IoErrorInMemoryDefaults, result.Status, "status");
        TestAssert.Equal(AppSettings.CurrentSchemaVersion, result.Settings.SchemaVersion, "in-memory defaults");
    }

    private static void FutureVersionReadOnly()
    {
        var paths = AppPaths.FromRoot(TestTemp.NewRoot());
        paths.EnsureDirectories();
        var future = AppSettings.Default() with { SchemaVersion = AppSettings.CurrentSchemaVersion + 5 };
        File.WriteAllText(paths.ConfigPath, ConfigLoader.Serialize(future));
        var before = File.ReadAllText(paths.ConfigPath);

        var result = ConfigLoader.Load(paths);
        TestAssert.Equal(ConfigLoadStatus.FutureVersionReadOnly, result.Status, "status");
        TestAssert.Equal(AppSettings.CurrentSchemaVersion, result.Settings.SchemaVersion, "in-memory defaults version");
        TestAssert.Equal(before, File.ReadAllText(paths.ConfigPath), "future file must not be overwritten on load");

        var refused = ConfigLoader.TrySave(paths, future, isSafeMode: false, userInitiated: true, out _);
        TestAssert.True(!refused, "future-schema save must be refused");
        TestAssert.Equal(before, File.ReadAllText(paths.ConfigPath), "future file must not be overwritten on save");
    }

    private static void FutureChangedFieldsProtected()
    {
        var paths = AppPaths.FromRoot(TestTemp.NewRoot());
        paths.EnsureDirectories();
        // Future schema with changed/unknown fields that full deserialization could not handle.
        var raw = """{"schema_version":99,"graphics":{"width":"ultra-wide","panels":[1,2,3]},"brand_new_section":{"a":[1,{"b":null}]},"master_volume_percent":"loud"}""";
        File.WriteAllText(paths.ConfigPath, raw);
        var before = File.ReadAllText(paths.ConfigPath);

        var result = ConfigLoader.Load(paths);
        TestAssert.Equal(ConfigLoadStatus.FutureVersionReadOnly, result.Status, "future must be detected schema-first, not quarantined as corrupt");
        TestAssert.True(result.CorruptBackupPath is null, "no quarantine of future files");
        TestAssert.Equal(before, File.ReadAllText(paths.ConfigPath), "future file untouched");
    }

    private static void TrySaveRefusesOnDiskFuture()
    {
        var paths = AppPaths.FromRoot(TestTemp.NewRoot());
        paths.EnsureDirectories();
        var future = AppSettings.Default() with { SchemaVersion = AppSettings.CurrentSchemaVersion + 5 };
        File.WriteAllText(paths.ConfigPath, ConfigLoader.Serialize(future));
        var before = File.ReadAllText(paths.ConfigPath);

        // Supplied settings are current-schema, but the on-disk file is future: must still refuse.
        var current = AppSettings.Default() with
        {
            Graphics = new GraphicsSettings(1920, 1080, "Fullscreen", "High"),
        };
        var refused = ConfigLoader.TrySave(paths, current, isSafeMode: false, userInitiated: true, out var error);
        TestAssert.True(!refused, "on-disk future must block save, got: " + error);
        TestAssert.Equal(before, File.ReadAllText(paths.ConfigPath), "on-disk future file untouched");
    }

    private static void PersistedRoundtrip()
    {
        var paths = AppPaths.FromRoot(TestTemp.NewRoot());
        ConfigLoader.Load(paths);
        var custom = AppSettings.Default() with
        {
            Graphics = new GraphicsSettings(1920, 1080, "Fullscreen", "High"),
            MasterVolumePercent = 42,
        };
        TestAssert.True(ConfigLoader.TrySave(paths, custom, isSafeMode: false, userInitiated: true, out var err), "save " + err);
        var reloaded = ConfigLoader.Load(paths);
        TestAssert.Equal(1920, reloaded.Settings.Graphics.Width, "width persisted");
        TestAssert.Equal(1080, reloaded.Settings.Graphics.Height, "height persisted");
        TestAssert.Equal("Fullscreen", reloaded.Settings.Graphics.WindowMode, "mode persisted");
        TestAssert.Equal(42, reloaded.Settings.MasterVolumePercent, "volume persisted");
    }

    private static void SafeModeNoOverwrite()
    {
        var paths = AppPaths.FromRoot(TestTemp.NewRoot());
        ConfigLoader.Load(paths);
        var user = AppSettings.Default() with
        {
            Graphics = new GraphicsSettings(1920, 1080, "Fullscreen", "High"),
        };
        ConfigLoader.TrySave(paths, user, isSafeMode: false, userInitiated: true, out _);
        var before = File.ReadAllText(paths.ConfigPath);

        // Boot-time (non-user) save in safe mode must be refused.
        var refused = ConfigLoader.TrySave(paths, SafeModePolicy.Apply(user), isSafeMode: true, userInitiated: false, out _);
        TestAssert.True(!refused, "safe-mode boot save must be refused");
        TestAssert.Equal(before, File.ReadAllText(paths.ConfigPath), "user prefs untouched by safe-mode boot");

        // Explicit user Apply in safe mode is allowed.
        TestAssert.True(ConfigLoader.TrySave(paths, user, isSafeMode: true, userInitiated: true, out _), "explicit user save allowed");
    }

    private static void SafeModeEffective()
    {
        var effective = SafeModePolicy.Apply(AppSettings.Default() with { AutoReconnectLastSession = true });
        TestAssert.Equal(1280, effective.Graphics.Width, "safe width");
        TestAssert.Equal(720, effective.Graphics.Height, "safe height");
        TestAssert.Equal("Windowed", effective.Graphics.WindowMode, "safe window");
        TestAssert.Equal("Low", effective.Graphics.Quality, "safe quality");
        TestAssert.True(!effective.AutoReconnectLastSession, "no auto reconnect in safe mode");
    }

    private static void NoticesCarryNoPaths()
    {
        var user = Environment.UserName;
        // Corrupt.
        var corruptPaths = AppPaths.FromRoot(TestTemp.NewRoot());
        corruptPaths.EnsureDirectories();
        File.WriteAllText(corruptPaths.ConfigPath, "{{{nope");
        CheckNotice(corruptPaths.RootDir, ConfigLoader.Load(corruptPaths).Notice, user);

        // Future.
        var futurePaths = AppPaths.FromRoot(TestTemp.NewRoot());
        futurePaths.EnsureDirectories();
        File.WriteAllText(futurePaths.ConfigPath, ConfigLoader.Serialize(AppSettings.Default() with { SchemaVersion = 99 }));
        var future = ConfigLoader.Load(futurePaths);
        CheckNotice(futurePaths.RootDir, future.Notice, user);
        ConfigLoader.TrySave(futurePaths, AppSettings.Default(), false, true, out var saveError);
        TestAssert.True(!saveError.Contains(futurePaths.RootDir), "save error carries full path");

        // I/O error.
        var ioPaths = AppPaths.FromRoot(TestTemp.NewRoot());
        ioPaths.EnsureDirectories();
        File.WriteAllText(ioPaths.ConfigPath, "{}");
        using (var held = new FileStream(ioPaths.ConfigPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            CheckNotice(ioPaths.RootDir, ConfigLoader.Load(ioPaths).Notice, user);
        }
    }

    private static void CheckNotice(string rootDir, string? notice, string user)
    {
        TestAssert.True(!string.IsNullOrEmpty(notice), "notice present");
        TestAssert.True(!notice!.Contains(rootDir), "notice must not carry full paths: " + notice);
        TestAssert.True(string.IsNullOrEmpty(user) || !notice.Contains(user), "notice must not carry OS username");
    }
}
