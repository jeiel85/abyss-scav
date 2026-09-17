using AbyssScav.App;
using AbyssScav.Domain;
using AbyssScav.Infra.Logging;
using Godot;

namespace AbyssScav.Gameplay;

/// <summary>
/// Tutorial T0 "The First Ping" step engine (docs/06 §7): a sequential,
/// honestly-detected checklist over the real run: steer, passive listen, ping,
/// dock, salvage, repair, extract. Detections read only live state (throttle,
/// sim counters, dock flag, repair counter, sim phase). Steps reached
/// out-of-order latch and complete the moment they become current, so resumed
/// profiles never strand. Completion persists via the profile's existing
/// Tutorial.CompletedSteps / Completed fields; the owning RunController owns
/// the actual FileSaveStore writes. Old saves carrying retired step IDs
/// (t0.approach, t0.winch) are ignored without a schema change, which reopens
/// previously completed tutorials until the new dock/repair stages are real.
/// </summary>
public partial class TutorialDirector : Control
{
    public const string StepSteer = "t0.steer";
    public const string StepListen = "t0.listen";
    public const string StepPing = "t0.ping";
    public const string StepDock = "t0.dock";
    public const string StepSalvage = "t0.salvage";
    public const string StepRepair = "t0.repair";
    public const string StepExtract = "t0.extract";
    /// <summary>Retired step: kept so old saves/tests referencing it compile; never in the order.</summary>
    public const string StepApproach = "t0.approach";
    /// <summary>Retired step: kept so old saves/tests referencing it compile; never in the order.</summary>
    public const string StepWinch = "t0.winch";

    public static readonly IReadOnlyList<string> StepOrder = new[]
    {
        StepSteer, StepListen, StepPing, StepDock, StepSalvage, StepRepair, StepExtract,
    };

    /// <summary>Steps that cannot be skipped: skipping them would falsify completion.</summary>
    public static readonly IReadOnlySet<string> MandatorySteps = new HashSet<string>(StringComparer.Ordinal)
    {
        StepDock, StepRepair, StepExtract,
    };

    private static readonly Dictionary<string, string> Titles = new(StringComparer.Ordinal)
    {
        [StepSteer] = "Steer the boat",
        [StepListen] = "Listen on passive sonar",
        [StepPing] = "Ping active sonar (F)",
        [StepDock] = "Dock at the station (J)",
        [StepSalvage] = "Salvage (E)",
        [StepRepair] = "Repair the breach (R)",
        [StepExtract] = "Extract (T)",
    };

    /// <summary>Raised for completed AND skipped steps so both persist and resume.</summary>
    public event Action<string>? StepCompleted;

    private readonly HashSet<string> _done = new(StringComparer.Ordinal);
    private readonly HashSet<string> _skipped = new(StringComparer.Ordinal);
    private int _current;
    private float _stepTime;
    private int _pulsesBaseline;
    private int _cargoBaseline;
    private int _repairBaseline;
    private bool _baselinesSet;
    private bool _everDocked;
    private float? _nearestLootMeters;
    private bool _quotaComplete;
    private bool _extracted;
    private string _skipNote = "";

    private VBoxContainer? _stepsBox;
    private Label? _detail;
    private Button? _skip;
    private readonly Dictionary<string, Label> _stepLabels = new(StringComparer.Ordinal);

    public string? CurrentStepId => _current < StepOrder.Count ? StepOrder[_current] : null;
    public bool AllComplete => _current >= StepOrder.Count;
    public IReadOnlySet<string> CompletedSteps => _done;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        BuildUi();
        RefreshUi();
    }

    /// <summary>Seed resume state from the profile; positions at first incomplete step.</summary>
    public void Setup(IReadOnlyList<string> alreadyDone)
    {
        _done.Clear();
        _skipped.Clear();
        foreach (var id in alreadyDone ?? Array.Empty<string>())
        {
            if (StepOrder.Contains(id, StringComparer.Ordinal))
            {
                _done.Add(id);
            }
        }
        _current = 0;
        while (_current < StepOrder.Count && _done.Contains(StepOrder[_current]))
        {
            _current++;
        }
        _stepTime = 0f;
        _baselinesSet = false;
        RefreshUi();
    }

    /// <summary>
    /// Advances detection for the current step. All inputs are live run state:
    /// throttle01 from the hull, sim counters, dock flag, repair counter, and
    /// measured loot range. Out-of-order verbs latch (_everDocked, baselines)
    /// so they complete the moment they become current; resumed profiles never
    /// strand.
    /// </summary>
    public void Tick(float dt, float throttle01, RunSimulation sim, float? nearestLootMeters)
    {
        if (AllComplete || sim is null) return;
        _nearestLootMeters = nearestLootMeters;
        _quotaComplete = sim.Contract.PrimaryComplete;
        if (sim.Phase == RunPhase.Extracted) _extracted = true;
        if (sim.IsDocked) _everDocked = true;
        if (!_baselinesSet)
        {
            _pulsesBaseline = sim.PulsesUsed;
            _cargoBaseline = sim.CargoItems.Count;
            _repairBaseline = sim.RepairsDone;
            _baselinesSet = true;
        }
        _stepTime += dt;
        var id = StepOrder[_current];
        var done = id switch
        {
            _ when id == StepSteer => CheckSteer(dt, throttle01),
            _ when id == StepListen => _stepTime >= 10f,
            _ when id == StepPing => sim.PulsesUsed > _pulsesBaseline || sim.PulsesUsed > 0,
            _ when id == StepDock => _everDocked || sim.IsDocked,
            _ when id == StepSalvage => sim.CargoItems.Count > _cargoBaseline || sim.CargoItems.Count > 0,
            _ when id == StepRepair => sim.RepairsDone > _repairBaseline || sim.RepairsDone > 0,
            _ when id == StepExtract => _extracted,
            _ => false,
        };
        if (done) CompleteCurrent(skipped: false);
        else RefreshDetail();
    }

    /// <summary>
    /// Tutorial-only affordance for OPTIONAL steps (steer/listen/ping/salvage).
    /// Mandatory steps (dock/repair/extract) are refused with a reason: skipping
    /// them would falsify completion. Extraction additionally requires the real
    /// phase flip, so a skipped extract can never count as completion.
    /// </summary>
    public bool TrySkip(out string reason)
    {
        reason = string.Empty;
        if (AllComplete) { reason = "Tutorial already complete."; return false; }
        var id = StepOrder[_current];
        if (MandatorySteps.Contains(id))
        {
            reason = id == StepExtract
                ? "Extraction requires real delivery to the green ring (T) — cannot skip."
                : id == StepDock
                    ? "Docking (J) at the station is required — cannot skip."
                    : "Breach repair (R) with sealant is required — cannot skip.";
            _skipNote = reason;
            RefreshDetail();
            return false;
        }
        CompleteCurrent(skipped: true);
        return true;
    }

    /// <summary>Tutorial-only affordance: marks the current optional step done without its verb.</summary>
    public void SkipCurrent()
    {
        _ = TrySkip(out _);
    }

    private bool CheckSteer(float dt, float throttle01)
    {
        // Sustained thrust: any dip below 0.2 restarts the 3 s clock.
        if (throttle01 > 0.2f) return _stepTime >= 3f;
        _stepTime = 0f;
        return false;
    }

    private void CompleteCurrent(bool skipped)
    {
        var id = StepOrder[_current];
        _done.Add(id);
        if (skipped)
        {
            _skipped.Add(id);
            if (GameServices.IsInitialized)
            {
                GodotLogBridge.Info(GameServices.Logger, $"Tutorial step skipped: {id} (logged, persists as done).");
            }
        }
        _current++;
        while (_current < StepOrder.Count && _done.Contains(StepOrder[_current]))
        {
            _current++;
        }
        _stepTime = 0f;
        _baselinesSet = false;
        _skipNote = "";
        RefreshUi();
        StepCompleted?.Invoke(id);
    }

    // ------------------------------------------------------------------ UI

    private void BuildUi()
    {
        var panel = new PanelContainer { Name = "TutorialPanel" };
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.03f, 0.09f, 0.12f, 0.88f),
            BorderColor = new Color("#29414b"),
            BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1,
            CornerRadiusTopLeft = 3, CornerRadiusTopRight = 3,
            CornerRadiusBottomLeft = 3, CornerRadiusBottomRight = 3,
            ContentMarginLeft = 10, ContentMarginRight = 10,
            ContentMarginTop = 8, ContentMarginBottom = 8,
        };
        panel.AddThemeStyleboxOverride("panel", style);
        panel.SetAnchorsPreset(LayoutPreset.TopLeft);
        panel.Position = new Vector2(12, 168);
        panel.CustomMinimumSize = new Vector2(300, 0);
        AddChild(panel);

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 3);
        panel.AddChild(box);

        var title = new Label { Text = "TUTORIAL — THE FIRST PING" };
        title.AddThemeFontSizeOverride("font_size", 14);
        title.AddThemeColorOverride("font_color", new Color("#71d9d1"));
        box.AddChild(title);

        _stepsBox = new VBoxContainer();
        _stepsBox.AddThemeConstantOverride("separation", 1);
        box.AddChild(_stepsBox);
        foreach (var id in StepOrder)
        {
            var label = new Label();
            label.AddThemeFontSizeOverride("font_size", 13);
            _stepsBox.AddChild(label);
            _stepLabels[id] = label;
        }

        _detail = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _detail.AddThemeFontSizeOverride("font_size", 12);
        _detail.AddThemeColorOverride("font_color", new Color("#dae4df"));
        box.AddChild(_detail);

        _skip = new Button { Text = "Skip step" };
        _skip.TooltipText = "Tutorial only: skips OPTIONAL steps (steer/listen/ping/salvage). Dock, repair, and extract are mandatory and cannot be skipped.";
        _skip.FocusMode = FocusModeEnum.All;
        _skip.Pressed += SkipCurrent;
        box.AddChild(_skip);
        _skip.CallDeferred(Button.MethodName.GrabFocus);
    }

    private void RefreshUi()
    {
        var parchment = new Color("#dae4df");
        var dim = new Color(0.45f, 0.55f, 0.53f);
        var cyan = new Color("#71d9d1");
        var amber = new Color("#dfa44d");
        foreach (var id in StepOrder)
        {
            if (!_stepLabels.TryGetValue(id, out var label)) continue;
            var skipped = _skipped.Contains(id);
            if (_done.Contains(id))
            {
                label.Text = (skipped ? "○ " : "✓ ") + Titles[id] + (skipped ? " (skipped)" : "");
                label.AddThemeColorOverride("font_color", dim);
            }
            else if (!AllComplete && StepOrder[_current] == id)
            {
                label.Text = "→ " + Titles[id];
                label.AddThemeColorOverride("font_color", amber);
            }
            else
            {
                label.Text = "· " + Titles[id];
                label.AddThemeColorOverride("font_color", parchment);
            }
        }
        if (_skip is not null)
        {
            _skip.Visible = !AllComplete;
            _skip.Disabled = AllComplete || (!AllComplete && MandatorySteps.Contains(StepOrder[_current]));
            _skip.TooltipText = !AllComplete && MandatorySteps.Contains(StepOrder[_current])
                ? "This step is mandatory (dock/repair/extract): complete it for real — skipping would falsify the dive log."
                : "Tutorial only: skips OPTIONAL steps (steer/listen/ping/salvage). Skips are logged.";
        }
        if (AllComplete && _detail is not null)
        {
            _detail.AddThemeColorOverride("font_color", cyan);
            _detail.Text = "Tutorial complete — well done. Extraction logged your first dive.";
        }
        RefreshDetail();
    }

    private void RefreshDetail()
    {
        if (_detail is null || AllComplete) return;
        var id = StepOrder[_current];
        string text = id switch
        {
            _ when id == StepSteer =>
                $"Hold thrust (W/S or left stick) above 20% for 3 s straight ({Math.Min(_stepTime, 3f):F1}/3.0 s).",
            _ when id == StepListen =>
                $"Drift and watch the sonar scope for 10 s — passive contacts fade in on their own ({Math.Min(_stepTime, 10f):F0}/10 s).",
            _ when id == StepPing =>
                "Press F (right shoulder on pad) for one active ping. Leave quiet running with Z first — ping is refused while quiet.",
            _ when id == StepApproach => _nearestLootMeters.HasValue
                ? $"Close to within 30 m of the nearest salvage marker — now {_nearestLootMeters.Value:F0} m. Follow the cyan diamonds."
                : "No salvage markers remain in this water, so this step cannot be completed here — use Skip step below.",
            _ when id == StepDock =>
                "Slow to ≤2 m/s within 25 m of the amber station marker at the objective site, then press J (pad D-pad Left) to dock. Undock with J — docking freezes the hull in place.",
            _ when id == StepSalvage =>
                "Press E (pad A) inside 15 m of salvage to bank it. Duplicates are rejected by the dive log, never double-counted.",
            _ when id == StepRepair =>
                "Training incident opened a real severity-1 breach: press R (pad B) to weld it with 1 sealant. Flooding, pressure, and audio are live — repair for real.",
            _ when id == StepExtract => _quotaComplete
                ? "Quota complete — return inside the green extraction ring and press T (right stick on pad)."
                : "Bank salvage until the top-center quota completes, then return to the green ring and press T.",
            _ => "",
        };
        if (!string.IsNullOrEmpty(_skipNote))
            text += "\n" + _skipNote;
        _detail.Text = text;
        if (string.IsNullOrEmpty(_skipNote) == false && !MandatorySteps.Contains(id))
            _skipNote = "";
    }
}
