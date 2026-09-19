namespace AbyssScav.Domain;

/// <summary>
/// Centralized field-module loadout policy (single-player only).
/// <para>
/// Exactly 22 of the 24 catalog modules have real, implemented in-run effects;
/// the remaining 2 (heat sink, vector fin) are catalog data only (purchase/equip
/// unavailable until their effects exist — owned copies are retained, never
/// deleted or charged). One slot per category (Sonar / Engine / Hull / Utility):
/// at most one equipped module per category. Blueprints are permanent unlocks:
/// equipping never consumes them, and the domain never mutates the profile.
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

    /// <summary>One emergency buoy charge; fired before failure, retention +0.3 (cap 0.9).</summary>
    public const string EmergencyBuoy = "module.utility.emergency_buoy";

    /// <summary>Longer pulse reach, louder pulse threat, faster cooldown.</summary>
    public const string WideArray = "module.sonar.wide_array";

    /// <summary>Sharper pulse returns (+0.1 confidence per ping), faster cooldown.</summary>
    public const string FocusBeam = "module.sonar.focus_beam";

    /// <summary>Biological contacts read better (+0.15 confidence), slower cooldown.</summary>
    public const string ResonanceClassifier = "module.sonar.resonance_classifier";

    /// <summary>False contacts need higher threat (ghost threshold 60 -> 80).</summary>
    public const string GhostFilter = "module.sonar.ghost_filter";

    /// <summary>More thrust, more engine noise.</summary>
    public const string OverdriveThruster = "module.engine.overdrive_thruster";

    /// <summary>Quieter engine with no thrust loss.</summary>
    public const string CavitationDampener = "module.engine.cavitation_dampener";

    /// <summary>Thrust x1.5 while hull is below 30% (escape burst).</summary>
    public const string EmergencyReverse = "module.engine.emergency_reverse";

    /// <summary>Higher hull pressure rating and extra max hull.</summary>
    public const string AbyssPlating = "module.hull.abyss_plating";

    /// <summary>Breach fill rate x0.5 (flooding spreads half as fast).</summary>
    public const string FloodBulkhead = "module.hull.flood_bulkhead";

    /// <summary>Pump rate 3/s -> 6/s (floods drain twice as fast).</summary>
    public const string SelfSealingFoam = "module.hull.self_sealing_foam";

    /// <summary>Creature strike damage x0.7.</summary>
    public const string ShockBuffer = "module.hull.shock_buffer";

    /// <summary>Drill cut 8s -> 5s.</summary>
    public const string DrillArm = "module.utility.drill_arm";

    /// <summary>Hull regen 2/s while no compartment is flooding.</summary>
    public const string RepairDrone = "module.utility.repair_drone";

    /// <summary>Two decoy charges; launched at your position, creatures within 300 m investigate for 20 s.</summary>
    public const string DecoyLauncher = "module.utility.decoy_launcher";

    /// <summary>One EMP charge; fired, creatures within 120 m are stunned for 6 s.</summary>
    public const string EmpCoil = "module.utility.emp_coil";

    /// <summary>Every module ID with an implemented in-run effect (exactly 22).</summary>
    public static readonly IReadOnlySet<string> SupportedIds = new HashSet<string>(StringComparer.Ordinal)
    {
        WhisperPulse, PassiveBooster, QuietProp, ReinforcedRib, PressureSkin, SalvageMagnet, EmergencyBuoy,
        WideArray, FocusBeam, ResonanceClassifier, GhostFilter,
        OverdriveThruster, CavitationDampener, EmergencyReverse,
        AbyssPlating, FloodBulkhead, SelfSealingFoam, ShockBuffer,
        DrillArm, RepairDrone, DecoyLauncher, EmpCoil,
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
        float SalvageRangeMeters,
        float PulseConfidenceBonus,
        float BioConfidenceBonus,
        float GhostThresholdBonus,
        float EmergencyThrustMult,
        float FloodFillMult,
        float PumpRateBonus,
        float CreatureDamageMult,
        float DrillDurationMult,
        float HullRegenPerSecond);

    private static readonly LoadoutEffects Neutral = new(
        PulseRangeMult: 1f,
        PulseThreatMult: 1f,
        PulseCooldownMult: 1f,
        PassiveRangeMult: 1f,
        EngineNoiseMult: 1f,
        EngineThrustMult: 1f,
        MaxHullBonus: 0f,
        HullRatingBonus: 0f,
        SalvageRangeMeters: DomainConstants.InteractRangeMeters,
        PulseConfidenceBonus: 0f,
        BioConfidenceBonus: 0f,
        GhostThresholdBonus: 0f,
        EmergencyThrustMult: 1f,
        FloodFillMult: 1f,
        PumpRateBonus: 0f,
        CreatureDamageMult: 1f,
        DrillDurationMult: 1f,
        HullRegenPerSecond: 0f);

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
            EmergencyBuoy => "Emergency buoy: one charge; fired before a failed run, failure retention +0.3 (cap 0.9).",
            WideArray => "Wide array: pulse range x1.3, pulse threat x1.2, cooldown 8.0s -> 6.5s. Bigger picture, louder pings.",
            FocusBeam => "Focus beam: pulse confidence +0.1 per ping, cooldown 8.0s -> 6.0s. Sharper returns, faster cycling.",
            ResonanceClassifier => "Resonance classifier: biological contacts +0.15 confidence per ping, cooldown 8.0s -> 9.0s. Better creature reads, slower cycling.",
            GhostFilter => "Ghost filter: false contacts need threat above 80 (was 60). Fewer phantom returns.",
            OverdriveThruster => "Overdrive thruster: thrust x1.45, engine noise x1.45. Faster, louder.",
            CavitationDampener => "Cavitation dampener: engine noise x0.65, no thrust loss. Quiet without the crawl.",
            EmergencyReverse => "Emergency reverse: thrust x1.5 while hull is below 30%. Escape burst when damaged.",
            AbyssPlating => "Abyss plating: hull pressure rating +40, max hull +100. Deep-rated armor.",
            FloodBulkhead => "Flood bulkhead: breach fill rate x0.5. Flooding spreads half as fast.",
            SelfSealingFoam => "Self-sealing foam: pump rate 3/s -> 6/s. Floods drain twice as fast.",
            ShockBuffer => "Shock buffer: creature strike damage x0.7. Softer hits.",
            DrillArm => "Drill arm: drill cut 8s -> 5s. Faster extraction cuts.",
            RepairDrone => "Repair drone: hull regen 2/s while no compartment is flooding.",
            DecoyLauncher => "Decoy launcher: two charges; launched at your position, creatures within 300 m investigate it for 20 s.",
            EmpCoil => "EMP coil: one charge; fired, creatures within 120 m are stunned for 6 s (no movement, no strikes).",
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
            EmergencyBuoy => "fx=buoy_retention_+0.3_cap0.9",
            WideArray => "fx=pulse_range_x1.3_threat_x1.2_cd_x0.8125",
            FocusBeam => "fx=pulse_conf_+0.1_cd_x0.75",
            ResonanceClassifier => "fx=bio_conf_+0.15_cd_x1.125",
            GhostFilter => "fx=ghost_threshold_+20",
            OverdriveThruster => "fx=thrust_x1.45_noise_x1.45",
            CavitationDampener => "fx=noise_x0.65",
            EmergencyReverse => "fx=thrust_x1.5_below_30pct_hull",
            AbyssPlating => "fx=hullrating_+40_maxhull_+100",
            FloodBulkhead => "fx=floodfill_x0.5",
            SelfSealingFoam => "fx=pump_+3",
            ShockBuffer => "fx=creature_damage_x0.7",
            DrillArm => "fx=drill_duration_x0.625",
            RepairDrone => "fx=hull_regen_2_not_flooding",
            DecoyLauncher => "fx=decoy_2_charges_300m_20s",
            EmpCoil => "fx=emp_1_charge_120m_stun_6s",
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
                WideArray => fx with { PulseRangeMult = 1.3f, PulseThreatMult = 1.2f, PulseCooldownMult = 6.5f / 8f },
                FocusBeam => fx with { PulseConfidenceBonus = 0.1f, PulseCooldownMult = 6f / 8f },
                ResonanceClassifier => fx with { BioConfidenceBonus = 0.15f, PulseCooldownMult = 9f / 8f },
                GhostFilter => fx with { GhostThresholdBonus = 20f },
                OverdriveThruster => fx with { EngineThrustMult = 1.45f, EngineNoiseMult = 1.45f },
                CavitationDampener => fx with { EngineNoiseMult = 0.65f },
                EmergencyReverse => fx with { EmergencyThrustMult = 1.5f },
                AbyssPlating => fx with { HullRatingBonus = 40f, MaxHullBonus = 100f },
                FloodBulkhead => fx with { FloodFillMult = 0.5f },
                SelfSealingFoam => fx with { PumpRateBonus = 3f },
                ShockBuffer => fx with { CreatureDamageMult = 0.7f },
                DrillArm => fx with { DrillDurationMult = 0.625f },
                RepairDrone => fx with { HullRegenPerSecond = 2f },
                _ => fx,
            };
        }
        return fx;
    }
}