using AbyssScav.Domain;
using Godot;

namespace AbyssScav.Presentation;

/// <summary>
/// Solo-run HUD: submarine instruments (depth/hull/pressure/noise/power),
/// objective readout, controls hint, message line, steady warning banner,
/// pause and settlement overlays. Industrial type hierarchy on ink-blue.
/// </summary>
public partial class RunHud : Control
{
    public event Action? ResumeRequested;
    public event Action? AbortRequested;
    public event Action? RetrySaveRequested;
    public event Action<bool>? BuddyToggled;

    /// <summary>Sonar-buddy callouts, default ON; pause-menu toggle flips this.</summary>
    public bool BuddyEnabled { get; private set; } = true;

    private Label? _instruments;
    private Label? _objective;
    private Label? _message;
    private Label? _warning;
    private Label? _cargo;
    private Control? _pauseOverlay;
    private Control? _endOverlay;
    private Label? _endTitle;
    private Label? _endBody;
    private Button? _retryButton;
    private Button? _endMenuButton;
    private Label? _hint;
    private Label? _buddy;
    private CheckButton? _buddyToggle;
    private float _messageTimer;

    public SonarDisplay Sonar { get; private set; } = null!;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        Build();
    }

    public override void _Process(double delta)
    {
        if (_messageTimer > 0f)
        {
            _messageTimer -= (float)delta;
            if (_messageTimer <= 0f && _message is not null)
            {
                _message.Text = "";
            }
        }
    }

    private static StyleBoxFlat PanelStyle() => new()
    {
        BgColor = new Color(0.03f, 0.09f, 0.12f, 0.88f),
        BorderColor = new Color("#29414b"),
        BorderWidthLeft = 1,
        BorderWidthRight = 1,
        BorderWidthTop = 1,
        BorderWidthBottom = 1,
        CornerRadiusTopLeft = 3,
        CornerRadiusTopRight = 3,
        CornerRadiusBottomLeft = 3,
        CornerRadiusBottomRight = 3,
        ContentMarginLeft = 10,
        ContentMarginRight = 10,
        ContentMarginTop = 8,
        ContentMarginBottom = 8,
    };

    private void Build()
    {
        var parchment = new Color("#dae4df");
        // Left instruments.
        var left = new PanelContainer { Name = "Instruments" };
        left.AddThemeStyleboxOverride("panel", PanelStyle());
        left.SetAnchorsPreset(LayoutPreset.TopLeft);
        left.Position = new Vector2(12, 12);
        left.CustomMinimumSize = new Vector2(300, 0);
        AddChild(left);
        _instruments = new Label { Name = "InstrumentText" };
        _instruments.AddThemeFontSizeOverride("font_size", 14);
        _instruments.AddThemeColorOverride("font_color", parchment);
        left.AddChild(_instruments);

        // Objective top-center.
        var obj = new PanelContainer();
        obj.AddThemeStyleboxOverride("panel", PanelStyle());
        obj.SetAnchorsPreset(LayoutPreset.CenterTop);
        obj.Position = new Vector2(-260, 12);
        obj.CustomMinimumSize = new Vector2(520, 0);
        AddChild(obj);
        _objective = new Label { HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _objective.AddThemeFontSizeOverride("font_size", 14);
        _objective.AddThemeColorOverride("font_color", parchment);
        obj.AddChild(_objective);

        // Sonar right.
        Sonar = new SonarDisplay { Name = "Sonar" };
        Sonar.SetAnchorsPreset(LayoutPreset.TopRight);
        Sonar.Position = new Vector2(-252, 12);
        AddChild(Sonar);

        // Warning banner below objective: steady amber, no flashing.
        _warning = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _warning.AddThemeFontSizeOverride("font_size", 15);
        _warning.AddThemeColorOverride("font_color", new Color("#dfa44d"));
        _warning.SetAnchorsPreset(LayoutPreset.CenterTop);
        _warning.Position = new Vector2(-260, 96);
        _warning.CustomMinimumSize = new Vector2(520, 0);
        AddChild(_warning);

        // Message line.
        _message = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _message.AddThemeFontSizeOverride("font_size", 14);
        _message.AddThemeColorOverride("font_color", new Color("#71d9d1"));
        _message.SetAnchorsPreset(LayoutPreset.CenterBottom);
        _message.Position = new Vector2(-400, -140);
        _message.CustomMinimumSize = new Vector2(800, 0);
        AddChild(_message);

        // Cargo + controls bottom-left.
        _cargo = new Label();
        _cargo.AddThemeFontSizeOverride("font_size", 13);
        _cargo.AddThemeColorOverride("font_color", parchment);
        _cargo.SetAnchorsPreset(LayoutPreset.BottomLeft);
        _cargo.Position = new Vector2(12, -120);
        _cargo.CustomMinimumSize = new Vector2(420, 0);
        _cargo.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        AddChild(_cargo);

        _hint = new Label
        {
            Text = "W/S surge · A/D sway · Space/Ctrl heave · Arrows yaw/pitch · Shift boost · Z quiet · F ping · E salvage · V survey · G service · R repair · J dock/undock · H drill hold · X winch · T extract · Esc pause",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _hint.AddThemeFontSizeOverride("font_size", 12);
        _hint.AddThemeColorOverride("font_color", new Color(0.62f, 0.72f, 0.70f));
        _hint.SetAnchorsPreset(LayoutPreset.BottomWide);
        _hint.OffsetLeft = 12;
        _hint.OffsetRight = -12;
        _hint.OffsetTop = -34;
        _hint.OffsetBottom = -6;
        AddChild(_hint);

        // Buddy line bottom-right: last callout, steady (no flashing, no timer).
        _buddy = new Label
        {
            Text = "BUDDY LINKED — callouts on",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _buddy.AddThemeFontSizeOverride("font_size", 13);
        _buddy.AddThemeColorOverride("font_color", new Color("#71d9d1"));
        _buddy.SetAnchorsPreset(LayoutPreset.BottomRight);
        _buddy.Position = new Vector2(-432, -120);
        _buddy.CustomMinimumSize = new Vector2(420, 0);
        AddChild(_buddy);

        BuildPauseOverlay();
        BuildEndOverlay();
    }

    private void BuildPauseOverlay()
    {
        _pauseOverlay = new PanelContainer();
        _pauseOverlay.AddThemeStyleboxOverride("panel", PanelStyle());
        _pauseOverlay.SetAnchorsPreset(LayoutPreset.Center);
        _pauseOverlay.Position = new Vector2(-190, -130);
        _pauseOverlay.CustomMinimumSize = new Vector2(380, 0);
        _pauseOverlay.Visible = false;
        AddChild(_pauseOverlay);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        _pauseOverlay.AddChild(box);
        var title = new Label { Text = "PAUSED — DIVE HELD", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 20);
        title.AddThemeColorOverride("font_color", new Color("#dae4df"));
        box.AddChild(title);
        var resume = new Button { Text = "Resume dive (Esc)" };
        resume.Pressed += () => ResumeRequested?.Invoke();
        var abort = new Button { Text = "Abort to menu" };
        abort.Pressed += () => AbortRequested?.Invoke();
        box.AddChild(resume);
        box.AddChild(abort);
        _buddyToggle = new CheckButton { Text = "Sonar buddy callouts", ButtonPressed = true };
        _buddyToggle.TooltipText = "Solo-only callout system: range, contact, and threshold warnings. No auto-steering. Default on.";
        _buddyToggle.Toggled += on =>
        {
            BuddyEnabled = on;
            BuddyToggled?.Invoke(on);
        };
        box.AddChild(_buddyToggle);
        resume.GrabFocus();
    }

    private void BuildEndOverlay()
    {
        _endOverlay = new PanelContainer();
        _endOverlay.AddThemeStyleboxOverride("panel", PanelStyle());
        _endOverlay.SetAnchorsPreset(LayoutPreset.Center);
        _endOverlay.Position = new Vector2(-260, -170);
        _endOverlay.CustomMinimumSize = new Vector2(520, 0);
        _endOverlay.Visible = false;
        AddChild(_endOverlay);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        _endOverlay.AddChild(box);
        _endTitle = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _endTitle.AddThemeFontSizeOverride("font_size", 22);
        _endTitle.AddThemeColorOverride("font_color", new Color("#dae4df"));
        box.AddChild(_endTitle);
        _endBody = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _endBody.AddThemeFontSizeOverride("font_size", 14);
        _endBody.AddThemeColorOverride("font_color", new Color("#dae4df"));
        box.AddChild(_endBody);
        _retryButton = new Button { Text = "Retry save (no double reward)", Visible = false };
        _retryButton.Pressed += () => RetrySaveRequested?.Invoke();
        box.AddChild(_retryButton);
        _endMenuButton = new Button { Text = "Return to menu" };
        _endMenuButton.Pressed += () => AbortRequested?.Invoke();
        box.AddChild(_endMenuButton);
    }

    /// <summary>
    /// Settlement write in flight: retry hidden and exit held so a pending
    /// settlement can never be abandoned mid-write (no lost settlement).
    /// </summary>
    public void SetEndButtons(bool retryVisible, bool menuEnabled)
    {
        if (_retryButton is not null)
        {
            _retryButton.Visible = retryVisible;
            _retryButton.Disabled = !retryVisible;
        }
        if (_endMenuButton is not null)
        {
            _endMenuButton.Disabled = !menuEnabled;
            _endMenuButton.TooltipText = menuEnabled ? "Return to menu." : "Settlement write in flight — exit held until it completes.";
        }
    }

    public void UpdateInstruments(RunSimulation sim, bool quiet, string frameName)
    {
        if (_instruments is null || _cargo is null || _objective is null) return;
        var hullPct = sim.MaxHull > 0f ? sim.HullIntegrity / sim.MaxHull * 100f : 0f;
        _instruments.Text =
            $"FRAME {frameName}   DEPTH {sim.DepthMeters:F0} m\n" +
            $"HULL {sim.HullIntegrity:F0}/{sim.MaxHull:F0} ({hullPct:F0}%)   MARGIN {sim.PressureMargin:F1}\n" +
            $"PWR {sim.PowerDemand:F0}/{sim.PowerSupply:F0} PU  SHED {sim.PowerShedLevel}{(sim.BrownoutActive ? " BROWNOUT" : "")}\n" +
            $"NOISE {sim.Noise:F0}  THREAT {sim.Threat:F0}{(quiet ? "  QUIET" : "")}  SEALANT {sim.Sealant}  WINCH {(sim.WinchUsed ? "SPENT" : "READY")}\n" +
            $"DOCK {(sim.IsDocked ? sim.DockedNodeId : "FREE")}  SPEED {sim.ShipSpeedMps:F1} m/s" +
            (sim.IsDrilling ? $"  DRILL {sim.DrillElapsedSeconds:F1}/{sim.DrillDurationSeconds:F0}s" : "") +
            $"  REPAIRS {sim.RepairsDone}";
        _cargo.Text = $"CARGO {sim.CargoUsedSlots}/{sim.CargoMaxSlots} slots  {sim.CargoUsedMassKg:F0}/{sim.CargoMaxMassKg:F0} kg  SECURED {sim.SecuredSalvageValue} cr  PULSES {sim.PulsesUsed}  SURVEYS {sim.SurveysDone}\n" +
            $"SONAR pulse {sim.ActivePulseRangeMeters:F0} m / passive {sim.PassiveSonarRangeMeters:F0} m  SALVAGE {sim.SalvageRangeMeters:F0} m";
        var parts = new List<string>();
        foreach (var o in sim.Contract.Objectives)
        {
            parts.Add($"{o.ObjectiveId} {o.Current}/{o.Required}{(o.IsComplete ? " ✓" : "")}");
        }
        _objective.Text = string.Join("   ·   ", parts);
    }

    public void ShowMessage(string text, float seconds = 4f)
    {
        if (_message is null) return;
        _message.Text = text;
        _messageTimer = seconds;
    }

    public void SetWarning(string text)
    {
        if (_warning is not null) _warning.Text = text;
    }

    /// <summary>Buddy callout line: steady text, amber only for critical.</summary>
    public void ShowBuddyCallout(string text, bool critical)
    {
        if (_buddy is null) return;
        _buddy.Text = (critical ? "BUDDY !! " : "BUDDY ") + text;
        _buddy.AddThemeColorOverride("font_color",
            critical ? new Color("#dfa44d") : new Color("#71d9d1"));
    }

    public void SetBuddyEnabled(bool enabled)
    {
        BuddyEnabled = enabled;
        if (_buddyToggle is not null) _buddyToggle.ButtonPressed = enabled;
    }

    public void SetPaused(bool paused)
    {
        if (_pauseOverlay is not null) _pauseOverlay.Visible = paused;
    }

    public void ShowEnd(string title, string body, bool showRetry)
    {
        if (_endOverlay is null) return;
        if (_endTitle is not null) _endTitle.Text = title;
        if (_endBody is not null) _endBody.Text = body;
        if (_retryButton is not null) _retryButton.Visible = showRetry;
        _endOverlay.Visible = true;
    }

    public void HideEnd()
    {
        if (_endOverlay is not null) _endOverlay.Visible = false;
        if (_retryButton is not null) _retryButton.Visible = false;
    }
}
