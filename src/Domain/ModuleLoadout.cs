namespace AbyssScav.Domain;

/// <summary>
/// Centralized field-module loadout policy (single-player only).
/// <para>
/// Exactly 6 of the 24 catalog modules have real, implemented in-run effects;
/// the remaining 18 are catalog data only (purchase/equip unavailable until
/// their effects exist — owned copies are retained, never deleted or charged).
/// One slot per category (Sonar / Engine / Hull / Utility): at most one
/// equipped module per category. Blueprints are permanent unlocks: equipping
/// never consumes them, and the domain never mutates the profile.
/// </para>
/// </summary>
public static class ModuleLoadout
{
    /// <summary>Shorter pulse reach, much less pulse threat, slightly faster cooldown.</summary>
    public const string WhisperPulse = "module.sonar.whisper_pulse";

    /// <summary>Wider passive (no-ping) detection range. No active-sonar effect.</summary>
    public const string PassiveBooster = "module.sonar.passive_booster";

    /// <summary>Quieter engine with less thrust. Physical + acoustic trade-off.</summary>
    public const string QuietProp = "module.engine.quiet_prop";

    /// <summary>Extra max hull. No pressure-rating change.</summary>
    public const string ReinforcedRib = "module.hull.reinforced_rib";

    /// <summary>Higher hull pressure rating. No max-hull change.</summary>
    public const string PressureSkin = "module.hull.pressure_skin";

    /// <summary>Longer manipulator (salvage) reach. No cargo change.</summary>
    public const string SalvageMagnet = "module.utility.salvage_magnet";

    /// <summary>Every module ID with an implemented in-run effect (exactly 6).</summary>
    public static readonly IReadOnlySet<string> SupportedIds = new HashSet<string>(StringComparer.Ordinal)
    {
        WhisperPulse, PassiveBooster, QuietProp, ReinforcedRib, PressureSkin, SalvageMagnet,
    };

    /// <summary>Resolved numeric effects of a validated loadout. All multipliers default to neutral.</summary>
    public sealed record LoadoutEffects(
        float PulseRangeMult,
        float PulseThreatMult,
        float PulseCooldownMult,
        float PassiveRangeMult,
        float EngineNoiseMult,
        float EngineThrustMult,
        float MaxHullBonus,
        float HullRatingBonus,
        float SalvageRangeMeters);

    private static readonly LoadoutEffects Neutral = new(
        PulseRangeMult: 1f,
        PulseThreatMult: 1f,
        PulseCooldownMult: 1f,
        PassiveRangeMult: 1f,
        EngineNoiseMult: 1f,
        EngineThrustMult: 1f,
        MaxHullBonus: 0f,
        HullRatingBonus: 0f,
        SalvageRangeMeters: DomainConstants.InteractRangeMeters);

    /// <summary>True when the module has an implemented in-run effect.</summary>
    public static bool IsSupported(string moduleId) =>
        !string.IsNullOrEmpty(moduleId) && SupportedIds.Contains(moduleId);

    /// <summary>
    /// Loadout slot (catalog category) for a module ID. Unknown IDs report "Unknown".
    /// </summary>
    public static string CategoryOf(string moduleId, ContentCatalog? catalog)
    {
        if (catalog is not null && catalog.Modules.TryGetValue(moduleId, out var def))
            return def.Category;
        return "Unknown";
    }

    /// <summary>
    /// Honest player-facing description of the module's TRUE in-run effect.
    /// Unsupported modules say so explicitly; no effect is ever invented.
    /// </summary>
    public static string Describe(string moduleId)
    {
        return moduleId switch
        {
            WhisperPulse => "Whisper pulse: pulse range x0.7 (shorter reach), pulse threat x0.4, cooldown 8.0s -> 7.5s. Quieter pings, smaller picture.",
            PassiveBooster => "Passive booster: passive sonar range x1.5. No active-pulse effect.",
            QuietProp => "Quiet prop: engine noise x0.6, thrust x0.85. Quieter, slower; quiet-running cap still applies.",
            ReinforcedRib => "Reinforced rib: max hull +150. No pressure-rating change.",
            PressureSkin => "Pressure skin: hull pressure rating +20. No max-hull change.",
            SalvageMagnet => "Salvage magnet: salvage reach 15m -> 24m. No cargo change.",
            _ => "No implemented in-run effect: unavailable for new purchase/equip (owned copies retained).",
        };
    }

    /// <summary>
    /// Deterministic catalog-hash fragment for a module: implemented effect key or
    /// "none". Part of <c>ContentCatalog</c> hashing so effect changes invalidate manifests.
    /// </summary>
    public static string EffectKey(string moduleId)
    {
        return moduleId switch
        {
            WhisperPulse => "fx=pulse_range_x0.7_threat_x0.4_cd_x0.9375",
            PassiveBooster => "fx=passive_range_x1.5",
            QuietProp => "fx=noise_x0.6_thrust_x0.85",
            ReinforcedRib => "fx=maxhull_+150",
            PressureSkin => "fx=hullrating_+20",
            SalvageMagnet => "fx=salvage_24m",
            _ => "fx=none",
        };
    }

    /// <summary>
    /// Research cost from the catalog, or -1 when the module is unknown or has no
    /// implemented effect (not purchasable). Centralizes the "supported ⇒
    /// purchasable" rule so UI never sells a no-effect blueprint.
    /// </summary>
    public static int PurchasableCostOf(ContentCatalog catalog, string moduleId)
    {
        if (catalog is null || !IsSupported(moduleId)) return -1;
        return catalog.Modules.TryGetValue(moduleId, out var def) ? def.ResearchCost : -1;
    }

    /// <summary>
    /// Validates a loadout: rejects unknown IDs, duplicates, category collisions
    /// (one slot per category), and supported-but-unimplemented equips. When
    /// <paramref name="ownedBlueprints"/> is non-null, unowned IDs are rejected
    /// too (UI + run-start gate); null skips the ownership check (tests, dry runs).
    /// Returns false with every reason; never throws on hostile input.
    /// </summary>
    public static bool TryValidate(
        IEnumerable<string>? moduleIds,
        ContentCatalog catalog,
        IReadOnlyCollection<string>? ownedBlueprints,
        out IReadOnlyList<string> errors)
    {
        var problems = new List<string>();
        if (catalog is null)
        {
            problems.Add("CONTENT-207 loadout check needs a content catalog.");
            errors = problems;
            return false;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var byCategory = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in moduleIds ?? Array.Empty<string>())
        {
            if (string.IsNullOrEmpty(id) || !catalog.Modules.TryGetValue(id, out var def))
            {
                problems.Add($"CONTENT-207 unknown module '{id}'.");
                continue;
            }
            if (!seen.Add(id))
            {
                problems.Add($"CONTENT-207 duplicate module '{id}' (one slot per category).");
                continue;
            }
            if (!IsSupported(id))
            {
                problems.Add($"CONTENT-207 module '{id}' has no implemented in-run effect and cannot be equipped.");
                continue;
            }
            if (!byCategory.Add(def.Category))
            {
                problems.Add($"CONTENT-207 category '{def.Category}' accepts one module (slot already taken).");
                continue;
            }
            if (ownedBlueprints is not null && !ownedBlueprints.Contains(id))
            {
                problems.Add($"CONTENT-207 module '{id}' is not unlocked on this profile.");
            }
        }
        errors = problems;
        return problems.Count == 0;
    }

    /// <summary>
    /// Resolves validated module IDs to numeric effects. Call
    /// <see cref="TryValidate"/> first; unknown/unsupported IDs are ignored here
    /// (defense in depth, never a fake effect).
    /// </summary>
    public static LoadoutEffects Resolve(IEnumerable<string>? moduleIds)
    {
        var fx = Neutral;
        foreach (var id in moduleIds ?? Array.Empty<string>())
        {
            fx = id switch
            {
                WhisperPulse => fx with { PulseRangeMult = 0.7f, PulseThreatMult = 0.4f, PulseCooldownMult = 7.5f / 8f },
                PassiveBooster => fx with { PassiveRangeMult = 1.5f },
                QuietProp => fx with { EngineNoiseMult = 0.6f, EngineThrustMult = 0.85f },
                ReinforcedRib => fx with { MaxHullBonus = 150f },
                PressureSkin => fx with { HullRatingBonus = 20f },
                SalvageMagnet => fx with { SalvageRangeMeters = 24f },
                _ => fx,
            };
        }
        return fx;
    }
}
