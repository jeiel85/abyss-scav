using System.Text.Json.Nodes;

namespace AbyssScav.Persistence;

/// <summary>
/// Single sequential migration step vN -&gt; v(N+1).
/// Intermediate steps must never be skipped (docs/09 §4).
/// </summary>
public interface ISaveMigration
{
    int FromVersion { get; }
    int ToVersion { get; }
    JsonObject Migrate(JsonObject source);
}

/// <summary>
/// Ordered migration runner. The production chain for schema v1 is empty
/// (no older real schemas exist); tests register explicit synthetic fixtures.
/// </summary>
public static class SaveMigrationChain
{
    public static JsonObject MigrateToCurrent(
        JsonObject source,
        int foundVersion,
        IReadOnlyList<ISaveMigration> chain)
    {
        var current = ProfileSave.CurrentSchemaVersion;
        var ordered = (chain ?? Array.Empty<ISaveMigration>())
            .OrderBy(m => m.FromVersion)
            .ThenBy(m => m.ToVersion)
            .ToList();

        var node = source;
        for (var v = foundVersion; v < current; v++)
        {
            var step = ordered.FirstOrDefault(m => m.FromVersion == v && m.ToVersion == v + 1);
            if (step is null)
            {
                throw new SaveMigrationException(
                    $"Missing sequential migration v{v} -> v{v + 1} (found v{foundVersion}, current v{current}). Original preserved.");
            }

            JsonObject next;
            try
            {
                next = step.Migrate(node);
            }
            catch (SaveException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new SaveMigrationException($"Migration v{v} -> v{v + 1} failed: {ex.Message}. Original preserved.", ex);
            }

            if (next is null)
            {
                throw new SaveMigrationException($"Migration v{v} -> v{v + 1} returned null. Original preserved.");
            }

            var outVersion = -1;
            try
            {
                outVersion = next["schema_version"]?.GetValue<int>() ?? -1;
            }
            catch (Exception ex)
            {
                throw new SaveMigrationException(
                    $"Migration v{v} -> v{v + 1} produced an unreadable schema_version. Original preserved: {ex.Message}", ex);
            }

            if (outVersion != v + 1)
            {
                throw new SaveMigrationException(
                    $"Migration v{v} -> v{v + 1} produced schema_version {outVersion}. Original preserved.");
            }

            node = next;
        }

        return node;
    }
}
