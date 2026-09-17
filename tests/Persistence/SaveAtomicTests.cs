namespace AbyssScav.Persistence.Tests;

internal static class SaveAtomicTests
{
    public static int Run()
    {
        var cases = new (string Name, Func<Task> Test)[]
        {
            ("missing_file_returns_defaults_without_writing", MissingReturnsDefaults),
            ("roundtrip_atomic_save_load", Roundtrip),
            ("truncated_primary_throws_corrupt_and_preserves_bytes", TruncationPreserved),
            ("corrupt_primary_requires_explicit_restore_never_silent", ExplicitRestoreOnly),
            ("backup_rotation_keeps_latest_plus_3", BackupRotation),
            ("restore_generation_installs_backup_and_quarantines_primary", RestoreInstalls),
            ("cancelled_save_preserves_primary_and_leaves_no_temp", CancelledPreserves),
            ("write_failure_preserves_primary", WriteFailurePreserves),
            ("bounded_values_clamped_and_ids_sanitized", BoundedValues),
            ("save_refuses_to_clobber_future_primary", SaveRefusesFuturePrimary),
            ("save_refuses_to_clobber_corrupt_primary", SaveRefusesCorruptPrimary),
            ("save_rejects_older_supplied_schema", SaveRejectsOlderSupplied),
            ("update_rejects_older_schema_result", UpdateRejectsOlderResult),
            ("malformed_shapes_rejected_without_overwrite", MalformedShapesRejected),
            ("backup_names_scoped_to_filename", BackupNamesScoped),
            ("cancelled_restore_preserves_primary", CancelledRestorePreserves),
            ("repeated_restores_keep_unique_quarantines", RepeatedRestoresUniqueQuarantines),
        };

        var fail = 0;
        foreach (var (name, test) in cases)
        {
            try
            {
                test().GetAwaiter().GetResult();
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

    private static async Task MissingReturnsDefaults()
    {
        var store = new FileSaveStore(TestTemp.SavesDir(TestTemp.NewRoot()));
        var loaded = await store.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        TestAssert.Equal(ProfileSave.CurrentSchemaVersion, loaded.SchemaVersion, "schema");
        TestAssert.Equal(0L, loaded.Currencies.Credits, "defaults");
        TestAssert.True(!File.Exists(store.PrimaryPath), "missing load must not create file");
    }

    private static async Task Roundtrip()
    {
        var store = new FileSaveStore(TestTemp.SavesDir(TestTemp.NewRoot()));
        var sample = TestData.Sample("roundtrip");
        await store.SaveAsync(sample, CancellationToken.None).ConfigureAwait(false);
        var loaded = await store.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        TestAssert.Equal(sample.Currencies.Credits, loaded.Currencies.Credits, "credits");
        TestAssert.Equal(sample.Unlocks.Blueprints[0], loaded.Unlocks.Blueprints[0], "blueprint");
        TestAssert.True(loaded.AppliedSettlementIds.Contains("roundtrip.applied"), "settlement id");
    }

    private static async Task TruncationPreserved()
    {
        var store = new FileSaveStore(TestTemp.SavesDir(TestTemp.NewRoot()));
        await store.SaveAsync(TestData.Sample("trunc"), CancellationToken.None).ConfigureAwait(false);
        var full = await File.ReadAllTextAsync(store.PrimaryPath).ConfigureAwait(false);
        var truncated = full.Substring(0, full.Length / 2);
        await File.WriteAllTextAsync(store.PrimaryPath, truncated).ConfigureAwait(false);

        await TestAssert.ThrowsAsync<SaveCorruptException>(
            () => store.LoadAsync(CancellationToken.None), "truncated load");
        TestAssert.Equal(truncated, await File.ReadAllTextAsync(store.PrimaryPath).ConfigureAwait(false), "truncated bytes preserved");
    }

    private static async Task ExplicitRestoreOnly()
    {
        var store = new FileSaveStore(TestTemp.SavesDir(TestTemp.NewRoot()));
        await store.SaveAsync(TestData.Sample("explicit"), CancellationToken.None).ConfigureAwait(false);
        await File.WriteAllTextAsync(store.PrimaryPath, "{ not json {{{").ConfigureAwait(false);
        var corruptBytes = await File.ReadAllTextAsync(store.PrimaryPath).ConfigureAwait(false);

        await TestAssert.ThrowsAsync<SaveCorruptException>(
            () => store.LoadAsync(CancellationToken.None), "corrupt load");
        // Load must never silently repair/overwrite the original.
        TestAssert.Equal(corruptBytes, await File.ReadAllTextAsync(store.PrimaryPath).ConfigureAwait(false), "corrupt original kept");

        await TestAssert.ThrowsAsync<SaveIOException>(
            () => store.RestoreBackupAsync(1, CancellationToken.None), "no backup yet");
        TestAssert.Equal(corruptBytes, await File.ReadAllTextAsync(store.PrimaryPath).ConfigureAwait(false), "failed restore keeps original");
    }

    private static async Task BackupRotation()
    {
        var store = new FileSaveStore(TestTemp.SavesDir(TestTemp.NewRoot()));
        for (var i = 1; i <= 5; i++)
        {
            var s = ProfileSave.Default() with { Currencies = new SaveCurrencies(i * 10, 0, 0) };
            await store.SaveAsync(s, CancellationToken.None).ConfigureAwait(false);
        }

        TestAssert.True(File.Exists(store.BackupPathFor(1)), "gen1 exists");
        TestAssert.True(File.Exists(store.BackupPathFor(2)), "gen2 exists");
        TestAssert.True(File.Exists(store.BackupPathFor(3)), "gen3 exists");

        var gen1 = await store.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        TestAssert.Equal(50L, gen1.Currencies.Credits, "primary latest");

        // Backup.1 holds the previous primary (40), .2 holds 30, .3 holds 20; 10 aged out.
        var b1 = await File.ReadAllTextAsync(store.BackupPathFor(1)).ConfigureAwait(false);
        var b2 = await File.ReadAllTextAsync(store.BackupPathFor(2)).ConfigureAwait(false);
        var b3 = await File.ReadAllTextAsync(store.BackupPathFor(3)).ConfigureAwait(false);
        TestAssert.True(b1.Contains("40"), "gen1 is previous write");
        TestAssert.True(b2.Contains("30"), "gen2 shifted");
        TestAssert.True(b3.Contains("20"), "gen3 oldest retained");

        var stray = Directory.GetFiles(store.BackupsDir, "*.json");
        TestAssert.Equal(3, stray.Length, "bounded to 3 generations");
    }

    private static async Task RestoreInstalls()
    {
        var store = new FileSaveStore(TestTemp.SavesDir(TestTemp.NewRoot()));
        var good = TestData.Sample("restore-good");
        await store.SaveAsync(good, CancellationToken.None).ConfigureAwait(false);
        var second = good with { Currencies = new SaveCurrencies(999, 0, 0) };
        await store.SaveAsync(second, CancellationToken.None).ConfigureAwait(false);

        await File.WriteAllTextAsync(store.PrimaryPath, "TRUNCATED{{").ConfigureAwait(false);
        var result = await store.RestoreBackupAsync(1, CancellationToken.None).ConfigureAwait(false);
        TestAssert.Equal(1, result.Generation, "generation");
        TestAssert.True(result.QuarantinedPath is not null && File.Exists(result.QuarantinedPath), "corrupt quarantined");

        var loaded = await store.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        // backup.1 holds the previous primary (good = 100 credits), not the latest 999.
        TestAssert.Equal(100L, loaded.Currencies.Credits, "backup.1 content installed");
    }

    private static async Task CancelledPreserves()
    {
        var store = new FileSaveStore(TestTemp.SavesDir(TestTemp.NewRoot()));
        var first = TestData.Sample("cancel");
        await store.SaveAsync(first, CancellationToken.None).ConfigureAwait(false);
        var before = await File.ReadAllTextAsync(store.PrimaryPath).ConfigureAwait(false);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        try
        {
            await store.SaveAsync(first with { Currencies = new SaveCurrencies(4242, 0, 0) }, cts.Token).ConfigureAwait(false);
            throw new Exception("Expected cancellation.");
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }

        TestAssert.Equal(before, await File.ReadAllTextAsync(store.PrimaryPath).ConfigureAwait(false), "cancelled save keeps primary");
        var temps = Directory.GetFiles(Path.GetDirectoryName(store.PrimaryPath)!, "*.tmp-*");
        TestAssert.Equal(0, temps.Length, "no temp left behind");
    }

    private static async Task WriteFailurePreserves()
    {
        var store = new FileSaveStore(TestTemp.SavesDir(TestTemp.NewRoot()));
        var first = TestData.Sample("writefail");
        await store.SaveAsync(first, CancellationToken.None).ConfigureAwait(false);
        var before = await File.ReadAllTextAsync(store.PrimaryPath).ConfigureAwait(false);

        // Force backup rotation to fail deterministically by replacing the
        // backups directory with a plain file.
        if (Directory.Exists(store.BackupsDir))
        {
            Directory.Delete(store.BackupsDir, recursive: true);
        }

        File.WriteAllText(store.BackupsDir, "not-a-directory");
        try
        {
            var next = first with { Currencies = new SaveCurrencies(7777, 0, 0) };
            var failed = false;
            try
            {
                await store.SaveAsync(next, CancellationToken.None).ConfigureAwait(false);
            }
            catch (SaveException)
            {
                failed = true;
            }

            TestAssert.True(failed, "write failure must surface, not fake success");
            TestAssert.Equal(before, await File.ReadAllTextAsync(store.PrimaryPath).ConfigureAwait(false), "primary preserved");
        }
        finally
        {
            File.Delete(store.BackupsDir);
        }
    }

    private static async Task BoundedValues()
    {
        var store = new FileSaveStore(TestTemp.SavesDir(TestTemp.NewRoot()));
        var hostile = ProfileSave.Default() with
        {
            Currencies = new SaveCurrencies(-50, 99_999_999_999L, 5),
            Unlocks = new SaveUnlocks(
                new List<string> { "BAD ID!", "", "module.sonar.whisper_pulse", "module.sonar.whisper_pulse" },
                new List<string> { "frame.mule" }),
            AppliedSettlementIds = new List<string> { "ok.id-1", "NOPE SPACES" },
            Stats = new SaveStats(-3, -1, -9, 4),
        };
        await store.SaveAsync(hostile, CancellationToken.None).ConfigureAwait(false);
        var loaded = await store.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        TestAssert.Equal(0L, loaded.Currencies.Credits, "negative clamped");
        TestAssert.Equal(ProfileSave.MaxCurrency, loaded.Currencies.ResearchData, "overflow clamped");
        TestAssert.Equal(1, loaded.Unlocks.Blueprints.Count, "invalid/dup ids dropped");
        TestAssert.Equal("module.sonar.whisper_pulse", loaded.Unlocks.Blueprints[0], "valid id kept");
        TestAssert.Equal(1, loaded.AppliedSettlementIds.Count, "settlement ids sanitized");
        TestAssert.Equal(0L, loaded.Stats.RunsCompleted, "stats clamped");
    }

    private static async Task SaveRefusesFuturePrimary()
    {
        var store = new FileSaveStore(TestTemp.SavesDir(TestTemp.NewRoot()));
        await store.SaveAsync(TestData.Sample("future-guard"), CancellationToken.None).ConfigureAwait(false);

        var future = ProfileSave.Default() with { SchemaVersion = ProfileSave.CurrentSchemaVersion + 2 };
        var raw = System.Text.Json.JsonSerializer.Serialize(future, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(store.PrimaryPath, raw).ConfigureAwait(false);
        var before = await File.ReadAllTextAsync(store.PrimaryPath).ConfigureAwait(false);

        // A current-schema save must never clobber a newer on-disk profile.
        await TestAssert.ThrowsAsync<SaveFutureVersionException>(
            () => store.SaveAsync(TestData.Sample("future-guard"), CancellationToken.None), "future on-disk guard");
        TestAssert.Equal(before, await File.ReadAllTextAsync(store.PrimaryPath).ConfigureAwait(false), "future primary preserved");
    }

    private static async Task SaveRefusesCorruptPrimary()
    {
        var store = new FileSaveStore(TestTemp.SavesDir(TestTemp.NewRoot()));
        await store.SaveAsync(TestData.Sample("corrupt-guard"), CancellationToken.None).ConfigureAwait(false);
        await File.WriteAllTextAsync(store.PrimaryPath, "{ truncated {{{").ConfigureAwait(false);
        var before = await File.ReadAllTextAsync(store.PrimaryPath).ConfigureAwait(false);

        await TestAssert.ThrowsAsync<SaveCorruptException>(
            () => store.SaveAsync(TestData.Sample("corrupt-guard"), CancellationToken.None), "corrupt on-disk guard");
        TestAssert.Equal(before, await File.ReadAllTextAsync(store.PrimaryPath).ConfigureAwait(false), "corrupt primary preserved");
    }

    private static async Task SaveRejectsOlderSupplied()
    {
        var store = new FileSaveStore(TestTemp.SavesDir(TestTemp.NewRoot()));
        var current = TestData.Sample("older-supplied");
        await store.SaveAsync(current, CancellationToken.None).ConfigureAwait(false);
        var before = await File.ReadAllTextAsync(store.PrimaryPath).ConfigureAwait(false);

        var older = current with { SchemaVersion = ProfileSave.CurrentSchemaVersion - 1 };
        await TestAssert.ThrowsAsync<SaveMigrationException>(
            () => store.SaveAsync(older, CancellationToken.None), "older supplied rejected");
        TestAssert.Equal(before, await File.ReadAllTextAsync(store.PrimaryPath).ConfigureAwait(false), "primary preserved");
    }

    private static async Task UpdateRejectsOlderResult()
    {
        var store = new FileSaveStore(TestTemp.SavesDir(TestTemp.NewRoot()));
        await store.SaveAsync(ProfileSave.Default(), CancellationToken.None).ConfigureAwait(false);
        var before = await File.ReadAllTextAsync(store.PrimaryPath).ConfigureAwait(false);

        await TestAssert.ThrowsAsync<SaveMigrationException>(
            () => store.UpdateAsync(c => c with { SchemaVersion = ProfileSave.CurrentSchemaVersion - 1 }, CancellationToken.None),
            "older update result rejected");
        TestAssert.Equal(before, await File.ReadAllTextAsync(store.PrimaryPath).ConfigureAwait(false), "primary preserved");
    }

    private static async Task MalformedShapesRejected()
    {
        // Each payload is well-formed JSON but missing a required section/field
        // (or carrying a wrong type): load must reject, never default-fill.
        var payloads = new (string Name, string Json)[]
        {
            ("missing_currencies", """{"schema_version":1,"unlocks":{"blueprints":[],"frames":[]},"tutorial":{"completed":false,"completed_steps":[]},"codex":{"discovered_ids":[]},"stats":{"runs_completed":0,"runs_failed":0,"total_credits_earned":0,"total_shards_earned":0},"applied_settlement_ids":[]}"""),
            ("missing_applied_ids", """{"schema_version":1,"currencies":{"credits":5,"research_data":0,"abyss_shards":0},"unlocks":{"blueprints":[],"frames":[]},"tutorial":{"completed":false,"completed_steps":[]},"codex":{"discovered_ids":[]},"stats":{"runs_completed":0,"runs_failed":0,"total_credits_earned":0,"total_shards_earned":0}}"""),
            ("missing_stats_field", """{"schema_version":1,"currencies":{"credits":5,"research_data":0,"abyss_shards":0},"unlocks":{"blueprints":[],"frames":[]},"tutorial":{"completed":false,"completed_steps":[]},"codex":{"discovered_ids":[]},"stats":{"runs_completed":0,"runs_failed":0,"total_credits_earned":0},"applied_settlement_ids":[]}"""),
            ("wrong_type_stats", """{"schema_version":1,"currencies":{"credits":5,"research_data":0,"abyss_shards":0},"unlocks":{"blueprints":[],"frames":[]},"tutorial":{"completed":false,"completed_steps":[]},"codex":{"discovered_ids":[]},"stats":"oops","applied_settlement_ids":[]}"""),
            ("wrong_type_credits", """{"schema_version":1,"currencies":{"credits":"many","research_data":0,"abyss_shards":0},"unlocks":{"blueprints":[],"frames":[]},"tutorial":{"completed":false,"completed_steps":[]},"codex":{"discovered_ids":[]},"stats":{"runs_completed":0,"runs_failed":0,"total_credits_earned":0,"total_shards_earned":0},"applied_settlement_ids":[]}"""),
            ("array_schema_version", """{"schema_version":[1],"currencies":{"credits":5,"research_data":0,"abyss_shards":0},"unlocks":{"blueprints":[],"frames":[]},"tutorial":{"completed":false,"completed_steps":[]},"codex":{"discovered_ids":[]},"stats":{"runs_completed":0,"runs_failed":0,"total_credits_earned":0,"total_shards_earned":0},"applied_settlement_ids":[]}"""),
            ("object_schema_version", """{"schema_version":{"v":1},"currencies":{"credits":5,"research_data":0,"abyss_shards":0},"unlocks":{"blueprints":[],"frames":[]},"tutorial":{"completed":false,"completed_steps":[]},"codex":{"discovered_ids":[]},"stats":{"runs_completed":0,"runs_failed":0,"total_credits_earned":0,"total_shards_earned":0},"applied_settlement_ids":[]}"""),
            ("missing_unlocks_frames", """{"schema_version":1,"currencies":{"credits":5,"research_data":0,"abyss_shards":0},"unlocks":{"blueprints":[]},"tutorial":{"completed":false,"completed_steps":[]},"codex":{"discovered_ids":[]},"stats":{"runs_completed":0,"runs_failed":0,"total_credits_earned":0,"total_shards_earned":0},"applied_settlement_ids":[]}"""),
        };

        foreach (var (name, json) in payloads)
        {
            var store = new FileSaveStore(TestTemp.SavesDir(TestTemp.NewRoot()));
            Directory.CreateDirectory(Path.GetDirectoryName(store.PrimaryPath)!);
            await File.WriteAllTextAsync(store.PrimaryPath, json).ConfigureAwait(false);
            try
            {
                await store.LoadAsync(CancellationToken.None).ConfigureAwait(false);
                throw new Exception($"payload {name}: expected SaveCorruptException but loaded.");
            }
            catch (SaveCorruptException)
            {
                // Expected.
            }

            TestAssert.Equal(json, await File.ReadAllTextAsync(store.PrimaryPath).ConfigureAwait(false), $"payload {name}: bytes preserved");
        }
    }

    private static async Task BackupNamesScoped()
    {
        var savesDir = TestTemp.SavesDir(TestTemp.NewRoot());
        var storeA = new FileSaveStore(savesDir, "profile_v1.json");
        var storeB = new FileSaveStore(savesDir, "alt.json");
        await storeA.SaveAsync(ProfileSave.Default() with { Currencies = new SaveCurrencies(11, 0, 0) }, CancellationToken.None).ConfigureAwait(false);
        await storeB.SaveAsync(ProfileSave.Default() with { Currencies = new SaveCurrencies(22, 0, 0) }, CancellationToken.None).ConfigureAwait(false);
        await storeA.SaveAsync(ProfileSave.Default() with { Currencies = new SaveCurrencies(111, 0, 0) }, CancellationToken.None).ConfigureAwait(false);
        await storeB.SaveAsync(ProfileSave.Default() with { Currencies = new SaveCurrencies(222, 0, 0) }, CancellationToken.None).ConfigureAwait(false);

        TestAssert.True(File.Exists(storeA.BackupPathFor(1)), "scoped backup A");
        TestAssert.True(File.Exists(storeB.BackupPathFor(1)), "scoped backup B");
        TestAssert.True(!string.Equals(storeA.BackupPathFor(1), storeB.BackupPathFor(1), StringComparison.Ordinal), "distinct paths");
        TestAssert.True((await File.ReadAllTextAsync(storeA.BackupPathFor(1)).ConfigureAwait(false)).Contains("11"), "A backup holds A history");
        TestAssert.True((await File.ReadAllTextAsync(storeB.BackupPathFor(1)).ConfigureAwait(false)).Contains("22"), "B backup holds B history");
    }

    private static async Task CancelledRestorePreserves()
    {
        var store = new FileSaveStore(TestTemp.SavesDir(TestTemp.NewRoot()));
        var good = TestData.Sample("cancel-restore");
        await store.SaveAsync(good, CancellationToken.None).ConfigureAwait(false);
        var second = good with { Currencies = new SaveCurrencies(31337, 0, 0) };
        await store.SaveAsync(second, CancellationToken.None).ConfigureAwait(false);
        await File.WriteAllTextAsync(store.PrimaryPath, "GARBAGE{{").ConfigureAwait(false);
        var before = await File.ReadAllTextAsync(store.PrimaryPath).ConfigureAwait(false);
        var quarantinesBefore = Directory.GetFiles(Path.GetDirectoryName(store.PrimaryPath)!, "*.corrupt.*");

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        try
        {
            await store.RestoreBackupAsync(1, cts.Token).ConfigureAwait(false);
            throw new Exception("Expected cancellation.");
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }

        TestAssert.Equal(before, await File.ReadAllTextAsync(store.PrimaryPath).ConfigureAwait(false), "cancelled restore keeps primary");
        TestAssert.Equal(quarantinesBefore.Length, Directory.GetFiles(Path.GetDirectoryName(store.PrimaryPath)!, "*.corrupt.*").Length, "no quarantine on cancel");
        var temps = Directory.GetFiles(Path.GetDirectoryName(store.PrimaryPath)!, "*.tmp-*");
        TestAssert.Equal(0, temps.Length, "no temp left behind");
        // The primary is still corrupt garbage: next load must report corrupt,
        // never a silent fresh game.
        await TestAssert.ThrowsAsync<SaveCorruptException>(
            () => store.LoadAsync(CancellationToken.None), "still corrupt, not new game");
    }

    private static async Task RepeatedRestoresUniqueQuarantines()
    {
        var store = new FileSaveStore(TestTemp.SavesDir(TestTemp.NewRoot()));
        var good = TestData.Sample("unique-q");
        await store.SaveAsync(good, CancellationToken.None).ConfigureAwait(false);
        await store.SaveAsync(good with { Currencies = new SaveCurrencies(600, 0, 0) }, CancellationToken.None).ConfigureAwait(false);
        await store.SaveAsync(good with { Currencies = new SaveCurrencies(700, 0, 0) }, CancellationToken.None).ConfigureAwait(false);

        await File.WriteAllTextAsync(store.PrimaryPath, "GARBAGE-1{{").ConfigureAwait(false);
        var first = await store.RestoreBackupAsync(1, CancellationToken.None).ConfigureAwait(false);
        TestAssert.True(first.QuarantinedPath is not null && File.Exists(first.QuarantinedPath), "first quarantine kept");

        await File.WriteAllTextAsync(store.PrimaryPath, "GARBAGE-2{{").ConfigureAwait(false);
        var secondRestore = await store.RestoreBackupAsync(2, CancellationToken.None).ConfigureAwait(false);
        TestAssert.True(secondRestore.QuarantinedPath is not null && File.Exists(secondRestore.QuarantinedPath), "second quarantine kept");
        TestAssert.True(!string.Equals(first.QuarantinedPath, secondRestore.QuarantinedPath, StringComparison.Ordinal), "quarantines unique");
        TestAssert.True(File.Exists(first.QuarantinedPath!), "first quarantine not overwritten");

        var loaded = await store.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        // b1=600 (installed by first restore), b2=100, so generation 2 installs 100.
        TestAssert.Equal(100L, loaded.Currencies.Credits, "generation 2 installed");
    }
}
