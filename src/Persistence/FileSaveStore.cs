using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AbyssScav.Persistence;

/// <summary>
/// File-backed <see cref="ISaveStore"/>: atomic temp-flush-replace primary,
/// latest + 3 backup generations, explicit restore only, sequential migrations,
/// future-schema read-only, and locked read-modify-write for no-lost-update.
/// Engine-independent: plain files + System.Text.Json, no Godot dependency.
/// </summary>
public sealed class FileSaveStore : ISaveStore
{
    public const int BackupGenerations = 3;

    /// <summary>
    /// How long to wait for a cross-process sidecar lock held by another
    /// process before failing closed. Tests may lower this; production
    /// defaults to 15s. Never infinite: callers must fail, never write unlocked.
    /// </summary>
    public static TimeSpan LockAcquireTimeout { get; set; } = TimeSpan.FromSeconds(15);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _savesDir;
    private readonly string _primaryPath;
    private readonly string _backupsDir;
    private readonly string _backupStem;
    private readonly string _backupExt;
    private readonly IReadOnlyList<ISaveMigration> _migrations;

    public string PrimaryPath => _primaryPath;
    public string BackupsDir => _backupsDir;

    public FileSaveStore(string savesDir, string fileName = "profile_v1.json", IReadOnlyList<ISaveMigration>? migrations = null)
    {
        if (string.IsNullOrWhiteSpace(savesDir))
        {
            throw new ArgumentException("Saves directory is required.", nameof(savesDir));
        }

        if (string.IsNullOrWhiteSpace(fileName) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("Invalid save file name.", nameof(fileName));
        }

        _savesDir = Path.GetFullPath(savesDir);
        _primaryPath = Path.Combine(_savesDir, fileName);
        _backupsDir = Path.Combine(_savesDir, "backups");
        _backupStem = Path.GetFileNameWithoutExtension(fileName);
        _backupExt = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(_backupStem))
        {
            _backupStem = fileName;
            _backupExt = string.Empty;
        }

        _migrations = migrations ?? Array.Empty<ISaveMigration>();
    }

    public string BackupPathFor(int generation) =>
        Path.Combine(_backupsDir, $"{_backupStem}.backup.{generation}{_backupExt}");

    // ---- ISaveStore ----

    /// <summary>
    /// Load primary. Missing file -&gt; in-memory defaults (no write).
    /// Corrupt/truncated/malformed -&gt; <see cref="SaveCorruptException"/>, original untouched.
    /// Future schema -&gt; <see cref="SaveFutureVersionException"/>, never overwritten.
    /// Older schema -&gt; sequential in-memory migration, file untouched until SaveAsync.
    /// </summary>
    public async Task<ProfileSave> LoadAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_savesDir);
        Directory.CreateDirectory(_backupsDir);

        if (!File.Exists(_primaryPath))
        {
            return ProfileSave.Default();
        }

        string raw;
        try
        {
            raw = await File.ReadAllTextAsync(_primaryPath, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SaveIOException(SaveErrorCodes.Corrupt, "Save file is unreadable: " + ex.Message, ex);
        }

        ct.ThrowIfCancellationRequested();
        return ParseAndMigrate(raw);
    }

    /// <summary>
    /// Atomic save: validate supplied schema (must equal current — older inputs
    /// are rejected, never silently relabeled) then, under gate + cross-process
    /// lock, validate the on-disk primary (future/corrupt on disk refuses the
    /// overwrite so a newer or damaged profile is never clobbered), stage
    /// temp -&gt; flush(true) -&gt; rotate backups -&gt; replace primary.
    /// Cancellation or write failure leaves the primary byte-identical.
    /// </summary>
    public async Task SaveAsync(ProfileSave save, CancellationToken ct)
    {
        if (save is null)
        {
            throw new ArgumentNullException(nameof(save));
        }

        ct.ThrowIfCancellationRequested();

        if (save.SchemaVersion > ProfileSave.CurrentSchemaVersion)
        {
            throw new SaveFutureVersionException(save.SchemaVersion);
        }

        if (save.SchemaVersion < ProfileSave.CurrentSchemaVersion)
        {
            throw new SaveMigrationException(
                $"Supplied save uses older schema v{save.SchemaVersion}; migrate it explicitly instead of relabeling. Primary preserved.");
        }

        var normalized = save.Normalized() with { SchemaVersion = ProfileSave.CurrentSchemaVersion };
        var payload = JsonSerializer.Serialize(normalized, JsonOptions);

        var gate = GateFor(_primaryPath);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var lockHandle = await AcquireCrossProcessLockAsync(_primaryPath, SaveErrorCodes.Write, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            // Validate the existing on-disk primary BEFORE touching it: never
            // clobber a future-schema or corrupt profile with a blind overwrite.
            try
            {
                Directory.CreateDirectory(_savesDir);
                Directory.CreateDirectory(_backupsDir);
                if (File.Exists(_primaryPath))
                {
                    _ = await ReadUnderLockAsync(ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (SaveException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new SaveIOException(SaveErrorCodes.Write, "Failed to validate existing save: " + ex.Message, ex);
            }

            var tempPath = _primaryPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await WriteTempFileAsync(tempPath, payload, ct).ConfigureAwait(false);
                RotateBackups();
                File.Move(tempPath, _primaryPath, overwrite: true);
            }
            catch (OperationCanceledException)
            {
                TryDelete(tempPath);
                throw;
            }
            catch (SaveException)
            {
                TryDelete(tempPath);
                throw;
            }
            catch (Exception ex)
            {
                TryDelete(tempPath);
                throw new SaveIOException(SaveErrorCodes.Write, "Failed to write save: " + ex.Message, ex);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Explicit restore of backup generation 1..3 over the primary.
    /// Order: validate backup, stage + flush replacement temp first, then copy
    /// the pre-existing primary to a UNIQUE no-overwrite quarantine, and only
    /// then atomically replace. Cancel/failure before the replace leaves the
    /// original primary in place (never a missing primary / false new game),
    /// and repeated restores never overwrite earlier quarantines.
    /// </summary>
    public async Task<RestoreResult> RestoreBackupAsync(int generation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (generation is < 1 or > BackupGenerations)
        {
            throw new SaveIOException(
                SaveErrorCodes.BackupRestore,
                $"Backup generation must be 1..{BackupGenerations}.",
                new ArgumentOutOfRangeException(nameof(generation)));
        }

        var gate = GateFor(_primaryPath);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var lockHandle = await AcquireCrossProcessLockAsync(_primaryPath, SaveErrorCodes.BackupRestore, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            var backupPath = BackupPathFor(generation);
            if (!File.Exists(backupPath))
            {
                throw new SaveIOException(
                    SaveErrorCodes.BackupRestore,
                    $"Backup generation {generation} does not exist.",
                    new FileNotFoundException("Backup not found.", backupPath));
            }

            string raw;
            try
            {
                raw = await File.ReadAllTextAsync(backupPath, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new SaveIOException(SaveErrorCodes.BackupRestore, "Backup is unreadable: " + ex.Message, ex);
            }

            // Validate before staging anything: a corrupt/future backup must not
            // disturb the primary in any way.
            var restored = ParseAndMigrate(raw);
            ct.ThrowIfCancellationRequested();

            Directory.CreateDirectory(_savesDir);
            var normalized = restored.Normalized() with { SchemaVersion = ProfileSave.CurrentSchemaVersion };
            var stagedPayload = JsonSerializer.Serialize(normalized, JsonOptions);
            var tempPath = _primaryPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await WriteTempFileAsync(tempPath, stagedPayload, ct).ConfigureAwait(false);
            }
            catch
            {
                TryDelete(tempPath);
                throw;
            }

            ct.ThrowIfCancellationRequested();

            string? quarantined = null;
            if (File.Exists(_primaryPath))
            {
                try
                {
                    quarantined = UniqueQuarantinePath(_primaryPath);
                    File.Copy(_primaryPath, quarantined);
                }
                catch (OperationCanceledException)
                {
                    TryDelete(tempPath);
                    throw;
                }
                catch (Exception ex)
                {
                    TryDelete(tempPath);
                    throw new SaveIOException(SaveErrorCodes.BackupRestore, "Failed to quarantine current primary: " + ex.Message, ex);
                }
            }

            ct.ThrowIfCancellationRequested();

            try
            {
                File.Move(tempPath, _primaryPath, overwrite: true);
            }
            catch (OperationCanceledException)
            {
                TryDelete(tempPath);
                throw;
            }
            catch (Exception ex)
            {
                TryDelete(tempPath);
                throw new SaveIOException(SaveErrorCodes.BackupRestore, "Failed to install backup: " + ex.Message, ex);
            }

            return new RestoreResult(restored, generation, _primaryPath, quarantined);
        }
        finally
        {
            gate.Release();
        }
    }

    // ---- locked read-modify-write (no lost update) ----

    /// <summary>
    /// Locked read-modify-write cycle held under an in-process gate plus a
    /// cross-process lock file, so concurrent stores/processes cannot lose updates.
    /// The mutation runs on the validated on-disk profile; the result must carry
    /// the current schema (older results are rejected, never relabeled) and is
    /// re-normalized and saved atomically. Lock acquisition fails closed with a
    /// typed error — writes never proceed unlocked.
    /// </summary>
    public async Task<ProfileSave> UpdateAsync(Func<ProfileSave, ProfileSave> mutate, CancellationToken ct)
    {
        if (mutate is null)
        {
            throw new ArgumentNullException(nameof(mutate));
        }

        var gate = GateFor(_primaryPath);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var lockHandle = await AcquireCrossProcessLockAsync(_primaryPath, SaveErrorCodes.Write, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            ProfileSave current;
            try
            {
                Directory.CreateDirectory(_savesDir);
                Directory.CreateDirectory(_backupsDir);
                current = await ReadUnderLockAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (SaveException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new SaveIOException(SaveErrorCodes.Write, "Failed to stage save: " + ex.Message, ex);
            }

            var mutated = mutate(current) ?? throw new SaveIOException(
                SaveErrorCodes.Write, "Update mutation returned null.", new InvalidOperationException("Null profile."));
            if (mutated.SchemaVersion > ProfileSave.CurrentSchemaVersion)
            {
                throw new SaveFutureVersionException(mutated.SchemaVersion);
            }

            if (mutated.SchemaVersion < ProfileSave.CurrentSchemaVersion)
            {
                throw new SaveMigrationException(
                    $"Update produced older schema v{mutated.SchemaVersion}; migrate explicitly instead of relabeling. Primary preserved.");
            }

            var normalized = mutated.Normalized() with { SchemaVersion = ProfileSave.CurrentSchemaVersion };
            var payload = JsonSerializer.Serialize(normalized, JsonOptions);

            var tempPath = _primaryPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await WriteTempFileAsync(tempPath, payload, ct).ConfigureAwait(false);
                RotateBackups();
                File.Move(tempPath, _primaryPath, overwrite: true);
            }
            catch (OperationCanceledException)
            {
                TryDelete(tempPath);
                throw;
            }
            catch (SaveException)
            {
                TryDelete(tempPath);
                throw;
            }
            catch (Exception ex)
            {
                TryDelete(tempPath);
                throw new SaveIOException(SaveErrorCodes.Write, "Failed to write save: " + ex.Message, ex);
            }

            return normalized;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Idempotent host-confirmed settlement: a repeated settlement id applies once.
    /// Already-applied ids return without rewriting the file. Invalid payloads are
    /// rejected before any disk mutation (no game-trust beyond host confirmation).
    /// The check-and-append runs inside the same gate + cross-process lock as
    /// UpdateAsync, so concurrent duplicates still credit exactly once.
    /// The ledger is never truncated: at capacity, new awards fail closed while
    /// duplicate ids still return AlreadyApplied.
    /// </summary>
    public async Task<SettlementApplyResult> ApplySettlementAsync(SettlementPayload payload, CancellationToken ct)
    {
        if (payload is null)
        {
            throw new ArgumentNullException(nameof(payload));
        }

        if (!payload.TryValidate(out var reason))
        {
            throw new SaveIOException(SaveErrorCodes.Write, "Rejected settlement payload: " + reason,
                new InvalidOperationException(reason));
        }

        var id = payload.Id.Trim();
        var gate = GateFor(_primaryPath);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var lockHandle = await AcquireCrossProcessLockAsync(_primaryPath, SaveErrorCodes.Write, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            ProfileSave current;
            try
            {
                Directory.CreateDirectory(_savesDir);
                Directory.CreateDirectory(_backupsDir);
                current = await ReadUnderLockAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (SaveException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new SaveIOException(SaveErrorCodes.Write, "Failed to stage settlement: " + ex.Message, ex);
            }

            if (current.AppliedSettlementIds.Contains(id, StringComparer.Ordinal))
            {
                return new SettlementApplyResult(SettlementApplyOutcome.AlreadyApplied, current);
            }

            if (current.AppliedSettlementIds.Count >= ProfileSave.MaxAppliedSettlementIds)
            {
                throw new SaveIOException(
                    SaveErrorCodes.Write,
                    $"Settlement ledger is at capacity ({ProfileSave.MaxAppliedSettlementIds}); refusing to award {id} rather than drop history. Primary preserved.",
                    new InvalidOperationException("Settlement ledger full."));
            }

            var credits = Math.Clamp(current.Currencies.Credits + payload.CreditsDelta, 0, ProfileSave.MaxCurrency);
            var research = Math.Clamp(current.Currencies.ResearchData + payload.ResearchDataDelta, 0, ProfileSave.MaxCurrency);
            var shards = Math.Clamp(current.Currencies.AbyssShards + payload.ShardsDelta, 0, ProfileSave.MaxCurrency);

            var mutated = current with
            {
                Currencies = new SaveCurrencies(credits, research, shards),
                Unlocks = new SaveUnlocks(
                    current.Unlocks.Blueprints.Concat(payload.UnlockBlueprintIds.Select(s => s.Trim())).ToList(),
                    current.Unlocks.Frames.Concat(payload.UnlockFrameIds.Select(s => s.Trim())).ToList()),
                Codex = new SaveCodex(
                    current.Codex.DiscoveredIds.Concat(payload.DiscoverCodexIds.Select(s => s.Trim())).ToList()),
                Stats = current.Stats with
                {
                    RunsCompleted = Math.Clamp(current.Stats.RunsCompleted + payload.RunsCompletedDelta, 0, ProfileSave.MaxStatValue),
                    RunsFailed = Math.Clamp(current.Stats.RunsFailed + payload.RunsFailedDelta, 0, ProfileSave.MaxStatValue),
                    TotalCreditsEarned = payload.CreditsDelta > 0
                        ? Math.Clamp(current.Stats.TotalCreditsEarned + payload.CreditsDelta, 0, ProfileSave.MaxStatValue)
                        : current.Stats.TotalCreditsEarned,
                    TotalShardsEarned = payload.ShardsDelta > 0
                        ? Math.Clamp(current.Stats.TotalShardsEarned + payload.ShardsDelta, 0, ProfileSave.MaxStatValue)
                        : current.Stats.TotalShardsEarned,
                },
                AppliedSettlementIds = current.AppliedSettlementIds.Concat(new[] { id }).ToList(),
            };

            var normalized = mutated.Normalized() with { SchemaVersion = ProfileSave.CurrentSchemaVersion };
            var serialized = JsonSerializer.Serialize(normalized, JsonOptions);
            var tempPath = _primaryPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await WriteTempFileAsync(tempPath, serialized, ct).ConfigureAwait(false);
                RotateBackups();
                File.Move(tempPath, _primaryPath, overwrite: true);
            }
            catch (OperationCanceledException)
            {
                TryDelete(tempPath);
                throw;
            }
            catch (SaveException)
            {
                TryDelete(tempPath);
                throw;
            }
            catch (Exception ex)
            {
                TryDelete(tempPath);
                throw new SaveIOException(SaveErrorCodes.Write, "Failed to write save: " + ex.Message, ex);
            }

            return new SettlementApplyResult(SettlementApplyOutcome.Applied, normalized);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Idempotent upfront insurance-premium charge (docs/05 §4): deducts the
    /// premium from credits and records the charge id in the same ledger as
    /// settlements, so a retry can never double-charge. Fails closed on
    /// insufficient funds (no mutation, distinct outcome) — paid coverage is
    /// never granted free. Invalid payloads are rejected before any disk
    /// mutation. The check-and-append runs inside the same gate + cross-process
    /// lock as <see cref="UpdateAsync"/>, so concurrent duplicates still apply
    /// exactly once.
    /// </summary>
    public async Task<InsuranceChargeResult> ApplyInsuranceChargeAsync(InsuranceChargePayload payload, CancellationToken ct)
    {
        if (payload is null)
        {
            throw new ArgumentNullException(nameof(payload));
        }

        if (!payload.TryValidate(out var reason))
        {
            throw new SaveIOException(SaveErrorCodes.Write, "Rejected insurance charge: " + reason,
                new InvalidOperationException(reason));
        }

        var id = payload.Id.Trim();
        var gate = GateFor(_primaryPath);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var lockHandle = await AcquireCrossProcessLockAsync(_primaryPath, SaveErrorCodes.Write, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            ProfileSave current;
            try
            {
                Directory.CreateDirectory(_savesDir);
                Directory.CreateDirectory(_backupsDir);
                current = await ReadUnderLockAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (SaveException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new SaveIOException(SaveErrorCodes.Write, "Failed to stage insurance charge: " + ex.Message, ex);
            }

            if (current.AppliedSettlementIds.Contains(id, StringComparer.Ordinal))
            {
                return new InsuranceChargeResult(InsuranceChargeOutcome.AlreadyApplied, current);
            }

            if (current.AppliedSettlementIds.Count >= ProfileSave.MaxAppliedSettlementIds)
            {
                throw new SaveIOException(
                    SaveErrorCodes.Write,
                    $"Settlement ledger is at capacity ({ProfileSave.MaxAppliedSettlementIds}); refusing to charge {id} rather than drop history. Primary preserved.",
                    new InvalidOperationException("Settlement ledger full."));
            }

            if (current.Currencies.Credits < payload.PremiumCredits)
            {
                return new InsuranceChargeResult(InsuranceChargeOutcome.InsufficientFunds, current);
            }

            var credits = Math.Clamp(current.Currencies.Credits - payload.PremiumCredits, 0, ProfileSave.MaxCurrency);
            var mutated = current with
            {
                Currencies = new SaveCurrencies(credits, current.Currencies.ResearchData, current.Currencies.AbyssShards),
                AppliedSettlementIds = current.AppliedSettlementIds.Concat(new[] { id }).ToList(),
            };

            var normalized = mutated.Normalized() with { SchemaVersion = ProfileSave.CurrentSchemaVersion };
            var serialized = JsonSerializer.Serialize(normalized, JsonOptions);
            var tempPath = _primaryPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await WriteTempFileAsync(tempPath, serialized, ct).ConfigureAwait(false);
                RotateBackups();
                File.Move(tempPath, _primaryPath, overwrite: true);
            }
            catch (OperationCanceledException)
            {
                TryDelete(tempPath);
                throw;
            }
            catch (SaveException)
            {
                TryDelete(tempPath);
                throw;
            }
            catch (Exception ex)
            {
                TryDelete(tempPath);
                throw new SaveIOException(SaveErrorCodes.Write, "Failed to write insurance charge: " + ex.Message, ex);
            }

            return new InsuranceChargeResult(InsuranceChargeOutcome.Applied, normalized);
        }
        finally
        {
            gate.Release();
        }
    }

    // ---- internals ----

    private ProfileSave ParseAndMigrate(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new SaveCorruptException("Save file is empty or whitespace (possible truncation). Original preserved.");
        }

        JsonObject root;
        try
        {
            var node = JsonNode.Parse(raw);
            root = node as JsonObject ?? throw new SaveCorruptException("Save root must be a JSON object. Original preserved.");
        }
        catch (SaveException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SaveCorruptException("Save file is not valid JSON (possible truncation). Original preserved: " + ex.Message, ex);
        }

        int foundVersion;
        try
        {
            var versionNode = root["schema_version"];
            if (versionNode is null || !versionNode.AsValue().TryGetValue<int>(out foundVersion))
            {
                throw new SaveCorruptException("Save is missing integer schema_version. Original preserved.");
            }
        }
        catch (SaveException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SaveCorruptException("Save schema_version is malformed (must be an integer). Original preserved: " + ex.Message, ex);
        }

        if (foundVersion > ProfileSave.CurrentSchemaVersion)
        {
            throw new SaveFutureVersionException(foundVersion);
        }

        JsonObject effective = root;
        if (foundVersion < ProfileSave.CurrentSchemaVersion)
        {
            effective = SaveMigrationChain.MigrateToCurrent(root, foundVersion, _migrations);
        }

        RequireShape(effective);

        try
        {
            var save = effective.Deserialize<ProfileSave>(JsonOptions);
            if (save is null)
            {
                throw new SaveCorruptException("Save deserialized to null. Original preserved.");
            }

            return save.Normalized() with { SchemaVersion = ProfileSave.CurrentSchemaVersion };
        }
        catch (SaveException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SaveCorruptException("Save shape is invalid. Original preserved: " + ex.Message, ex);
        }
    }

    /// <summary>
    /// Reject malformed profiles instead of silently defaulting missing
    /// sections: every required section and field must be present with the
    /// right JSON kind, otherwise progression could be silently reset.
    /// </summary>
    private static void RequireShape(JsonObject root)
    {
        JsonObject Section(string name)
        {
            var node = FindKey(root, name);
            if (node is JsonObject obj)
            {
                return obj;
            }

            throw new SaveCorruptException($"Save is missing required section '{name}'. Original preserved.");
        }

        static void RequireNumber(JsonObject obj, string section, string field)
        {
            var node = FindKey(obj, field);
            if (node is null || node.GetValueKind() != JsonValueKind.Number)
            {
                throw new SaveCorruptException($"Save section '{section}' is missing numeric field '{field}'. Original preserved.");
            }
        }

        static void RequireBool(JsonObject obj, string section, string field)
        {
            var kind = FindKey(obj, field)?.GetValueKind();
            if (kind is not JsonValueKind.True and not JsonValueKind.False)
            {
                throw new SaveCorruptException($"Save section '{section}' is missing boolean field '{field}'. Original preserved.");
            }
        }

        static void RequireArray(JsonObject obj, string section, string field)
        {
            if (FindKey(obj, field) is not JsonArray)
            {
                throw new SaveCorruptException($"Save section '{section}' is missing array field '{field}'. Original preserved.");
            }
        }

        var currencies = Section("currencies");
        RequireNumber(currencies, "currencies", "credits");
        RequireNumber(currencies, "currencies", "research_data");
        RequireNumber(currencies, "currencies", "abyss_shards");

        var unlocks = Section("unlocks");
        RequireArray(unlocks, "unlocks", "blueprints");
        RequireArray(unlocks, "unlocks", "frames");

        var tutorial = Section("tutorial");
        RequireBool(tutorial, "tutorial", "completed");
        RequireArray(tutorial, "tutorial", "completed_steps");

        var codex = Section("codex");
        RequireArray(codex, "codex", "discovered_ids");

        var stats = Section("stats");
        RequireNumber(stats, "stats", "runs_completed");
        RequireNumber(stats, "stats", "runs_failed");
        RequireNumber(stats, "stats", "total_credits_earned");
        RequireNumber(stats, "stats", "total_shards_earned");

        if (FindKey(root, "applied_settlement_ids") is not JsonArray)
        {
            throw new SaveCorruptException("Save is missing required array 'applied_settlement_ids'. Original preserved.");
        }
    }

    private static JsonNode? FindKey(JsonObject obj, string name)
    {
        foreach (var kv in obj)
        {
            if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return kv.Value;
            }
        }

        return null;
    }

    private async Task<ProfileSave> ReadUnderLockAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(_primaryPath))
        {
            return ProfileSave.Default();
        }

        string raw;
        try
        {
            raw = await File.ReadAllTextAsync(_primaryPath, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SaveIOException(SaveErrorCodes.Corrupt, "Save file is unreadable: " + ex.Message, ex);
        }

        return ParseAndMigrate(raw);
    }

    private void RotateBackups()
    {
        try
        {
            if (!File.Exists(_primaryPath))
            {
                return;
            }

            Directory.CreateDirectory(_backupsDir);

            var oldest = BackupPathFor(BackupGenerations);
            TryDelete(oldest);

            for (var g = BackupGenerations - 1; g >= 1; g--)
            {
                var src = BackupPathFor(g);
                var dst = BackupPathFor(g + 1);
                if (File.Exists(src))
                {
                    File.Move(src, dst, overwrite: true);
                }
            }

            File.Copy(_primaryPath, BackupPathFor(1), overwrite: true);
        }
        catch (Exception ex)
        {
            throw new SaveIOException(SaveErrorCodes.Write, "Failed to rotate backups: " + ex.Message, ex);
        }
    }

    /// <summary>
    /// Unique quarantine path that never overwrites an earlier quarantine:
    /// timestamp base, then .1/.2/... suffixes until free.
    /// </summary>
    private static string UniqueQuarantinePath(string primaryPath)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var candidate = primaryPath + ".corrupt." + stamp;
        var n = 0;
        while (File.Exists(candidate) || Directory.Exists(candidate))
        {
            n++;
            candidate = primaryPath + ".corrupt." + stamp + "." + n;
        }

        return candidate;
    }

    private static async Task WriteTempFileAsync(string tempPath, string payload, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(payload);
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(true);
            }
        }
        catch (OperationCanceledException)
        {
            TryDelete(tempPath);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(tempPath);
            throw new SaveIOException(SaveErrorCodes.Write, "Failed to stage save temp file: " + ex.Message, ex);
        }
    }

    private static SemaphoreSlim GateFor(string primaryPath) =>
        Gates.GetOrAdd(Path.GetFullPath(primaryPath), _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// Acquire the cross-process sidecar lock, waiting (cancellable) while
    /// another process holds it. Fails closed with a typed error on timeout —
    /// callers must never proceed to write without the lock.
    /// </summary>
    private static async Task<FileStream> AcquireCrossProcessLockAsync(string primaryPath, string errorCode, CancellationToken ct)
    {
        var lockPath = primaryPath + ".lock";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(lockPath) ?? ".");
        }
        catch (Exception ex)
        {
            throw new SaveIOException(errorCode, "Failed to prepare save lock: " + ex.Message, ex);
        }

        var deadline = DateTime.UtcNow + LockAcquireTimeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    throw new SaveIOException(
                        errorCode,
                        "Timed out waiting for the save lock held by another process. No writes were performed.",
                        ex);
                }

                try
                {
                    await Task.Delay(25, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort cleanup only.
        }
    }
}
