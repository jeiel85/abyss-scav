using System.Text.Json;
using System.Text.Json.Nodes;

namespace AbyssScav.Persistence.Tests;

/// <summary>
/// Explicit synthetic older-version fixtures for migration tests only.
/// These versions never existed in the game; they exist solely to prove the
/// sequential chain, failure preservation, and no-skip rules.
/// </summary>
internal static class MigrationFixtures
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static void WriteSyntheticV0(string primaryPath, long credits)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(primaryPath)!);
        var obj = new JsonObject
        {
            ["schema_version"] = 0,
            ["credits"] = credits,
        };
        File.WriteAllText(primaryPath, obj.ToJsonString(Opts));
    }

    public sealed class V0ToV1 : ISaveMigration
    {
        public int FromVersion => 0;
        public int ToVersion => 1;

        public JsonObject Migrate(JsonObject source)
        {
            var credits = source["credits"]?.GetValue<long>() ?? 0;
            var save = ProfileSave.Default() with
            {
                Currencies = new SaveCurrencies(credits, 0, 0),
            };
            var json = JsonSerializer.Serialize(save, new JsonSerializerOptions { WriteIndented = true });
            return JsonNode.Parse(json)!.AsObject();
        }
    }

    public sealed class SkipV0ToV2 : ISaveMigration
    {
        public int FromVersion => 0;
        public int ToVersion => 2;

        public JsonObject Migrate(JsonObject source) => source;
    }

    public sealed class FailingV0ToV1 : ISaveMigration
    {
        public int FromVersion => 0;
        public int ToVersion => 1;

        public JsonObject Migrate(JsonObject source) =>
            throw new InvalidOperationException("synthetic migrator boom");
    }

    public sealed class WrongVersionV0ToV1 : ISaveMigration
    {
        public int FromVersion => 0;
        public int ToVersion => 1;

        public JsonObject Migrate(JsonObject source)
        {
            source["schema_version"] = 99;
            return source;
        }
    }

    public sealed class StringVersionV0ToV1 : ISaveMigration
    {
        public int FromVersion => 0;
        public int ToVersion => 1;

        public JsonObject Migrate(JsonObject source)
        {
            source["schema_version"] = "one";
            return source;
        }
    }
}

internal static class MigrationTests
{
    public static int Run()
    {
        var cases = new (string Name, Func<Task> Test)[]
        {
            ("missing_migration_step_preserves_original", MissingStepPreservesOriginal),
            ("skip_migration_never_allowed", SkipNeverAllowed),
            ("successful_migration_reads_without_overwriting_until_save", SuccessDefersWrite),
            ("failed_migration_preserves_original", FailedPreservesOriginal),
            ("wrong_output_version_rejected_preserves_original", WrongVersionRejected),
            ("unreadable_output_version_rejected_preserves_original", UnreadableVersionRejected),
            ("future_schema_load_and_save_never_overwrite", FutureNeverOverwrites),
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

    private static async Task MissingStepPreservesOriginal()
    {
        var root = TestTemp.NewRoot();
        var store = new FileSaveStore(TestTemp.SavesDir(root));
        MigrationFixtures.WriteSyntheticV0(store.PrimaryPath, 50);
        var before = await File.ReadAllTextAsync(store.PrimaryPath).ConfigureAwait(false);

        // Production chain is empty: v0 has no registered step.
        await TestAssert.ThrowsAsync<SaveMigrationException>(
            () => store.LoadAsync(CancellationToken.None), "missing step");
        TestAssert.Equal(before, await File.ReadAllTextAsync(store.PrimaryPath).ConfigureAwait(false), "original preserved");
    }

    private static async Task SkipNeverAllowed()
    {
        var root = TestTemp.NewRoot();
        // Direct v0->v2 skip entry must NOT satisfy the v0->v1 step.
        var store = new FileSaveStore(
            TestTemp.SavesDir(root),
            "profile_v1.json",
            new ISaveMigration[] { new MigrationFixtures.SkipV0ToV2() });
        MigrationFixtures.WriteSyntheticV0(store.PrimaryPath, 50);
        var before = await File.ReadAllTextAsync(store.PrimaryPath).ConfigureAwait(false);

        await TestAssert.ThrowsAsync<SaveMigrationException>(
            () => store.LoadAsync(CancellationToken.None), "skip rejected");
        TestAssert.Equal(before, await File.ReadAllTextAsync(store.PrimaryPath).ConfigureAwait(false), "original preserved");
    }

    private static async Task SuccessDefersWrite()
    {
        var root = TestTemp.NewRoot();
        var store = new FileSaveStore(
            TestTemp.SavesDir(root),
            "profile_v1.json",
            new ISaveMigration[] { new MigrationFixtures.V0ToV1() });
        MigrationFixtures.WriteSyntheticV0(store.PrimaryPath, 77);
        var before = await File.ReadAllTextAsync(store.PrimaryPath).ConfigureAwait(false);

        var loaded = await store.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        TestAssert.Equal(1, loaded.SchemaVersion, "migrated to v1");
        TestAssert.Equal(77L, loaded.Currencies.Credits, "credits carried");
        // Load must not overwrite the v0 file; persistence happens on explicit Save.
        TestAssert.Equal(before, await File.ReadAllTextAsync(store.PrimaryPath).ConfigureAwait(false), "load defers write");

        await store.SaveAsync(loaded, CancellationToken.None).ConfigureAwait(false);
        var reloaded = await store.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        TestAssert.Equal(77L, reloaded.Currencies.Credits, "persisted after explicit save");
        TestAssert.True(File.Exists(store.BackupPathFor(1)), "pre-migration original kept as backup.1");
    }

    private static async Task FailedPreservesOriginal()
    {
        var root = TestTemp.NewRoot();
        var store = new FileSaveStore(
            TestTemp.SavesDir(root),
            "profile_v1.json",
            new ISaveMigration[] { new MigrationFixtures.FailingV0ToV1() });
        MigrationFixtures.WriteSyntheticV0(store.PrimaryPath, 10);
        var before = await File.ReadAllTextAsync(store.PrimaryPath).ConfigureAwait(false);

        await TestAssert.ThrowsAsync<SaveMigrationException>(
            () => store.LoadAsync(CancellationToken.None), "failing step");
        TestAssert.Equal(before, await File.ReadAllTextAsync(store.PrimaryPath).ConfigureAwait(false), "original preserved");
    }

    private static async Task WrongVersionRejected()
    {
        var root = TestTemp.NewRoot();
        var store = new FileSaveStore(
            TestTemp.SavesDir(root),
            "profile_v1.json",
            new ISaveMigration[] { new MigrationFixtures.WrongVersionV0ToV1() });
        MigrationFixtures.WriteSyntheticV0(store.PrimaryPath, 10);
        var before = await File.ReadAllTextAsync(store.PrimaryPath).ConfigureAwait(false);

        await TestAssert.ThrowsAsync<SaveMigrationException>(
            () => store.LoadAsync(CancellationToken.None), "wrong output version");
        TestAssert.Equal(before, await File.ReadAllTextAsync(store.PrimaryPath).ConfigureAwait(false), "original preserved");
    }

    private static async Task UnreadableVersionRejected()
    {
        var root = TestTemp.NewRoot();
        var store = new FileSaveStore(
            TestTemp.SavesDir(root),
            "profile_v1.json",
            new ISaveMigration[] { new MigrationFixtures.StringVersionV0ToV1() });
        MigrationFixtures.WriteSyntheticV0(store.PrimaryPath, 10);
        var before = await File.ReadAllTextAsync(store.PrimaryPath).ConfigureAwait(false);

        await TestAssert.ThrowsAsync<SaveMigrationException>(
            () => store.LoadAsync(CancellationToken.None), "unreadable output version");
        TestAssert.Equal(before, await File.ReadAllTextAsync(store.PrimaryPath).ConfigureAwait(false), "original preserved");
    }

    private static async Task FutureNeverOverwrites()
    {
        var root = TestTemp.NewRoot();
        var store = new FileSaveStore(TestTemp.SavesDir(root));
        var future = ProfileSave.Default() with { SchemaVersion = ProfileSave.CurrentSchemaVersion + 3 };
        Directory.CreateDirectory(TestTemp.SavesDir(root));
        var raw = JsonSerializer.Serialize(future, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(store.PrimaryPath, raw).ConfigureAwait(false);
        var before = await File.ReadAllTextAsync(store.PrimaryPath).ConfigureAwait(false);

        await TestAssert.ThrowsAsync<SaveFutureVersionException>(
            () => store.LoadAsync(CancellationToken.None), "future load rejected");
        TestAssert.Equal(before, await File.ReadAllTextAsync(store.PrimaryPath).ConfigureAwait(false), "future load no overwrite");

        await TestAssert.ThrowsAsync<SaveFutureVersionException>(
            () => store.SaveAsync(future, CancellationToken.None), "future save refused");
        TestAssert.Equal(before, await File.ReadAllTextAsync(store.PrimaryPath).ConfigureAwait(false), "future save no overwrite");
    }
}
