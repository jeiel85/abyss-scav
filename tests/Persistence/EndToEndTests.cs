using System.Text.Json;
using System.Text.Json.Nodes;

namespace AbyssScav.Persistence.Tests;

/// <summary>
/// Full-filesystem integration: two fresh store instances round-trip every
/// profile field, then a subsequent save is verified on disk (valid JSON with
/// all keys, no temp litter, backup.1 holding the previous payload).
/// </summary>
internal static class EndToEndTests
{
    public static int Run()
    {
        var cases = new (string Name, Func<Task> Test)[]
        {
            ("two_stores_roundtrip_all_fields_with_disk_verification", TwoStoresRoundtrip),
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

    private static async Task TwoStoresRoundtrip()
    {
        var savesDir = TestTemp.SavesDir(TestTemp.NewRoot());
        var writer = new FileSaveStore(savesDir);

        var full = new ProfileSave(
            ProfileSave.CurrentSchemaVersion,
            new SaveCurrencies(12345, 678, 90),
            new SaveUnlocks(
                new List<string> { "module.sonar.whisper_pulse", "module.engine.ion_drive" },
                new List<string> { "frame.mule", "frame.warden" }),
            new SaveTutorial(true, new List<string> { "tutorial.move", "tutorial.sonar" }),
            new SaveCodex(new List<string> { "codex.leech", "codex.wreck" }),
            new SaveStats(7, 2, 50000, 11),
            new List<string> { "run.e2e.1", "run.e2e.2" });
        await writer.SaveAsync(full, CancellationToken.None).ConfigureAwait(false);

        // A completely fresh instance reads back every field.
        var reader = new FileSaveStore(savesDir);
        var loaded = await reader.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        TestAssert.Equal(12345L, loaded.Currencies.Credits, "credits");
        TestAssert.Equal(678L, loaded.Currencies.ResearchData, "research");
        TestAssert.Equal(90L, loaded.Currencies.AbyssShards, "shards");
        TestAssert.Equal(2, loaded.Unlocks.Blueprints.Count, "blueprints");
        TestAssert.True(loaded.Unlocks.Blueprints.Contains("module.engine.ion_drive"), "blueprint value");
        TestAssert.Equal(2, loaded.Unlocks.Frames.Count, "frames");
        TestAssert.True(loaded.Tutorial.Completed, "tutorial completed");
        TestAssert.Equal(2, loaded.Tutorial.CompletedSteps.Count, "tutorial steps");
        TestAssert.Equal(2, loaded.Codex.DiscoveredIds.Count, "codex");
        TestAssert.Equal(7L, loaded.Stats.RunsCompleted, "runs completed");
        TestAssert.Equal(2L, loaded.Stats.RunsFailed, "runs failed");
        TestAssert.Equal(50000L, loaded.Stats.TotalCreditsEarned, "credits earned");
        TestAssert.Equal(11L, loaded.Stats.TotalShardsEarned, "shards earned");
        TestAssert.Equal(2, loaded.AppliedSettlementIds.Count, "settlement ids");

        // On-disk primary must be valid JSON carrying every required key.
        var raw = await File.ReadAllTextAsync(reader.PrimaryPath).ConfigureAwait(false);
        var root = JsonNode.Parse(raw)!.AsObject();
        foreach (var key in new[] { "schema_version", "currencies", "unlocks", "tutorial", "codex", "stats", "applied_settlement_ids" })
        {
            TestAssert.True(root.ContainsKey(key), "disk has " + key);
        }

        TestAssert.Equal(0, Directory.GetFiles(savesDir, "*.tmp-*").Length, "no temp litter");

        // A subsequent save via the second instance rotates backup.1 to the first payload.
        var updated = loaded with { Currencies = new SaveCurrencies(20000, 678, 90) };
        await reader.SaveAsync(updated, CancellationToken.None).ConfigureAwait(false);

        var backupRaw = await File.ReadAllTextAsync(reader.BackupPathFor(1)).ConfigureAwait(false);
        var backupRoot = JsonNode.Parse(backupRaw)!.AsObject();
        TestAssert.Equal(12345L, backupRoot["currencies"]!["credits"]!.GetValue<long>(), "backup.1 holds previous payload");

        var primaryRaw = await File.ReadAllTextAsync(reader.PrimaryPath).ConfigureAwait(false);
        var primaryRoot = JsonNode.Parse(primaryRaw)!.AsObject();
        TestAssert.Equal(20000L, primaryRoot["currencies"]!["credits"]!.GetValue<long>(), "primary holds latest");

        var fresh = new FileSaveStore(savesDir);
        var reloaded = await fresh.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        TestAssert.Equal(20000L, reloaded.Currencies.Credits, "fresh instance reads latest");
        TestAssert.Equal(2, reloaded.AppliedSettlementIds.Count, "settlement ids survive rotation");
    }
}
