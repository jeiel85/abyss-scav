using AbyssScav.Domain;
using AbyssScav.Persistence;
using Godot;

namespace AbyssScav.Gameplay;

/// <summary>
/// Debug/test-only engine scene (headless): real-physics verification that the
/// playable slice actually plays. NOT a mock: corridors are built by the same
/// WorldBuilder, the ship is the same SubmarineController RigidBody3D, verbs
/// are the same RunSimulation calls, and settlement goes through a real
/// FileSaveStore in a temp dir (user saves untouched). Autopilot only — no
/// manual completion is claimed. Run headless:
/// Godot --headless --path . res://scenes/run_playback_test.tscn
/// Release builds refuse to run this scene.
/// </summary>
public partial class RunPlaybackTest : Node3D
{
    private enum Stage { Setup, Sweep, Quiet, Play, SaveVerify, Tutorial, TutorialSave, Done }

    private Stage _stage = Stage.Setup;
    private ContentCatalog? _catalog;
    private readonly List<string> _log = new();
    private bool _ok = true;
    private int _frame;

    // Sweep state.
    private readonly Queue<(GeneratedWorld World, string Tag)> _sweepQueue = new();
    private (GeneratedWorld World, List<PhysicsPathVerifier.SweepJob> Jobs, string Tag, Node3D Root)? _activeSweep;
    private int _drainFrames;
    private int _sweepIndex;
    private int _sweepBlocked;

    // Quiet state.
    private Node3D? _quietRoot;
    private SubmarineController? _quietSub;
    private int _quietPhase;
    private int _quietFrames;
    private double _quietSpeedSum;
    private int _quietSpeedN;
    private float _quietMaxForce;
    private double _speedNormal;
    private float _forceNormal;
    private double _speedQuiet;
    private float _forceQuiet;

    // Play state.
    private Node3D? _playRoot;
    private GeneratedWorld? _playWorld;
    private RunSimulation? _playSim;
    private SubmarineController? _playSub;
    private List<Vector3> _playRoute = new();
    private int _playWp;
    private bool _playReturning;
    private List<Vector3> _playReturn = new();
    private int _playRetIdx;
    private int _playFrames;
    private Vector3 _stuckPos;
    private int _stuckFrames;
    private float _pingTimer;
    private RunSettlementDraft? _playDraft;

    // Tutorial state (T0 pass: same fixed options as the menu/contract launch).
    private Node3D? _tutRoot;
    private GeneratedWorld? _tutWorld;
    private RunSimulation? _tutSim;
    private SubmarineController? _tutSub;
    private TutorialDirector? _tutDirector;
    private SonarBuddy? _tutBuddy;
    private FileSaveStore? _tutStore;
    private string _tutDir = "";
    private List<Vector3> _tutRoute = new();
    private int _tutWp;
    private List<Vector3> _tutReturn = new();
    private int _tutRetIdx;
    private int _tutFrames;
    private Vector3 _tutStuckPos;
    private int _tutStuckFrames;
    private float _tutPingTimer;
    private bool _tutReturning;
    private RunSettlementDraft? _tutDraft;
    private readonly List<string> _tutStepsSeen = new();

    private void Check(bool cond, string name, string detail = "")
    {
        _log.Add((cond ? "SMOKE PASS " : "SMOKE FAIL ") + name + (detail == "" ? "" : " | " + detail));
        if (!cond) _ok = false;
    }

    public override void _Ready()
    {
        if (!OS.IsDebugBuild())
        {
            GD.Print("SMOKE FAIL test.scene | release build refuses the debug test scene.");
            GetTree()?.CallDeferred(SceneTree.MethodName.Quit, 1);
            return;
        }
        AbyssInput.EnsureRegistered();
        if (!ContentCatalog.TryBuild(out var catalog, out var errors) || catalog is null)
        {
            Check(false, "catalog.build", string.Join("; ", errors));
            Finish();
            return;
        }
        _catalog = catalog;
        Check(true, "catalog.build", catalog.CatalogHash);
        EnqueueSweeps(catalog);
        _frame = 0;
    }

    private void EnqueueSweeps(ContentCatalog catalog)
    {
        ulong[] seeds = [7UL, 4242UL, 918273UL];
        foreach (var biome in catalog.Biomes.Keys.OrderBy(x => x))
        {
            var contract = catalog.Contracts.Values
                .Where(c => c.AllowedBiomeIds.Contains(biome))
                .OrderBy(c => c.Id).First().Id;
            foreach (var seed in seeds)
            {
                var req = new RunGenerationRequest(seed, biome, contract, catalog);
                if (!TrenchGenerator.TryGenerate(req, out var world, out var verdict, out var reason) || world is null)
                {
                    Check(false, $"sweep.gen.{biome}.{seed}", reason);
                    continue;
                }
                if (!verdict.IsValid)
                {
                    Check(false, $"sweep.valid.{biome}.{seed}", string.Join("; ", verdict.Errors));
                    continue;
                }
                // Worlds are built lazily when their sweep activates: only one
                // world's bodies may occupy the physics space at a time.
                _sweepQueue.Enqueue((world, $"{biome}.{seed}"));
            }
        }
        Check(_sweepQueue.Count > 0, "sweep.enqueued", $"worlds={_sweepQueue.Count}");
        _stage = Stage.Sweep;
    }

    public override void _PhysicsProcess(double delta)
    {
        _frame++;
        if (_frame % 600 == 0)
        {
            GD.Print($"SMOKE INFO heartbeat | frame={_frame} stage={_stage}");
        }
        switch (_stage)
        {
            case Stage.Sweep: PumpSweep(); break;
            case Stage.Quiet: PumpQuiet(); break;
            case Stage.Play: PumpPlay(Math.Min((float)delta, 0.5f)); break;
            case Stage.SaveVerify: PumpSaveVerify(); break;
            case Stage.Tutorial: PumpTutorial(Math.Min((float)delta, 0.5f)); break;
            case Stage.TutorialSave: PumpTutorialSave(); break;
            case Stage.Done:
            case Stage.Setup:
                break;
        }
    }

    // ------------------------------------------------------------- sweep

    private void PumpSweep()
    {
        if (_drainFrames > 0)
        {
            // Let freed bodies leave the physics space before the next world.
            _drainFrames--;
            return;
        }
        if (_activeSweep is null)
        {
            if (_sweepQueue.Count == 0)
            {
                _stage = Stage.Quiet;
                _quietPhase = -1;
                return;
            }
            var (pendingWorld, pendingTag) = _sweepQueue.Dequeue();
            var newRoot = new Node3D { Name = "Sweep_" + pendingTag.Replace('.', '_').Replace('/', '_') };
            AddChild(newRoot);
            WorldBuilder.Build(newRoot, pendingWorld, _catalog!);
            _activeSweep = (pendingWorld, PhysicsPathVerifier.BuildJobs(pendingWorld), pendingTag, newRoot);
            _sweepIndex = 0;
            _sweepBlocked = 0;
            return; // let new bodies register one frame before querying.
        }
        var (world, jobs, tag, root) = _activeSweep.Value;
        _ = world;
        var space = GetWorld3D().DirectSpaceState;
        var budget = 12;
        while (budget-- > 0 && _sweepIndex < jobs.Count)
        {
            var job = jobs[_sweepIndex++];
            if (!PhysicsPathVerifier.SampleFits(space, job, System.Array.Empty<Rid>(), out var at, out var blocker))
            {
                if (_sweepBlocked == 0)
                {
                    Check(false, $"path.clear.{tag}", $"first block at {at} ({blocker}) sample {_sweepIndex}/{jobs.Count}");
                }
                _sweepBlocked++;
            }
        }
        if (_sweepIndex >= jobs.Count)
        {
            if (_sweepBlocked == 0)
            {
                Check(true, $"path.clear.{tag}", $"samples={jobs.Count} blocked=0 capsule r={PhysicsPathVerifier.ShipRadius} h={PhysicsPathVerifier.ShipHeight}");
            }
            else
            {
                Check(false, $"path.blocked.{tag}", $"blocked={_sweepBlocked}/{jobs.Count}");
            }
            root.QueueFree();
            _activeSweep = null;
            _drainFrames = 3;
        }
    }

    // ------------------------------------------------------------- quiet

    private void PumpQuiet()
    {
        if (_quietPhase < 0)
        {
            _quietRoot = new Node3D { Name = "QuietRoot" };
            AddChild(_quietRoot);
            _quietSub = new SubmarineController { Name = "QuietSub" };
            _quietRoot.AddChild(_quietSub);
            _quietSub.GlobalPosition = Vector3.Zero;
            StartQuietPhase(0);
            return;
        }
        if (_quietSub is null || !IsInstanceValid(_quietSub)) return;
        _quietFrames++;
        _quietSpeedSum += _quietSub.LinearVelocity.Length();
        _quietSpeedN++;
        var f = _quietSub.LastAppliedForce.Length();
        if (f > _quietMaxForce) _quietMaxForce = f;
        if (_quietFrames >= 180)
        {
            var avg = _quietSpeedSum / Math.Max(_quietSpeedN, 1);
            if (_quietPhase == 0)
            {
                _speedNormal = avg;
                _forceNormal = _quietMaxForce;
                // Reset for the quiet leg.
                _quietSub.LinearVelocity = Vector3.Zero;
                _quietSub.AngularVelocity = Vector3.Zero;
                _quietSub.GlobalPosition = Vector3.Zero;
                StartQuietPhase(1);
            }
            else if (_quietPhase == 1)
            {
                _speedQuiet = avg;
                _forceQuiet = _quietMaxForce;
                var boostOff = !_quietSub.BoostHeld;
                Check(_speedQuiet < 0.5 * _speedNormal, "quiet.caps_thrust",
                    $"normal={_speedNormal:F1}m/s quiet={_speedQuiet:F1}m/s (need <50%)");
                Check(_forceQuiet <= 8200f, "quiet.caps_force",
                    $"normal_max={_forceNormal:F0}N quiet_max={_forceQuiet:F0}N (need <=8200)");
                Check(boostOff, "quiet.disables_boost", "BoostHeld=false while quiet with TestBoost set");
                _quietSub.TestDriveWorld = null;
                _quietSub.TestBoost = false;
                _quietSub.QuietMode = false;
                _quietRoot?.QueueFree();
                _quietSub = null;
                BeginPlay();
            }
        }
    }

    private void StartQuietPhase(int phase)
    {
        _quietPhase = phase;
        _quietFrames = 0;
        _quietSpeedSum = 0;
        _quietSpeedN = 0;
        _quietMaxForce = 0;
        if (_quietSub is null) return;
        _quietSub.TestDriveWorld = new Vector3(0f, 0f, -25f);
        _quietSub.TestBoost = true; // boost attempted in both legs; quiet must null it.
        _quietSub.QuietMode = phase == 1;
    }

    // ------------------------------------------------------------- play

    private void BeginPlay()
    {
        var catalog = _catalog!;
        const string biome = "biome.shelf_graveyard";
        const string contract = "contract.salvage_quota";
        const ulong seed = 4242UL;
        var req = new RunGenerationRequest(seed, biome, contract, catalog);
        if (!TrenchGenerator.TryGenerate(req, out var world, out var verdict, out var reason) || world is null || !verdict.IsValid)
        {
            Check(false, "play.gen", reason ?? string.Join("; ", verdict?.Errors ?? System.Array.Empty<string>()));
            _stage = Stage.Done;
            Finish();
            return;
        }
        if (!RunSimulation.TryCreate(world, catalog, "difficulty.standard", "frame.mule", null,
                RunSimulation.InsuranceBasic, out var sim, out var simReason) || sim is null)
        {
            Check(false, "play.sim", simReason);
            _stage = Stage.Done;
            Finish();
            return;
        }
        _playWorld = world;
        _playSim = sim;
        _playRoot = new Node3D { Name = "PlayRoot" };
        AddChild(_playRoot);
        WorldBuilder.Build(_playRoot, world, catalog);
        _playSub = new SubmarineController { Name = "PlaySub" };
        _playRoot.AddChild(_playSub);
        _playSub.GlobalPosition = WorldBuilder.ToG(world.GetNode(world.ExtractionNodeId).Position) + new Vector3(0f, 2f, 12f);
        _playRoute = world.RouteFromExtractionToObjective
            .Select(id => WorldBuilder.ToG(world.GetNode(id).Position)).ToList();
        _playReturn = new List<Vector3>(_playRoute);
        _playReturn.Reverse();
        _playRetIdx = 1; // return route starts at the objective.
        _playWp = 1;
        _playFrames = 0;
        _stuckPos = _playSub.GlobalPosition;
        _stuckFrames = 0;
        _pingTimer = 0f;
        Engine.TimeScale = 8.0; // debug only: same physics, fewer wall-clock seconds.
        Check(true, "play.autopilot_start", "autopilot only; no manual completion claimed.");
        _stage = Stage.Play;
    }

    private void PumpPlay(float dt)
    {
        if (_playSim is null || _playSub is null || _playWorld is null || !IsInstanceValid(_playSub))
        {
            Check(false, "play.nodes", "play nodes lost.");
            _stage = Stage.Done;
            Finish();
            return;
        }
        _playFrames++;
        var sim = _playSim;
        var sub = _playSub;
        var world = _playWorld;

        if (sim.Phase == RunPhase.Failed)
        {
            Check(false, "play.survived", sim.FailureReason);
            TeardownPlay();
            _stage = Stage.Done;
            Finish();
            return;
        }

        Vector3 target;
        float speed;
        if (!sim.Contract.PrimaryComplete && !_playReturning)
        {
            var lootTarget = NearestUnsecuredLoot(sim, world, sub.GlobalPosition, 80f);
            if (lootTarget.HasValue && (lootTarget.Value - sub.GlobalPosition).Length() > DomainConstants.InteractRangeMeters)
            {
                target = lootTarget.Value;
                speed = 8f;
            }
            else if (_playWp < _playRoute.Count)
            {
                target = _playRoute[_playWp];
                speed = 16f;
                if ((target - sub.GlobalPosition).Length() < 12f) _playWp++;
            }
            else
            {
                _playReturning = true;
                target = _playReturn[_playRetIdx];
                speed = 16f;
            }
        }
        else
        {
            // Return along the reversed physical route, never a straight cut.
            _playReturning = true;
            target = _playRetIdx < _playReturn.Count ? _playReturn[_playRetIdx] : _playReturn[^1];
            speed = 16f;
            if ((target - sub.GlobalPosition).Length() < 12f && _playRetIdx < _playReturn.Count - 1)
            {
                _playRetIdx++;
            }
        }

        var toTarget = target - sub.GlobalPosition;
        var dist = toTarget.Length();
        if (dist > 30f) speed = 16f;
        else if (dist > 12f) speed = Math.Min(speed, 8f);
        else speed = 3f;
        sub.TestDriveWorld = dist > 0.5f ? toTarget / dist * speed : Vector3.Zero;

        // Real verbs: salvage anything in reach, ping on cooldown, extract in zone.
        foreach (var l in world.LootSpawns)
        {
            if ((WorldBuilder.ToG(l.Position) - sub.GlobalPosition).Length() > DomainConstants.InteractRangeMeters)
            {
                continue;
            }
            var r = sim.TrySalvage(l.SpawnId); // duplicates rejected by the sim; honest path.
            _ = r;
        }
        _pingTimer += dt;
        if (_pingTimer > 15f && sim.PulseCooldownRemaining <= 0f)
        {
            _pingTimer = 0f;
            _ = sim.Pulse();
        }
        var extractDist = (WorldBuilder.ToG(world.GetNode(world.ExtractionNodeId).Position) - sub.GlobalPosition).Length();
        // Attempt radius is slightly generous; TryExtract honestly enforces 30 m.
        // A rejection is not a test failure — keep homing until the budget ends.
        if (_playReturning && sim.Contract.PrimaryComplete && extractDist <= 33f)
        {
            var result = sim.TryExtract();
            if (result.Success && result.Settlement is not null)
            {
                _playDraft = result.Settlement;
                Check(true, "play.extracted",
                    $"credits={result.Settlement.Credits} salvage={result.Settlement.SecuredSalvageValue} frames={_playFrames}");
                TeardownPlay();
                _stage = Stage.SaveVerify;
                return;
            }
            if (_playFrames > 5900)
            {
                Check(false, "play.extract", result.Reason);
                TeardownPlay();
                _stage = Stage.Done;
                Finish();
                return;
            }
        }

        sub.PollContinuous();
        sim.Tick(dt, WorldBuilder.ToS(sub.GlobalPosition),
            new ShipControlInput(Math.Min(sub.Throttle01, 1f), false, false));

        // Stuck guard: verified-clear centerlines should track; skip ahead if not.
        _stuckFrames++;
        if (_stuckFrames >= 120)
        {
            var moved = (sub.GlobalPosition - _stuckPos).Length();
            _stuckPos = sub.GlobalPosition;
            _stuckFrames = 0;
            if (moved < 3f && !_playReturning && _playWp < _playRoute.Count - 1)
            {
                _playWp++;
                _log.Add($"SMOKE INFO play.skip_wp | index={_playWp}");
            }
        }
        if (_playFrames > 6000)
        {
            Check(false, "play.timeout", $"frames={_playFrames} complete={sim.Contract.PrimaryComplete} returning={_playReturning}");
            TeardownPlay();
            _stage = Stage.Done;
            Finish();
        }
    }

    private static Vector3? NearestUnsecuredLoot(RunSimulation sim, GeneratedWorld world, Vector3 ship, float maxMeters)
    {
        var secured = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in sim.CargoItems) secured.Add(item.LootSpawnId);
        Vector3? best = null;
        var bestDist = maxMeters;
        foreach (var l in world.LootSpawns)
        {
            if (secured.Contains(l.SpawnId)) continue;
            var d = (WorldBuilder.ToG(l.Position) - ship).Length();
            if (d < bestDist) { bestDist = d; best = WorldBuilder.ToG(l.Position); }
        }
        return best;
    }

    private void TeardownPlay()
    {
        Engine.TimeScale = 1.0;
        if (_playSub is not null)
        {
            _playSub.TestDriveWorld = null;
            _playSub.TestBoost = false;
        }
    }

    // ------------------------------------------------------------- save

    private void PumpSaveVerify()
    {
        _stage = Stage.Done; // single-shot; settlement IO is awaited via async continuation below.
        VerifySaveAsync();
    }

    private async void VerifySaveAsync()
    {
        try
        {
            var draft = _playDraft;
            if (draft is null)
            {
                Check(false, "save.draft", "no settlement draft.");
                BeginTutorial();
                return;
            }
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "abyss-smoke-" + System.Guid.NewGuid().ToString("N"));
            var store = new FileSaveStore(dir);
            var payload = new SettlementPayload(draft.SettlementId, draft.RetainedCredits, draft.ResearchData, draft.Shards,
                System.Array.Empty<string>(), System.Array.Empty<string>(), System.Array.Empty<string>(),
                draft.Outcome == SettlementOutcome.Success ? 1 : 0,
                draft.Outcome == SettlementOutcome.Failed ? 1 : 0);
            var first = await store.ApplySettlementAsync(payload, CancellationToken.None);
            Check(first.Outcome == SettlementApplyOutcome.Applied, "save.applied_once", $"{draft.SettlementId} {first.Outcome}");
            var second = await store.ApplySettlementAsync(payload, CancellationToken.None);
            Check(second.Outcome == SettlementApplyOutcome.AlreadyApplied, "save.exactly_once", $"retry={second.Outcome}");
            var profile = await store.LoadAsync(CancellationToken.None);
            Check(profile.Currencies.Credits == draft.RetainedCredits, "save.ledger",
                $"credits={profile.Currencies.Credits} need={draft.RetainedCredits}");
            Check(profile.AppliedSettlementIds.Contains(draft.SettlementId, StringComparer.Ordinal), "save.id_recorded", draft.SettlementId);
            try { System.IO.Directory.Delete(dir, recursive: true); } catch { }
        }
        catch (Exception ex)
        {
            Check(false, "save.exception", ex.GetType().Name + ": " + ex.Message);
        }
        if (_playRoot is not null && IsInstanceValid(_playRoot)) _playRoot.QueueFree();
        BeginTutorial();
    }

    // ------------------------------------------------------------- tutorial (T0)

    /// <summary>
    /// Tutorial-mode pass: the exact fixed T0 options (shelf_graveyard /
    /// salvage_quota / seed 4242 / skiff / standard / none), driven through the
    /// real verbs by step order — steer, listen, ping, approach, salvage, winch,
    /// extract — with the production TutorialDirector + SonarBuddy in the loop.
    /// </summary>
    private void BeginTutorial()
    {
        var catalog = _catalog!;
        var options = AbyssScav.App.RunLaunchOptions.Tutorial();
        if (!options.TryValidate(catalog, out var problems))
        {
            Check(false, "tutorial.options", string.Join("; ", problems));
            Finish();
            return;
        }
        Check(options.IsTutorial, "tutorial.options", "fixed T0: shelf_graveyard/salvage_quota/4242/skiff/standard/none");
        var req = new RunGenerationRequest(options.RunSeed, options.BiomeId, options.ContractId, catalog);
        if (!TrenchGenerator.TryGenerate(req, out var world, out var verdict, out var reason) || world is null || !verdict.IsValid)
        {
            Check(false, "tutorial.gen", reason ?? string.Join("; ", verdict?.Errors ?? System.Array.Empty<string>()));
            Finish();
            return;
        }
        if (!RunSimulation.TryCreate(world, catalog, options.DifficultyId, options.FrameId,
                options.ModifierIds, options.InsuranceId, out var sim, out var simReason) || sim is null)
        {
            Check(false, "tutorial.sim", simReason);
            Finish();
            return;
        }
        _tutWorld = world;
        _tutSim = sim;
        _tutRoot = new Node3D { Name = "TutorialRoot" };
        AddChild(_tutRoot);
        WorldBuilder.Build(_tutRoot, world, catalog);
        _tutSub = new SubmarineController { Name = "TutorialSub" };
        _tutRoot.AddChild(_tutSub);
        _tutSub.GlobalPosition = WorldBuilder.ToG(world.GetNode(world.ExtractionNodeId).Position) + new Vector3(0f, 2f, 12f);
        _tutDirector = new TutorialDirector { Name = "TutorialDirector" };
        _tutRoot.AddChild(_tutDirector);
        _tutDirector.Setup(Array.Empty<string>());
        _tutDirector.StepCompleted += id =>
        {
            _tutStepsSeen.Add(id);
            _ = PersistTutorialStepAsync(id);
        };
        _tutBuddy = new SonarBuddy { Name = "SonarBuddy" };
        _tutRoot.AddChild(_tutBuddy);
        _tutBuddy.Enabled = true;
        _tutDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "abyss-tut-" + System.Guid.NewGuid().ToString("N"));
        _tutStore = new FileSaveStore(_tutDir);
        _tutRoute = world.RouteFromExtractionToObjective
            .Select(id => WorldBuilder.ToG(world.GetNode(id).Position)).ToList();
        _tutReturn = new List<Vector3>(_tutRoute);
        _tutReturn.Reverse();
        _tutRetIdx = 1;
        _tutWp = 1;
        _tutFrames = 0;
        _tutStuckPos = _tutSub.GlobalPosition;
        _tutStuckFrames = 0;
        _tutPingTimer = 0f;
        _tutReturning = false;
        Engine.TimeScale = 8.0; // debug only: same physics, fewer wall-clock seconds.
        Check(true, "tutorial.autopilot_start", "step-ordered verbs; director + buddy in the loop.");
        _stage = Stage.Tutorial;
    }

    private void PumpTutorial(float dt)
    {
        if (_tutSim is null || _tutSub is null || _tutWorld is null || _tutDirector is null || _tutBuddy is null ||
            !IsInstanceValid(_tutSub))
        {
            Check(false, "tutorial.nodes", "tutorial nodes lost.");
            Finish();
            return;
        }
        _tutFrames++;
        var sim = _tutSim;
        var sub = _tutSub;
        var world = _tutWorld;
        var director = _tutDirector;
        var buddy = _tutBuddy;

        if (sim.Phase == RunPhase.Failed)
        {
            Check(false, "tutorial.survived", sim.FailureReason);
            TeardownTutorial();
            Finish();
            return;
        }

        // Navigation mirrors the play autopilot: loot-homing outbound along the
        // physical route, reversed route home once the quota banks.
        Vector3 target;
        float speed;
        var canReturn = sim.Contract.PrimaryComplete && director.CompletedSteps.Contains(TutorialDirector.StepDock) && director.CompletedSteps.Contains(TutorialDirector.StepRepair);
        if (!canReturn && !_tutReturning)
        {
            var lootTarget = !sim.Contract.PrimaryComplete ? NearestUnsecuredLoot(sim, world, sub.GlobalPosition, 80f) : null;
            if (lootTarget.HasValue && (lootTarget.Value - sub.GlobalPosition).Length() > DomainConstants.InteractRangeMeters)
            {
                target = lootTarget.Value;
                speed = 8f;
            }
            else if (_tutWp < _tutRoute.Count)
            {
                target = _tutRoute[_tutWp];
                speed = 16f;
                if ((target - sub.GlobalPosition).Length() < 12f) _tutWp++;
            }
            else
            {
                _tutReturning = true;
                target = _tutReturn[_tutRetIdx];
                speed = 16f;
            }
        }
        else
        {
            _tutReturning = true;
            target = _tutRetIdx < _tutReturn.Count ? _tutReturn[_tutRetIdx] : _tutReturn[^1];
            speed = 16f;
            if ((target - sub.GlobalPosition).Length() < 12f && _tutRetIdx < _tutReturn.Count - 1)
            {
                _tutRetIdx++;
            }
        }

        var dockCandidate = world.Nodes.FirstOrDefault(n => sim.IsDockableNode(n) && (WorldBuilder.ToG(n.Position) - sub.GlobalPosition).Length() <= 40f);
        if (director.CurrentStepId == TutorialDirector.StepDock && !sim.IsDocked && dockCandidate is not null)
        {
            target = WorldBuilder.ToG(dockCandidate.Position);
            speed = 2f;
        }

        var toTarget = target - sub.GlobalPosition;
        var dist = toTarget.Length();
        if (dist > 30f) speed = 16f;
        else if (dist > 12f) speed = Math.Min(speed, 8f);
        else speed = 3f;
        sub.TestDriveWorld = dist > 0.5f ? toTarget / dist * speed : Vector3.Zero;

        // Step-ordered real verbs: ping only for the ping step, dock for dock step,
        // repair for repair step, extract only for the extract step with quota banked.
        foreach (var l in world.LootSpawns)
        {
            if ((WorldBuilder.ToG(l.Position) - sub.GlobalPosition).Length() > DomainConstants.InteractRangeMeters)
            {
                continue;
            }
            _ = sim.TrySalvage(l.SpawnId); // duplicates rejected by the sim; honest path.
        }
        _tutPingTimer += dt;
        if (director.CurrentStepId == TutorialDirector.StepPing && _tutPingTimer > 1f && sim.PulseCooldownRemaining <= 0f)
        {
            _tutPingTimer = 0f;
            _ = sim.Pulse();
        }
        if (director.CurrentStepId == TutorialDirector.StepDock && !sim.IsDocked)
        {
            var dockNode = world.Nodes.FirstOrDefault(n => sim.IsDockableNode(n) && (WorldBuilder.ToG(n.Position) - sub.GlobalPosition).Length() <= DomainConstants.DockRangeMeters);
            if (dockNode is not null)
            {
                sub.LinearVelocity = Vector3.Zero;
                var dock = sim.TryDock(dockNode.Id, 0f);
                if (dock.Success)
                {
                    _ = sim.TryInjectTrainingBreach();
                }
            }
        }
        if (director.CurrentStepId == TutorialDirector.StepRepair)
        {
            _ = sim.TryRepairHull(0);
            if (sim.IsDocked)
            {
                _ = sim.TryUndock();
            }
        }
        if (sim.IsDocked && director.CurrentStepId != TutorialDirector.StepDock)
        {
            _ = sim.TryUndock();
        }
        var extractDist = (WorldBuilder.ToG(world.GetNode(world.ExtractionNodeId).Position) - sub.GlobalPosition).Length();
        if (director.CurrentStepId == TutorialDirector.StepExtract && sim.Contract.PrimaryComplete && extractDist <= 33f)
        {
            var result = sim.TryExtract();
            if (result.Success && result.Settlement is not null)
            {
                _tutDraft = result.Settlement;
                // Production observes the flipped phase on the next physics tick;
                // advance the director once the same way before asserting.
                director.Tick(0.1f, 0f, sim, null);
                Check(director.AllComplete, "tutorial.all_complete",
                    $"steps={string.Join(",", _tutStepsSeen)} frames={_tutFrames}");
                TeardownTutorial();
                _stage = Stage.TutorialSave;
                return;
            }
            if (_tutFrames > 6900)
            {
                Check(false, "tutorial.extract", result.Reason);
                TeardownTutorial();
                Finish();
                return;
            }
        }

        sub.PollContinuous();
        sim.Tick(dt, WorldBuilder.ToS(sub.GlobalPosition),
            new ShipControlInput(Math.Min(sub.Throttle01, 1f), false, false));
        buddy.Tick(dt, sim, sub.GlobalPosition, -sub.GlobalTransform.Basis.Z,
            GetWorld3D().DirectSpaceState, sub.GetRid());
        director.Tick(dt, sub.Throttle01, sim, NearestTutorialLoot(sim, world, sub.GlobalPosition));

        _tutStuckFrames++;
        if (_tutStuckFrames >= 120)
        {
            var moved = (sub.GlobalPosition - _tutStuckPos).Length();
            _tutStuckPos = sub.GlobalPosition;
            _tutStuckFrames = 0;
            if (moved < 3f && !_tutReturning && _tutWp < _tutRoute.Count - 1)
            {
                _tutWp++;
                _log.Add($"SMOKE INFO tutorial.skip_wp | index={_tutWp}");
            }
        }
        if (_tutFrames > 7000)
        {
            Check(false, "tutorial.timeout",
                $"frames={_tutFrames} step={director.CurrentStepId} complete={sim.Contract.PrimaryComplete} returning={_tutReturning}");
            TeardownTutorial();
            Finish();
        }
    }

    private static float? NearestTutorialLoot(RunSimulation sim, GeneratedWorld world, Vector3 ship)
    {
        var secured = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in sim.CargoItems) secured.Add(item.LootSpawnId);
        float? best = null;
        foreach (var l in world.LootSpawns)
        {
            if (secured.Contains(l.SpawnId)) continue;
            var d = (WorldBuilder.ToG(l.Position) - ship).Length();
            if (!best.HasValue || d < best.Value) best = d;
        }
        return best;
    }

    private void TeardownTutorial()
    {
        Engine.TimeScale = 1.0;
        if (_tutSub is not null)
        {
            _tutSub.TestDriveWorld = null;
            _tutSub.TestBoost = false;
        }
    }

    /// <summary>Mirrors the production per-step persist path (union into CompletedSteps).</summary>
    private async Task PersistTutorialStepAsync(string stepId)
    {
        var store = _tutStore;
        if (store is null) return;
        try
        {
            await store.UpdateAsync(p =>
            {
                var steps = new List<string>(p.Tutorial.CompletedSteps ?? new List<string>());
                if (!steps.Contains(stepId, StringComparer.Ordinal)) steps.Add(stepId);
                return p with { Tutorial = new Persistence.SaveTutorial(p.Tutorial.Completed, steps) };
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            GD.Print($"SMOKE INFO tutorial.persist_step | {stepId} {ex.GetType().Name}");
        }
    }

    private void PumpTutorialSave()
    {
        _stage = Stage.Done; // single-shot; IO continues async below.
        VerifyTutorialSaveAsync();
    }

    private async void VerifyTutorialSaveAsync()
    {
        try
        {
            var store = _tutStore;
            var director = _tutDirector;
            var buddy = _tutBuddy;
            if (store is null || director is null || buddy is null)
            {
                Check(false, "tutorial.save_nodes", "tutorial nodes lost before save verify.");
                Finish();
                return;
            }
            Check(_tutDraft is not null, "tutorial.extracted",
                _tutDraft is null ? "no settlement draft" : $"credits={_tutDraft.Credits} frames={_tutFrames}");
            // Controller extraction path (not the manual write below): the real
            // extraction flipped the sim phase and the director observed it on
            // the post-extract tick, so AllComplete is genuinely achieved.
            Check(director.AllComplete, "tutorial.controller_extraction_path",
                $"allComplete={director.AllComplete} steps={string.Join(",", _tutStepsSeen)} draft={(_tutDraft is null ? "none" : _tutDraft.SettlementId)}");
            // Final completion write: all seen steps unioned, Completed=true.
            var seen = _tutStepsSeen.Distinct(StringComparer.Ordinal).ToList();
            await store.UpdateAsync(p =>
            {
                var steps = new List<string>(p.Tutorial.CompletedSteps ?? new List<string>());
                foreach (var id in seen)
                {
                    if (!steps.Contains(id, StringComparer.Ordinal)) steps.Add(id);
                }
                return p with { Tutorial = new Persistence.SaveTutorial(true, steps) };
            }, CancellationToken.None).ConfigureAwait(false);
            var profile = await store.LoadAsync(CancellationToken.None).ConfigureAwait(false);
            Check(profile.Tutorial.Completed, "tutorial.completed", $"steps={profile.Tutorial.CompletedSteps.Count}");
            var missing = TutorialDirector.StepOrder
                .Where(id => !profile.Tutorial.CompletedSteps.Contains(id, StringComparer.Ordinal)).ToList();
            Check(missing.Count == 0, "tutorial.steps_persisted",
                missing.Count == 0 ? string.Join(",", profile.Tutorial.CompletedSteps) : "missing=" + string.Join(",", missing));
            Check(buddy.CalloutLog.Count >= 1, "buddy.callout",
                buddy.CalloutLog.Count >= 1 ? $"n={buddy.CalloutLog.Count} first={buddy.CalloutLog[0]}" : "no callouts emitted");
            Check(buddy.Trail.Count >= 1, "buddy.trail", $"fixes={buddy.Trail.Count}");
            try { System.IO.Directory.Delete(_tutDir, recursive: true); } catch { }
        }
        catch (Exception ex)
        {
            Check(false, "tutorial.save_exception", ex.GetType().Name + ": " + ex.Message);
        }
        if (_tutRoot is not null && IsInstanceValid(_tutRoot)) _tutRoot.QueueFree();
        Finish();
    }

    private void Finish()
    {
        Engine.TimeScale = 1.0;
        _log.Add(_ok ? "SMOKE RESULT PASS" : "SMOKE RESULT FAIL");
        foreach (var line in _log) GD.Print(line);
        GD.Print("SMOKE DONE ok=" + _ok);
        GetTree()?.CallDeferred(SceneTree.MethodName.Quit, _ok ? 0 : 1);
    }
}
