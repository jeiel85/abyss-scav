using AbyssScav.Domain;

namespace AbyssScav.App;

/// <summary>
/// Solo-run launch selection. Built by the contract-select UI, validated
/// against the domain catalog, then handed to the run scene via
/// <see cref="Gameplay.RunLaunchContext"/>. Plain data only, no Godot dependency.
/// </summary>
public sealed record RunLaunchOptions(
    string BiomeId,
    string ContractId,
    string DifficultyId,
    string FrameId,
    ulong RunSeed,
    IReadOnlyList<string> ModifierIds,
    string InsuranceId,
    bool IsTutorial = false,
    IReadOnlyList<string>? ModuleIds = null,
    IReadOnlyList<string>? OwnedBlueprints = null,
    IReadOnlyList<string>? ConsumableIds = null)
{
    /// <summary>Equipped module IDs (empty means stock; never null).</summary>
    public IReadOnlyList<string> EffectiveModuleIds => ModuleIds ?? Array.Empty<string>();

    /// <summary>Blueprint snapshot at launch for the unowned gate (null skips it).</summary>
    public IReadOnlyCollection<string>? EffectiveOwnedBlueprints => OwnedBlueprints;

    /// <summary>Equipped consumable IDs (empty means none; never null).</summary>
    public IReadOnlyList<string> EffectiveConsumableIds => ConsumableIds ?? Array.Empty<string>();
    public static RunLaunchOptions DefaultFromCatalog(ContentCatalog catalog)
    {
        var biome = catalog.Biomes.Keys.OrderBy(x => x).First();
        var contract = catalog.Contracts.Values
            .Where(c => c.AllowedBiomeIds.Contains(biome))
            .OrderBy(c => c.Id).First();
        var difficulty = catalog.Difficulties.ContainsKey("difficulty.standard")
            ? "difficulty.standard"
            : catalog.Difficulties.Keys.OrderBy(x => x).First();
        var frame = catalog.Frames.ContainsKey("frame.skiff")
            ? "frame.skiff"
            : catalog.Frames.Keys.OrderBy(x => x).First();
        return new RunLaunchOptions(biome, contract.Id, difficulty, frame,
            1234UL, Array.Empty<string>(), RunSimulation.InsuranceNone);
    }

    /// <summary>
    /// First-run guided contract T0 "The First Ping" (docs/06 §7): fixed water,
    /// contract, seed, frame, difficulty, and insurance; no modifiers and an
    /// empty (stock) module loadout so the tutorial stays replayable as a baseline.
    /// Callers must still run <see cref="TryValidate"/> — never a fake launch.
    /// </summary>
    public static RunLaunchOptions Tutorial() => new(
        "biome.shelf_graveyard",
        "contract.salvage_quota",
        "difficulty.standard",
        "frame.skiff",
        4242UL,
        Array.Empty<string>(),
        RunSimulation.InsuranceNone,
        true);

    /// <summary>Validates IDs against the catalog; returns false with every reason.</summary>
    public bool TryValidate(ContentCatalog catalog, out IReadOnlyList<string> errors)
    {
        var problems = new List<string>();
        if (catalog is null)
        {
            problems.Add("GEN-001 content catalog is missing.");
            errors = problems;
            return false;
        }
        if (!catalog.Biomes.ContainsKey(BiomeId))
            problems.Add($"GEN-002 unknown biome '{BiomeId}'.");
        if (!catalog.Contracts.TryGetValue(ContractId, out var contract))
            problems.Add($"GEN-003 unknown contract '{ContractId}'.");
        else if (!contract.AllowedBiomeIds.Contains(BiomeId))
            problems.Add($"GEN-004 contract '{ContractId}' is not valid in biome '{BiomeId}'.");
        if (!catalog.Difficulties.ContainsKey(DifficultyId))
            problems.Add($"CONTENT-203 unknown difficulty '{DifficultyId}'.");
        if (!catalog.Frames.ContainsKey(FrameId))
            problems.Add($"CONTENT-204 unknown frame '{FrameId}'.");
        if (InsuranceId is not (RunSimulation.InsuranceNone or RunSimulation.InsuranceBasic or RunSimulation.InsurancePremium))
            problems.Add($"CONTENT-205 unknown insurance '{InsuranceId}'.");
        foreach (var m in ModifierIds ?? Array.Empty<string>())
        {
            if (!catalog.Modifiers.ContainsKey(m))
                problems.Add($"CONTENT-206 unknown modifier '{m}'.");
        }
        if (!ModuleLoadout.TryValidate(EffectiveModuleIds, catalog, ownedBlueprints: null, out var modErrors))
        {
            // Structural gate only (unknown/duplicate/unsupported/category);
            // the unowned gate runs at the UI + run-start with the live profile.
            problems.AddRange(modErrors);
        }
        foreach (var id in EffectiveConsumableIds)
        {
            if (!catalog.Consumables.ContainsKey(id))
                problems.Add($"CONTENT-208 unknown consumable '{id}'.");
        }
        errors = problems;
        return problems.Count == 0;
    }
}
