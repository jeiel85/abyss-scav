using System.Text.Json;

namespace AbyssScav.Foundation;

public enum ConfigLoadStatus
{
    Ok,
    CreatedDefaults,
    RecoveredFromCorruption,
    RecoveredInMemoryOnly,
    FutureVersionReadOnly,
    IoErrorInMemoryDefaults,
}

public sealed record ConfigLoadResult(
    AppSettings Settings,
    ConfigLoadStatus Status,
    string? Notice,
    string? CorruptBackupPath,
    int FoundVersion);

/// <summary>
/// Robust config loader: missing -&gt; defaults; corrupt -&gt; quarantine + defaults;
/// future schema -&gt; never overwrite, in-memory defaults + read-only notice.
/// Schema is inspected via JsonDocument BEFORE full deserialization so a future
/// file with changed fields is never mistaken for corruption.
/// Notices carry file names and error kinds only: never full paths, usernames,
/// or raw exception text (those may leak profile paths into logs/UI).
/// </summary>
public static class ConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static ConfigLoadResult Load(AppPaths paths)
    {
        try
        {
            paths.EnsureDirectories();
        }
        catch
        {
            return InMemoryDefaults(
                ConfigLoadStatus.IoErrorInMemoryDefaults,
                "Settings storage is unavailable (I/O error). Running on in-memory defaults; nothing was written.");
        }

        if (!File.Exists(paths.ConfigPath))
        {
            var defaults = AppSettings.Default();
            try
            {
                AtomicFileStore.WriteAllTextAtomic(paths.ConfigPath, Serialize(defaults));
            }
            catch
            {
                return InMemoryDefaults(
                    ConfigLoadStatus.IoErrorInMemoryDefaults,
                    "Default settings could not be written (I/O error). Running on in-memory defaults.");
            }

            return new ConfigLoadResult(defaults, ConfigLoadStatus.CreatedDefaults, "Created default settings.", null, 0);
        }

        string raw;
        try
        {
            raw = File.ReadAllText(paths.ConfigPath);
        }
        catch
        {
            // I/O errors are not JSON corruption: leave the original file untouched.
            return InMemoryDefaults(
                ConfigLoadStatus.IoErrorInMemoryDefaults,
                "Settings file could not be read (I/O error). Running on in-memory defaults; the original file was left untouched.");
        }

        int foundVersion;
        bool hasVersion;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            hasVersion = TryReadSchemaVersion(doc, out foundVersion);
        }
        catch (JsonException)
        {
            return RecoverCorrupt(paths);
        }

        if (!hasVersion)
        {
            return RecoverCorrupt(paths);
        }

        if (foundVersion > AppSettings.CurrentSchemaVersion)
        {
            // Future schema: write forbidden. Boot continues with in-memory defaults.
            var defaults = AppSettings.Default();
            return new ConfigLoadResult(
                defaults,
                ConfigLoadStatus.FutureVersionReadOnly,
                $"Settings schema v{foundVersion} is newer than supported v{AppSettings.CurrentSchemaVersion}. Using defaults without overwriting your file.",
                null,
                foundVersion);
        }

        AppSettings? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<AppSettings>(raw, JsonOptions);
        }
        catch
        {
            return RecoverCorrupt(paths);
        }

        if (parsed is null)
        {
            return RecoverCorrupt(paths);
        }

        if (parsed.SchemaVersion < AppSettings.CurrentSchemaVersion)
        {
            // v1 scope: only one schema so far; normalize and keep file (no destructive migration yet).
            var normalized = parsed.Normalized() with { SchemaVersion = AppSettings.CurrentSchemaVersion };
            return new ConfigLoadResult(normalized, ConfigLoadStatus.Ok, "Migrated older settings to current schema in memory.", null, parsed.SchemaVersion);
        }

        return new ConfigLoadResult(parsed.Normalized(), ConfigLoadStatus.Ok, null, null, parsed.SchemaVersion);
    }

    /// <summary>
    /// Persist settings. Refuses when the supplied settings carry a future schema,
    /// when the on-disk file carries a future schema, or when running in safe mode
    /// without an explicit user-initiated save (never overwrite prefs merely from safe mode).
    /// </summary>
    public static bool TrySave(AppPaths paths, AppSettings settings, bool isSafeMode, bool userInitiated, out string error)
    {
        if (settings.SchemaVersion > AppSettings.CurrentSchemaVersion)
        {
            error = "Refused to write future-schema settings.";
            return false;
        }

        if (isSafeMode && !userInitiated)
        {
            error = "Refused to overwrite user preferences from safe mode without explicit user action.";
            return false;
        }

        if (IsOnDiskFutureVersion(paths))
        {
            error = "On-disk settings are from a newer version. Refused to overwrite them.";
            return false;
        }

        try
        {
            AtomicFileStore.WriteAllTextAtomic(paths.ConfigPath, Serialize(settings.Normalized()));
            error = string.Empty;
            return true;
        }
        catch
        {
            error = "Failed to save settings (I/O error). The previous file was left untouched.";
            return false;
        }
    }

    public static string Serialize(AppSettings settings) =>
        JsonSerializer.Serialize(settings, JsonOptions);

    private static bool TryReadSchemaVersion(JsonDocument doc, out int version)
    {
        version = 0;
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in doc.RootElement.EnumerateObject())
        {
            if (string.Equals(property.Name, "schema_version", StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out version);
            }
        }

        return false;
    }

    private static bool IsOnDiskFutureVersion(AppPaths paths)
    {
        try
        {
            if (!File.Exists(paths.ConfigPath))
            {
                return false;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(paths.ConfigPath));
            return TryReadSchemaVersion(doc, out var version) && version > AppSettings.CurrentSchemaVersion;
        }
        catch
        {
            // Unreadable or corrupt on disk: Load owns recovery; do not block a save here.
            return false;
        }
    }

    private static ConfigLoadResult InMemoryDefaults(ConfigLoadStatus status, string notice) =>
        new(AppSettings.Default(), status, notice, null, 0);

    private static ConfigLoadResult RecoverCorrupt(AppPaths paths)
    {
        string backupName;
        try
        {
            backupName = Quarantine(paths);
        }
        catch
        {
            // Quarantine failed: preserve the original, run in-memory, say so explicitly.
            return InMemoryDefaults(
                ConfigLoadStatus.RecoveredInMemoryOnly,
                "Settings file is corrupt, but it could not be quarantined. Running on in-memory defaults; the original file was left untouched.");
        }

        var defaults = AppSettings.Default();
        try
        {
            AtomicFileStore.WriteAllTextAtomic(paths.ConfigPath, Serialize(defaults));
        }
        catch
        {
            return new ConfigLoadResult(
                defaults,
                ConfigLoadStatus.RecoveredInMemoryOnly,
                $"Settings file was corrupt and was quarantined as {backupName}, but defaults could not be written. Running on in-memory defaults.",
                backupName,
                0);
        }

        return new ConfigLoadResult(
            defaults,
            ConfigLoadStatus.RecoveredFromCorruption,
            $"Settings file was corrupt and has been reset to defaults. The original was quarantined as {backupName}.",
            backupName,
            0);
    }

    /// <summary>
    /// Moves the corrupt file aside under a unique no-overwrite name. Returns the
    /// file name only (never a full path). Throws on failure; the caller must then
    /// preserve the original and fall back to in-memory defaults.
    /// </summary>
    private static string Quarantine(AppPaths paths)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        for (var i = 0; i < 100; i++)
        {
            var name = i == 0 ? $"settings.cfg.corrupt.{stamp}" : $"settings.cfg.corrupt.{stamp}_{i + 1}";
            var dest = Path.Combine(paths.ConfigDir, name);
            try
            {
                File.Move(paths.ConfigPath, dest, overwrite: false);
                return name;
            }
            catch (IOException) when (File.Exists(dest))
            {
                continue;
            }
        }

        throw new IOException("Could not quarantine settings file.");
    }
}
