using System.Text.Json.Serialization;

namespace AbyssScav.Persistence;

/// <summary>
/// Progression currencies (docs/05 §1). All values are bounded and
/// clamped by <see cref="ProfileSave.Normalized"/> so hand-edited files stay safe.
/// </summary>
public sealed record SaveCurrencies(
    [property: JsonPropertyName("credits")] long Credits,
    [property: JsonPropertyName("research_data")] long ResearchData,
    [property: JsonPropertyName("abyss_shards")] long AbyssShards);

/// <summary>Blueprint / frame unlocks (docs/09 §2).</summary>
public sealed record SaveUnlocks(
    [property: JsonPropertyName("blueprints")] List<string> Blueprints,
    [property: JsonPropertyName("frames")] List<string> Frames);

/// <summary>Tutorial state (docs/09 §2).</summary>
public sealed record SaveTutorial(
    [property: JsonPropertyName("completed")] bool Completed,
    [property: JsonPropertyName("completed_steps")] List<string> CompletedSteps);

/// <summary>Codex discoveries (docs/09 §2).</summary>
public sealed record SaveCodex(
    [property: JsonPropertyName("discovered_ids")] List<string> DiscoveredIds);

/// <summary>Statistics (docs/09 §2).</summary>
public sealed record SaveStats(
    [property: JsonPropertyName("runs_completed")] long RunsCompleted,
    [property: JsonPropertyName("runs_failed")] long RunsFailed,
    [property: JsonPropertyName("total_credits_earned")] long TotalCreditsEarned,
    [property: JsonPropertyName("total_shards_earned")] long TotalShardsEarned);

/// <summary>
/// Local-first profile save (docs/09 §1-§2). JSON shape uses snake_case keys.
/// Current schema is v1; older real schemas do not exist yet, so the
/// migration chain ships empty and tests register explicit synthetic fixtures.
/// </summary>
public sealed record ProfileSave(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("currencies")] SaveCurrencies Currencies,
    [property: JsonPropertyName("unlocks")] SaveUnlocks Unlocks,
    [property: JsonPropertyName("tutorial")] SaveTutorial Tutorial,
    [property: JsonPropertyName("codex")] SaveCodex Codex,
    [property: JsonPropertyName("stats")] SaveStats Stats,
    [property: JsonPropertyName("applied_settlement_ids")] List<string> AppliedSettlementIds)
{
    public const int CurrentSchemaVersion = 1;

    public const long MaxCurrency = 999_999_999L;
    public const long MaxStatValue = 999_999_999L;
    public const int MaxIdsPerList = 512;
    public const int MaxIdLength = 128;

    /// <summary>
    /// Append-only settlement ledger bound. History is never truncated:
    /// new awards past this bound fail closed instead of dropping ids.
    /// </summary>
    public const int MaxAppliedSettlementIds = 10000;

    public static ProfileSave Default() => new(
        CurrentSchemaVersion,
        new SaveCurrencies(0, 0, 0),
        new SaveUnlocks(new List<string>(), new List<string>()),
        new SaveTutorial(false, new List<string>()),
        new SaveCodex(new List<string>()),
        new SaveStats(0, 0, 0, 0),
        new List<string>());

    /// <summary>
    /// Clamp numerics to safe bounded ranges and sanitize id lists:
    /// keep only lower snake/dot-namespace ids and dedupe.
    /// Catalog lists (unlocks/codex/tutorial) are truncated to a safe bound;
    /// the append-only settlement ledger is NEVER truncated so duplicates stay
    /// detectable (new awards past capacity fail closed in ApplySettlementAsync).
    /// Never throws on hostile input.
    /// </summary>
    public ProfileSave Normalized()
    {
        return this with
        {
            Currencies = new SaveCurrencies(
                Math.Clamp(Currencies.Credits, 0, MaxCurrency),
                Math.Clamp(Currencies.ResearchData, 0, MaxCurrency),
                Math.Clamp(Currencies.AbyssShards, 0, MaxCurrency)),
            Unlocks = new SaveUnlocks(
                SanitizeIds(Unlocks.Blueprints),
                SanitizeIds(Unlocks.Frames)),
            Tutorial = new SaveTutorial(
                Tutorial.Completed,
                SanitizeIds(Tutorial.CompletedSteps)),
            Codex = new SaveCodex(SanitizeIds(Codex.DiscoveredIds)),
            Stats = new SaveStats(
                Math.Clamp(Stats.RunsCompleted, 0, MaxStatValue),
                Math.Clamp(Stats.RunsFailed, 0, MaxStatValue),
                Math.Clamp(Stats.TotalCreditsEarned, 0, MaxStatValue),
                Math.Clamp(Stats.TotalShardsEarned, 0, MaxStatValue)),
            AppliedSettlementIds = SanitizeIds(AppliedSettlementIds, preserveOrder: true, maxCount: int.MaxValue),
        };
    }

    internal static List<string> SanitizeIds(IEnumerable<string>? ids, bool preserveOrder = false, int maxCount = MaxIdsPerList)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var outList = new List<string>();
        if (ids is null)
        {
            return outList;
        }

        foreach (var raw in ids)
        {
            if (raw is null)
            {
                continue;
            }

            var id = raw.Trim();
            if (id.Length == 0 || id.Length > MaxIdLength || !SaveIds.IsValid(id))
            {
                continue;
            }

            if (!seen.Add(id))
            {
                continue;
            }

            outList.Add(id);
            if (outList.Count >= maxCount)
            {
                break;
            }
        }

        if (!preserveOrder)
        {
            outList.Sort(StringComparer.Ordinal);
        }

        return outList;
    }
}

/// <summary>Stable-id character rules (docs/16 §10): lower snake/dot namespace.</summary>
public static class SaveIds
{
    public static bool IsValid(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > ProfileSave.MaxIdLength)
        {
            return false;
        }

        foreach (var c in id)
        {
            var ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '.' || c == '-';
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }
}
