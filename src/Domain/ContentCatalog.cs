using System.Security.Cryptography;
using System.Text;

namespace AbyssScav.Domain;

/// <summary>
/// Engine-independent typed content catalog with stable IDs.
/// <para>
/// CONTENTS (exact, no placeholders): 3 biomes, 3 submarine frames,
/// 8 contract archetypes, 24 modules (6 sonar / 6 engine / 6 hull / 6 utility),
/// 4 difficulties, 11 creatures (8 + 3 apex), 8 relic traits, 8 modifiers.
/// </para>
/// <para>
/// INTEGRATION: build once at boot via <see cref="TryBuild"/>. Publish
/// <see cref="CatalogHash"/> inside the run manifest so hosts and joiners can
/// reject mismatched content before spawning anything. A failed build returns
/// every reason; fix data, never catch-and-continue with a partial catalog.
/// </para>
/// </summary>
public sealed class ContentCatalog
{
    /// <summary>All biomes by stable ID.</summary>
    public IReadOnlyDictionary<string, BiomeDef> Biomes { get; }

    /// <summary>All submarine frames by stable ID.</summary>
    public IReadOnlyDictionary<string, SubFrameDef> Frames { get; }

    /// <summary>All contracts by stable ID.</summary>
    public IReadOnlyDictionary<string, ContractDef> Contracts { get; }

    /// <summary>All modules by stable ID.</summary>
    public IReadOnlyDictionary<string, ModuleDef> Modules { get; }

    /// <summary>All difficulties by stable ID.</summary>
    public IReadOnlyDictionary<string, DifficultyDef> Difficulties { get; }

    /// <summary>All creatures by stable ID.</summary>
    public IReadOnlyDictionary<string, CreatureDef> Creatures { get; }

    /// <summary>All relic traits by stable ID.</summary>
    public IReadOnlyDictionary<string, RelicTraitDef> RelicTraits { get; }

    /// <summary>All contract modifiers by stable ID.</summary>
    public IReadOnlyDictionary<string, ModifierDef> Modifiers { get; }

    /// <summary>Deterministic SHA256 over the canonical catalog text (hex, "sha256:" prefixed).</summary>
    public string CatalogHash { get; }

    private ContentCatalog(
        IReadOnlyDictionary<string, BiomeDef> biomes,
        IReadOnlyDictionary<string, SubFrameDef> frames,
        IReadOnlyDictionary<string, ContractDef> contracts,
        IReadOnlyDictionary<string, ModuleDef> modules,
        IReadOnlyDictionary<string, DifficultyDef> difficulties,
        IReadOnlyDictionary<string, CreatureDef> creatures,
        IReadOnlyDictionary<string, RelicTraitDef> traits,
        IReadOnlyDictionary<string, ModifierDef> modifiers,
        string hash)
    {
        Biomes = biomes;
        Frames = frames;
        Contracts = contracts;
        Modules = modules;
        Difficulties = difficulties;
        Creatures = creatures;
        RelicTraits = traits;
        Modifiers = modifiers;
        CatalogHash = hash;
    }

    /// <summary>
    /// Builds the built-in catalog, rejecting duplicates and dangling cross-references.
    /// Returns false with every reason when content is inconsistent; the out catalog
    /// is null in that case and must not be used.
    /// </summary>
    public static bool TryBuild(out ContentCatalog? catalog, out IReadOnlyList<string> errors)
    {
        var problems = new List<string>();

        var biomes = BuiltInBiomes();
        var frames = BuiltInFrames();
        var difficulties = BuiltInDifficulties();
        var creatures = BuiltInCreatures();
        var traits = BuiltInRelicTraits();
        var modifiers = BuiltInModifiers();
        var modules = BuiltInModules();
        var contracts = BuiltInContracts();

        CheckIds("biome", biomes.Select(b => b.Id), problems);
        CheckIds("frame", frames.Select(f => f.Id), problems);
        CheckIds("contract", contracts.Select(c => c.Id), problems);
        CheckIds("module", modules.Select(m => m.Id), problems);
        CheckIds("difficulty", difficulties.Select(d => d.Id), problems);
        CheckIds("creature", creatures.Select(c => c.Id), problems);
        CheckIds("relic-trait", traits.Select(t => t.Id), problems);
        CheckIds("modifier", modifiers.Select(m => m.Id), problems);

        // Global duplicate sweep: no two entries of any kind may share one ID.
        var allIds = biomes.Select(b => b.Id)
            .Concat(frames.Select(f => f.Id))
            .Concat(contracts.Select(c => c.Id))
            .Concat(modules.Select(m => m.Id))
            .Concat(difficulties.Select(d => d.Id))
            .Concat(creatures.Select(c => c.Id))
            .Concat(traits.Select(t => t.Id))
            .Concat(modifiers.Select(m => m.Id))
            .ToList();
        foreach (var group in allIds.GroupBy(id => id).Where(g => g.Count() > 1))
            problems.Add($"CONTENT-101 duplicate stable id '{group.Key}' appears {group.Count()} times.");

        var biomeIds = new HashSet<string>(biomes.Select(b => b.Id));
        var traitIds = new HashSet<string>(traits.Select(t => t.Id));
        var branches = new HashSet<string>(StringComparer.Ordinal) { "Navigation", "Engineering", "Salvage", "Survival" };

        // Cross-reference checks: contracts may only name real biomes and quest targets
        // from the fixed quest-item vocabulary below.
        foreach (var c in contracts)
        {
            if (c.BasePayout <= 0)
                problems.Add($"CONTENT-102 contract '{c.Id}' has non-positive base payout.");
            if (c.PrimaryObjectives.Count == 0)
                problems.Add($"CONTENT-102 contract '{c.Id}' has no primary objective.");
            foreach (var biome in c.AllowedBiomeIds)
                if (!biomeIds.Contains(biome))
                    problems.Add($"CONTENT-103 contract '{c.Id}' references unknown biome '{biome}'.");
            foreach (var o in c.PrimaryObjectives)
            {
                if (o.Required <= 0)
                    problems.Add($"CONTENT-102 contract '{c.Id}' objective '{o.ObjectiveId}' has non-positive requirement.");
                if ((o.Kind is ContractObjectiveKind.QuestItem or ContractObjectiveKind.ServiceNode) &&
                    !QuestItems.IsKnown(o.TargetQuestItemId))
                    problems.Add($"CONTENT-103 contract '{c.Id}' objective '{o.ObjectiveId}' references unknown quest target '{o.TargetQuestItemId}'.");
            }
        }

        foreach (var m in modules)
        {
            if (m.Tier is < 1 or > 4)
                problems.Add($"CONTENT-104 module '{m.Id}' has out-of-range tier {m.Tier}.");
            if (m.PowerIdle < 0 || m.PowerActive < m.PowerIdle)
                problems.Add($"CONTENT-104 module '{m.Id}' has inconsistent power draw.");
            if (m.NoiseMultiplier <= 0)
                problems.Add($"CONTENT-104 module '{m.Id}' has non-positive noise multiplier.");
            if (!branches.Contains(m.ResearchBranch))
                problems.Add($"CONTENT-103 module '{m.Id}' references unknown research branch '{m.ResearchBranch}'.");
            if (m.ResearchCost <= 0)
                problems.Add($"CONTENT-104 module '{m.Id}' has non-positive research cost.");
        }

        foreach (var b in biomes)
        {
            if (b.MinDepthMeters <= 0 || b.MeanDepthMeters <= b.MinDepthMeters || b.MaxDepthMeters <= b.MeanDepthMeters)
                problems.Add($"CONTENT-105 biome '{b.Id}' has inconsistent depth band.");
            if (b.PressureModifier <= 0 || b.CorridorHalfWidthMeters < DomainConstants.MinClearanceMeters)
                problems.Add($"CONTENT-105 biome '{b.Id}' has non-viable pressure or corridor geometry.");
        }

        foreach (var f in frames)
        {
            if (f.MaxHull <= 0 || f.HullRating <= 0 || f.PowerSupply <= 0)
                problems.Add($"CONTENT-106 frame '{f.Id}' has non-positive hull or power rating.");
            if (f.CargoSlots <= 0 || f.CargoMaxMassKg <= 0 || f.NoiseFactor <= 0)
                problems.Add($"CONTENT-106 frame '{f.Id}' has non-positive cargo or noise rating.");
        }

        foreach (var t in traits)
            if (t.ThreatOnRecover < 0)
                problems.Add($"CONTENT-107 relic trait '{t.Id}' has negative threat weight.");

        if (problems.Count > 0)
        {
            catalog = null;
            errors = problems;
            return false;
        }

        var hash = ComputeHash(biomes, frames, contracts, modules, difficulties, creatures, traits, modifiers);
        catalog = new ContentCatalog(
            biomes.ToDictionary(b => b.Id),
            frames.ToDictionary(f => f.Id),
            contracts.ToDictionary(c => c.Id),
            modules.ToDictionary(m => m.Id),
            difficulties.ToDictionary(d => d.Id),
            creatures.ToDictionary(c => c.Id),
            traits.ToDictionary(t => t.Id),
            modifiers.ToDictionary(m => m.Id),
            hash);
        errors = Array.Empty<string>();
        return true;
    }

    private static void CheckIds(string kind, IEnumerable<string> ids, List<string> problems)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (!StableIds.IsValid(id))
                problems.Add($"CONTENT-100 {kind} id '{id}' is not a well-formed stable id.");
            else if (!seen.Add(id))
                problems.Add($"CONTENT-101 duplicate {kind} id '{id}'.");
        }
    }

    /// <summary>
    /// Canonical hash input: version line plus one sorted line per entry.
    /// Module lines include sorted tags plus the loadout effect key, so adding
    /// tags or wiring a module effect deterministically changes the hash.
    /// </summary>
    internal static string ComputeHash(
        IEnumerable<BiomeDef> biomes,
        IEnumerable<SubFrameDef> frames,
        IEnumerable<ContractDef> contracts,
        IEnumerable<ModuleDef> modules,
        IEnumerable<DifficultyDef> difficulties,
        IEnumerable<CreatureDef> creatures,
        IEnumerable<RelicTraitDef> traits,
        IEnumerable<ModifierDef> modifiers)
    {
        var lines = new List<string> { "version=" + DomainConstants.CatalogVersion };
        foreach (var b in biomes.OrderBy(x => x.Id))
            lines.Add($"biome|{b.Id}|{b.MinDepthMeters:F1}|{b.MeanDepthMeters:F1}|{b.MaxDepthMeters:F1}|{b.PressureModifier:F3}|{b.CorridorHalfWidthMeters:F1}|{b.ThreatWeight:F3}|{b.SalvageRichness:F3}|{b.PulseRangeMeters:F0}|{b.PassiveRangeMeters:F0}");
        foreach (var f in frames.OrderBy(x => x.Id))
            lines.Add($"frame|{f.Id}|{f.MaxHull:F0}|{f.HullRating:F1}|{f.PowerSupply:F0}|{f.CargoSlots}|{f.CargoMaxMassKg:F0}|{f.NoiseFactor:F3}");
        foreach (var c in contracts.OrderBy(x => x.Id))
        {
            lines.Add($"contract|{c.Id}|{c.Archetype}|{c.Tier}|{c.BasePayout}|{string.Join("+", c.AllowedBiomeIds.OrderBy(x => x))}");
            foreach (var o in c.PrimaryObjectives.OrderBy(x => x.ObjectiveId))
                lines.Add($"objective|{c.Id}|{o.ObjectiveId}|{o.Kind}|{o.Required}|{o.TargetQuestItemId}");
        }
        foreach (var m in modules.OrderBy(x => x.Id))
            lines.Add($"module|{m.Id}|{m.Category}|{m.Tier}|{m.PowerIdle:F1}|{m.PowerActive:F1}|{m.NoiseMultiplier:F3}|{m.CooldownSeconds:F1}|{m.ResearchBranch}|{m.ResearchCost}|tags={string.Join("+", m.Tags.OrderBy(x => x))}|{ModuleLoadout.EffectKey(m.Id)}");
        foreach (var d in difficulties.OrderBy(x => x.Id))
            lines.Add($"difficulty|{d.Id}|{d.DetectMultiplier:F3}|{d.ThreatRateMultiplier:F3}|{d.BreachProbabilityMultiplier:F3}|{d.RepairEfficiency:F3}|{d.ResourceMultiplier:F3}|{d.PayoutMultiplier:F3}|{d.PulseCooldownMultiplier:F3}");
        foreach (var c in creatures.OrderBy(x => x.Id))
            lines.Add($"creature|{c.Id}|{(c.IsApex ? 1 : 0)}|{c.BaseHearingMeters:F0}|{c.SpeedMetersPerSecond:F1}|{c.AttackRangeMeters:F0}|{c.AttackDamage:F0}|{c.Aggression:F3}");
        foreach (var t in traits.OrderBy(x => x.Id))
            lines.Add($"trait|{t.Id}|{t.ThreatOnRecover:F1}");
        foreach (var m in modifiers.OrderBy(x => x.Id))
            lines.Add($"modifier|{m.Id}");

        var bytes = Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n");
        var digest = SHA256.HashData(bytes);
        return "sha256:" + Convert.ToHexString(digest).ToLowerInvariant();
    }

    // ------------------------------------------------------------------ data

    private static List<BiomeDef> BuiltInBiomes() => new()
    {
        new BiomeDef("biome.shelf_graveyard", "Continental Shelf Graveyard",
            1500f, 2250f, 3000f, 1.00f, 28f, 0.7f, 1.0f, 450f, 220f,
            "Wreck fields in sediment. Introductory pressure, simple predators."),
        new BiomeDef("biome.black_trench", "Black Trench",
            3000f, 4500f, 6000f, 1.35f, 18f, 1.2f, 1.4f, 380f, 180f,
            "Narrow canyons, thermal vents, electromagnetic interference."),
        new BiomeDef("biome.hadal_ruins", "Hadal Ruins",
            6000f, 7500f, 9000f, 1.70f, 22f, 1.6f, 1.8f, 320f, 150f,
            "Non-human structures, crushing pressure, distorted sonar."),
    };

    private static List<SubFrameDef> BuiltInFrames() => new()
    {
        new SubFrameDef("frame.skiff", "Skiff",
            700f, 34f, 100f, 12, 900f, 0.85f,
            "Agile, low capacity, solo friendly."),
        new SubFrameDef("frame.mule", "Mule",
            1000f, 55f, 120f, 20, 2200f, 1.20f,
            "Heavy cargo, slow, built for crews of 2-4."),
        new SubFrameDef("frame.warden", "Warden",
            900f, 80f, 140f, 14, 1200f, 1.35f,
            "Armored power plant. Loud, deep-rated."),
    };

    private static List<ContractDef> BuiltInContracts() => new()
    {
        new ContractDef("contract.salvage_quota", "SalvageQuota", 1, 600,
            new[] { "biome.shelf_graveyard", "biome.black_trench", "biome.hadal_ruins" },
            new[] { new ContractObjectiveSpec("bank_salvage", ContractObjectiveKind.SalvageValue, 600, "", "Bank 600 secured salvage credits via TrySalvage.") },
            "TrySalvage loose loot until the quota banks; TryExtract when complete.",
            "Fill the hold with scrap, crates, and relics, then get out."),
        new ContractDef("contract.blackbox_recovery", "BlackBoxRecovery", 1, 850,
            new[] { "biome.shelf_graveyard", "biome.black_trench" },
            new[] { new ContractObjectiveSpec("recover_blackbox", ContractObjectiveKind.QuestItem, 1, QuestItems.BlackBox, "Recover the wreck black box via TrySalvage at the objective node.") },
            "Pulse to find the wreck, TrySalvage the quest black box, TryExtract.",
            "Locate the downed wreck and bring its recorder home."),
        new ContractDef("contract.facility_core", "FacilityCoreExtraction", 2, 1100,
            new[] { "biome.black_trench", "biome.hadal_ruins" },
            new[]
            {
                new ContractObjectiveSpec("stabilize_core", ContractObjectiveKind.ServiceNode, 1, QuestItems.CoreService, "Stabilize the core via docked TryServiceContractNode at the objective station."),
                new ContractObjectiveSpec("recover_core", ContractObjectiveKind.QuestItem, 1, QuestItems.Core, "Recover the core via docked 8 s drill (H): direct TrySalvage is refused."),
            },
            "Dock (J) at the facility, TryServiceContractNode stabilizes the core, then drill (H) 8 s recovers it.",
            "Breach the dead facility, stabilize its core, carry it out."),
        new ContractDef("contract.bio_sample", "BioSampleHunt", 1, 750,
            new[] { "biome.shelf_graveyard", "biome.black_trench", "biome.hadal_ruins" },
            new[] { new ContractObjectiveSpec("recover_samples", ContractObjectiveKind.QuestItem, 3, QuestItems.BioSample, "Recover 3 bio samples via TrySalvage.") },
            "TrySalvage three quest bio-sample spawns spread along the route.",
            "Harvest viable tissue from three marked colonies."),
        new ContractDef("contract.beacon_repair", "BeaconRepair", 1, 700,
            new[] { "biome.shelf_graveyard", "biome.black_trench" },
            new[] { new ContractObjectiveSpec("repair_beacons", ContractObjectiveKind.ServiceNode, 2, QuestItems.BeaconService, "Service 2 beacons via TryServiceContractNode (1 sealant each).") },
            "Visit both beacon nodes, TryServiceContractNode each, TryExtract.",
            "Bring two dead relay beacons back onto the network."),
        new ContractDef("contract.survey_scan", "SurveyScan", 1, 650,
            new[] { "biome.shelf_graveyard", "biome.black_trench", "biome.hadal_ruins" },
            new[] { new ContractObjectiveSpec("survey_contacts", ContractObjectiveKind.Survey, 5, "", "Survey 5 distinct contacts via Pulse then TrySurvey.") },
            "Pulse to reveal contacts, close range, TrySurvey five of them.",
            "Chart the trench: five classified contacts, no shortcuts."),
        new ContractDef("contract.rescue_pod", "RescuePodRecovery", 2, 950,
            new[] { "biome.black_trench", "biome.hadal_ruins" },
            new[]
            {
                new ContractObjectiveSpec("recover_pod", ContractObjectiveKind.QuestItem, 1, QuestItems.Pod, "Recover the escape pod via TrySalvage (heavy: needs free mass and slots)."),
                new ContractObjectiveSpec("pod_salvage", ContractObjectiveKind.SalvageValue, 150, "", "Bank 150 additional salvage."),
            },
            "TrySalvage the heavy quest pod, bank 150 salvage, TryExtract.",
            "Cut loose an escape pod and tow out its manifest too."),
        new ContractDef("contract.apex_observe", "ApexObservation", 3, 1400,
            new[] { "biome.black_trench", "biome.hadal_ruins" },
            new[] { new ContractObjectiveSpec("observe_apex", ContractObjectiveKind.ObserveApex, 20, "", "Hold within 80 m of a pulse-revealed apex for 20 cumulative seconds, then extract.") },
            "Pulse to reveal the apex, stay in range (tracked in Tick), then TryExtract to escape.",
            "Get close enough to classify an apex — and live to file the report."),
    };

    private static List<ModuleDef> BuiltInModules()
    {
        var list = new List<ModuleDef>();
        void Add(string id, string cat, int tier, float idle, float active, float noise, float cd, string tags, string branch, int cost) =>
            list.Add(new ModuleDef(id, cat, tier, idle, active, noise, cd, tags.Split(','), branch, cost));

        // Sonar (6): distinct stealth / range / classification trade-offs.
        Add("module.sonar.wide_array", "Sonar", 2, 3f, 18f, 1.20f, 6.5f, "sonar,range", "Navigation", 140);
        Add("module.sonar.focus_beam", "Sonar", 3, 2f, 16f, 1.00f, 6.0f, "sonar,precision", "Navigation", 260);
        Add("module.sonar.whisper_pulse", "Sonar", 2, 2f, 14f, 0.55f, 7.5f, "sonar,stealth", "Navigation", 180);
        Add("module.sonar.resonance_classifier", "Sonar", 3, 3f, 20f, 1.30f, 9.0f, "sonar,biology", "Navigation", 300);
        Add("module.sonar.ghost_filter", "Sonar", 2, 2f, 12f, 0.80f, 8.0f, "sonar,clarity", "Survival", 170);
        Add("module.sonar.passive_booster", "Sonar", 1, 1f, 6f, 0.70f, 5.0f, "sonar,passive", "Navigation", 80);
        // Engine (6).
        Add("module.engine.quiet_prop", "Engine", 2, 4f, 30f, 0.60f, 0f, "engine,stealth", "Engineering", 170);
        Add("module.engine.overdrive_thruster", "Engine", 3, 6f, 45f, 1.45f, 25f, "engine,speed", "Engineering", 290);
        Add("module.engine.vector_fin", "Engine", 1, 2f, 14f, 0.90f, 6f, "engine,agility", "Engineering", 70);
        Add("module.engine.emergency_reverse", "Engine", 2, 2f, 22f, 1.10f, 18f, "engine,escape", "Survival", 150);
        Add("module.engine.heat_sink", "Engine", 2, 3f, 10f, 0.85f, 30f, "engine,heat", "Engineering", 160);
        Add("module.engine.cavitation_dampener", "Engine", 3, 3f, 12f, 0.65f, 0f, "engine,stealth", "Engineering", 280);
        // Hull (6).
        Add("module.hull.reinforced_rib", "Hull", 1, 1f, 2f, 1.00f, 0f, "hull,armor", "Survival", 75);
        Add("module.hull.pressure_skin", "Hull", 2, 2f, 4f, 1.00f, 0f, "hull,pressure", "Survival", 165);
        Add("module.hull.self_sealing_foam", "Hull", 2, 1f, 3f, 1.00f, 0f, "hull,repair", "Survival", 175);
        Add("module.hull.shock_buffer", "Hull", 2, 1f, 3f, 1.00f, 0f, "hull,impact", "Survival", 155);
        Add("module.hull.flood_bulkhead", "Hull", 3, 2f, 6f, 1.00f, 0f, "hull,flood", "Survival", 270);
        Add("module.hull.abyss_plating", "Hull", 4, 3f, 8f, 1.15f, 0f, "hull,pressure,armor", "Survival", 400);
        // Utility (6).
        Add("module.utility.emp_coil", "Utility", 3, 4f, 25f, 1.20f, 40f, "utility,defense", "Engineering", 285);
        Add("module.utility.decoy_launcher", "Utility", 2, 2f, 15f, 1.10f, 35f, "utility,decoy", "Salvage", 165);
        Add("module.utility.drill_arm", "Utility", 2, 2f, 35f, 1.60f, 20f, "utility,drill", "Salvage", 190);
        Add("module.utility.salvage_magnet", "Utility", 1, 2f, 12f, 1.00f, 12f, "utility,salvage", "Salvage", 85);
        Add("module.utility.repair_drone", "Utility", 3, 3f, 18f, 0.90f, 45f, "utility,repair", "Engineering", 295);
        Add("module.utility.emergency_buoy", "Utility", 1, 1f, 5f, 1.00f, 0f, "utility,safety", "Survival", 65);
        return list;
    }

    private static List<DifficultyDef> BuiltInDifficulties() => new()
    {
        new DifficultyDef("difficulty.casual", "Casual Dive",
            0.80f, 0.80f, 0.75f, 1.00f, 1.10f, 0.90f, 1.00f, true),
        new DifficultyDef("difficulty.standard", "Standard",
            1.00f, 1.00f, 1.00f, 1.00f, 1.00f, 1.00f, 1.00f, true),
        new DifficultyDef("difficulty.blackwater", "Blackwater",
            1.20f, 1.25f, 1.00f, 0.80f, 1.00f, 1.15f, 1.00f, true),
        new DifficultyDef("difficulty.custom", "Custom",
            1.00f, 1.00f, 1.00f, 1.00f, 1.00f, 1.00f, 1.00f, false),
    };

    private static List<CreatureDef> BuiltInCreatures() => new()
    {
        new CreatureDef("creature.needle_eel", "Needle Eel", false, 120f, 14f, 10f, 15f, 0.60f, "Fast investigator."),
        new CreatureDef("creature.bell_maw", "Bell Maw", false, 150f, 8f, 12f, 25f, 0.70f, "Waits motionless, hunts by sound."),
        new CreatureDef("creature.glass_ray", "Glass Ray", false, 80f, 10f, 8f, 5f, 0.20f, "Reflects sonar strongly; living decoy."),
        new CreatureDef("creature.silt_stalker", "Silt Stalker", false, 100f, 12f, 10f, 30f, 0.80f, "Ambushes from sediment."),
        new CreatureDef("creature.lampreech", "Lampreech", false, 90f, 9f, 8f, 10f, 0.50f, "Clamps the hull and drinks power."),
        new CreatureDef("creature.chorus_colony", "Chorus Colony", false, 140f, 4f, 6f, 0f, 0.30f, "Broadcasts false sonar contacts."),
        new CreatureDef("creature.hull_grazer", "Hull Grazer", false, 70f, 7f, 8f, 8f, 0.40f, "Scrapes the hull, spiking noise."),
        new CreatureDef("creature.warden_crab", "Warden Crab", false, 110f, 9f, 10f, 35f, 0.90f, "Defends facility ground."),
        new CreatureDef("creature.apex_long_choir", "The Long Choir", true, 250f, 16f, 16f, 50f, 1.00f, "Mimics voices; herds prey by sound."),
        new CreatureDef("creature.apex_pale_leviathan", "Pale Leviathan", true, 300f, 18f, 20f, 70f, 1.00f, "A silhouette that closes off routes."),
        new CreatureDef("creature.apex_trench_mother", "Trench Mother", true, 200f, 12f, 16f, 60f, 1.00f, "Defends a living nest."),
    };

    private static List<RelicTraitDef> BuiltInRelicTraits() => new()
    {
        new RelicTraitDef("trait.valuable", "Valuable", 0f),
        new RelicTraitDef("trait.resonant", "Resonant", 4f),
        new RelicTraitDef("trait.fragile", "Fragile", 0f),
        new RelicTraitDef("trait.hazardous", "Hazardous", 6f),
        new RelicTraitDef("trait.parasitic", "Parasitic", 8f),
        new RelicTraitDef("trait.memory_bearing", "Memory-bearing", 2f),
        new RelicTraitDef("trait.unstable", "Unstable", 5f),
        new RelicTraitDef("trait.ancient_power", "Ancient Power Source", 10f),
    };

    private static List<ModifierDef> BuiltInModifiers() => new()
    {
        new ModifierDef("modifier.low_visibility", "Low Visibility",
            "Host effect: passive sonar range x0.7, survey range x0.85."),
        new ModifierDef("modifier.sonar_blackout", "Sonar Blackout Zones",
            "Host effect: pulse cooldown x2, pulse range x0.7."),
        new ModifierDef("modifier.severe_current", "Severe Current",
            "Host effect: engine power draw +10 PU, noise target +8."),
        new ModifierDef("modifier.fragile_hull", "Fragile Hull Condition",
            "Host effect: max hull x0.75, breach probability x1.3."),
        new ModifierDef("modifier.nesting_season", "Predator Nesting Season",
            "Host effect: creature aggression x1.3, threat ramp x1.2."),
        new ModifierDef("modifier.no_ping_bonus", "No Active Sonar Bonus",
            "Host effect: +150 credit bonus when the run extracts with zero pulses used."),
        new ModifierDef("modifier.time_window", "Time Window",
            "Host effect: extraction deadline 2400 s; expiry fails the run."),
        new ModifierDef("modifier.reactor_instability", "Reactor Instability",
            "Host effect: periodic supply dips of -30 PU for 5 s on the event stream."),
    };
}

/// <summary>
/// Fixed quest-target vocabulary shared by contract definitions and the generator.
/// Quest items are unique per run (duplicate recovery is rejected by the simulation).
/// </summary>
public static class QuestItems
{
    /// <summary>Wreck recorder for black-box contracts.</summary>
    public const string BlackBox = "quest.blackbox";
    /// <summary>Facility core item for core contracts.</summary>
    public const string Core = "quest.core";
    /// <summary>Stabilization service on the core node for core contracts.</summary>
    public const string CoreService = "service.core_stabilize";
    /// <summary>Bio-sample quest family: quest.bio.1 .. quest.bio.3.</summary>
    public const string BioSample = "quest.bio";
    /// <summary>Escape pod for rescue contracts.</summary>
    public const string Pod = "quest.pod";
    /// <summary>Beacon service family: service.beacon.1 .. service.beacon.2.</summary>
    public const string BeaconService = "service.beacon";

    /// <summary>True for quest IDs the generator can place and the sim can complete.</summary>
    public static bool IsKnown(string id)
    {
        if (id is BlackBox or Core or CoreService or Pod) return true;
        if (id is BioSample or BeaconService) return true;
        if (id.StartsWith("quest.bio.", StringComparison.Ordinal)) return true;
        if (id.StartsWith("service.beacon.", StringComparison.Ordinal)) return true;
        return false;
    }
}
