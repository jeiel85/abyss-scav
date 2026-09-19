using AbyssScav.App;
using AbyssScav.Domain;
using AbyssScav.Foundation;
using AbyssScav.Infra.Logging;
using AbyssScav.Net;
using AbyssScav.Persistence;
using AbyssScav.Presentation;
using AbyssScav.Protocol;
using Godot;
using SysVec = System.Numerics.Vector3;

namespace AbyssScav.Gameplay;

/// <summary>
/// Solo run scene root: owns catalog → generate → validate → simulate, builds
/// physical corridors faithfully from the world, ticks the domain sim with the
/// Godot ship pose every physics frame, and drives HUD/sonar/audio/settlement.
/// Mission fail, pause/abort, and honest settlement persistence included.
/// </summary>
public partial class RunController : Node3D
{
    private ContentCatalog? _catalog;
    private GeneratedWorld? _world;
    private RunSimulation? _sim;
    private RunLaunchOptions? _options;
    private SubmarineController? _sub;
    private RunHud? _hud;
    private ProceduralAudio? _audio;
    private FileSaveStore? _store;
    private TutorialDirector? _director;
    private SonarBuddy? _buddy;
    private bool _isTutorial;
    private bool _tutorialSaveUsable = true;
    private string _tutorialEndNote = "";
    private bool _trainingBreachDone;

    private bool _quiet;
    private bool _paused;
    private bool _ended;
    private bool _buildReady;
    private RunSettlementDraft? _pendingDraft;
    private readonly HashSet<string> _tutorialPendingSteps = new(StringComparer.Ordinal);
    private bool _tutorialExtractionObserved;
    private CancellationTokenSource? _settleCts;
    private bool _settleInFlight;
    private string _saveState = "";
    private readonly Dictionary<string, MeshInstance3D> _threatMarkers = new();
    private readonly Dictionary<string, MeshInstance3D> _lootMarkers = new();
    private IReadOnlyList<ContactSnapshot> _lastPulseContacts = Array.Empty<ContactSnapshot>();
    private readonly HashSet<string> _surveyedCreatureIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _salvagedTraitIds = new(StringComparer.Ordinal);
    private float _statusPoll;
    private Label? _fatalLabel;
    private GodotNetworkSession? _session;
    private bool _isCoopHost;
    private bool _isCoopClient;
    private float _snapshotTimer;
    private bool _lastLocalDrill;
    private readonly byte[] _snapshotBuffer = new byte[MessageBounds.MaxPayload(MessageType.Snapshot)];

    public override void _Ready()
    {
        AbyssInput.EnsureRegistered();
        if (!GameServices.IsInitialized)
        {
            ShowFatal(Localization.T("Boot services unavailable. Return to menu and relaunch."));
            return;
        }
        _store = new FileSaveStore(GameServices.Paths.SavesDir);
        _audio = new ProceduralAudio();
        AddChild(_audio);

        if (!ContentCatalog.TryBuild(out var catalog, out var errors) || catalog is null)
        {
            ShowFatal(Localization.T("Content catalog failed: {0}", (object)string.Join("; ", errors)));
            return;
        }
        _catalog = catalog;
        _options = RunLaunchContext.Pending ?? RunLaunchOptions.DefaultFromCatalog(catalog);
        RunLaunchContext.Pending = null;
        if (!_options.TryValidate(catalog, out var problems))
        {
            ShowFatal(Localization.T("Launch selection invalid: {0}", (object)string.Join("; ", problems)));
            return;
        }
        // Fresh ownership at run start: a module-equipped production launch
        // must verify against the live profile (never a snapshot, never a null
        // skip-gate). Stock loadouts skip the gate exactly as before.
        if (_options.EffectiveModuleIds.Count > 0)
        {
            _hud?.ShowMessage(Localization.T("Verifying module ownership…"), 3f);
            VerifyOwnershipAndBuildAsync();
            return;
        }
        BuildRun(ownedForGate: null);
    }

    /// <summary>
    /// Async-safe ownership gate: loads the actual live profile on a worker,
    /// then continues the build on the main thread. Missing/unowned modules
    /// fail explicitly; the snapshot on the launch options is never trusted.
    /// </summary>
    private async void VerifyOwnershipAndBuildAsync()
    {
        var options = _options;
        var catalog = _catalog;
        var store = _store;
        if (options is null || catalog is null || store is null) return;
        var moduleIds = options.EffectiveModuleIds.ToArray();
        ProfileSave live;
        try
        {
            live = await store.LoadAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (SaveException ex)
        {
            if (!IsInstanceValid(this)) return;
            ShowFatal(Localization.T("Module loadout cannot be verified: profile unreadable [{0}]: {1}", (object)ex.Code, ex.Message));
            return;
        }
        catch (Exception ex)
        {
            if (!IsInstanceValid(this)) return;
            ShowFatal(Localization.T("Module loadout cannot be verified: profile unreadable ({0}).", (object)ex.GetType().Name));
            return;
        }
        if (!IsInstanceValid(this) || _ended) return;
        var owned = new HashSet<string>(live.Unlocks.Blueprints, StringComparer.Ordinal);
        if (!ModuleLoadout.TryValidate(moduleIds, catalog, owned, out var modErrors))
        {
            ShowFatal(Localization.T("Module loadout refused at run start: {0}", (object)string.Join("; ", modErrors)));
            return;
        }
        BuildRun(ownedForGate: owned);
    }

    /// <summary>World + sim + scene construction. Runs once per launch.</summary>
    private void BuildRun(IReadOnlyCollection<string>? ownedForGate)
    {
        if (_buildReady || _ended) return;
        var catalog = _catalog;
        var options = _options;
        if (catalog is null || options is null) return;
        // A module-equipped launch must carry live ownership into the gate:
        // null here would silently skip it, so fail closed instead.
        IReadOnlyCollection<string>? gate = options.EffectiveModuleIds.Count > 0
            ? ownedForGate ?? throw new InvalidOperationException("Live module ownership is required for an equipped launch.")
            : options.EffectiveOwnedBlueprints;
        if (options.EffectiveModuleIds.Count > 0 && gate is null)
        {
            ShowFatal(Localization.T("Module loadout refused at run start: live ownership unavailable."));
            return;
        }
        _buildReady = true;
        var request = new RunGenerationRequest(options.RunSeed, options.BiomeId, options.ContractId, catalog);
        if (!TrenchGenerator.TryGenerate(request, out var world, out var verdict, out var reason) || world is null)
        {
            ShowFatal(Localization.T("Generation refused: {0}", (object)reason));
            return;
        }
        if (!verdict.IsValid)
        {
            ShowFatal(Localization.T("World invalid: {0}", (object)string.Join("; ", verdict.Errors)));
            return;
        }
        _world = world;
        _isTutorial = options.IsTutorial;
        if (!RunSimulation.TryCreate(world, catalog, options.DifficultyId, options.FrameId,
                options.ModifierIds, options.InsuranceId, out var sim, out var simReason,
                options.EffectiveModuleIds, gate, options.IsTutorial,
                options.EffectiveConsumableIds) || sim is null)
        {
            ShowFatal(Localization.T("Run rejected: {0}", (object)simReason));
            return;
        }
        _sim = sim;
        sim.EventRaised += OnSimEvent;
        SetupCoopMode();

        WorldBuilder.Build(this, world, catalog);
        SpawnSub(world);
        BuildHud();
        if (!_isCoopHost && !_isCoopClient) BuildBuddy();
        if (_isTutorial)
        {
            BuildTutorial();
        }
        // Flow bookkeeping: RunLoading -> InRun once the scene is constructed.
        GameServices.Flow.TryTransition(AppScene.InRun, out _);
        _hud?.ShowMessage(Localization.T("Dive live: {0} in {1}. Follow cyan landmarks; F to ping.", Localization.T(catalog.Contracts[world.ContractId].Archetype), Localization.T(catalog.Biomes[world.BiomeId].DisplayName)), 7f);
        if (_isTutorial)
        {
            _hud?.ShowMessage(Localization.T("Tutorial: The First Ping — follow the top-left checklist. Dock (J), drill (H), and repair (R) are mandatory; optional steps can skip."), 8f);
        }
        GodotLogBridge.Info(GameServices.Logger, $"Run started seed={world.RunSeed} biome={world.BiomeId} contract={world.ContractId} nodes={world.Nodes.Count}.");
    }

    private void SpawnSub(GeneratedWorld world)
    {
        _sub = new SubmarineController { Name = "Submarine", QuietMode = _quiet };
        // Physical thrust follows the domain loadout (quiet prop x0.85); the
        // quiet-running cap in the controller still applies on top.
        _sub.ThrustMultiplier = _sim?.EngineThrustMultiplier ?? 1f;
        AddChild(_sub);
        var start = WorldBuilder.ToG(world.GetNode(world.ExtractionNodeId).Position);
        _sub.GlobalPosition = start + new Vector3(0f, 2f, 12f);
        // Face along the first route leg so the player starts oriented.
        var dest = start;
        if (world.RouteFromExtractionToObjective.Count >= 2)
        {
            dest = WorldBuilder.ToG(world.GetNode(world.RouteFromExtractionToObjective[1]).Position);
        }
        var look = dest - _sub.GlobalPosition;
        if (look.Length() > 1f)
        {
            _sub.LookAt(_sub.GlobalPosition + look, Vector3.Up);
        }
        _sub.BodyEntered += OnHullBump;
        CacheMarkers(world);
    }

    private void CacheMarkers(GeneratedWorld world)
    {
        foreach (var l in world.LootSpawns)
        {
            var node = GetNodeOrNull<MeshInstance3D>("Loot_" + l.SpawnId.Replace('.', '_'));
            if (node is not null) _lootMarkers[l.SpawnId] = node;
        }
    }

    private void OnHullBump(Node body)
    {
        // Hull scrape: small honest feedback, domain damage stays with creatures/pressure.
        _hud?.ShowMessage(Localization.T("Hull scrape — corridor wall. Ease off the thrust."), 2.5f);
        _audio?.PlayClunk();
    }

    private void BuildHud()
    {
        _hud = new RunHud { Name = "RunHud" };
        AddChild(_hud);
        // Defer one frame so RunHud._Ready built Sonar.
        _hud.ResumeRequested += () => SetPaused(false);
        _hud.AbortRequested += AbortToMenu;
        _hud.RetrySaveRequested += () => RetrySettlementAsync();
        _hud.BuddyToggled += on =>
        {
            if (_buddy is not null) _buddy.Enabled = on;
            _hud?.ShowMessage(on ? Localization.T("Sonar buddy on.") : Localization.T("Sonar buddy off — instruments only."), 2.5f);
        };
        _audio?.ApplyVolume(GameServices.Settings.MasterVolumePercent);
    }

    /// <summary>
    /// Solo-only buddy: every run is solo, so the buddy ships in every run
    /// (default ON, pause-menu toggleable). The tutorial director is separate.
    /// Buddy blips reuse ProceduralAudio.PlayTick (short chirp); breach/threat
    /// alarms stay event-driven so alerts are never audio-only.
    /// </summary>
    private void BuildBuddy()
    {
        _buddy = new SonarBuddy { Name = "SonarBuddy" };
        AddChild(_buddy);
        _buddy.CalloutRaised += (text, critical) =>
        {
            _hud?.ShowBuddyCallout(text, critical);
            _audio?.PlayTick();
        };
    }

    /// <summary>
    /// Co-op run mode (docs/02 §9): the host sim is the world authority and
    /// broadcasts ~15 Hz snapshots; clients run their own sim as prediction and
    /// correct it from snapshots, sending interaction intents for authorization.
    /// Ships stay independent (each player's hull/pressure/sonar is local).
    /// </summary>
    private void SetupCoopMode()
    {
        var session = CoopLaunchContext.Session;
        if (session is null || !session.IsActive || _sim is null) return;
        _session = session;
        _isCoopHost = session.IsHost;
        _isCoopClient = !session.IsHost;
        if (_isCoopHost)
        {
            session.IntentReceived += OnCoopIntent;
        }
        else
        {
            session.SnapshotReceived += OnCoopSnapshot;
            _lastLocalDrill = _sim.IsDrilling;
        }
    }

    private void BroadcastWorldSnapshot()
    {
        if (_sim is null || _session is null) return;
        var domain = _sim.BuildWorldStateSnapshot();
        var snap = new WorldSnapshot(
            _session.SessionId, domain.Phase, domain.FailureReason, domain.MajorEventId,
            domain.MajorEventRemaining, domain.SecuredSalvageValue, domain.SalvagedLootMask,
            domain.ServicedNodesMask, domain.SurveysDone, domain.PulsesUsed, domain.ObserveSeconds,
            domain.DrillLootIndex, domain.DrillElapsedSeconds,
            domain.Creatures.Select(c => new CreatureWire(c.X, c.Y, c.Z, c.State, c.StateTime, c.StunnedSeconds, c.SonarExposedSeconds)).ToList());
        if (WorldSnapshotCodec.TryEncode(snap, _snapshotBuffer, out var written))
        {
            _session.BroadcastSnapshot(_snapshotBuffer.AsSpan(0, written).ToArray());
        }
    }

    private void SendCoopIntent(IntentType type, string targetId, Vector3 godotPosition, int extra)
    {
        if (_session is null) return;
        var pos = WorldBuilder.ToS(godotPosition);
        var intent = new PlayerIntent(_session.SessionId, type, targetId, pos.X, pos.Y, pos.Z, extra);
        Span<byte> buffer = stackalloc byte[MessageBounds.MaxPayload(MessageType.PlayerIntent)];
        if (PlayerIntentCodec.TryEncode(intent, buffer, out var written))
        {
            // Host peer id is always 1 (NetworkLobby.HostPeerId).
            _session.SendIntent(1, buffer[..written].ToArray());
        }
    }

    /// <summary>Host: authorize a remote player's interaction against the shared world.</summary>
    private void OnCoopIntent(ulong peerId, byte[] payload)
    {
        if (_sim is null || _ended || _session is null) return;
        if (!PlayerIntentCodec.TryDecode(payload, out var intent) || intent is null) return;
        if (intent.SessionId != _session.SessionId) return;
        var pos = new SysVec(intent.PositionX, intent.PositionY, intent.PositionZ);
        switch (intent.Type)
        {
            case IntentType.Salvage:
                _sim.TrySalvageFrom(intent.TargetId, pos);
                break;
            case IntentType.DrillStart:
                _sim.TryStartDrillFrom(intent.TargetId, pos);
                break;
            case IntentType.DrillCancel:
                _sim.TryCancelDrill();
                break;
            case IntentType.Survey:
                _sim.ApplyRemoteSurvey();
                break;
            case IntentType.Service:
                _sim.ApplyRemoteService(intent.TargetId, pos);
                break;
            case IntentType.Pulse:
                _sim.ApplyRemotePulse(pos);
                break;
        }
    }

    /// <summary>Client: correct the local prediction from the host's world snapshot.</summary>
    private void OnCoopSnapshot(ulong peerId, byte[] payload)
    {
        if (_sim is null || _ended || _session is null) return;
        if (!WorldSnapshotCodec.TryDecode(payload, out var snap) || snap is null) return;
        if (snap.SessionId != _session.SessionId) return;
        var domain = new WorldStateSnapshot(
            snap.Phase, snap.FailureReason, snap.MajorEventId, snap.MajorEventRemaining,
            snap.SecuredSalvageValue, snap.SalvagedLootMask, snap.ServicedNodesMask,
            snap.SurveysDone, snap.PulsesUsed, snap.ObserveSeconds, snap.DrillLootIndex,
            snap.DrillElapsedSeconds,
            snap.Creatures.Select(c => new CreatureStateWire(c.X, c.Y, c.Z, c.State, c.StateTime, c.StunnedSeconds, c.SonarExposedSeconds)).ToList());
        if (!_sim.ApplyWorldSnapshot(domain)) return;
        if (snap.Phase == (byte)RunPhase.Extracted)
        {
            OnHostExtracted();
        }
    }

    /// <summary>Client: the host's run ended by extraction — the expedition is over.</summary>
    private void OnHostExtracted()
    {
        if (_ended) return;
        _ended = true;
        _hud?.ShowEnd(Localization.T("EXPEDITION COMPLETE"), Localization.T("The host extracted. This profile was not credited."), false);
    }

    /// <summary>Client: the session died (host left or network loss) — end honestly.</summary>
    private void OnHostDisconnected()
    {
        if (_ended) return;
        _ended = true;
        _hud?.ShowEnd(Localization.T("HOST DISCONNECTED"), Localization.T("The host left. Run ended; nothing was credited."), false);
    }

    private void BuildTutorial()
    {
        _director = new TutorialDirector { Name = "TutorialDirector" };
        AddChild(_director);
        _director.Setup(Array.Empty<string>());
        _director.StepCompleted += OnTutorialStep;
        LoadTutorialStateAsync();
    }

    private async void LoadTutorialStateAsync()
    {
        if (_store is null || _director is null) return;
        // Reads stay allowed in every mode (including the unsaved session and
        // safe-mode, which only overrides config): resume from real progress.
        try
        {
            var profile = await _store.LoadAsync(CancellationToken.None);
            if (!IsInstanceValid(this) || _director is null) return;
            _director.Setup(profile.Tutorial.CompletedSteps ?? new List<string>());
            if (profile.Tutorial.Completed && _hud is not null && IsInstanceValid(_hud))
            {
                _hud.ShowMessage(Localization.T("Tutorial replay — steps already logged; checklist resumes complete."), 5f);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (SaveException ex)
        {
            if (!IsInstanceValid(this)) return;
            _tutorialSaveUsable = false;
            _hud?.ShowMessage(Localization.T("Tutorial progress cannot be saved [{0}]: the run continues, steps just will not persist.", (object)ex.Code), 6f);
        }
        catch (Exception ex)
        {
            if (!IsInstanceValid(this)) return;
            _tutorialSaveUsable = false;
            _hud?.ShowMessage(Localization.T("Tutorial progress cannot be saved ({0}): the run continues.", (object)ex.GetType().Name), 6f);
        }
    }

    /// <summary>
    /// Step handler retains the step in memory only. The single file write is
    /// held until finish (extraction): steps retry next tutorial launch when
    /// the run ends without extraction, with no timeout fake-claim.
    /// </summary>
    private void OnTutorialStep(string stepId)
    {
        _tutorialPendingSteps.Add(stepId);
    }

    /// <summary>
    /// Single held tutorial write at finish. All Node reads happen on the main
    /// thread BEFORE the worker callback: the callback only unions captured
    /// step IDs and the captured extraction flag. Completed persists only when
    /// every expected step ID is unioned AND a real extraction was observed —
    /// a skipped extract never counts as completion. Errors are reported
    /// visibly and the caller must not show success text when unwritten.
    /// Returns true when the completion state was persisted.
    /// </summary>
    private async Task<bool> PersistTutorialCompletionAsync(CancellationToken ct)
    {
        if (_store is null || _director is null || !_tutorialSaveUsable) return false;
        if (GameServices.WritesSuspendedByChoice) return false;
        // Main-thread facts: never touch the director inside the worker.
        var seenSteps = new List<string>(_director.CompletedSteps);
        foreach (var s in _tutorialPendingSteps)
        {
            if (!seenSteps.Contains(s, StringComparer.Ordinal)) seenSteps.Add(s);
        }
        var extractionObserved = _tutorialExtractionObserved;
        try
        {
            var updated = await _store.UpdateAsync(p =>
            {
                var steps = new List<string>(p.Tutorial.CompletedSteps ?? new List<string>());
                foreach (var id in seenSteps)
                {
                    if (!steps.Contains(id, StringComparer.Ordinal)) steps.Add(id);
                }
                var allUnioned = TutorialDirector.StepOrder.All(id => steps.Contains(id, StringComparer.Ordinal));
                var completed = p.Tutorial.Completed || (allUnioned && extractionObserved);
                return p with { Tutorial = new SaveTutorial(completed, steps) };
            }, ct);
            if (ct.IsCancellationRequested || !IsInstanceValid(this)) return false;
            _tutorialSaveUsable = true;
            return updated.Tutorial.CompletedSteps.Count > 0 || updated.Tutorial.Completed;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (SaveException ex)
        {
            if (!IsInstanceValid(this)) return false;
            _tutorialSaveUsable = false;
            _hud?.ShowMessage(Localization.T("Tutorial progress not saved [{0}]: the run still counts; steps retry next tutorial launch.", (object)ex.Code), 6f);
            return false;
        }
        catch (Exception ex)
        {
            if (!IsInstanceValid(this)) return false;
            _tutorialSaveUsable = false;
            _hud?.ShowMessage(Localization.T("Tutorial progress not saved ({0}): the run still counts; steps retry next tutorial launch.", (object)ex.GetType().Name), 6f);
            return false;
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_sim is null || _sub is null || _world is null || _hud is null || _paused || _ended)
        {
            return;
        }
        var dt = Math.Min((float)delta, 0.5f);
        _sub.PollContinuous();
        var throttle = _quiet ? Math.Min(_sub.Throttle01, 0.25f) : _sub.Throttle01;
        var input = new ShipControlInput(throttle, _quiet, _sub.BoostHeld);
        _sim.Tick(dt, WorldBuilder.ToS(_sub.GlobalPosition), input);

        if (_isCoopHost)
        {
            _snapshotTimer += dt;
            if (_snapshotTimer >= 0.066f)
            {
                _snapshotTimer = 0f;
                BroadcastWorldSnapshot();
            }
        }
        else if (_isCoopClient)
        {
            // Drill-cancel watcher: the local sim cancelled its drill (undock,
            // out of range, target gone) — tell the host so the remote cut stops.
            var drilling = _sim.IsDrilling;
            if (_lastLocalDrill && !drilling)
            {
                SendCoopIntent(IntentType.DrillCancel, string.Empty, _sub.GlobalPosition, 0);
            }
            _lastLocalDrill = drilling;
            if (_session is not null && !_session.IsActive)
            {
                OnHostDisconnected();
                return;
            }
        }

        if (_sim.Phase == RunPhase.Failed)
        {
            OnRunFailed();
            return;
        }
        _buddy?.Tick(dt, _sim, _sub.GlobalPosition, -_sub.GlobalTransform.Basis.Z,
            GetWorld3D().DirectSpaceState, _sub.GetRid());
        if (_isTutorial && !_trainingBreachDone && _sim.IsDocked)
        {
            // Authored training incident, once per tutorial run: a small real
            // severity-1 breach with live flooding/pressure/audio. Repair uses
            // the normal sealant path; never a winch substitution, never fatal.
            var breach = _sim.TryInjectTrainingBreach();
            if (breach.Success)
            {
                _trainingBreachDone = true;
                _audio?.PlayAlarm();
                _hud.ShowMessage(Localization.T("Training incident: real hull breach — press R to weld it with sealant."), 6f);
            }
        }
        if (_director is not null && !_ended)
        {
            _director.Tick(dt, _sub.Throttle01, _sim, NearestLootDistance());
        }
        _statusPoll += dt;
        if (_statusPoll > 0.15f)
        {
            _statusPoll = 0f;
            RefreshHud();
        }
        SyncThreatMarkers();
        SyncLootMarkers();
    }

    private void RefreshHud()
    {
        if (_sim is null || _sub is null || _world is null || _hud is null || _catalog is null) return;
        var frameName = _catalog.Frames.TryGetValue(_options!.FrameId, out var f) ? f.DisplayName : _options.FrameId;
        _hud.UpdateInstruments(_sim, _quiet, frameName);
        var heading = MathF.Atan2(-_sub.GlobalTransform.Basis.Z.X, -_sub.GlobalTransform.Basis.Z.Z);
        var objective = _world.GetNode(_world.ObjectiveNodeId).Position;
        var extract = _world.GetNode(_world.ExtractionNodeId).Position;
        float nearest = -1f;
        foreach (var c in _sim.CreatureStates)
        {
            var d = (WorldBuilder.ToG(c.Position) - _sub.GlobalPosition).Length();
            if (nearest < 0f || d < nearest) nearest = d;
        }
        var pulseRange = PulseRange();
        _hud.Sonar.UpdateContacts(_sim.Contacts, pulseRange, _sub.GlobalPosition, heading, objective, extract, nearest, _quiet);
        if (_buddy is not null) _hud.Sonar.SetTrail(_buddy.Trail);
        var objName = _world.ObjectiveNodeId;
        _hud.Sonar.SetObjectiveText(objName.Replace("node.", Localization.T("SITE") + " "));
        // Steady warning banner (no flashing): threat, power, hull, cooldown.
        var warn = "";
        if (_sim.IsDrilling) warn = Localization.T("DRILL TURNING — {0:F0}s left, 35 PU, hold position (H cancels)", _sim.DrillRemainingSeconds);
        else if (_sim.IsDocked) warn = Localization.T("DOCKED — drill (H) or undock (J)");
        else if (_sim.ActiveMajorEvent is not null) warn = MajorEventLabel();
        else if (_sim.Threat > 70f) warn = Localization.T("THREAT HIGH — go quiet (Z) or break contact");
        else if (_sim.BrownoutActive) warn = Localization.T("BROWNOUT — sonar offline, cut thrust");
        else if (_sim.HullIntegrity < _sim.MaxHull * 0.3f) warn = Localization.T("HULL CRITICAL — repair (R) or winch (X)");
        else if (_sim.PressureMargin < 0f) warn = Localization.T("CRUSH DEPTH — ascend or ease deeper load");
        _hud.SetWarning(warn);
        if (_sim.Threat > 70f && _audio is not null)
        {
            // Alarm is event-driven elsewhere; keep continuous cue off to avoid noise spam.
        }
    }

    private string MajorEventLabel()
    {
        if (_sim is null || _sim.ActiveMajorEvent is null) return "";
        return _sim.ActiveMajorEvent switch
        {
            "event.acoustic_disturbance" => Localization.T("ACOUSTIC DISTURBANCE — passive sonar halved, pulse slower ({0:F0}s)", _sim.MajorEventRemaining),
            "event.facility_alarm" => Localization.T("FACILITY ALARM — creatures converging ({0:F0}s)", _sim.MajorEventRemaining),
            "event.anomaly" => Localization.T("SONAR ANOMALY — contacts unreliable ({0:F0}s)", _sim.MajorEventRemaining),
            "event.current_shift" => Localization.T("CURRENT SHIFT — engine draw +15 PU, noise +10 ({0:F0}s)", _sim.MajorEventRemaining),
            "event.collapsing_trench" => Localization.T("COLLAPSING TRENCH — debris zone, avoid ({0:F0}s)", _sim.MajorEventRemaining),
            "event.false_distress_beacon" => Localization.T("FALSE DISTRESS BEACON — trap ({0:F0}s)", _sim.MajorEventRemaining),
            "event.relic_resonance" => Localization.T("RELIC RESONANCE — noise +30, creatures converge ({0:F0}s)", _sim.MajorEventRemaining),
            "event.extraction_ambush" => Localization.T("EXTRACTION AMBUSH — creatures at extraction ({0:F0}s)", _sim.MajorEventRemaining),
            _ => "",
        };
    }

    private float PulseRange()
    {
        // Actual loadout-aware range from the sim (biome x module x blackout),
        // never a stale constant.
        if (_sim is not null) return _sim.ActivePulseRangeMeters;
        if (_catalog is null || _world is null) return DomainConstants.PulseBaseRangeMeters;
        var range = _catalog.Biomes[_world.BiomeId].PulseRangeMeters;
        if (_options!.ModifierIds.Contains("modifier.sonar_blackout")) range *= 0.7f;
        return range;
    }

    public override void _Input(InputEvent @event)
    {
        if (@event.IsActionPressed("abyss_pause"))
        {
            if (!_ended) SetPaused(!_paused);
            GetViewport().SetInputAsHandled();
            return;
        }
        if (_paused || _ended || _sim is null || _world is null) return;
        var sub = _sub;
        var world = _world;
        if (sub is null || world is null) return;
        if (@event.IsActionPressed("abyss_silent"))
        {
            _quiet = !_quiet;
            sub.QuietMode = _quiet; // physical thrust cap, not just domain.
            _hud?.ShowMessage(_quiet ? Localization.T("Quiet running: thrust capped, active sonar disabled.") : Localization.T("Normal running."), 3f);
            if (_hud is not null)
            {
                _hud.Sonar.UpdateContacts(Array.Empty<ContactSnapshot>(), PulseRange(), sub.GlobalPosition, 0f,
                    world.GetNode(world.ObjectiveNodeId).Position, world.GetNode(world.ExtractionNodeId).Position, -1f, _quiet);
            }
        }
        else if (@event.IsActionPressed("abyss_ping")) DoPing();
        else if (@event.IsActionPressed("abyss_interact")) DoSalvage();
        else if (@event.IsActionPressed("abyss_survey")) DoSurvey();
        else if (@event.IsActionPressed("abyss_service")) DoService();
        else if (@event.IsActionPressed("abyss_repair")) DoRepair();
        else if (@event.IsActionPressed("abyss_dock")) DoDockToggle();
        else if (@event.IsActionPressed("abyss_drill")) DoDrillStart();
        else if (@event.IsActionReleased("abyss_drill")) DoDrillRelease();
        else if (@event.IsActionPressed("abyss_winch")) DoWinch();
        else if (@event.IsActionPressed("abyss_buoy")) DoBuoy();
        else if (@event.IsActionPressed("abyss_decoy")) DoDecoy();
        else if (@event.IsActionPressed("abyss_emp")) DoEmp();
        else if (@event.IsActionPressed("abyss_consumable_1")) DoConsumable(0);
        else if (@event.IsActionPressed("abyss_consumable_2")) DoConsumable(1);
        else if (@event.IsActionPressed("abyss_consumable_3")) DoConsumable(2);
        else if (@event.IsActionPressed("abyss_consumable_4")) DoConsumable(3);
        else if (@event.IsActionPressed("abyss_consumable_5")) DoConsumable(4);
        else if (@event.IsActionPressed("abyss_consumable_6")) DoConsumable(5);
        else if (@event.IsActionPressed("abyss_consumable_7")) DoConsumable(6);
        else if (@event.IsActionPressed("abyss_consumable_8")) DoConsumable(7);
        else if (@event.IsActionPressed("abyss_extract")) DoExtract();
    }

    private void DoPing()
    {
        if (_sim is null || _hud is null || _audio is null || _sub is null || _world is null) return;
        var result = _sim.Pulse();
        if (!result.Success)
        {
            _hud.ShowMessage(Localization.T("Ping refused: {0}", (object)Localization.T(result.Reason, result.Args)), 3f);
            return;
        }
        if (_isCoopClient) SendCoopIntent(IntentType.Pulse, string.Empty, _sub.GlobalPosition, 0);
        _lastPulseContacts = result.Contacts;
        _audio.PlayPing();
        _hud.ShowMessage(Localization.T("Ping: {0} contacts. +{1:F0} threat.", result.Contacts.Count, result.ThreatAdded), 3f);
        RefreshHud();
    }

    private void DoSalvage()
    {
        if (_sim is null || _sub is null || _world is null || _hud is null || _audio is null) return;
        var reach = _sim.SalvageRangeMeters; // actual loadout-aware reach, not a stale constant.
        var best = NearestLoot(reach);
        if (best is null)
        {
            _hud.ShowMessage(Localization.T("No salvage within {0:F0} m — ping (F), close in, then E.", reach), 3f);
            return;
        }
        var result = _sim.TrySalvage(best.SpawnId);
        if (result.Success)
        {
            if (_isCoopClient) SendCoopIntent(IntentType.Salvage, best.SpawnId, _sub.GlobalPosition, 0);
            _audio.PlayClunk();
            _hud.ShowMessage(Localization.T("Secured {0} +{1} cr{2}.", Localization.T(best.Kind.ToString()), result.ValueBanked, result.QuestItemId != "" ? " [" + result.QuestItemId + "]" : ""), 4f);
            // Codex discovery (real play only): relic/bio/quest salvage carries a
            // catalog trait id; scrap and crates carry none.
            if (!string.IsNullOrEmpty(best.TraitId) && _catalog is not null
                && _catalog.RelicTraits.ContainsKey(best.TraitId))
            {
                _salvagedTraitIds.Add(best.TraitId);
            }
        }
        else
        {
            _hud.ShowMessage(Localization.T("Salvage refused: {0}", (object)Localization.T(result.Reason, result.Args)), 4f);
        }
        RefreshHud();
    }

    private void DoSurvey()
    {
        if (_sim is null || _sub is null || _hud is null || _audio is null) return;
        var ship = WorldBuilder.ToS(_sub.GlobalPosition);
        ContactSnapshot? best = null;
        var bestDist = float.MaxValue;
        foreach (var c in _sim.Contacts)
        {
            if (c.Surveyed) continue;
            var d = (c.ApproxPosition - ship).Length();
            if (d < bestDist) { bestDist = d; best = c; }
        }
        if (best is null)
        {
            _hud.ShowMessage(Localization.T("No unsurveyed contacts — ping (F) first."), 3f);
            return;
        }
        var result = _sim.TrySurvey(best.ContactId);
        _hud.ShowMessage(result.Success ? Localization.T("Surveyed {0} ({1} total).", Localization.T(best.Class.ToString()), _sim.SurveysDone) : Localization.T("Survey refused: {0}", (object)Localization.T(result.Reason, result.Args)), 4f);
        if (result.Success)
        {
            if (_isCoopClient) SendCoopIntent(IntentType.Survey, string.Empty, _sub.GlobalPosition, 0);
            _audio.PlayTick();
            RecordSurveyDiscovery(best);
        }
        RefreshHud();
    }

    /// <summary>
    /// Codex discovery (real play only): a successfully surveyed biological
    /// contact resolves through the world threat spawns to its catalog
    /// creature id. Ghost/terrain/structure/salvage contacts resolve to
    /// nothing and are ignored — never invented.
    /// </summary>
    private void RecordSurveyDiscovery(ContactSnapshot contact)
    {
        if (contact.Class != SonarClass.Biological) return;
        if (_world is null || _catalog is null) return;
        var spawnId = contact.ContactId.StartsWith("contact.bio.", StringComparison.Ordinal)
            ? contact.ContactId["contact.bio.".Length..]
            : contact.ContactId.StartsWith("contact.passive.", StringComparison.Ordinal)
                ? contact.ContactId["contact.passive.".Length..]
                : null;
        if (string.IsNullOrEmpty(spawnId)) return;
        var spawn = _world.ThreatSpawns.FirstOrDefault(t => t.SpawnId == spawnId);
        if (spawn is null) return;
        if (_catalog.Creatures.ContainsKey(spawn.CreatureId))
        {
            _surveyedCreatureIds.Add(spawn.CreatureId);
        }
    }

    private void DoService()
    {
        if (_sim is null || _sub is null || _world is null || _hud is null || _audio is null) return;
        var ship = _sub.GlobalPosition;
        string? bestNode = null;
        var bestDist = float.MaxValue;
        foreach (var n in _world.Nodes)
        {
            if (string.IsNullOrEmpty(n.ServiceId)) continue;
            var d = (WorldBuilder.ToG(n.Position) - ship).Length();
            if (d < bestDist) { bestDist = d; bestNode = n.Id; }
        }
        if (bestNode is null)
        {
            _hud.ShowMessage(Localization.T("No contract service node on this route leg."), 3f);
            return;
        }
        var result = _sim.TryServiceContractNode(bestNode);
        _hud.ShowMessage(result.Success ? Localization.T("Serviced {0} (sealant {1}).", bestNode, result.SealantRemaining) : Localization.T("Service refused: {0}", (object)Localization.T(result.Reason, result.Args)), 4f);
        if (result.Success)
        {
            if (_isCoopClient) SendCoopIntent(IntentType.Service, bestNode, _sub.GlobalPosition, 0);
            _audio.PlayChime();
        }
        RefreshHud();
    }

    private void DoRepair()
    {
        if (_sim is null || _hud is null || _audio is null) return;
        var zones = _sim.FloodZones;
        var worst = -1;
        var worstScore = 0f;
        for (var i = 0; i < zones.Count; i++)
        {
            var score = zones[i].Severity * 10f + zones[i].FloodPercent * 0.1f;
            if (zones[i].Severity > 0 && score > worstScore) { worstScore = score; worst = i; }
        }
        if (worst < 0)
        {
            _hud.ShowMessage(Localization.T("No breaches — compartments holding."), 3f);
            return;
        }
        var result = _sim.TryRepairHull(worst);
        _hud.ShowMessage(result.Success ? Localization.T("Welded {0} (sealant {1}).", zones[worst].ZoneId, result.SealantRemaining) : Localization.T("Repair refused: {0}", (object)Localization.T(result.Reason, result.Args)), 4f);
        if (result.Success) _audio.PlayClunk();
        RefreshHud();
    }

    private void DoDockToggle()
    {
        if (_sim is null || _sub is null || _world is null || _hud is null) return;
        if (_sim.IsDocked)
        {
            var result = _sim.TryUndock();
            if (!result.Success)
            {
                _hud.ShowMessage(Localization.T("Undock refused: {0}", (object)Localization.T(result.Reason, result.Args)), 3f);
                return;
            }
            ApplyDockFreeze();
            _hud.ShowMessage(Localization.T("Undocked — hull free. Drill cancelled if one was running (no award)."), 4f);
            RefreshHud();
            return;
        }
        // Nearest dockable station within honest dock range; speed from the
        // live rigid body (host validates pose + speed, never teleports).
        string? bestNode = null;
        var bestDist = float.MaxValue;
        foreach (var n in _world.Nodes)
        {
            if (!_sim.IsDockableNode(n)) continue;
            var d = (WorldBuilder.ToG(n.Position) - _sub.GlobalPosition).Length();
            if (d < bestDist) { bestDist = d; bestNode = n.Id; }
        }
        if (bestNode is null)
        {
            _hud.ShowMessage(Localization.T("No docking station on this route leg."), 3f);
            return;
        }
        var speed = _sub.LinearVelocity.Length();
        var dock = _sim.TryDock(bestNode, speed);
        if (!dock.Success)
        {
            _hud.ShowMessage(Localization.T("Dock refused: {0}", (object)Localization.T(dock.Reason, dock.Args)), 4f);
            return;
        }
        ApplyDockFreeze();
        _audio?.PlayClunk();
        _hud.ShowMessage(Localization.T("Docked at {0} — hull held at safe offset. Drill (H) available; undock with J.", (object)bestNode), 5f);
        RefreshHud();
    }

    private void ApplyDockFreeze()
    {
        if (_sub is null || _sim is null) return;
        var frozen = _paused || _sim.IsDocked;
        _sub.Freeze = frozen;
        _sub.LinearVelocity = Vector3.Zero;
        _sub.AngularVelocity = Vector3.Zero;
    }

    private void DoDrillStart()
    {
        if (_sim is null || _sub is null || _world is null || _hud is null || _audio is null) return;
        if (_sim.IsDrilling)
        {
            // Toggle: second press cancels with no award.
            var cancel = _sim.TryCancelDrill();
            if (cancel.Success && _isCoopClient) SendCoopIntent(IntentType.DrillCancel, string.Empty, _sub.GlobalPosition, 0);
            _hud.ShowMessage(cancel.Success ? Localization.T("Drill cancelled — no salvage banked.") : Localization.T("Drill cancel refused: {0}", (object)Localization.T(cancel.Reason, cancel.Args)), 3f);
            RefreshHud();
            return;
        }
        if (!_sim.IsDocked)
        {
            _hud.ShowMessage(Localization.T("Drill needs a docked station: slow to ≤2 m/s within 25 m and press J first."), 4f);
            return;
        }
        var reach = _sim.DrillRangeMeters;
        var secured = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in _sim.CargoItems) secured.Add(item.LootSpawnId);
        LootSpawn? best = null;
        var bestDist = reach;
        foreach (var l in _world.LootSpawns)
        {
            if (secured.Contains(l.SpawnId)) continue;
            var d = (WorldBuilder.ToG(l.Position) - _sub.GlobalPosition).Length();
            if (d < bestDist) { bestDist = d; best = l; }
        }
        if (best is null)
        {
            _hud.ShowMessage(Localization.T("No drill target within {0:F0} m — cores drill here; other salvage uses E. Hold H for 8 s once docked.", reach), 4f);
            return;
        }
        var result = _sim.TryStartDrill(best.SpawnId);
        if (result.Success && _isCoopClient) SendCoopIntent(IntentType.DrillStart, best.SpawnId, _sub.GlobalPosition, 0);
        _hud.ShowMessage(result.Success
            ? Localization.T("Drill turning on {0} — hold H and hold position 8 s (35 PU, threat rising). Release/second-press cancels with no award.", (object)Localization.T(best.Kind.ToString()))
            : Localization.T("Drill refused: {0}", (object)Localization.T(result.Reason, result.Args)), 4f);
        RefreshHud();
    }

    private void DoDrillRelease()
    {
        if (_sim is null || _sub is null || _hud is null) return;
        if (!_sim.IsDrilling) return;
        // Hold semantics: releasing H before the 8 s cut completes cancels it.
        var cancel = _sim.TryCancelDrill();
        if (cancel.Success)
        {
            if (_isCoopClient) SendCoopIntent(IntentType.DrillCancel, string.Empty, _sub.GlobalPosition, 0);
            _hud.ShowMessage(Localization.T("Drill released early — no salvage banked."), 3f);
        }
        RefreshHud();
    }

    private void DoWinch()
    {
        if (_sim is null || _sub is null || _hud is null) return;
        var result = _sim.TryUseWinch(out var pos);
        if (!result.Success)
        {
            _hud.ShowMessage(Localization.T("Winch refused: {0}", (object)Localization.T(result.Reason, result.Args)), 3f);
            return;
        }
        _sub.GlobalPosition = WorldBuilder.ToG(pos);
        _sub.LinearVelocity = Vector3.Zero;
        _sub.AngularVelocity = Vector3.Zero;
        _hud.ShowMessage(Localization.T("Emergency winch fired — back at last safe water."), 4f);
        RefreshHud();
    }

    private void DoBuoy()
    {
        if (_sim is null || _hud is null) return;
        var result = _sim.TryFireBuoy();
        if (!result.Success)
        {
            _hud.ShowMessage(Localization.T("Buoy refused: {0}", (object)Localization.T(result.Reason, result.Args)), 3f);
            return;
        }
        _hud.ShowMessage(Localization.T("Emergency buoy fired — secured salvage recovery boosted on failure."), 4f);
        RefreshHud();
    }

    private void DoDecoy()
    {
        if (_sim is null || _hud is null) return;
        var result = _sim.TryLaunchDecoy();
        if (!result.Success)
        {
            _hud.ShowMessage(Localization.T("Decoy refused: {0}", (object)Localization.T(result.Reason, result.Args)), 3f);
            return;
        }
        _hud.ShowMessage(Localization.T("Acoustic decoy launched — creatures within 300 m investigate it for 20 s."), 4f);
        RefreshHud();
    }

    private void DoEmp()
    {
        if (_sim is null || _hud is null) return;
        var result = _sim.TryFireEmp();
        if (!result.Success)
        {
            _hud.ShowMessage(Localization.T("EMP refused: {0}", (object)Localization.T(result.Reason, result.Args)), 3f);
            return;
        }
        _hud.ShowMessage(Localization.T("EMP coil fired — creatures within 120 m stunned for 6 s."), 4f);
        RefreshHud();
    }

    private void DoConsumable(int slotIndex)
    {
        if (_sim is null || _hud is null || _catalog is null) return;
        var equipped = _options?.EffectiveConsumableIds ?? Array.Empty<string>();
        if (slotIndex < 0 || slotIndex >= equipped.Count)
        {
            _hud.ShowMessage(Localization.T("No consumable in slot {0} — equip one at the contract screen.", slotIndex + 1), 3f);
            return;
        }
        var id = equipped[slotIndex];
        var result = _sim.TryUseConsumable(id);
        if (!result.Success)
        {
            _hud.ShowMessage(Localization.T("Consumable refused: {0}", (object)Localization.T(result.Reason, result.Args)), 3f);
            return;
        }
        _hud.ShowMessage(Localization.T("Used {0}.", (object)Localization.T(_catalog.Consumables[id].DisplayName)), 3f);
        RefreshHud();
    }

    private void DoExtract()
    {
        if (_sim is null || _hud is null || _audio is null) return;
        var result = _sim.TryExtract();
        if (!result.Success || result.Settlement is null)
        {
            _hud.ShowMessage(Localization.T("Extraction refused: {0}", (object)Localization.T(result.Reason, result.Args)), 5f);
            return;
        }
        if (_isTutorial && _director is not null)
        {
            // _PhysicsProcess stops at _ended, so the director would never
            // observe the flipped phase on a later tick. Observe it here, the
            // same way the headless playback test advances the director once
            // after extraction — no extra runtime tick is simulated.
            _director.Tick(0.1f, 0f, _sim, null);
        }
        _audio.PlayChime();
        OnRunSucceeded(result.Settlement);
    }

    private LootSpawn? NearestLoot(float maxMeters)
    {
        if (_sim is null || _sub is null || _world is null) return null;
        var secured = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in _sim.CargoItems) secured.Add(item.LootSpawnId);
        LootSpawn? best = null;
        var bestDist = maxMeters;
        foreach (var l in _world.LootSpawns)
        {
            if (secured.Contains(l.SpawnId)) continue; // never re-target banked salvage.
            var d = (WorldBuilder.ToG(l.Position) - _sub.GlobalPosition).Length();
            if (d < bestDist) { bestDist = d; best = l; }
        }
        return best;
    }

    /// <summary>Measured range to the nearest unsecured loot; null when none remain.</summary>
    private float? NearestLootDistance()
    {
        if (_sim is null || _sub is null || _world is null) return null;
        var secured = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in _sim.CargoItems) secured.Add(item.LootSpawnId);
        float? best = null;
        foreach (var l in _world.LootSpawns)
        {
            if (secured.Contains(l.SpawnId)) continue;
            var d = (WorldBuilder.ToG(l.Position) - _sub.GlobalPosition).Length();
            if (!best.HasValue || d < best.Value) best = d;
        }
        return best;
    }

    private static object[] TranslateArgs(object[] args)
    {
        if (args.Length == 0) return args;
        var translated = new object[args.Length];
        for (var i = 0; i < args.Length; i++)
            translated[i] = args[i] is string s ? Localization.T(s) : args[i];
        return translated;
    }

    private void OnSimEvent(RunEvent evt)
    {
        var text = Localization.T(evt.Message, TranslateArgs(evt.Args));
        switch (evt.Kind)
        {
            case "creature.strike":
            case "hull.breach":
                _audio?.PlayAlarm();
                _hud?.ShowMessage(text, 4f);
                break;
            case "contract.objective":
            case "contract.complete":
            case "drill.complete":
            case "dock.done":
                _audio?.PlayChime();
                _hud?.ShowMessage(text, 5f);
                break;
            case "drill.cancelled":
            case "dock.released":
            case "drill.started":
                _hud?.ShowMessage(text, 4f);
                break;
            case "run.failed":
                break;
            default:
                if (evt.Kind.StartsWith("power.", StringComparison.Ordinal) ||
                    evt.Kind.StartsWith("pressure.", StringComparison.Ordinal) ||
                    evt.Kind.StartsWith("event.", StringComparison.Ordinal) ||
                    evt.Kind is "consumable.used" or "buoy.fired" or "decoy.launched" or "emp.fired")
                {
                    _hud?.ShowMessage(text, 3f);
                }
                break;
        }
        GodotLogBridge.Info(GameServices.Logger, $"[run] {evt.Kind}: {string.Format(evt.Message, evt.Args)}");
    }

    private void SyncThreatMarkers()
    {
        if (_sim is null) return;
        foreach (var c in _sim.CreatureStates)
        {
            if (!_threatMarkers.TryGetValue(c.Id, out var marker) || !IsInstanceValid(marker))
            {
                marker = new MeshInstance3D
                {
                    Mesh = new PrismMesh { Size = new Vector3(2.4f, 5f, 2.4f) },
                    MaterialOverride = new StandardMaterial3D
                    {
                        AlbedoColor = new Color("#dfa44d"),
                        EmissionEnabled = true,
                        Emission = new Color("#dfa44d"),
                        EmissionEnergyMultiplier = c.IsApex ? 1.6f : 0.8f,
                    },
                };
                AddChild(marker);
                _threatMarkers[c.Id] = marker;
            }
            var target = WorldBuilder.ToG(c.Position);
            marker.GlobalPosition = marker.GlobalPosition.Lerp(target, 0.06f);
            marker.LookAt(_sub!.GlobalPosition, Vector3.Up);
        }
    }

    private void SyncLootMarkers()
    {
        if (_sim is null) return;
        var secured = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in _sim.CargoItems) secured.Add(item.LootSpawnId);
        foreach (var kv in _lootMarkers)
        {
            if (!IsInstanceValid(kv.Value)) continue;
            kv.Value.Visible = !secured.Contains(kv.Key);
        }
    }

    private void OnRunFailed()
    {
        if (_ended || _sim is null) return;
        if (_isCoopHost) BroadcastWorldSnapshot(); // final: clients learn the run ended.
        _ended = true;
        var draft = _sim.BuildFailureSettlement();
        _audio?.PlayAlarm();
        if (draft is null)
        {
            _hud?.ShowEnd(Localization.T("DIVE FAILED"), Localization.T(_sim.FailureReason) + "\n" + Localization.T("Settlement unavailable (phase mismatch). Return to menu; nothing was credited."), false);
            return;
        }
        _pendingDraft = draft;
        SettleAsync(draft, failed: true);
    }

    private void OnRunSucceeded(RunSettlementDraft draft)
    {
        if (_ended) return;
        if (_isCoopHost) BroadcastWorldSnapshot(); // final: clients learn the run ended.
        _ended = true;
        _pendingDraft = draft;
        if (_isTutorial)
        {
            // Real extraction observed on the main thread: the held tutorial
            // write at settle decides completion from this flag, so a skipped
            // extract can never count as completion.
            _tutorialExtractionObserved = true;
            _tutorialEndNote = "";
            _hud?.ShowMessage(Localization.T("Extraction confirmed — settling the dive."), 5f);
        }
        SettleAsync(draft, failed: false);
    }

    private async void SettleAsync(RunSettlementDraft draft, bool failed)
    {
        if (_hud is null || _store is null || !IsInstanceValid(this)) return;
        if (_settleInFlight) return; // no parallel retries: one write at a time.
        // Unsaved session by choice: no write of any kind (settlement or
        // tutorial); the profile file stays byte-identical. Reads stayed
        // allowed, so the run still counts on screen — just not persisted.
        if (GameServices.WritesSuspendedByChoice)
        {
            var choiceNote = Localization.T("Not saved by choice: this unsaved session records nothing to the profile.");
            var tutNote = _isTutorial ? "\n" + Localization.T("Tutorial steps retry next tutorial launch.") : "";
            _hud.ShowEnd(failed ? Localization.T("DIVE FAILED") : Localization.T("EXTRACTION COMPLETE"),
                (failed ? Localization.T(_sim!.FailureReason) + "\n" : "") + Localization.T("Settlement {0}: {1} cr, {2} research, {3} shards.", draft.SettlementId, draft.RetainedCredits, draft.ResearchData, draft.Shards) + "\n" + choiceNote + tutNote, false);
            _hud.SetEndButtons(retryVisible: false, menuEnabled: true);
            GodotLogBridge.Info(GameServices.Logger, $"Settlement {draft.SettlementId} skipped (unsaved session by choice).");
            return;
        }
        _settleInFlight = true;
        _settleCts?.Dispose();
        _settleCts = new CancellationTokenSource();
        var ct = _settleCts.Token;
        _saveState = Localization.T("Saving settlement…");
        _hud.ShowEnd(failed ? Localization.T("DIVE FAILED") : Localization.T("EXTRACTION COMPLETE"),
            (failed ? Localization.T(_sim!.FailureReason) + "\n" : "") + Localization.T("Settlement {0}: {1} cr, {2} research, {3} shards.", draft.SettlementId, draft.RetainedCredits, draft.ResearchData, draft.Shards) + "\n" + _saveState, false);
        _hud.SetEndButtons(retryVisible: false, menuEnabled: false); // hold exit until the write lands.
        var payload = new SettlementPayload(draft.SettlementId, draft.RetainedCredits, draft.ResearchData, draft.Shards,
            Array.Empty<string>(), Array.Empty<string>(), BuildCodexDiscovery(draft, failed),
            draft.Outcome == SettlementOutcome.Success ? 1 : 0,
            draft.Outcome == SettlementOutcome.Failed ? 1 : 0);
        try
        {
            var result = await _store.ApplySettlementAsync(payload, ct);
            if (ct.IsCancellationRequested || !IsInstanceValid(this) || _hud is null || !IsInstanceValid(_hud)) return;
            var note = result.Outcome == SettlementApplyOutcome.AlreadyApplied
                ? Localization.T("Already credited (duplicate safely ignored).")
                : Localization.T("Credited once to the settlement ledger.");
            GodotLogBridge.Info(GameServices.Logger, $"Settlement {draft.SettlementId} applied: {result.Outcome}.");
            var endNote = "";
            if (_isTutorial && !failed)
            {
                // Held tutorial write: no timeout fake-claim. Success text
                // appears only when actually persisted; failures stay visible
                // and steps retry next tutorial launch.
                _hud.ShowEnd(failed ? Localization.T("DIVE FAILED") : Localization.T("EXTRACTION COMPLETE"),
                    Localization.T("Settlement {0}: {1} cr, {2} research, {3} shards.", draft.SettlementId, draft.RetainedCredits, draft.ResearchData, draft.Shards) + "\n" + note + "\n" + Localization.T("Saving tutorial progress…"), false);
                var tutOk = await PersistTutorialCompletionAsync(ct);
                if (ct.IsCancellationRequested || !IsInstanceValid(this) || _hud is null || !IsInstanceValid(_hud)) return;
                endNote = tutOk
                    ? "\n" + Localization.T("Tutorial complete: The First Ping logged to your profile.")
                    : "\n" + Localization.T("Tutorial finished, but progress could not be saved — the run still counts; steps retry next tutorial launch.");
                if (tutOk) _hud.ShowMessage(Localization.T("Tutorial complete: The First Ping."), 7f);
            }
            _tutorialEndNote = endNote;
            _hud.ShowEnd(failed ? Localization.T("DIVE FAILED") : Localization.T("EXTRACTION COMPLETE"),
                Localization.T("Settlement {0}: {1} cr, {2} research, {3} shards.", draft.SettlementId, draft.RetainedCredits, draft.ResearchData, draft.Shards) + "\n" + note + endNote, false);
            _hud.SetEndButtons(retryVisible: false, menuEnabled: true);
        }
        catch (OperationCanceledException)
        {
            return; // scene torn down; write was cancelled before touching the ledger.
        }
        catch (SaveException ex)
        {
            if (ct.IsCancellationRequested || !IsInstanceValid(this) || _hud is null || !IsInstanceValid(_hud)) return;
            // No fake credit: visible retry keeps the same idempotent id, so a
            // retry can never double-award even across processes.
            GodotLogBridge.Error(GameServices.Logger, $"Settlement save failed [{ex.Code}]: {ex.Message}", ex.Code);
            _hud.ShowEnd(failed ? Localization.T("DIVE FAILED") : Localization.T("EXTRACTION COMPLETE"),
                Localization.T("Settlement {0}: {1} cr pending — save failed [{2}]: {3}\nNothing was credited. Retry when storage is available.", draft.SettlementId, draft.RetainedCredits, (object)ex.Code, ex.Message), true);
            _hud.SetEndButtons(retryVisible: true, menuEnabled: true);
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested || !IsInstanceValid(this) || _hud is null || !IsInstanceValid(_hud)) return;
            GodotLogBridge.Error(GameServices.Logger, "Settlement save failed unexpectedly: " + ex.GetType().Name, "SAVE-001");
            _hud.ShowEnd(failed ? Localization.T("DIVE FAILED") : Localization.T("EXTRACTION COMPLETE"),
                Localization.T("Settlement {0}: {1} cr pending — unexpected save fault.\nNothing was credited. Retry when storage is available.", draft.SettlementId, draft.RetainedCredits), true);
            _hud.SetEndButtons(retryVisible: true, menuEnabled: true);
        }
        finally
        {
            _settleInFlight = false;
        }
    }

    private void RetrySettlementAsync()
    {
        if (_pendingDraft is null || _hud is null || _settleInFlight) return;
        _hud.HideEnd();
        SettleAsync(_pendingDraft, _sim?.Phase == RunPhase.Failed);
    }

    /// <summary>
    /// Codex ids genuinely observed this run: surveyed creature ids plus
    /// salvaged relic trait ids (both tracked live from real player verbs),
    /// plus the biome id on a completed (extracted) run. Every id is checked
    /// against the catalog; unknown ids are dropped. Unlock lists stay empty:
    /// blueprint purchase flow is not implemented (recorded-only research).
    /// Bounded to a handful of ids, far under SettlementPayload limits.
    /// </summary>
    private string[] BuildCodexDiscovery(RunSettlementDraft draft, bool failed)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (_catalog is not null)
        {
            foreach (var id in _surveyedCreatureIds)
            {
                if (_catalog.Creatures.ContainsKey(id)) found.Add(id);
            }
            foreach (var id in _salvagedTraitIds)
            {
                if (_catalog.RelicTraits.ContainsKey(id)) found.Add(id);
            }
            if (!failed && _catalog.Biomes.ContainsKey(draft.BiomeId))
            {
                found.Add(draft.BiomeId);
            }
        }
        return found.OrderBy(id => id, StringComparer.Ordinal).ToArray();
    }

    private void SetPaused(bool paused)
    {
        if (_ended) return;
        _paused = paused;
        if (_sub is not null)
        {
            // Pause and docking both freeze the hull; undocking while paused
            // stays frozen until resume.
            _sub.Freeze = paused || (_sim?.IsDocked ?? false);
            if (!paused && !(_sim?.IsDocked ?? false))
            {
                _sub.LinearVelocity = Vector3.Zero;
                _sub.AngularVelocity = Vector3.Zero;
            }
        }
        _hud?.SetPaused(paused);
    }

    private void AbortToMenu()
    {
        if (_settleInFlight)
        {
            // A settlement write is in flight: hold exit until it completes so
            // the award can never be lost by leaving mid-write.
            _hud?.ShowMessage(Localization.T("Settlement write in flight — exit held until it completes."), 3f);
            return;
        }
        SetPaused(false);
        _ended = true;
        if (!GameServices.IsInitialized) return;
        var svc = GameServices.Registry.TryResolve<SceneFlowService>(out var s) ? s : null;
        if (svc is not null)
        {
            // InRun -> MainMenu is legal; Settlement overlay already settled or
            // explicitly abandoned by the player (no silent credit either way).
            if (!svc.Navigate(AppScene.MainMenu))
            {
                GetTree()?.CallDeferred(SceneTree.MethodName.ChangeSceneToFile, "res://scenes/main_menu.tscn");
            }
            return;
        }
        GetTree()?.CallDeferred(SceneTree.MethodName.ChangeSceneToFile, "res://scenes/main_menu.tscn");
    }

    public override void _ExitTree()
    {
        try { _settleCts?.Cancel(); }
        catch { }
        finally { _settleCts?.Dispose(); _settleCts = null; }
        if (_sim is not null) _sim.EventRaised -= OnSimEvent;
    }

    private void ShowFatal(string text)
    {
        GD.PushError(text);
        if (GameServices.IsInitialized)
        {
            GodotLogBridge.Error(GameServices.Logger, text, "CONTENT-001");
        }
        var layer = new CanvasLayer { Layer = 50 };
        AddChild(layer);
        var bg = new ColorRect { Color = new Color("#071622") };
        bg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        layer.AddChild(bg);
        var center = new CenterContainer();
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        layer.AddChild(center);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 10);
        center.AddChild(box);
        _fatalLabel = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(480, 0), HorizontalAlignment = HorizontalAlignment.Center };
        _fatalLabel.AddThemeColorOverride("font_color", new Color("#dae4df"));
        box.AddChild(_fatalLabel);
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        box.AddChild(row);
        var menu = new Button { Text = Localization.T("Return to menu"), CustomMinimumSize = new Vector2(220, 38) };
        menu.Pressed += () => GetTree()?.CallDeferred(SceneTree.MethodName.ChangeSceneToFile, "res://scenes/main_menu.tscn");
        row.AddChild(menu);
        _ended = true;
    }
}
