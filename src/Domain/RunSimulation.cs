using System.Numerics;

namespace AbyssScav.Domain;

/// <summary>Host-side ship control input for one <see cref="RunSimulation.Tick"/> step.</summary>
/// <remarks>
/// The domain never integrates ship motion: presentation (Godot physics) owns the
/// pose and passes it back each tick. Only throttle-adjacent signals that drive
/// power/noise/threat math are consumed here.
/// </remarks>
public struct ShipControlInput
{
    /// <summary>Engine throttle 0..1 (clamped). Negative values are clamped, never applied.</summary>
    public float Throttle01;
    /// <summary>Silent running: caps engine output, disables active sonar.</summary>
    public bool SilentRunning;
    /// <summary>Short-burn boost: raises power draw and noise.</summary>
    public bool Boost;

    /// <summary>Creates input with safe clamping applied.</summary>
    public ShipControlInput(float throttle01, bool silentRunning, bool boost)
    {
        Throttle01 = float.IsFinite(throttle01) ? Math.Clamp(throttle01, 0f, 1f) : 0f;
        SilentRunning = silentRunning;
        Boost = boost;
    }
}

/// <summary>Run lifecycle phase (host-authoritative).</summary>
public enum RunPhase
{
    /// <summary>Run is live.</summary>
    Active = 0,
    /// <summary>Extraction succeeded; settlement draft is available.</summary>
    Extracted = 1,
    /// <summary>Run failed (hull loss or deadline); failure draft is available.</summary>
    Failed = 2,
}

/// <summary>Sonar signal class (docs/03 §4).</summary>
public enum SonarClass
{
    /// <summary>Terrain return.</summary>
    Terrain = 0,
    /// <summary>Structure return (extraction, objective, serviced nodes).</summary>
    Structure = 1,
    /// <summary>Salvage return.</summary>
    Salvage = 2,
    /// <summary>Biological return.</summary>
    Biological = 3,
    /// <summary>Unclassified return (includes false ghosts; host never labels them as such).</summary>
    Unknown = 4,
}

/// <summary>Creature FSM state (docs/03 §5).</summary>
public enum CreatureState
{
    /// <summary>Waiting; triggered by sound or sonar exposure.</summary>
    Dormant = 0,
    /// <summary>Moving to investigate a stimulus.</summary>
    Investigate = 1,
    /// <summary>Shadowing the ship at mid range.</summary>
    Stalk = 2,
    /// <summary>Committed pursuit.</summary>
    Hunt = 3,
    /// <summary>Striking the hull.</summary>
    Attack = 4,
    /// <summary>Breaking off; cooling down back to dormant.</summary>
    Disengage = 5,
}

/// <summary>Host event. Subscribe via <see cref="RunSimulation.EventRaised"/>; recent history is also kept in <see cref="RunSimulation.RecentEvents"/>.</summary>
public sealed record RunEvent(float TimeSeconds, string Kind, string Message, params object[] Args);

/// <summary>One flood zone (docs/03 §1: exactly 4 compartments).</summary>
public sealed record FloodZoneState(string ZoneId, int Severity, float FloodPercent);

/// <summary>One secured cargo item. Quest items are unique per run.</summary>
public sealed record CargoItem(string LootSpawnId, LootKind Kind, int ValueCredits, float MassKg, string QuestItemId);

/// <summary>Sonar contact snapshot for presentation. Positions are approximate; error shrinks with confidence.</summary>
public sealed record ContactSnapshot(string ContactId, SonarClass Class, Vector3 ApproxPosition, float RangeMeters, float Confidence, bool Surveyed);

/// <summary>Creature snapshot for presentation (positions are host-authoritative approximations, not physics).</summary>
public sealed record CreatureSnapshot(string Id, string CreatureId, Vector3 Position, CreatureState State, bool IsApex);

/// <summary>Contract objective progress.</summary>
public sealed record ObjectiveProgress(string ObjectiveId, ContractObjectiveKind Kind, int Required, int Current, bool IsComplete);

/// <summary>Contract progress snapshot.</summary>
public sealed record ContractProgressSnapshot(
    string ContractId,
    IReadOnlyList<ObjectiveProgress> Objectives,
    bool PrimaryComplete,
    int PulsesUsed,
    int SurveysDone,
    float ObserveSeconds);

/// <summary>Result of <see cref="RunSimulation.Pulse"/>.</summary>
public sealed record PulseResult(bool Success, string Reason, IReadOnlyList<ContactSnapshot> Contacts, float ThreatAdded, float CooldownSeconds, params object[] Args);

/// <summary>Result of <see cref="RunSimulation.TrySalvage"/>.</summary>
public sealed record SalvageResult(bool Success, string Reason, int ValueBanked, string QuestItemId, params object[] Args);

/// <summary>Result of <see cref="RunSimulation.TrySurvey"/>.</summary>
public sealed record SurveyResult(bool Success, string Reason, params object[] Args);

/// <summary>Result of <see cref="RunSimulation.TryRepairHull"/>.</summary>
public sealed record RepairResult(bool Success, string Reason, int SealantRemaining, params object[] Args);

/// <summary>Result of <see cref="RunSimulation.TryServiceContractNode"/>.</summary>
public sealed record ServiceResult(bool Success, string Reason, int SealantRemaining, params object[] Args);

/// <summary>Result of <see cref="RunSimulation.TryUseWinch"/>.</summary>
public sealed record WinchResult(bool Success, string Reason, Vector3 ReturnPosition, params object[] Args);

/// <summary>Result of <see cref="RunSimulation.TryDock"/>.</summary>
public sealed record DockResult(bool Success, string Reason, string? StationId, params object[] Args);

/// <summary>Result of <see cref="RunSimulation.TryUndock"/>.</summary>
public sealed record UndockResult(bool Success, string Reason, params object[] Args);

/// <summary>Result of drill start/cancel calls.</summary>
public sealed record DrillResult(bool Success, string Reason, params object[] Args);

/// <summary>Result of the tutorial-only training breach injection.</summary>
public sealed record TrainingBreachResult(bool Success, string Reason, int ZoneIndex, params object[] Args);

/// <summary>Result of <see cref="RunSimulation.TryExtract"/>.</summary>
public sealed record ExtractResult(bool Success, string Reason, RunSettlementDraft? Settlement, params object[] Args);

/// <summary>
/// Settlement draft produced by the host. It is a plain, signed-free data bag the
/// persistence layer maps into its own idempotent settlement envelope later.
/// <see cref="RunSimulation.RunInstanceId"/> scopes <see cref="RunSettlementDraft.SettlementId"/>
/// to one host run instance: replays of the same seed mint distinct IDs, so the
/// idempotency key never denies a replay its payout, while a repeated draft from
/// the same instance reuses its ID and applies exactly once.
/// <para>
/// INTEGRATION (presentation/persistence coder): map to
/// <c>AbyssScav.Persistence.SettlementPayload</c> as
/// <c>new SettlementPayload(draft.SettlementId, draft.RetainedCredits, draft.ResearchData, draft.Shards,
/// Array.Empty&lt;string&gt;(), Array.Empty&lt;string&gt;(), Array.Empty&lt;string&gt;(),
/// draft.Outcome == SettlementOutcome.Success ? 1 : 0,
/// draft.Outcome == SettlementOutcome.Failed ? 1 : 0)</c>,
/// then apply through the existing idempotent path (docs/16 §9). Blueprint and
/// frame unlocks are intentionally empty here: discovery persistence stays on the
/// profile side, which owns that policy.
/// </para>
/// </summary>
public sealed record RunSettlementDraft(
    string SettlementId,
    ulong RunSeed,
    string BiomeId,
    string ContractId,
    string DifficultyId,
    string InsuranceId,
    SettlementOutcome Outcome,
    long BasePayout,
    long SecuredSalvageValue,
    long ObjectiveBonus,
    long RiskBonus,
    long RepairCost,
    long Credits,
    long RetainedCredits,
    long ResearchData,
    long Shards);

/// <summary>Settlement outcome carried by <see cref="RunSettlementDraft"/>.</summary>
public enum SettlementOutcome
{
    /// <summary>Objectives complete, extraction reached.</summary>
    Success = 0,
    /// <summary>Hull loss or deadline; insurance retention applied.</summary>
    Failed = 1,
}

/// <summary>
/// Host-only solo run simulation (docs/03). Owns hull, power, noise, threat,
/// pressure stress, flooding, sonar contacts, creature FSM, cargo, contract
/// progress, extraction, repair resources, and the one-use winch.
/// <para>
/// INTEGRATION (presentation coder): create with <see cref="TryCreate"/>, then per
/// physics frame call <see cref="Tick"/> with the Godot ship pose and a
/// <see cref="ShipControlInput"/>. Wire player verbs to <see cref="Pulse"/>,
/// <see cref="TrySalvage"/>, <see cref="TrySurvey"/>, <see cref="TryRepairHull"/>,
/// <see cref="TryServiceContractNode"/>, <see cref="TryUseWinch"/>, and
/// <see cref="TryExtract"/>. Read state properties to drive HUD; subscribe to
/// <see cref="EventRaised"/> for creaks, breaches, brownouts, AI shifts, and
/// objective updates. Every mutating call validates first and returns a reason —
/// there are no silent failures and no fake successes.
/// </para>
/// </summary>
public sealed class RunSimulation
{
    /// <summary>Insurance: no coverage. Retention 20%, rate 0% (free).</summary>
    public const string InsuranceNone = "insurance.none";
    /// <summary>
    /// Insurance: basic. Retention 50%, rate 8% of departure cost.
    /// The domain defines no departure cost and deducts nothing: the rate only
    /// prices a future charge flow, and retention math assumes the caller has paid.
    /// Keep paid selectors disabled until that flow exists.
    /// </summary>
    public const string InsuranceBasic = "insurance.basic";
    /// <summary>
    /// Insurance: premium. Retention 70%, rate 15% of departure cost.
    /// Same no-charge caveat as <see cref="InsuranceBasic"/>.
    /// </summary>
    public const string InsurancePremium = "insurance.premium";

    /// <summary>
    /// Priced insurance policy: retention paid on secured cargo after a failed run,
    /// plus the docs/05 §4 rate as a fraction of the (not yet defined) departure cost.
    /// </summary>
    public sealed record InsuranceQuote(string InsuranceId, double Retention, double Rate);

    /// <summary>
    /// Returns the priced policy for an insurance ID without creating a run or
    /// charging anything. Returns false with a reason for unknown IDs.
    /// </summary>
    public static bool TryGetInsuranceQuote(string insuranceId, out InsuranceQuote? quote, out string reason)
    {
        quote = insuranceId switch
        {
            InsuranceNone => new InsuranceQuote(InsuranceNone, 0.2, 0.0),
            InsuranceBasic => new InsuranceQuote(InsuranceBasic, 0.5, 0.08),
            InsurancePremium => new InsuranceQuote(InsurancePremium, 0.7, 0.15),
            _ => null,
        };
        if (quote is null)
        {
            reason = $"CONTENT-205 unknown insurance '{insuranceId}'.";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    private static readonly string[] ZoneIds =
    {
        "compartment.fore", "compartment.midship", "compartment.aft", "compartment.engineering",
    };

    private readonly GeneratedWorld _world;
    private readonly BiomeDef _biome;
    private readonly ContractDef _contract;
    private readonly DifficultyDef _difficulty;
    private readonly SubFrameDef _frame;
    private readonly HashSet<string> _modifierIds;
    private readonly string _insuranceId;
    private readonly string[] _moduleIds;
    private readonly ModuleLoadout.LoadoutEffects _loadout;
    private readonly float _hullRatingEffective;
    private readonly Guid _runInstanceId;
    private RunSettlementDraft? _failureDraft;

    private DeterministicRandom _pressureRng;
    private DeterministicRandom _aiRng;
    private DeterministicRandom _eventRng;
    private DeterministicRandom _sonarRng;

    private readonly int[] _severity = new int[4];
    private readonly float[] _flood = new float[4];
    private readonly List<CargoItem> _cargo = new();
    private readonly HashSet<string> _questRecovered = new(StringComparer.Ordinal);
    private readonly HashSet<string> _servicedNodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ContactRuntime> _contacts = new(StringComparer.Ordinal);
    private readonly List<CreatureRuntime> _creatures = new();
    private readonly HashSet<string> _collectedLoot = new(StringComparer.Ordinal);
    private readonly Queue<RunEvent> _recent = new();
    private int _salvageValueProgress;
    private int _surveyProgress;
    private float _observeSeconds;

    private float _stressAccumulator;
    private float _creakTimer;
    private float _passiveTimer;
    private float _surgeTimer;
    private float _burstTimer;
    private float _weldTimer;
    private float _drainTimer;
    private float _dipTimer;
    private float _dipCooldown;
    private float _noiseSpike;
    private float _safeTrackTimer;
    private float _pressureEventMod = 1f;
    private bool _silentRunning;    private Vector3 _lastPulsePosition;
    private bool _hasPulsed;
    private readonly bool _isTutorialRun;
    private bool _trainingBreachInjected;
    private float _shipSpeedMps;
    private string? _dockedNodeId;
    private Vector3 _dockAnchor;
    private string? _drillLootSpawnId;
    private float _drillElapsedSeconds;

    private RunSimulation(
        GeneratedWorld world, ContentCatalog catalog, DifficultyDef difficulty,
        SubFrameDef frame, HashSet<string> modifiers, string insuranceId,
        string[] moduleIds, ModuleLoadout.LoadoutEffects loadout, bool isTutorialRun)
    {
        _world = world;
        _biome = catalog.Biomes[world.BiomeId];
        _contract = catalog.Contracts[world.ContractId];
        _difficulty = difficulty;
        _frame = frame;
        _modifierIds = modifiers;
        _insuranceId = insuranceId;
        _moduleIds = moduleIds;
        _loadout = loadout;
        _hullRatingEffective = frame.HullRating + loadout.HullRatingBonus;
        // Unique host run instance: replays of the same seed are distinct runs, so
        // each instance mints distinct settlement IDs and idempotency never denies
        // a replay its payout. World generation stays seed-deterministic and the
        // layout hash is untouched by this ID.
        _isTutorialRun = isTutorialRun;
        _runInstanceId = Guid.NewGuid();

        _pressureRng = new DeterministicRandom(DeterministicRandom.Derive(world.RunSeed, GenStreams.Pressure));
        _aiRng = new DeterministicRandom(DeterministicRandom.Derive(world.RunSeed, GenStreams.Ai));
        _eventRng = new DeterministicRandom(DeterministicRandom.Derive(world.RunSeed, GenStreams.Event));
        _sonarRng = new DeterministicRandom(DeterministicRandom.Derive(world.RunSeed, GenStreams.Sonar));

        MaxHull = (float)Math.Round(frame.MaxHull * (modifiers.Contains("modifier.fragile_hull") ? 0.75 : 1.0) + loadout.MaxHullBonus);
        HullIntegrity = MaxHull;
        Sealant = Math.Max(1, (int)Math.Round(3 * difficulty.ResourceMultiplier));
        ShipPosition = world.GetNode(world.ExtractionNodeId).Position;

        foreach (var spawn in world.ThreatSpawns)
        {
            var def = catalog.Creatures[spawn.CreatureId];
            _creatures.Add(new CreatureRuntime(spawn.SpawnId, def, spawn.Position, spawn.IsApex));
        }

        ElapsedSeconds = 0f;
        Phase = RunPhase.Active;
        FailureReason = string.Empty;
        LastSafePosition = ShipPosition;
        HasSafePosition = true;

        Raise("run.start", "Run {0} started in {1} on {2}.", _world.RunSeed, _biome.DisplayName, _contract.Archetype);
    }

    // ------------------------------------------------------------ construction

    /// <summary>
    /// Creates a host simulation for a validated world.
    /// INTEGRATION: <c>RunSimulation.TryCreate(world, catalog, "difficulty.standard", "frame.skiff", null, "insurance.basic", out var sim, out var reason)</c>.
    /// Returns false with a reason for unknown difficulty/frame/modifier/insurance
    /// IDs or an invalid world; never returns a half-built simulation.
    /// <para>
    /// Optional <paramref name="moduleIds"/> equips field modules (one slot per
    /// category, at most the 6 supported by <see cref="ModuleLoadout"/>): unknown,
    /// duplicate, category-colliding, or unsupported IDs are rejected, and when
    /// <paramref name="ownedBlueprints"/> is non-null, unowned IDs are rejected
    /// too. Omitted modules default to an empty (stock) loadout, so older calls
    /// behave exactly as before. Blueprints are never consumed by equipping.
    /// </para>
    /// </summary>
    public static bool TryCreate(
        GeneratedWorld world,
        ContentCatalog catalog,
        string difficultyId,
        string frameId,
        IEnumerable<string>? modifierIds,
        string insuranceId,
        out RunSimulation? simulation,
        out string reason,
        IEnumerable<string>? moduleIds = null,
        IReadOnlyCollection<string>? ownedBlueprints = null,
        bool isTutorialRun = false)
    {
        simulation = null;
        reason = string.Empty;

        if (world is null) { reason = "CONTENT-201 run world is null."; return false; }
        if (catalog is null) { reason = "CONTENT-202 content catalog is null."; return false; }
        if (!catalog.Difficulties.TryGetValue(difficultyId, out var difficulty))
        { reason = $"CONTENT-203 unknown difficulty '{difficultyId}'."; return false; }
        if (!catalog.Frames.TryGetValue(frameId, out var frame))
        { reason = $"CONTENT-204 unknown frame '{frameId}'."; return false; }
        if (insuranceId is not (InsuranceNone or InsuranceBasic or InsurancePremium))
        { reason = $"CONTENT-205 unknown insurance '{insuranceId}'."; return false; }
        if (!ModuleLoadout.TryValidate(moduleIds, catalog, ownedBlueprints, out var modErrors))
        { reason = string.Join("; ", modErrors); return false; }

        var mods = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in modifierIds ?? Array.Empty<string>())
        {
            if (!catalog.Modifiers.ContainsKey(m))
            { reason = $"CONTENT-206 unknown modifier '{m}'."; return false; }
            mods.Add(m);
        }

        var verdict = TrenchGenerator.Validate(world, catalog);
        if (!verdict.IsValid)
        { reason = "GEN-060 world failed validation: " + string.Join("; ", verdict.Errors); return false; }

        simulation = new RunSimulation(world, catalog, difficulty, frame, mods, insuranceId,
            (moduleIds ?? Array.Empty<string>()).ToArray(), ModuleLoadout.Resolve(moduleIds), isTutorialRun);
        return true;
    }

    // ------------------------------------------------------------------ state

    /// <summary>Generated world backing this run.</summary>
    public GeneratedWorld World => _world;

    /// <summary>
    /// Unique host run-instance ID, assigned at creation. Distinct instances over
    /// the same seed produce identical worlds but distinct settlement IDs.
    /// </summary>
    public Guid RunInstanceId => _runInstanceId;

    /// <summary>Current lifecycle phase.</summary>
    public RunPhase Phase { get; private set; }

    /// <summary>Failure reason when <see cref="Phase"/> is <see cref="RunPhase.Failed"/>.</summary>
    public string FailureReason { get; private set; }

    /// <summary>Authoritative ship position as last supplied to <see cref="Tick"/> (meters).</summary>
    public Vector3 ShipPosition { get; private set; }

    /// <summary>Current depth in meters (biome mean minus local Y).</summary>
    public float DepthMeters => _biome.MeanDepthMeters - ShipPosition.Y;

    /// <summary>Elapsed run time in seconds.</summary>
    public float ElapsedSeconds { get; private set; }

    /// <summary>Hull integrity 0..<see cref="MaxHull"/>.</summary>
    public float HullIntegrity { get; private set; }

    /// <summary>Maximum hull after modifier and loadout adjustment.</summary>
    public float MaxHull { get; }

    /// <summary>Equipped module IDs (immutable snapshot; empty means stock).</summary>
    public IReadOnlyList<string> ModuleIds => _moduleIds;

    /// <summary>Effective hull pressure rating after loadout (frame rating + bonus).</summary>
    public float HullRatingEffective => _hullRatingEffective;

    /// <summary>Actual active-pulse range in meters (biome × loadout × blackout).</summary>
    public float ActivePulseRangeMeters
    {
        get
        {
            var range = _biome.PulseRangeMeters * _loadout.PulseRangeMult;
            if (_modifierIds.Contains("modifier.sonar_blackout")) range *= 0.7f;
            return range;
        }
    }

    /// <summary>Actual passive-sonar range in meters (biome × loadout × visibility).</summary>
    public float PassiveSonarRangeMeters
    {
        get
        {
            var range = _biome.PassiveRangeMeters * _loadout.PassiveRangeMult;
            if (_modifierIds.Contains("modifier.low_visibility")) range *= 0.7f;
            return range;
        }
    }

    /// <summary>Actual salvage (manipulator) reach in meters (15 stock, 24 with magnet).</summary>
    public float SalvageRangeMeters => _loadout.SalvageRangeMeters;

    /// <summary>Engine noise multiplier from loadout (0.6 with quiet prop, else 1).</summary>
    public float EngineNoiseMultiplier => _loadout.EngineNoiseMult;

    /// <summary>
    /// Engine thrust multiplier from loadout (0.85 with quiet prop, else 1).
    /// Presentation must apply this to physical thrust; the quiet-running cap
    /// still applies on top.
    /// </summary>
    public float EngineThrustMultiplier => _loadout.EngineThrustMult;

    /// <summary>Available power supply in PU (after reactor dips).</summary>
    public float PowerSupply { get; private set; }

    /// <summary>Current power demand in PU.</summary>
    public float PowerDemand { get; private set; }

    /// <summary>Load-shed level: 0 none, 1 sonar degraded, 2 sonar offline plus brownout.</summary>
    public int PowerShedLevel { get; private set; }

    /// <summary>True while demand exceeds supply by more than 20 PU.</summary>
    public bool BrownoutActive { get; private set; }

    /// <summary>Normalized noise 0..100.</summary>
    public float Noise { get; private set; }

    /// <summary>Hidden threat clock 0..100 (docs/00 §5).</summary>
    public float Threat { get; private set; }

    /// <summary>Hull rating minus effective pressure; negative means stress checks.</summary>
    public float PressureMargin { get; private set; }

    /// <summary>Remaining hull-sealant charges. Never negative: spends are checked first.</summary>
    public int Sealant { get; private set; }

    /// <summary>Sealant charges spent (drives repair-cost settlement math).</summary>
    public int SealantUsed { get; private set; }

    /// <summary>Active-pulse cooldown remaining in seconds.</summary>
    public float PulseCooldownRemaining { get; private set; }

    /// <summary>Pulses fired this run (drives no-ping bonus and threat).</summary>
    public int PulsesUsed { get; private set; }

    /// <summary>Contacts successfully surveyed.</summary>
    public int SurveysDone { get; private set; }

    /// <summary>Secured salvage credit value banked via <see cref="TrySalvage"/>.</summary>
    public int SecuredSalvageValue { get; private set; }

    /// <summary>Cargo slots used.</summary>
    public int CargoUsedSlots => _cargo.Count;

    /// <summary>Cargo slots fitted.</summary>
    public int CargoMaxSlots => _frame.CargoSlots;

    /// <summary>Cargo mass used in kg.</summary>
    public float CargoUsedMassKg { get; private set; }

    /// <summary>Cargo mass capacity in kg.</summary>
    public float CargoMaxMassKg => _frame.CargoMaxMassKg;

    /// <summary>Secured cargo snapshot.</summary>
    public IReadOnlyList<CargoItem> CargoItems => _cargo;

    /// <summary>Quest items recovered this run.</summary>
    public IReadOnlyList<string> QuestItemsRecovered => _questRecovered.ToList();

    /// <summary>Flood zones snapshot (exactly 4).</summary>
    public IReadOnlyList<FloodZoneState> FloodZones
    {
        get
        {
            var list = new List<FloodZoneState>(4);
            for (var i = 0; i < 4; i++) list.Add(new FloodZoneState(ZoneIds[i], _severity[i], _flood[i]));
            return list;
        }
    }

    /// <summary>Known contacts snapshot (fresh only; expired contacts drop out).</summary>
    public IReadOnlyList<ContactSnapshot> Contacts
    {
        get
        {
            var list = new List<ContactSnapshot>();
            foreach (var c in _contacts.Values)
            {
                if (ElapsedSeconds - c.LastSeenSeconds > 60f) continue;
                list.Add(new ContactSnapshot(c.Id, c.Class, c.DisplayPosition, c.RangeMeters, c.Confidence, c.Surveyed));
            }
            return list;
        }
    }

    /// <summary>Creature snapshot for presentation.</summary>
    public IReadOnlyList<CreatureSnapshot> CreatureStates =>
        _creatures.Select(c => new CreatureSnapshot(c.Id, c.Def.Id, c.Position, c.State, c.IsApex)).ToList();

    /// <summary>Last recorded safe position for the one-use winch.</summary>
    public Vector3 LastSafePosition { get; private set; }

    /// <summary>True when a safe position has been recorded.</summary>
    public bool HasSafePosition { get; private set; }

    /// <summary>True once the one-use winch has been spent.</summary>
    public bool WinchUsed { get; private set; }

    /// <summary>True while docked at a station node. Docking freezes the ship at a safe offset; the domain never teleports.</summary>
    public bool IsDocked => _dockedNodeId is not null;

    /// <summary>Docked station node ID (null when undocked).</summary>
    public string? DockedNodeId => _dockedNodeId;

    /// <summary>Ship pose captured at docking (safe offset, never a cross-map teleport).</summary>
    public Vector3 DockAnchor => _dockAnchor;

    /// <summary>Host-measured ship speed in m/s from pose deltas (authoritative for dock validation when no explicit reading is supplied).</summary>
    public float ShipSpeedMps => _shipSpeedMps;

    /// <summary>True while the drill bit is turning on a pending target.</summary>
    public bool IsDrilling => _drillLootSpawnId is not null;

    /// <summary>Pending drill target spawn ID (null when idle).</summary>
    public string? DrillingLootSpawnId => _drillLootSpawnId;

    /// <summary>Drill elapsed seconds, bounded 0..<see cref="DomainConstants.DrillDurationSeconds"/>.</summary>
    public float DrillElapsedSeconds => _drillElapsedSeconds;

    /// <summary>Drill duration in seconds (bounded 8 s).</summary>
    public float DrillDurationSeconds => DomainConstants.DrillDurationSeconds;

    /// <summary>Drill seconds remaining.</summary>
    public float DrillRemainingSeconds => Math.Max(0f, DomainConstants.DrillDurationSeconds - _drillElapsedSeconds);

    /// <summary>Actual drill reach in meters (manipulator reach + 10 m station assist).</summary>
    public float DrillRangeMeters => SalvageRangeMeters + DomainConstants.DrillExtraReachMeters;

    /// <summary>Successful hull repairs this run (tutorial T0 counts these, never breach-state polling).</summary>
    public int RepairsDone { get; private set; }

    /// <summary>True for tutorial-scenario runs (enables the one-off training breach).</summary>
    public bool IsTutorialRun => _isTutorialRun;

    /// <summary>Settlement draft after extraction or failure (null while active).</summary>
    public RunSettlementDraft? Settlement { get; private set; }

    /// <summary>Recent host events (ring buffer, last 128).</summary>
    public IReadOnlyList<RunEvent> RecentEvents => _recent.ToList();

    /// <summary>Raised for every host event (creaks, breaches, brownouts, AI shifts, objectives).</summary>
    public event Action<RunEvent>? EventRaised;

    /// <summary>Contract progress snapshot.</summary>
    public ContractProgressSnapshot Contract
    {
        get
        {
            var list = new List<ObjectiveProgress>();
            foreach (var spec in _contract.PrimaryObjectives)
            {
                var current = spec.Kind switch
                {
                    ContractObjectiveKind.SalvageValue => _salvageValueProgress,
                    ContractObjectiveKind.QuestItem => QuestProgress(spec),
                    ContractObjectiveKind.ServiceNode => ServiceProgress(spec),
                    ContractObjectiveKind.Survey => _surveyProgress,
                    ContractObjectiveKind.ObserveApex => (int)Math.Floor(_observeSeconds),
                    _ => 0,
                };
                list.Add(new ObjectiveProgress(spec.ObjectiveId, spec.Kind, spec.Required, current, current >= spec.Required));
            }
            return new ContractProgressSnapshot(_contract.Id, list, list.All(o => o.IsComplete), PulsesUsed, SurveysDone, _observeSeconds);
        }
    }

    // -------------------------------------------------------------------- tick

    /// <summary>
    /// Advances the host simulation by <paramref name="dtSeconds"/> using the
    /// presentation-supplied ship pose. <paramref name="dtSeconds"/> is clamped to
    /// (0, 0.5]; non-positive or non-finite steps are ignored. No-ops once the run
    /// has ended. Ship motion is never integrated here.
    /// </summary>
    public void Tick(float dtSeconds, Vector3 shipPosition, ShipControlInput input)
    {
        if (Phase != RunPhase.Active) return;
        if (!float.IsFinite(dtSeconds) || dtSeconds <= 0f) return;
        if (!IsFinite(shipPosition)) return;
        var dt = Math.Min(dtSeconds, 0.5f);

        // Host-measured speed from pose deltas (m/s). Teleports in tests read
        // high for one tick and settle to zero on the next idle tick, so dock
        // validation observes a real approach speed, never a guessed one.
        var travel = Vector3.Distance(shipPosition, ShipPosition);
        _shipSpeedMps = dt > 0f ? travel / dt : 0f;
        ShipPosition = shipPosition;
        ElapsedSeconds += dt;
        _silentRunning = input.SilentRunning;

        if (_modifierIds.Contains("modifier.time_window") && ElapsedSeconds > 2400f)
        {
            FailRun("Extraction window expired before objectives were delivered.");
            return;
        }

        TrackSafePosition(dt);

        // Power.
        var thr = input.SilentRunning ? Math.Min(input.Throttle01, 0.25f) : input.Throttle01;
        var floodedCount = 0;
        for (var i = 0; i < 4; i++) if (_flood[i] > 0.5f || _severity[i] > 0) floodedCount++;
        _burstTimer = Math.Max(0f, _burstTimer - dt);
        _weldTimer = Math.Max(0f, _weldTimer - dt);
        _drainTimer = Math.Max(0f, _drainTimer - dt);

        var supply = _frame.PowerSupply;
        if (_modifierIds.Contains("modifier.reactor_instability"))
        {
            _dipCooldown -= dt;
            if (_dipCooldown <= 0f)
            {
                _dipTimer = 5f;
                _dipCooldown = _eventRng.NextFloat(20f, 40f);
                Raise("power.dip", "Reactor instability: supply dip for 5 s.");
            }
        }
        _dipTimer = Math.Max(0f, _dipTimer - dt);
        if (_dipTimer > 0f) supply -= 30f;
        PowerSupply = Math.Max(0f, supply);

        var demand = 15f; // life support (protected, never shed)
        demand += 10f + 35f * thr * (input.Boost ? 1.5f : 1f);
        demand += Math.Min(30f, 5f + 8f * floodedCount);
        if (_burstTimer > 0f) demand += 20f;
        if (_weldTimer > 0f) demand += 25f;
        if (_drainTimer > 0f) demand += 10f;
        if (_drillLootSpawnId is not null) demand += DomainConstants.DrillPowerDrawPU;
        if (_modifierIds.Contains("modifier.severe_current")) demand += 10f;
        PowerDemand = demand;

        var deficit = demand - PowerSupply;
        var shed = deficit <= 0f ? 0 : deficit > 20f ? 2 : 1;
        if (shed != PowerShedLevel)
        {
            PowerShedLevel = shed;
            Raise("power.shed", shed == 0 ? "Power load nominal." : "Power deficit {0:F0} PU: shed level {1}.", deficit, shed);
        }
        var brownout = deficit > 20f;
        if (brownout && !BrownoutActive) Raise("power.brownout", "Brownout: active sonar offline until load drops.");
        BrownoutActive = brownout;

        // Noise.
        var engineTerm = (8f + 45f * thr * (input.Boost ? 1.3f : 1f)) * _frame.NoiseFactor * _loadout.EngineNoiseMult;
        _noiseSpike = Math.Max(0f, _noiseSpike - 18f * dt);
        var target = engineTerm + _noiseSpike;
        if (_drillLootSpawnId is not null) target += 18f; // drill bit + cuttings pump.
        if (_modifierIds.Contains("modifier.severe_current")) target += 8f;
        target = Math.Clamp(target, 0f, 100f);
        var slew = 25f * dt;
        Noise = Math.Abs(target - Noise) <= slew ? target : Noise + Math.Sign(target - Noise) * slew;

        // Threat ramp (docs/00 §5): time + noise coupling + apex presence.
        var nesting = _modifierIds.Contains("modifier.nesting_season") ? 1.2f : 1f;
        var apexWeight = _creatures.Any(c => c.IsApex) ? 0.1f : 0f;
        Threat = Math.Min(100f, Threat + (0.15f + (Noise / 100f) * 0.6f * _difficulty.DetectMultiplier + apexWeight) * _difficulty.ThreatRateMultiplier * nesting * dt);
        if (_drillLootSpawnId is not null)
            Threat = Math.Min(100f, Threat + DomainConstants.DrillThreatPerSecond * dt);

        UpdateDrill(dt);

        // Pressure.
        var depthPressure = Math.Max(0f, DepthMeters) / 100f;
        if (_surgeTimer > 0f) { _surgeTimer -= dt; if (_surgeTimer <= 0f) { _pressureEventMod = 1f; Raise("pressure.eased", "Pressure surge eased."); } }
        var effective = depthPressure * _biome.PressureModifier * _pressureEventMod;
        PressureMargin = _hullRatingEffective - effective;

        _stressAccumulator += dt;
        _creakTimer += dt;
        if (PressureMargin < 5f && _pressureEventMod == 1f && _creakTimer > 5f)
        {
            _creakTimer = 0f;
            if (_eventRng.Chance(0.2))
            {
                _surgeTimer = 20f;
                _pressureEventMod = 1.2f;
                Raise("pressure.surge", "Pressure surge: effective load +20% for 20 s.");
            }
        }
        if (_stressAccumulator >= 1f)
        {
            _stressAccumulator = 0f;
            if (PressureMargin < 0f)
            {
                if (_creakTimer > 9f)
                {
                    _creakTimer = 0f;
                    Raise("hull.creak", "Hull creak at {0:F0} m (margin {1:F1}).", DepthMeters, PressureMargin);
                }
                var p = 0.05 * ((-PressureMargin) / 10.0 + 0.2) * _difficulty.BreachProbabilityMultiplier;
                if (_modifierIds.Contains("modifier.fragile_hull")) p *= 1.3;
                if (_pressureRng.Chance(p)) InflictBreach("pressure stress");
            }
        }

        // Flooding: breaches fill; pumps always fight back (protected load).
        for (var i = 0; i < 4; i++)
        {
            if (_severity[i] > 0)
            {
                var rate = _severity[i] switch { 1 => 1.5f, 2 => 4f, _ => 9f };
                _flood[i] = Math.Min(100f, _flood[i] + rate * dt);
            }
            if (_flood[i] > 0f && PowerShedLevel < 2)
                _flood[i] = Math.Max(0f, _flood[i] - 3f * dt);
        }

        PulseCooldownRemaining = Math.Max(0f, PulseCooldownRemaining - dt);

        UpdatePassiveContacts(dt);
        UpdateCreatures(dt, thr);

        // Apex observation accumulates only after a pulse has revealed the apex.
        if (_contract.Id == "contract.apex_observe")
        {
            var apex = NearestRevealedApex();
            if (apex is not null && Vector3.Distance(apex.Position, ShipPosition) <= DomainConstants.ObserveRangeMeters)
            {
                var before = (int)Math.Floor(_observeSeconds);
                _observeSeconds += dt;
                if ((int)Math.Floor(_observeSeconds) != before)
                    Raise("contract.observe", "Apex observation {0:F0} s.", _observeSeconds);
                if (before < RequiredObserve() && _observeSeconds >= RequiredObserve())
                    Raise("contract.objective", "Apex observation complete. Extract.");
            }
        }

        if (HullIntegrity <= 0f)
        {
            HullIntegrity = 0f;
            FailRun("Hull integrity zero: submersible lost.");
        }
    }

    // --------------------------------------------------------------- sonar/api

    /// <summary>
    /// Emits an active 360° pulse (docs/03 §4). Fails while silent, while the pulse
    /// is cooling down, or during a level-2 power shed. Reveals contacts in range
    /// with confidence per the spec ladder (0.3 first ping, +0.2 repeat,
    /// +0.25 triangulation, +0.15 close). Adds threat and a noise spike.
    /// INTEGRATION: <c>var r = sim.Pulse(); if (r.Success) DrawContacts(r.Contacts);</c>
    /// </summary>
    public PulseResult Pulse()
    {
        if (Phase != RunPhase.Active)
            return new PulseResult(false, "Run is not active.", Array.Empty<ContactSnapshot>(), 0f, PulseCooldownRemaining, Array.Empty<object>());
        if (_silentRunning)
            return new PulseResult(false, "Silent running: active sonar disabled.", Array.Empty<ContactSnapshot>(), 0f, PulseCooldownRemaining, Array.Empty<object>());
        if (PulseCooldownRemaining > 0f)
            return new PulseResult(false, "Pulse cooling down ({0:F1} s).", Array.Empty<ContactSnapshot>(), 0f, PulseCooldownRemaining, PulseCooldownRemaining);
        if (BrownoutActive || PowerShedLevel >= 2)
            return new PulseResult(false, "Pulse offline: shed level 2 brownout.", Array.Empty<ContactSnapshot>(), 0f, PulseCooldownRemaining, Array.Empty<object>());

        var range = _biome.PulseRangeMeters * _loadout.PulseRangeMult;
        if (_modifierIds.Contains("modifier.sonar_blackout")) range *= 0.7f;

        var moved = !_hasPulsed || Vector3.Distance(ShipPosition, _lastPulsePosition) > 30f;
        _lastPulsePosition = ShipPosition;
        _hasPulsed = true;

        var fresh = new HashSet<string>(StringComparer.Ordinal);
        void Upsert(string id, SonarClass cls, Vector3 truePos, float dist)
        {
            fresh.Add(id);
            if (!_contacts.TryGetValue(id, out var c))
            {
                c = new ContactRuntime(id, cls, truePos);
                _contacts[id] = c;
            }
            c.Class = cls;
            c.TruePosition = truePos;
            c.RangeMeters = dist;
            c.LastSeenSeconds = ElapsedSeconds;
            c.Confidence = Math.Min(1f, c.Confidence + (c.Pings == 0 ? 0.3f : 0.2f));
            if (moved && !c.Triangulated && c.Pings > 0)
            {
                c.Triangulated = true;
                c.Confidence = Math.Min(1f, c.Confidence + 0.25f);
            }
            if (dist < 100f) c.Confidence = Math.Min(1f, c.Confidence + 0.15f);
            c.Pings++;
            // Display error shrinks with confidence: up to 20 m at first ping.
            var err = (1f - c.Confidence) * 20f;
            c.DisplayPosition = err <= 0.01f ? truePos : truePos + new Vector3(
                _sonarRng.NextFloat(-err, err), _sonarRng.NextFloat(-err, err), _sonarRng.NextFloat(-err, err));
        }

        foreach (var node in _world.Nodes)
        {
            var dist = Vector3.Distance(node.Position, ShipPosition);
            if (dist > range) continue;
            var cls = node.Kind is WorldNodeKind.Objective || node.Kind is WorldNodeKind.Extraction || !string.IsNullOrEmpty(node.ServiceId)
                ? SonarClass.Structure : SonarClass.Terrain;
            Upsert("contact.node." + node.Id, cls, node.Position, dist);
        }
        foreach (var loot in _world.LootSpawns)
        {
            if (_collectedLoot.Contains(loot.SpawnId)) continue;
            var dist = Vector3.Distance(loot.Position, ShipPosition);
            if (dist > range) continue;
            Upsert("contact.loot." + loot.SpawnId, SonarClass.Salvage, loot.Position, dist);
        }
        foreach (var creature in _creatures)
        {
            var dist = Vector3.Distance(creature.Position, ShipPosition);
            if (dist > range * 0.9f) continue;
            Upsert("contact.bio." + creature.Id, SonarClass.Biological, creature.Position, dist);
            creature.SonarExposedSeconds = 15f;
        }

        // Ghosts: high threat breeds false returns; chorus colonies sing along.
        var ghosts = 0;
        if (Threat > 60f) ghosts++;
        foreach (var creature in _creatures)
        {
            if (creature.Def.Id == "creature.chorus_colony" &&
                Vector3.Distance(creature.Position, ShipPosition) < 300f && ghosts < 2)
                ghosts++;
        }
        for (var i = 0; i < ghosts; i++)
        {
            var bearing = _sonarRng.NextFloat(0f, MathF.PI * 2f);
            var dist = _sonarRng.NextFloat(100f, 300f);
            var pos = ShipPosition + new Vector3(MathF.Cos(bearing) * dist, _sonarRng.NextFloat(-20f, 20f), MathF.Sin(bearing) * dist);
            var id = $"contact.ghost.{PulsesUsed}.{i}";
            _contacts[id] = new ContactRuntime(id, SonarClass.Unknown, pos)
            {
                Confidence = 0.3f,
                RangeMeters = dist,
                DisplayPosition = pos,
                LastSeenSeconds = ElapsedSeconds,
                Pings = 1,
                IsGhost = true,
            };
            fresh.Add(id);
        }
        if (ghosts > 0) Raise("sonar.ghost", "Pulse returned possible false contacts.");

        PulsesUsed++;
        var added = 6f * _difficulty.DetectMultiplier * _loadout.PulseThreatMult;
        Threat = Math.Min(100f, Threat + added);
        _noiseSpike += 25f;
        _burstTimer = 2f;

        var cooldown = DomainConstants.PulseBaseCooldownSeconds * _difficulty.PulseCooldownMultiplier * _loadout.PulseCooldownMult;
        if (_modifierIds.Contains("modifier.sonar_blackout")) cooldown *= 2f;
        if (PowerShedLevel == 1) cooldown *= 1.5f;
        PulseCooldownRemaining = cooldown;

        Raise("sonar.pulse", "Active pulse: {0} contacts in range.", fresh.Count);
        return new PulseResult(true, string.Empty,
            fresh.Select(id => ToSnapshot(_contacts[id])).ToList(), added, PulseCooldownRemaining, Array.Empty<object>());
    }

    /// <summary>
    /// Attempts salvage of a loot spawn. Requires an active run, an uncollected
    /// spawn within <see cref="SalvageRangeMeters"/> (15 m stock, 24 m with the
    /// salvage magnet), a free cargo slot, and free mass. Quest items are unique: re-salvage is rejected.
    /// INTEGRATION: <c>var r = sim.TrySalvage(spawnId);</c> — show r.Reason on failure.
    /// </summary>
    public SalvageResult TrySalvage(string lootSpawnId)
    {
        if (Phase != RunPhase.Active)
            return new SalvageResult(false, "Run is not active.", 0, string.Empty, Array.Empty<object>());
        var loot = _world.LootSpawns.FirstOrDefault(l => l.SpawnId == lootSpawnId);
        if (loot is null)
            return new SalvageResult(false, "Unknown loot '{0}'.", 0, string.Empty, lootSpawnId);
        if (_collectedLoot.Contains(lootSpawnId))
            return new SalvageResult(false, "Already secured: duplicate salvage rejected.", 0, loot.QuestItemId, Array.Empty<object>());
        if (!string.IsNullOrEmpty(loot.QuestItemId) && _questRecovered.Contains(loot.QuestItemId))
            return new SalvageResult(false, "Quest item already secured: duplicate rejected.", 0, loot.QuestItemId, Array.Empty<object>());
        if (loot.Kind == LootKind.Core)
            return new SalvageResult(false, "Facility core requires docked drill extraction: dock (J) at the facility, then hold drill (H) for 8 s. Direct manipulator recovery is refused.", 0, loot.QuestItemId, Array.Empty<object>());
        var dist = Vector3.Distance(loot.Position, ShipPosition);
        if (dist > SalvageRangeMeters)
            return new SalvageResult(false, "Out of manipulator range ({0:F0} m, need {1:F0} m).", 0, loot.QuestItemId, dist, SalvageRangeMeters);
        if (_cargo.Count >= _frame.CargoSlots)
            return new SalvageResult(false, "Cargo hold full: no free slot.", 0, loot.QuestItemId, Array.Empty<object>());
        if (CargoUsedMassKg + loot.MassKg > _frame.CargoMaxMassKg)
            return new SalvageResult(false, "Too heavy: needs {0:F0} kg free.", 0, loot.QuestItemId, loot.MassKg);

        var value = Math.Max(0, (int)Math.Round(loot.ValueCredits * _difficulty.ResourceMultiplier));
        BankLoot(loot, value);
        return new SalvageResult(true, string.Empty, value, loot.QuestItemId, Array.Empty<object>());
    }

    private void BankLoot(LootSpawn loot, int value)
    {
        _collectedLoot.Add(loot.SpawnId);
        _cargo.Add(new CargoItem(loot.SpawnId, loot.Kind, value, loot.MassKg, loot.QuestItemId));
        CargoUsedMassKg += loot.MassKg;
        SecuredSalvageValue += value;
        _salvageValueProgress += value;

        if (!string.IsNullOrEmpty(loot.QuestItemId))
        {
            _questRecovered.Add(loot.QuestItemId);
            CheckObjective("Quest item secured.");
        }

        Threat = Math.Min(100f, Threat + TraitThreat(loot.TraitId));

        Raise("salvage.secured", "Secured {0} (+{1} cr).", loot.Kind, value);
        CheckObjective("Salvage banked.");
    }

    /// <summary>
    /// Surveys a known contact inside scaled survey range. Contacts need at least
    /// one ping (confidence ≥ 0.3); each contact counts once. Ghost contacts fade
    /// with an explicit reason and grant nothing.
    /// INTEGRATION: <c>var r = sim.TrySurvey(contactId);</c>
    /// </summary>
    public SurveyResult TrySurvey(string contactId)
    {
        if (Phase != RunPhase.Active)
            return new SurveyResult(false, "Run is not active.", Array.Empty<object>());
        if (!_contacts.TryGetValue(contactId, out var c))
            return new SurveyResult(false, "Unknown contact '{0}'. Ping first.", contactId);
        if (ElapsedSeconds - c.LastSeenSeconds > 60f)
            return new SurveyResult(false, "Contact expired: ping again.", Array.Empty<object>());
        if (c.Surveyed)
            return new SurveyResult(false, "Already surveyed: duplicate rejected.", Array.Empty<object>());
        if (c.IsGhost)
        {
            c.Surveyed = true;
            return new SurveyResult(false, "Contact faded on approach: likely sonar ghost.", "ghost");
        }
        if (c.Confidence < 0.3f)
            return new SurveyResult(false, "Confidence too low: ping again.", Array.Empty<object>());
        var range = DomainConstants.SurveyRangeMeters;
        if (_modifierIds.Contains("modifier.low_visibility")) range *= 0.85f;
        var dist = Vector3.Distance(c.TruePosition, ShipPosition);
        if (dist > range)
            return new SurveyResult(false, "Out of survey range ({0:F0} m, need {1:F0} m).", dist, range);

        c.Surveyed = true;
        SurveysDone++;
        _surveyProgress++;
        Raise("contact.surveyed", "Surveyed {0} contact ({1} total).", c.Class, SurveysDone);
        CheckObjective("Survey logged.");
        return new SurveyResult(true, string.Empty, Array.Empty<object>());
    }

    /// <summary>
    /// Repairs one hull compartment. Requires a live breach in the zone and at
    /// least one sealant charge; the spend is checked before anything mutates, so
    /// sealant can never go negative. Adds a noise spike and threat (hammer work).
    /// INTEGRATION: <c>var r = sim.TryRepairHull(zoneIndex);</c> with zoneIndex 0..3
    /// matching <see cref="FloodZones"/> order.
    /// </summary>
    public RepairResult TryRepairHull(int zoneIndex)
    {
        if (Phase != RunPhase.Active)
            return new RepairResult(false, "Run is not active.", Sealant, Array.Empty<object>());
        if (zoneIndex < 0 || zoneIndex >= 4)
            return new RepairResult(false, "Zone index out of range (0..3).", Sealant, Array.Empty<object>());
        if (_severity[zoneIndex] <= 0)
            return new RepairResult(false, "No breach in that compartment.", Sealant, Array.Empty<object>());
        var cost = _flood[zoneIndex] >= 100f ? 2 : 1;
        if (Sealant < cost)
            return new RepairResult(false, "Needs {0} sealant, have {1}.", Sealant, cost, Sealant);

        Sealant -= cost;
        SealantUsed += cost;
        _severity[zoneIndex] = Math.Max(0, _severity[zoneIndex] - 1);
        _flood[zoneIndex] *= 1f - _difficulty.RepairEfficiency;
        RepairsDone++;
        _noiseSpike += 10f;
        Threat = Math.Min(100f, Threat + 2f);
        Raise("repair.done", "Welded {0}: severity {1}, sealant {2}.", ZoneIds[zoneIndex], _severity[zoneIndex], Sealant);
        return new RepairResult(true, string.Empty, Sealant, Array.Empty<object>());
    }

    /// <summary>
    /// Services a contract node: beacon repair (1 sealant each) or facility-core
    /// stabilization (2 sealant). Requires the node to carry an unserviced contract
    /// target inside <see cref="DomainConstants.ServiceRangeMeters"/>. Physical
    /// docking/EVA visuals are presentation-side; this call is the authoritative
    /// domain interaction and the only path that advances service objectives.
    /// INTEGRATION: <c>var r = sim.TryServiceContractNode(nodeId);</c>
    /// </summary>
    public ServiceResult TryServiceContractNode(string nodeId)
    {
        if (Phase != RunPhase.Active)
            return new ServiceResult(false, "Run is not active.", Sealant, Array.Empty<object>());
        var node = _world.Nodes.FirstOrDefault(n => n.Id == nodeId);
        if (node is null)
            return new ServiceResult(false, "Unknown node '{0}'.", Sealant, nodeId);
        if (string.IsNullOrEmpty(node.ServiceId))
            return new ServiceResult(false, "Node has no contract service target.", Sealant, Array.Empty<object>());
        if (_servicedNodes.Contains(node.ServiceId))
            return new ServiceResult(false, "Service target already completed: duplicate rejected.", Sealant, Array.Empty<object>());
        if (!IsServiceRequired(node.ServiceId))
            return new ServiceResult(false, "Service target '{0}' is not part of this contract.", Sealant, node.ServiceId);
        var dist = Vector3.Distance(node.Position, ShipPosition);
        if (dist > DomainConstants.ServiceRangeMeters)
            return new ServiceResult(false, "Out of service range ({0:F0} m, need {1:F0} m).", Sealant, dist, DomainConstants.ServiceRangeMeters);
        if (node.ServiceId == QuestItems.CoreService && _dockedNodeId != node.Id)
            return new ServiceResult(false, "Core stabilization requires docking (J) at the facility first.", Sealant, Array.Empty<object>());

        var cost = node.ServiceId == QuestItems.CoreService ? 2 : 1;
        if (Sealant < cost)
            return new ServiceResult(false, "Needs {0} sealant, have {1}.", Sealant, cost, Sealant);

        Sealant -= cost;
        SealantUsed += cost;
        _servicedNodes.Add(node.ServiceId);
        _weldTimer = 5f;
        _noiseSpike += 12f;
        Threat = Math.Min(100f, Threat + 4f);
        Raise("service.done", "Serviced {0} at {1}.", node.ServiceId, nodeId);
        CheckObjective("Service complete.");
        return new ServiceResult(true, string.Empty, Sealant, Array.Empty<object>());
    }

    /// <summary>
    /// Spends the one-use emergency winch, returning the last safe position the
    /// presentation layer must move the ship to. The domain never teleports the
    /// ship itself. Second uses are rejected.
    /// INTEGRATION: <c>var r = sim.TryUseWinch(out var pos); if (r.Success) MoveShip(pos);</c>
    /// </summary>
    public WinchResult TryUseWinch(out Vector3 returnPosition)
    {
        returnPosition = default;
        if (Phase != RunPhase.Active)
            return new WinchResult(false, "Run is not active.", default, Array.Empty<object>());
        if (WinchUsed)
            return new WinchResult(false, "Winch already spent: one use per run.", default, Array.Empty<object>());
        if (_dockedNodeId is not null)
            return new WinchResult(false, "Undock (J) before firing the winch: no teleport while docked.", default, Array.Empty<object>());
        if (!HasSafePosition)
            return new WinchResult(false, "No safe position recorded yet.", default, Array.Empty<object>());

        WinchUsed = true;
        returnPosition = LastSafePosition;
        Raise("winch.used", "Emergency winch fired: returning to last safe position.");
        return new WinchResult(true, string.Empty, LastSafePosition, Array.Empty<object>());
    }

    /// <summary>
    /// True when a node offers a docking station: objective sites and contract
    /// service nodes. Route/branch waypoints and extraction have no station.
    /// </summary>
    public bool IsDockableNode(WorldNode node) =>
        node.Kind == WorldNodeKind.Objective || !string.IsNullOrEmpty(node.ServiceId);

    /// <summary>
    /// Docks at a station node. Requires an active run, a dockable node within
    /// <see cref="DomainConstants.DockRangeMeters"/> (25 m), and ship speed at
    /// or below <see cref="DomainConstants.DockMaxSpeedMetersPerSecond"/> (2 m/s).
    /// Speed uses the explicit <paramref name="measuredSpeedMps"/> reading when
    /// finite and non-negative (presentation passes the rigid-body velocity);
    /// otherwise the host-measured pose-delta speed applies. Docking captures
    /// the current pose as the safe offset: the ship is frozen there, never
    /// teleported across the map. One station at a time; already docked is refused.
    /// INTEGRATION: <c>var r = sim.TryDock(nodeId, sub.LinearVelocity.Length());</c>
    /// on success freeze the hull in place; on <c>TryUndock</c> unfreeze.
    /// </summary>
    public DockResult TryDock(string nodeId, float? measuredSpeedMps = null)
    {
        if (Phase != RunPhase.Active)
            return new DockResult(false, "Run is not active.", null, Array.Empty<object>());
        if (_dockedNodeId is not null)
            return new DockResult(false, "Already docked at '{0}': undock (J) first.", _dockedNodeId, _dockedNodeId);
        var node = _world.Nodes.FirstOrDefault(n => n.Id == nodeId);
        if (node is null)
            return new DockResult(false, "Unknown node '{0}'.", null, nodeId);
        if (!IsDockableNode(node))
            return new DockResult(false, "No docking station at '{0}': dock only at objective or service stations.", null, nodeId);
        var dist = Vector3.Distance(node.Position, ShipPosition);
        if (dist > DomainConstants.DockRangeMeters)
            return new DockResult(false, "Out of docking range ({0:F0} m, need {1:F0} m).", null, dist, DomainConstants.DockRangeMeters);
        var speed = _shipSpeedMps;
        if (measuredSpeedMps.HasValue && float.IsFinite(measuredSpeedMps.Value) && measuredSpeedMps.Value >= 0f)
            speed = measuredSpeedMps.Value;
        if (speed > DomainConstants.DockMaxSpeedMetersPerSecond + 0.05f)
            return new DockResult(false, "Too fast to dock ({0:F1} m/s, need ≤{1:F0} m/s).", null, speed, DomainConstants.DockMaxSpeedMetersPerSecond);

        _dockedNodeId = node.Id;
        _dockAnchor = ShipPosition;
        Raise("dock.done", "Docked at {0} ({1:F0} m, {2:F1} m/s). Station drill available (H).", node.Id, dist, speed);
        return new DockResult(true, string.Empty, node.Id, Array.Empty<object>());
    }

    /// <summary>
    /// Undocks from the current station. Clears any pending drill with no award:
    /// an interrupted cut never banks salvage. Undocked is refused.
    /// </summary>
    public UndockResult TryUndock()
    {
        if (Phase != RunPhase.Active)
            return new UndockResult(false, "Run is not active.", Array.Empty<object>());
        if (_dockedNodeId is null)
            return new UndockResult(false, "Not docked.", Array.Empty<object>());
        var station = _dockedNodeId;
        _dockedNodeId = null;
        if (_drillLootSpawnId is not null)
        {
            _drillLootSpawnId = null;
            _drillElapsedSeconds = 0f;
            Raise("drill.cancelled", "Drill cancelled on undock from {0}: no salvage banked.", station);
        }
        Raise("dock.released", "Undocked from {0}.", station);
        return new UndockResult(true, string.Empty, Array.Empty<object>());
    }

    /// <summary>
    /// Starts a timed drill cut on a loot spawn. Requires docked, an uncollected
    /// spawn within <see cref="DrillRangeMeters"/> (stock 25 m), free cargo, and
    /// no active cut. The station tool works stock: no research module is ever
    /// required. Facility cores can ONLY be recovered this way; other loot may
    /// drill for the same value with no bonus (an 8 s waste, never a fake yield).
    /// INTEGRATION: hold/toggle H to run; <see cref="Tick"/> advances the cut.
    /// </summary>
    public DrillResult TryStartDrill(string lootSpawnId)
    {
        if (Phase != RunPhase.Active)
            return new DrillResult(false, "Run is not active.", Array.Empty<object>());
        if (_dockedNodeId is null)
            return new DrillResult(false, "Drill requires docking (J) at a station first.", Array.Empty<object>());
        if (_drillLootSpawnId is not null)
            return new DrillResult(false, "Drill already running: cancel (H) first.", Array.Empty<object>());
        var loot = _world.LootSpawns.FirstOrDefault(l => l.SpawnId == lootSpawnId);
        if (loot is null)
            return new DrillResult(false, "Unknown loot '{0}'.", lootSpawnId);
        if (_collectedLoot.Contains(lootSpawnId))
            return new DrillResult(false, "Already secured: duplicate drill rejected.", Array.Empty<object>());
        if (!string.IsNullOrEmpty(loot.QuestItemId) && _questRecovered.Contains(loot.QuestItemId))
            return new DrillResult(false, "Quest item already secured: duplicate rejected.", Array.Empty<object>());
        var dist = Vector3.Distance(loot.Position, ShipPosition);
        if (dist > DrillRangeMeters)
            return new DrillResult(false, "Out of drill range ({0:F0} m, need {1:F0} m).", dist, DrillRangeMeters);
        if (_cargo.Count >= _frame.CargoSlots)
            return new DrillResult(false, "Cargo hold full: no free slot.", Array.Empty<object>());
        if (CargoUsedMassKg + loot.MassKg > _frame.CargoMaxMassKg)
            return new DrillResult(false, "Too heavy: needs {0:F0} kg free.", loot.MassKg);

        _drillLootSpawnId = lootSpawnId;
        _drillElapsedSeconds = 0f;
        Raise("drill.started", "Drill started on {0} ({1:F0} m): hold position 8 s. 35 PU, threat rising.", loot.Kind, dist);
        return new DrillResult(true, string.Empty, Array.Empty<object>());
    }

    /// <summary>
    /// Cancels the active drill cut with no award and no duplicate pickup.
    /// Idle (no cut) is refused explicitly.
    /// </summary>
    public DrillResult TryCancelDrill()
    {
        if (Phase != RunPhase.Active)
            return new DrillResult(false, "Run is not active.", Array.Empty<object>());
        if (_drillLootSpawnId is null)
            return new DrillResult(false, "No drill running.", Array.Empty<object>());
        _drillLootSpawnId = null;
        _drillElapsedSeconds = 0f;
        Raise("drill.cancelled", "Drill cancelled: no salvage banked.");
        return new DrillResult(true, string.Empty, Array.Empty<object>());
    }

    /// <summary>
    /// Tutorial-only authored incident: opens one small real breach (severity 1,
    /// zone 0) with live flooding, pressure, and audio paths. Succeeds once per
    /// tutorial run; every other call is refused with a reason. This is not a
    /// general damage API: non-tutorial runs and repeat calls are always refused.
    /// Repair uses the normal <see cref="TryRepairHull"/> sealant path.
    /// </summary>
    public TrainingBreachResult TryInjectTrainingBreach()
    {
        if (Phase != RunPhase.Active)
            return new TrainingBreachResult(false, "Run is not active.", -1, Array.Empty<object>());
        if (!_isTutorialRun)
            return new TrainingBreachResult(false, "Training breach is tutorial-only.", -1, Array.Empty<object>());
        if (_trainingBreachInjected)
            return new TrainingBreachResult(false, "Training breach already injected this run.", -1, Array.Empty<object>());
        if (_severity.Any(s => s > 0))
            return new TrainingBreachResult(false, "Hull already breached: repair (R) first.", -1, Array.Empty<object>());
        _trainingBreachInjected = true;
        _severity[0] = 1;
        _noiseSpike += 8f;
        Raise("hull.breach", "Breach in {0} (severity 1) from training incident. Repair (R) with sealant.", ZoneIds[0]);
        return new TrainingBreachResult(true, string.Empty, 0, Array.Empty<object>());
    }

    private void UpdateDrill(float dt)
    {
        if (_drillLootSpawnId is null) return;
        // Dock drift guard: leaving station range breaks the cut and releases
        // the dock, never a cross-map teleport.
        if (_dockedNodeId is not null)
        {
            var station = _world.Nodes.FirstOrDefault(n => n.Id == _dockedNodeId);
            if (station is not null && Vector3.Distance(station.Position, ShipPosition) > DomainConstants.DockRangeMeters + 5f)
            {
                _dockedNodeId = null;
                _drillLootSpawnId = null;
                _drillElapsedSeconds = 0f;
                Raise("dock.released", "Drifted out of station range: undocked, drill cancelled with no award.");
                return;
            }
        }
        else
        {
            _drillLootSpawnId = null;
            _drillElapsedSeconds = 0f;
            Raise("drill.cancelled", "Drill cancelled: undocked with no award.");
            return;
        }
        var loot = _world.LootSpawns.FirstOrDefault(l => l.SpawnId == _drillLootSpawnId);
        if (loot is null || _collectedLoot.Contains(loot.SpawnId))
        {
            _drillLootSpawnId = null;
            _drillElapsedSeconds = 0f;
            Raise("drill.cancelled", "Drill cancelled: target gone, no duplicate award.");
            return;
        }
        if (Vector3.Distance(loot.Position, ShipPosition) > DrillRangeMeters)
        {
            _drillLootSpawnId = null;
            _drillElapsedSeconds = 0f;
            Raise("drill.cancelled", "Drill cancelled: out of range, no salvage banked.");
            return;
        }
        _drillElapsedSeconds = Math.Min(DomainConstants.DrillDurationSeconds, _drillElapsedSeconds + dt);
        if (_drillElapsedSeconds < DomainConstants.DrillDurationSeconds) return;
        // Full duration served: re-validate capacity/distance like a normal pickup.
        if (_cargo.Count >= _frame.CargoSlots || CargoUsedMassKg + loot.MassKg > _frame.CargoMaxMassKg)
        {
            _drillLootSpawnId = null;
            _drillElapsedSeconds = 0f;
            Raise("drill.cancelled", "Drill finished but the hold cannot take it: no award, no duplicate.");
            return;
        }
        var value = Math.Max(0, (int)Math.Round(loot.ValueCredits * _difficulty.ResourceMultiplier));
        _drillLootSpawnId = null;
        _drillElapsedSeconds = 0f;
        BankLoot(loot, value);
        Raise("drill.complete", "Drill cut complete on {0} (+{1} cr).", loot.Kind, value);
    }

    /// <summary>
    /// Attempts extraction at the origin node. Succeeds only with primary
    /// objectives complete inside <see cref="DomainConstants.ExtractionRadiusMeters"/>;
    /// anything else returns an explicit reason — never a fake success.
    /// INTEGRATION: <c>var r = sim.TryExtract(); if (r.Success) SubmitSettlement(r.Settlement);</c>
    /// </summary>
    public ExtractResult TryExtract()
    {
        if (Phase != RunPhase.Active)
            return new ExtractResult(false, "Run is not active.", null, Array.Empty<object>());
        var extraction = _world.GetNode(_world.ExtractionNodeId).Position;
        var dist = Vector3.Distance(extraction, ShipPosition);
        if (dist > DomainConstants.ExtractionRadiusMeters)
            return new ExtractResult(false, "Not in the extraction zone ({0:F0} m, need {1:F0} m).", null, dist, DomainConstants.ExtractionRadiusMeters);
        var progress = Contract;
        if (!progress.PrimaryComplete)
        {
            var missing = string.Join(", ", progress.Objectives.Where(o => !o.IsComplete).Select(o => $"{o.ObjectiveId} {o.Current}/{o.Required}"));
            return new ExtractResult(false, "Primary objectives incomplete: {0}.", null, missing);
        }

        Phase = RunPhase.Extracted;
        Settlement = BuildSuccessSettlement();
        Raise("extract.success", "Extracted with {0} cr.", Settlement.Credits);
        return new ExtractResult(true, string.Empty, Settlement, Array.Empty<object>());
    }

    /// <summary>
    /// Builds the failure settlement for a failed run. Retains only what was
    /// actually earned: secured cargo at the insurance retention (none 20% /
    /// basic 50% / premium 70%), scanned research in full (docs/05 §3), and
    /// quest discoveries. No contract base, no shards, no participation research:
    /// an empty failure settles to zero. The draft is cached per instance, so
    /// repeated calls return equal drafts with the same settlement ID.
    /// Only valid once <see cref="Phase"/> is <see cref="RunPhase.Failed"/>;
    /// otherwise returns null.
    /// INTEGRATION: call after observing the failed phase, then submit like a success draft.
    /// </summary>
    public RunSettlementDraft? BuildFailureSettlement()
    {
        if (Phase != RunPhase.Failed) return null;
        if (_failureDraft is not null) return _failureDraft;
        var retention = Retention();
        var credits = (long)Math.Round(SecuredSalvageValue * retention);
        var research = SurveysDone * 10L + _questRecovered.Count * 5L;
        _failureDraft = new RunSettlementDraft(
            SettlementId(), _world.RunSeed, _world.BiomeId, _world.ContractId,
            _difficulty.Id, _insuranceId, SettlementOutcome.Failed,
            _contract.BasePayout, SecuredSalvageValue, 0, 0, 0,
            credits, credits, research, 0);
        return _failureDraft;
    }

    // ------------------------------------------------------------------ internals

    private void TrackSafePosition(float dt)
    {
        _safeTrackTimer += dt;
        if (_safeTrackTimer < 5f) return;
        _safeTrackTimer = 0f;
        var worst = 0;
        for (var i = 0; i < 4; i++) worst = Math.Max(worst, _severity[i]);
        if (HullIntegrity > MaxHull * 0.5f && worst < 2 && Threat < 70f)
        {
            LastSafePosition = ShipPosition;
            HasSafePosition = true;
        }
    }

    private void InflictBreach(string cause)
    {
        var zone = _pressureRng.NextInt(0, 4);
        var roll = _pressureRng.NextDouble();
        var incoming = roll < 0.6 ? 1 : roll < 0.9 ? 2 : 3;
        _severity[zone] = Math.Min(3, Math.Max(_severity[zone], incoming));
        _noiseSpike += 8f;
        Raise("hull.breach", "Breach in {0} (severity {1}) from {2}.", ZoneIds[zone], _severity[zone], cause);
    }

    private void UpdatePassiveContacts(float dt)
    {
        _passiveTimer += dt;
        if (_passiveTimer < 0.5f) return;
        _passiveTimer = 0f;
        var range = _biome.PassiveRangeMeters * _loadout.PassiveRangeMult;
        if (_modifierIds.Contains("modifier.low_visibility")) range *= 0.7f;
        foreach (var creature in _creatures)
        {
            var dist = Vector3.Distance(creature.Position, ShipPosition);
            var hearable = range * (0.3 + 0.7 * creature.Def.Aggression);
            if (dist > hearable) continue;
            var id = "contact.passive." + creature.Id;
            if (!_contacts.TryGetValue(id, out var c))
            {
                c = new ContactRuntime(id, SonarClass.Unknown, creature.Position);
                _contacts[id] = c;
            }
            c.Class = SonarClass.Biological;
            c.TruePosition = creature.Position;
            c.RangeMeters = dist;
            c.LastSeenSeconds = ElapsedSeconds;
            c.Confidence = Math.Max(c.Confidence, 0.2f);
            var err = (1f - c.Confidence) * 30f;
            c.DisplayPosition = creature.Position + new Vector3(
                _sonarRng.NextFloat(-err, err), _sonarRng.NextFloat(-err, err), _sonarRng.NextFloat(-err, err));
        }
    }

    private void UpdateCreatures(float dt, float throttle)
    {
        var nesting = _modifierIds.Contains("modifier.nesting_season") ? 1.3f : 1f;
        foreach (var creature in _creatures)
        {
            if (creature.SonarExposedSeconds > 0f) creature.SonarExposedSeconds -= dt;
            creature.AttackCooldown = Math.Max(0f, creature.AttackCooldown - dt);
            var toShip = ShipPosition - creature.Position;
            var dist = toShip.Length();
            var dir = dist > 0.001f ? toShip / dist : Vector3.Zero;

            var hear = creature.Def.BaseHearingMeters * (0.35f + 1.3f * (Noise / 100f)) * _difficulty.DetectMultiplier;
            if (creature.SonarExposedSeconds > 0f) hear *= 1.5f;
            var stalkRange = hear * 0.6f;
            var huntRange = creature.IsApex ? 60f : 40f;

            creature.StateTime += dt;
            switch (creature.State)
            {
                case CreatureState.Dormant:
                    if (dist < hear * creature.Def.Aggression * nesting)
                        SetState(creature, CreatureState.Investigate, "investigating noise");
                    break;
                case CreatureState.Investigate:
                    creature.Position += dir * creature.Def.SpeedMetersPerSecond * 0.6f * dt;
                    if (dist < stalkRange) SetState(creature, CreatureState.Stalk, "shadowing the ship");
                    else if (creature.StateTime > 12f || dist > hear * 2f) SetState(creature, CreatureState.Dormant, "lost interest");
                    break;
                case CreatureState.Stalk:
                    creature.Position += dir * creature.Def.SpeedMetersPerSecond * 0.8f * dt;
                    if (dist < huntRange && Threat > 25f * creature.Def.Aggression) SetState(creature, CreatureState.Hunt, "committed to the hunt");
                    else if (dist > hear * 1.6f && creature.StateTime > 8f) SetState(creature, CreatureState.Disengage, "breaking off");
                    break;
                case CreatureState.Hunt:
                    creature.Position += dir * creature.Def.SpeedMetersPerSecond * dt;
                    if (dist < creature.Def.AttackRangeMeters) SetState(creature, CreatureState.Attack, "striking");
                    else if (dist > hear * 2f) SetState(creature, CreatureState.Disengage, "outpaced");
                    break;
                case CreatureState.Attack:
                    if (creature.AttackCooldown <= 0f)
                    {
                        creature.AttackCooldown = 4f;
                        creature.AttackCount++;
                        Strike(creature);
                        if (creature.AttackCount >= 3)
                        {
                            creature.AttackCount = 0;
                            SetState(creature, CreatureState.Disengage, "disengaging after strikes");
                        }
                        else SetState(creature, CreatureState.Hunt, "circling for another strike");
                    }
                    break;
                case CreatureState.Disengage:
                    creature.Position -= dir * creature.Def.SpeedMetersPerSecond * 0.5f * dt;
                    if (creature.StateTime > 10f)
                    {
                        creature.AttackCount = 0;
                        SetState(creature, CreatureState.Dormant, "gone quiet");
                    }
                    break;
            }
        }
    }

    private void Strike(CreatureRuntime creature)
    {
        var damage = creature.Def.AttackDamage;
        HullIntegrity = Math.Max(0f, HullIntegrity - damage);
        _noiseSpike += 8f;
        Raise("creature.strike", "{0} struck for {1:F0} damage.", creature.Def.DisplayName, damage);

        if (creature.Def.Id == "creature.lampreech")
        {
            _drainTimer = 10f;
            Raise("creature.lampreech", "Lampreech attached: +10 PU drain for 10 s.");
        }
        else if (creature.Def.Id == "creature.hull_grazer")
        {
            _noiseSpike += 15f;
            Raise("creature.grazer", "Hull grazer scraping: noise spike.");
        }
        else if (creature.Def.Id == "creature.chorus_colony")
        {
            Threat = Math.Min(100f, Threat + 10f);
            Raise("creature.chorus", "Chorus swell: false contacts likely.");
        }
        else if (creature.IsApex)
        {
            _surgeTimer = 20f;
            _pressureEventMod = 1.2f;
            Threat = Math.Min(100f, Threat + 8f);
            Raise("creature.apex", "Apex pressure event: route discipline failing.");
        }

        if (_aiRng.Chance(0.35))
        {
            var zone = _aiRng.NextInt(0, 4);
            _severity[zone] = Math.Min(3, _severity[zone] + 1);
            Raise("hull.breach", "Breach in {0} (severity {1}) from creature strike.", ZoneIds[zone], _severity[zone]);
        }
    }

    private void SetState(CreatureRuntime creature, CreatureState next, string why)
    {
        if (creature.State == next) return;
        creature.State = next;
        creature.StateTime = 0f;
        Raise("creature.state", "{0}: {1} ({2}).", creature.Def.DisplayName, next, why);
    }

    private CreatureRuntime? NearestRevealedApex()
    {
        CreatureRuntime? best = null;
        var bestDist = float.MaxValue;
        foreach (var creature in _creatures)
        {
            if (!creature.IsApex) continue;
            if (!_contacts.TryGetValue("contact.bio." + creature.Id, out _)) continue;
            var dist = Vector3.Distance(creature.Position, ShipPosition);
            if (dist < bestDist) { bestDist = dist; best = creature; }
        }
        return best;
    }

    private int RequiredObserve() =>
        _contract.PrimaryObjectives.Where(o => o.Kind == ContractObjectiveKind.ObserveApex).Sum(o => o.Required);

    private int QuestProgress(ContractObjectiveSpec spec)
    {
        if (spec.TargetQuestItemId == QuestItems.BioSample)
        {
            var total = 0;
            for (var i = 1; i <= spec.Required + 3; i++)
                if (_questRecovered.Contains($"quest.bio.{i}")) total++;
            return Math.Min(total, spec.Required);
        }
        return _questRecovered.Contains(spec.TargetQuestItemId) ? 1 : 0;
    }

    private int ServiceProgress(ContractObjectiveSpec spec)
    {
        if (spec.TargetQuestItemId == QuestItems.BeaconService)
        {
            var total = 0;
            for (var i = 1; i <= spec.Required + 2; i++)
                if (_servicedNodes.Contains($"service.beacon.{i}")) total++;
            return Math.Min(total, spec.Required);
        }
        return _servicedNodes.Contains(spec.TargetQuestItemId) ? 1 : 0;
    }

    private bool IsServiceRequired(string serviceId)
    {
        foreach (var spec in _contract.PrimaryObjectives)
        {
            if (spec.Kind != ContractObjectiveKind.ServiceNode) continue;
            if (spec.TargetQuestItemId == QuestItems.BeaconService && serviceId.StartsWith("service.beacon.", StringComparison.Ordinal))
                return true;
            if (spec.TargetQuestItemId == serviceId) return true;
        }
        return false;
    }

    private void CheckObjective(string update)
    {
        var progress = Contract;
        foreach (var o in progress.Objectives)
        {
            if (!o.IsComplete) continue;
            var key = "objective." + o.ObjectiveId;
            if (_completedAnnounced.Add(key))
                Raise("contract.objective", "Objective '{0}' complete. {1}", o.ObjectiveId, update);
        }
        if (progress.PrimaryComplete && _completedAnnounced.Add("objective.ALL"))
            Raise("contract.complete", "Primary objectives complete. Extract.");
    }

    private readonly HashSet<string> _completedAnnounced = new(StringComparer.Ordinal);

    private float TraitThreat(string traitId) =>
        traitId switch
        {
            "trait.resonant" => 4f,
            "trait.hazardous" => 6f,
            "trait.parasitic" => 8f,
            "trait.memory_bearing" => 2f,
            "trait.unstable" => 5f,
            "trait.ancient_power" => 10f,
            _ => 0f,
        };

    private void FailRun(string reason)
    {
        if (Phase != RunPhase.Active) return;
        Phase = RunPhase.Failed;
        FailureReason = reason;
        Settlement = null;
        Raise("run.failed", reason);
    }

    private RunSettlementDraft BuildSuccessSettlement()
    {
        // docs/05 §2, exactly: Payout = Base + SecuredSalvage + Objectives + RiskBonus - RepairCost - LostGear.
        // The difficulty uplift lives inside RiskBonus as Base*(mult-1) and is applied
        // once; LostGear is 0 here because gear loss is a profile-side policy outside
        // the domain.
        var surveyBonus = SurveysDone * 25;
        var noPing = _modifierIds.Contains("modifier.no_ping_bonus") && PulsesUsed == 0 ? 150 : 0;
        var objectiveBonus = surveyBonus + noPing;
        var biomeTier = _world.BiomeId == "biome.hadal_ruins" ? 2 : _world.BiomeId == "biome.black_trench" ? 1 : 0;
        var riskBonus = (long)Math.Round(_contract.BasePayout * 0.1 * biomeTier + _contract.BasePayout * (_difficulty.PayoutMultiplier - 1.0));
        var repairCost = (long)(SealantUsed * 40 + Math.Round((MaxHull - HullIntegrity) * 2.0));
        var credits = Math.Max(0L, _contract.BasePayout + SecuredSalvageValue + objectiveBonus + riskBonus - repairCost);
        var research = 20L + SurveysDone * 10L + 30L + _questRecovered.Count * 5L;
        return new RunSettlementDraft(
            SettlementId(), _world.RunSeed, _world.BiomeId, _world.ContractId,
            _difficulty.Id, _insuranceId, SettlementOutcome.Success,
            _contract.BasePayout, SecuredSalvageValue, objectiveBonus, riskBonus, repairCost,
            credits, credits, research, ShardsEarned());
    }

    private long ShardsEarned()
    {
        var shards = 0L;
        if (_world.BiomeId == "biome.hadal_ruins") shards++;
        if (_difficulty.Id == "difficulty.blackwater") shards++;
        if (_contract.Id == "contract.apex_observe" && Contract.PrimaryComplete) shards++;
        return shards;
    }

    private double Retention()
    {
        // Retention math assumes the caller has paid for the selected policy; the
        // domain charges nothing (see InsuranceBasic).
        if (TryGetInsuranceQuote(_insuranceId, out var quote, out _) && quote is not null)
            return quote.Retention;
        return 0.5;
    }

    private string SettlementId()
    {
        var shortContract = _contract.Id.StartsWith("contract.", StringComparison.Ordinal)
            ? _contract.Id["contract.".Length..] : _contract.Id;
        return $"settle.{_world.RunSeed:x16}.{_runInstanceId:N}.{shortContract}";
    }

    private void Raise(string kind, string message, params object[]? args)
    {
        var evt = new RunEvent(ElapsedSeconds, kind, message, args ?? Array.Empty<object>());
        _recent.Enqueue(evt);
        while (_recent.Count > 128) _recent.Dequeue();
        try
        {
            EventRaised?.Invoke(evt);
        }
        catch
        {
            // A throwing subscriber must never corrupt host state; the event stays
            // in RecentEvents so no information is lost.
        }
    }

    private static bool IsFinite(Vector3 v) =>
        float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private static ContactSnapshot ToSnapshot(ContactRuntime c) =>
        new(c.Id, c.Class, c.DisplayPosition, c.RangeMeters, c.Confidence, c.Surveyed);

    private sealed class ContactRuntime
    {
        public readonly string Id;
        public SonarClass Class;
        public Vector3 TruePosition;
        public Vector3 DisplayPosition;
        public float RangeMeters;
        public float Confidence;
        public float LastSeenSeconds;
        public int Pings;
        public bool Triangulated;
        public bool Surveyed;
        public bool IsGhost;

        public ContactRuntime(string id, SonarClass cls, Vector3 pos)
        {
            Id = id;
            Class = cls;
            TruePosition = pos;
            DisplayPosition = pos;
        }
    }

    private sealed class CreatureRuntime
    {
        public readonly string Id;
        public readonly CreatureDef Def;
        public Vector3 Position;
        public CreatureState State;
        public float StateTime;
        public float AttackCooldown;
        public int AttackCount;
        public float SonarExposedSeconds;
        public bool IsApex => Def.IsApex;

        public CreatureRuntime(string id, CreatureDef def, Vector3 position, bool apex)
        {
            Id = id;
            Def = def;
            Position = position;
            State = CreatureState.Dormant;
        }
    }
}
