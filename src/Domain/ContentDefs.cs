namespace AbyssScav.Domain;

/// <summary>
/// Tunable constants shared by generation and simulation. Changing any value
/// changes gameplay balance and may change generated layouts; the catalog
/// version (<see cref="CatalogVersion"/>) must be bumped with such changes so
/// run manifests stay comparable.
/// </summary>
public static class DomainConstants
{
    /// <summary>Catalog + generation schema version. Part of determinism and manifests.</summary>
    public const string CatalogVersion = "abyss-scav.catalog/1";

    /// <summary>Network protocol version for run manifests (docs/16 §5).</summary>
    public const ushort ProtocolVersion = 1;

    /// <summary>Ship must be within this range (meters) to salvage loot.</summary>
    public const float InteractRangeMeters = 15f;

    /// <summary>Ship must be within this range (meters) to service a contract node.</summary>
    public const float ServiceRangeMeters = 20f;

    /// <summary>Ship must be within this range (meters) to dock at a station node.</summary>
    public const float DockRangeMeters = 25f;

    /// <summary>Docking requires ship speed at or below this (m/s).</summary>
    public const float DockMaxSpeedMetersPerSecond = 2f;

    /// <summary>Timed drill extraction takes this long (seconds, bounded).</summary>
    public const float DrillDurationSeconds = 8f;

    /// <summary>Drill power draw while the bit turns (PU, docs/03 §1: drill 35).</summary>
    public const float DrillPowerDrawPU = 35f;

    /// <summary>Threat added per drill-second (0.8/s × 8 s = 6.4 per extraction).</summary>
    public const float DrillThreatPerSecond = 0.8f;

    /// <summary>Extra drill reach beyond the manipulator: stock 15 m → 25 m drill.</summary>
    public const float DrillExtraReachMeters = 10f;

    /// <summary>Maximum sonar survey range before biome/modifier scaling (meters).</summary>
    public const float SurveyRangeMeters = 150f;

    /// <summary>Base active-pulse range before biome/modifier scaling (meters).</summary>
    public const float PulseBaseRangeMeters = 450f;

    /// <summary>Base active-pulse cooldown in seconds.</summary>
    public const float PulseBaseCooldownSeconds = 8f;

    /// <summary>Extraction succeeds within this range (meters) of the extraction node.</summary>
    public const float ExtractionRadiusMeters = 30f;

    /// <summary>Apex observation counts while the ship is within this range (meters).</summary>
    public const float ObserveRangeMeters = 80f;

    /// <summary>Reference submarine hull diameter (meters) for clearance checks.</summary>
    public const float HullDiameterMeters = 6f;

    /// <summary>Minimum navigable clearance: hull diameter × 1.8 (docs/04 §4).</summary>
    public const float MinClearanceMeters = 10.8f;

    /// <summary>Minimum separation between world nodes (meters); below this counts as overlap.</summary>
    public const float MinNodeSeparationMeters = 25f;

    /// <summary>Maximum allowed route segment length (meters).</summary>
    public const float MaxSegmentLengthMeters = 400f;

    /// <summary>Maximum forced-narrow segments per run (docs/04 §4).</summary>
    public const int MaxNarrowSegments = 2;

    /// <summary>Segment narrower than this (meters) counts as a forced-narrow segment.</summary>
    public const float NarrowSegmentMeters = 14f;

    /// <summary>Minimum graph edges between extraction and the primary objective (docs/04 §4).</summary>
    public const int MinObjectiveEdgeDistance = 3;

    /// <summary>Generation retries per stage before the safe fallback layout (docs/04 §6).</summary>
    public const int MaxStageRetries = 3;
}

/// <summary>Salvage/interactable kinds placed by the generator (docs/03 §7).</summary>
public enum LootKind
{
    /// <summary>Loose scrap, value only.</summary>
    Scrap = 0,
    /// <summary>Heavy crate, value with real mass pressure on cargo.</summary>
    Crate = 1,
    /// <summary>Fragile relic with a content-bible trait.</summary>
    Relic = 2,
    /// <summary>Biological sample, quest-relevant for bio contracts.</summary>
    BioSample = 3,
    /// <summary>Quest black box (unique per run).</summary>
    BlackBox = 4,
    /// <summary>Quest facility core (unique per run).</summary>
    Core = 5,
    /// <summary>Quest rescue pod (unique, heavy).</summary>
    Pod = 6,
}

/// <summary>World node roles used by routing, objectives, and sonar classification.</summary>
public enum WorldNodeKind
{
    /// <summary>Run start and extraction point, near the origin.</summary>
    Extraction = 0,
    /// <summary>Main trench route waypoint.</summary>
    Route = 1,
    /// <summary>Dead-end branch waypoint.</summary>
    Branch = 2,
    /// <summary>Primary objective site (graph distance ≥ 3 from extraction).</summary>
    Objective = 3,
}

/// <summary>Contract objective kinds the host simulation can progress.</summary>
public enum ContractObjectiveKind
{
    /// <summary>Bank a required secured-salvage credit value via <c>TrySalvage</c>.</summary>
    SalvageValue = 0,
    /// <summary>Recover a unique quest item via <c>TrySalvage</c> (duplicate-safe).</summary>
    QuestItem = 1,
    /// <summary>Repair/stabilize a world node via <c>TryServiceContractNode</c>.</summary>
    ServiceNode = 2,
    /// <summary>Actively survey contacts via <c>TrySurvey</c>.</summary>
    Survey = 3,
    /// <summary>Hold within observe range of a revealed apex via normal <c>Tick</c> presence.</summary>
    ObserveApex = 4,
}

/// <summary>Biome definition (docs/06 §1). All distances in meters, depths in meters.</summary>
public sealed record BiomeDef(
    string Id,
    string DisplayName,
    float MinDepthMeters,
    float MeanDepthMeters,
    float MaxDepthMeters,
    float PressureModifier,
    float CorridorHalfWidthMeters,
    float ThreatWeight,
    float SalvageRichness,
    float PulseRangeMeters,
    float PassiveRangeMeters,
    string Description);

/// <summary>Submarine frame definition (docs/05 §7).</summary>
public sealed record SubFrameDef(
    string Id,
    string DisplayName,
    float MaxHull,
    float HullRating,
    float PowerSupply,
    int CargoSlots,
    float CargoMaxMassKg,
    float NoiseFactor,
    string Description);

/// <summary>Contract definition: one entry per archetype (docs/03 §10).</summary>
public sealed record ContractDef(
    string Id,
    string Archetype,
    int Tier,
    int BasePayout,
    IReadOnlyList<string> AllowedBiomeIds,
    IReadOnlyList<ContractObjectiveSpec> PrimaryObjectives,
    string DomainInteraction,
    string Description);

/// <summary>One primary objective inside a contract definition.</summary>
public sealed record ContractObjectiveSpec(
    string ObjectiveId,
    ContractObjectiveKind Kind,
    int Required,
    string TargetQuestItemId,
    string Description);

/// <summary>Field-installable module definition (docs/06 §3). Data is authoritative here;
/// loadout wiring lives outside the domain.</summary>
public sealed record ModuleDef(
    string Id,
    string Category,
    int Tier,
    float PowerIdle,
    float PowerActive,
    float NoiseMultiplier,
    float CooldownSeconds,
    IReadOnlyList<string> Tags,
    string ResearchBranch,
    int ResearchCost);

/// <summary>Difficulty definition (docs/00 §10). Multipliers apply to host-side systems only.</summary>
public sealed record DifficultyDef(
    string Id,
    string DisplayName,
    float DetectMultiplier,
    float ThreatRateMultiplier,
    float BreachProbabilityMultiplier,
    float RepairEfficiency,
    float ResourceMultiplier,
    float PayoutMultiplier,
    float PulseCooldownMultiplier,
    bool Ranked);

/// <summary>Creature definition driving the host-side FSM (docs/06 §2).</summary>
public sealed record CreatureDef(
    string Id,
    string DisplayName,
    bool IsApex,
    float BaseHearingMeters,
    float SpeedMetersPerSecond,
    float AttackRangeMeters,
    float AttackDamage,
    float Aggression,
    string Description);

/// <summary>Relic trait tag (docs/06 §4). Traits flavor loot and payouts; hazardous traits
/// add threat weight on recovery.</summary>
public sealed record RelicTraitDef(string Id, string DisplayName, float ThreatOnRecover);

/// <summary>
/// Contract modifier definition (docs/06 §5). Every entry documents a real,
/// implemented host-side effect; modifiers with presentation-only aspects say so.
/// </summary>
public sealed record ModifierDef(
    string Id,
    string DisplayName,
    string DomainEffect);
