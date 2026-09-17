namespace AbyssScav.Persistence.Tests;

internal static class SettlementTests
{
    public static int Run()
    {
        var cases = new (string Name, Func<Task> Test)[]
        {
            ("settlement_applies_once_and_second_is_noop_without_rewrite", RepeatedOnlyOnce),
            ("invalid_settlement_rejected_before_any_write", InvalidRejected),
            ("concurrent_updates_have_no_lost_update", ConcurrentUpdates),
            ("concurrent_stores_and_duplicate_settlements_apply_once", ConcurrentStores),
            ("settlement_history_beyond_512_fully_preserved", HistoryBeyond512Preserved),
            ("settlement_ledger_capacity_fails_closed", LedgerCapacityFailsClosed),
            ("lock_contention_fails_closed_and_preserves_primary", LockContentionFailsClosed),
            ("child_processes_apply_settlements_without_lost_update", ChildProcessesApply),
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

    private static async Task RepeatedOnlyOnce()
    {
        var store = new FileSaveStore(TestTemp.SavesDir(TestTemp.NewRoot()));
        await store.SaveAsync(ProfileSave.Default(), CancellationToken.None).ConfigureAwait(false);
        var payload = SettlementPayload.ForCredits("run.2026-09-17.a", 120);

        var first = await store.ApplySettlementAsync(payload, CancellationToken.None).ConfigureAwait(false);
        TestAssert.Equal(SettlementApplyOutcome.Applied, first.Outcome, "first applies");
        TestAssert.Equal(120L, first.Save.Currencies.Credits, "credited");

        var bytesAfterFirst = await File.ReadAllTextAsync(store.PrimaryPath).ConfigureAwait(false);
        var second = await store.ApplySettlementAsync(payload, CancellationToken.None).ConfigureAwait(false);
        TestAssert.Equal(SettlementApplyOutcome.AlreadyApplied, second.Outcome, "repeat noop");
        TestAssert.Equal(120L, second.Save.Currencies.Credits, "no double credit");
        TestAssert.Equal(
            bytesAfterFirst,
            await File.ReadAllTextAsync(store.PrimaryPath).ConfigureAwait(false),
            "repeat leaves file byte-identical");
    }

    private static async Task InvalidRejected()
    {
        var store = new FileSaveStore(TestTemp.SavesDir(TestTemp.NewRoot()));
        await store.SaveAsync(ProfileSave.Default(), CancellationToken.None).ConfigureAwait(false);
        var before = await File.ReadAllTextAsync(store.PrimaryPath).ConfigureAwait(false);

        var bad = new SettlementPayload(
            "BAD ID WITH SPACES", 10_000_000L, 0, 0,
            new[] { "not valid!" }, Array.Empty<string>(), Array.Empty<string>(), 5, 0);
        var rejected = false;
        try
        {
            await store.ApplySettlementAsync(bad, CancellationToken.None).ConfigureAwait(false);
        }
        catch (SaveException)
        {
            rejected = true;
        }

        TestAssert.True(rejected, "invalid payload rejected");
        TestAssert.Equal(before, await File.ReadAllTextAsync(store.PrimaryPath).ConfigureAwait(false), "no write on reject");
    }

    private static async Task ConcurrentUpdates()
    {
        var store = new FileSaveStore(TestTemp.SavesDir(TestTemp.NewRoot()));
        await store.SaveAsync(ProfileSave.Default(), CancellationToken.None).ConfigureAwait(false);

        const int writers = 16;
        const long delta = 10;
        var tasks = Enumerable.Range(0, writers).Select(_ => Task.Run(async () =>
        {
            await store.UpdateAsync(current => current with
            {
                Currencies = current.Currencies with { Credits = current.Currencies.Credits + delta },
            }, CancellationToken.None).ConfigureAwait(false);
        })).ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);

        var loaded = await store.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        TestAssert.Equal(writers * delta, loaded.Currencies.Credits, "no lost update");
    }

    private static async Task ConcurrentStores()
    {
        var savesDir = TestTemp.SavesDir(TestTemp.NewRoot());
        var storeA = new FileSaveStore(savesDir);
        var storeB = new FileSaveStore(savesDir);
        await storeA.SaveAsync(ProfileSave.Default(), CancellationToken.None).ConfigureAwait(false);

        // Distinct settlements from two store handles plus racing duplicates
        // of the same id: every id must credit exactly once.
        var ids = Enumerable.Range(0, 8).Select(i => $"run.concurrent.{i}").ToList();
        var tasks = new List<Task>();
        foreach (var id in ids)
        {
            var payload = SettlementPayload.ForCredits(id, 25);
            tasks.Add(Task.Run(() => storeA.ApplySettlementAsync(payload, CancellationToken.None)));
            tasks.Add(Task.Run(() => storeB.ApplySettlementAsync(payload, CancellationToken.None)));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        var loaded = await storeA.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        TestAssert.Equal(8 * 25L, loaded.Currencies.Credits, "each settlement credited once across stores");
        TestAssert.Equal(8, loaded.AppliedSettlementIds.Count, "all ids present once");
    }

    private static async Task HistoryBeyond512Preserved()
    {
        var store = new FileSaveStore(TestTemp.SavesDir(TestTemp.NewRoot()));
        const int history = 600;
        var ids = Enumerable.Range(0, history).Select(i => $"run.hist.{i}").ToList();
        var seeded = ProfileSave.Default() with { AppliedSettlementIds = ids };
        await store.SaveAsync(seeded, CancellationToken.None).ConfigureAwait(false);

        var loaded = await store.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        TestAssert.Equal(history, loaded.AppliedSettlementIds.Count, "full history preserved past 512");
        TestAssert.True(loaded.AppliedSettlementIds.Contains("run.hist.0"), "oldest kept");
        TestAssert.True(loaded.AppliedSettlementIds.Contains("run.hist.599"), "latest kept");

        // Duplicates of both the oldest and latest ids must be detected —
        // with truncation they would be lost and pay out again.
        var before = await File.ReadAllTextAsync(store.PrimaryPath).ConfigureAwait(false);
        var dupOld = await store.ApplySettlementAsync(SettlementPayload.ForCredits("run.hist.0", 50), CancellationToken.None).ConfigureAwait(false);
        TestAssert.Equal(SettlementApplyOutcome.AlreadyApplied, dupOld.Outcome, "oldest duplicate noop");
        var dupNew = await store.ApplySettlementAsync(SettlementPayload.ForCredits("run.hist.599", 50), CancellationToken.None).ConfigureAwait(false);
        TestAssert.Equal(SettlementApplyOutcome.AlreadyApplied, dupNew.Outcome, "latest duplicate noop");
        TestAssert.Equal(0L, dupNew.Save.Currencies.Credits, "no duplicate payout");
        TestAssert.Equal(before, await File.ReadAllTextAsync(store.PrimaryPath).ConfigureAwait(false), "duplicates leave file identical");

        var fresh = await store.ApplySettlementAsync(SettlementPayload.ForCredits("run.hist.600", 50), CancellationToken.None).ConfigureAwait(false);
        TestAssert.Equal(SettlementApplyOutcome.Applied, fresh.Outcome, "new id still awarded");
        TestAssert.Equal(50L, fresh.Save.Currencies.Credits, "new id credited");
        TestAssert.Equal(history + 1, fresh.Save.AppliedSettlementIds.Count, "history grew");
    }

    private static async Task LedgerCapacityFailsClosed()
    {
        var store = new FileSaveStore(TestTemp.SavesDir(TestTemp.NewRoot()));
        var full = Enumerable.Range(0, ProfileSave.MaxAppliedSettlementIds).Select(i => $"run.cap.{i}").ToList();
        await store.SaveAsync(ProfileSave.Default() with { AppliedSettlementIds = full }, CancellationToken.None).ConfigureAwait(false);
        var loaded = await store.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        TestAssert.Equal(ProfileSave.MaxAppliedSettlementIds, loaded.AppliedSettlementIds.Count, "ledger at capacity round-trips");
        var before = await File.ReadAllTextAsync(store.PrimaryPath).ConfigureAwait(false);

        // A duplicate at capacity must still be a clean AlreadyApplied, not an error.
        var dup = await store.ApplySettlementAsync(SettlementPayload.ForCredits("run.cap.0", 25), CancellationToken.None).ConfigureAwait(false);
        TestAssert.Equal(SettlementApplyOutcome.AlreadyApplied, dup.Outcome, "duplicate at capacity noop");

        // A genuinely new award must fail closed — no credit, no truncation.
        var refused = false;
        try
        {
            await store.ApplySettlementAsync(SettlementPayload.ForCredits("run.cap.new", 25), CancellationToken.None).ConfigureAwait(false);
        }
        catch (SaveException)
        {
            refused = true;
        }

        TestAssert.True(refused, "new award at capacity refused");
        TestAssert.Equal(before, await File.ReadAllTextAsync(store.PrimaryPath).ConfigureAwait(false), "ledger untouched");
        var again = await store.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        TestAssert.Equal(ProfileSave.MaxAppliedSettlementIds, again.AppliedSettlementIds.Count, "no history dropped");
        TestAssert.Equal(0L, again.Currencies.Credits, "no payout");
    }

    private static async Task LockContentionFailsClosed()
    {
        var savesDir = TestTemp.SavesDir(TestTemp.NewRoot());
        var store = new FileSaveStore(savesDir);
        await store.SaveAsync(ProfileSave.Default(), CancellationToken.None).ConfigureAwait(false);
        var before = await File.ReadAllTextAsync(store.PrimaryPath).ConfigureAwait(false);

        // Simulate another OS process holding the sidecar lock.
        var lockPath = store.PrimaryPath + ".lock";
        using var externalHold = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var previousTimeout = FileSaveStore.LockAcquireTimeout;
        FileSaveStore.LockAcquireTimeout = TimeSpan.FromMilliseconds(400);
        try
        {
            var failed = false;
            try
            {
                await store.SaveAsync(TestData.Sample("lock-guard"), CancellationToken.None).ConfigureAwait(false);
            }
            catch (SaveException)
            {
                failed = true;
            }

            TestAssert.True(failed, "contended save must fail closed, never write unlocked");
            TestAssert.Equal(before, await File.ReadAllTextAsync(store.PrimaryPath).ConfigureAwait(false), "primary preserved");
        }
        finally
        {
            FileSaveStore.LockAcquireTimeout = previousTimeout;
        }

        // After the external holder releases, writes succeed again.
        externalHold.Dispose();
        await store.SaveAsync(TestData.Sample("lock-guard"), CancellationToken.None).ConfigureAwait(false);
        var reloaded = await store.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        TestAssert.Equal(100L, reloaded.Currencies.Credits, "recovered after lock release");
    }

    private static async Task ChildProcessesApply()
    {
        var savesDir = TestTemp.SavesDir(TestTemp.NewRoot());
        var seed = new FileSaveStore(savesDir);
        await seed.SaveAsync(ProfileSave.Default(), CancellationToken.None).ConfigureAwait(false);

        var dll = System.Reflection.Assembly.GetExecutingAssembly().Location;
        var ids = Enumerable.Range(0, 6).Select(i => $"run.child.{i}").ToList();

        // Six distinct settlements across six real OS processes, plus a racing
        // duplicate pair for one id: every id credits exactly once.
        var runs = ids.Select(id => RunChildAsync(dll, savesDir, id, 25)).ToList();
        runs.Add(RunChildAsync(dll, savesDir, "run.child.dup", 25));
        runs.Add(RunChildAsync(dll, savesDir, "run.child.dup", 25));
        var results = await Task.WhenAll(runs).ConfigureAwait(false);
        foreach (var r in results)
        {
            TestAssert.True(r.ExitCode == 0, $"child exit 0, got {r.ExitCode}: {r.Stderr}");
            TestAssert.True(r.Stdout.Contains("CHILD_RESULT"), "child reported outcome: " + r.Stdout);
        }

        var loaded = await seed.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        TestAssert.Equal(7 * 25L, loaded.Currencies.Credits, "each id credited once across processes");
        TestAssert.Equal(7, loaded.AppliedSettlementIds.Count, "all ids present once");
    }

    private sealed record ChildRun(int ExitCode, string Stdout, string Stderr);

    private static async Task<ChildRun> RunChildAsync(string assemblyPath, string savesDir, string id, long delta)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("dotnet", $"\"{assemblyPath}\" --child-apply \"{savesDir}\" \"{id}\" {delta}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var proc = System.Diagnostics.Process.Start(psi)
            ?? throw new Exception("Failed to start child process for " + id);
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new Exception("Child process timed out for " + id);
        }

        return new ChildRun(proc.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
    }
}
