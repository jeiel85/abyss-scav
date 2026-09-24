using AbyssScav.App;
using AbyssScav.Domain;
using AbyssScav.Foundation;
using Godot;

namespace AbyssScav.Presentation;

/// <summary>
/// Solo-run HUD: submarine instruments (depth/hull/pressure/noise/power),
/// objective readout, controls hint, message line, steady warning banner,
/// pause and settlement overlays. Industrial type hierarchy on ink-blue.
/// Colours come from the selected sonar palette and the high-contrast switch
/// (<see cref="HudTheme"/>); every key named on screen follows the player's
/// current bindings (<see cref="InputBindings"/>).
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
    private HudTheme _theme = new(SonarPalette.Standard, false);
    private string _warningSource = "";

    public SonarDisplay Sonar { get; private set; } = null!;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        _theme = HudTheme.Current();
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

    private void Build()
    {
        var parchment = _theme.Text;
        // Left instruments.
        var left = new PanelContainer { Name = "Instruments" };
        left.AddThemeStyleboxOverride("panel", _theme.PanelStyle());
        left.SetAnchorsPreset(LayoutPreset.TopLeft);
        left.Position = new Vector2(12, 12);
        left.CustomMinimumSize = new Vector2(300, 0);
        AddChild(left);
        _instruments = new Label { Name = "InstrumentText" };
        _theme.StyleLabel(_instruments, 14, parchment);
        left.AddChild(_instruments);

        // Objective top-center.
        var obj = new PanelContainer();
        obj.AddThemeStyleboxOverride("panel", _theme.PanelStyle());
        obj.SetAnchorsPreset(LayoutPreset.CenterTop);
        obj.Position = new Vector2(-260, 12);
        obj.CustomMinimumSize = new Vector2(520, 0);
        AddChild(obj);
        _objective = new Label { HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _theme.StyleLabel(_objective, 14, parchment);
        obj.AddChild(_objective);

        // Sonar right.
        Sonar = new SonarDisplay { Name = "Sonar" };
        Sonar.ApplyHudTheme(_theme);
        Sonar.SetAnchorsPreset(LayoutPreset.TopRight);
        Sonar.Position = new Vector2(-252, 12);
        AddChild(Sonar);

        // Warning banner below objective: steady amber, no flashing.
        _warning = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _theme.StyleLabel(_warning, 15, _theme.Alert);
        _warning.SetAnchorsPreset(LayoutPreset.CenterTop);
        _warning.Position = new Vector2(-260, 96);
        _warning.CustomMinimumSize = new Vector2(520, 0);
        AddChild(_warning);

        // Message line.
        _message = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _theme.StyleLabel(_message, 14, _theme.Accent);
        _message.SetAnchorsPreset(LayoutPreset.CenterBottom);
        _message.Position = new Vector2(-400, -140);
        _message.CustomMinimumSize = new Vector2(800, 0);
        AddChild(_message);

        // Cargo + controls bottom-left.
        _cargo = new Label();
        _theme.StyleLabel(_cargo, 13, parchment);
        _cargo.SetAnchorsPreset(LayoutPreset.BottomLeft);
        _cargo.Position = new Vector2(12, -120);
        _cargo.CustomMinimumSize = new Vector2(420, 0);
        _cargo.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        AddChild(_cargo);

        _hint = new Label
        {
            Text = InputBindings.ControlsHint(),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _theme.StyleLabel(_hint, 12, _theme.TextDim);
        _hint.SetAnchorsPreset(LayoutPreset.BottomWide);
        _hint.OffsetLeft = 12;
        _hint.OffsetRight = -12;
        _hint.OffsetTop = -34;
        _hint.OffsetBottom = -6;
        AddChild(_hint);

        // Buddy line bottom-right: last callout, steady (no flashing, no timer).
        _buddy = new Label
        {
            Text = Localization.T("BUDDY LINKED — callouts on"),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _theme.StyleLabel(_buddy, 13, _theme.Accent);
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
        _pauseOverlay.AddThemeStyleboxOverride("panel", _theme.PanelStyle());
        _pauseOverlay.SetAnchorsPreset(LayoutPreset.Center);
        _pauseOverlay.Position = new Vector2(-190, -130);
        _pauseOverlay.CustomMinimumSize = new Vector2(380, 0);
        _pauseOverlay.Visible = false;
        AddChild(_pauseOverlay);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        _pauseOverlay.AddChild(box);
        var title = new Label { Text = Localization.T("PAUSED — DIVE HELD"), HorizontalAlignment = HorizontalAlignment.Center };
        _theme.StyleLabel(title, 20, _theme.Text);
        box.AddChild(title);
        var resume = new Button { Text = Localization.T("Resume dive (Esc)") };
        resume.Pressed += () => ResumeRequested?.Invoke();
        var abort = new Button { Text = Localization.T("Abort to menu") };
        abort.Pressed += () => AbortRequested?.Invoke();
        box.AddChild(resume);
        box.AddChild(abort);
        _buddyToggle = new CheckButton { Text = Localization.T("Sonar buddy callouts"), ButtonPressed = true };
        _buddyToggle.TooltipText = Localization.T("Solo-only callout system: range, contact, and threshold warnings. No auto-steering. Default on.");
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
        _endOverlay.AddThemeStyleboxOverride("panel", _theme.PanelStyle());
        _endOverlay.SetAnchorsPreset(LayoutPreset.Center);
        _endOverlay.Position = new Vector2(-260, -170);
        _endOverlay.CustomMinimumSize = new Vector2(520, 0);
        _endOverlay.Visible = false;
        AddChild(_endOverlay);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        _endOverlay.AddChild(box);
        _endTitle = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _theme.StyleLabel(_endTitle, 22, _theme.Text);
        box.AddChild(_endTitle);
        _endBody = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _theme.StyleLabel(_endBody, 14, _theme.Text);
        box.AddChild(_endBody);
        _retryButton = new Button { Text = Localization.T("Retry save (no double reward)"), Visible = false };
        _retryButton.Pressed += () => RetrySaveRequested?.Invoke();
        box.AddChild(_retryButton);
        _endMenuButton = new Button { Text = Localization.T("Return to menu") };
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
            _endMenuButton.TooltipText = menuEnabled ? Localization.T("Return to menu.") : Localization.T("Settlement write in flight — exit held until it completes.");
        }
    }

    public void UpdateInstruments(RunSimulation sim, bool quiet, string frameName)
    {
        if (_instruments is null || _cargo is null || _objective is null) return;
        var hullPct = sim.MaxHull > 0f ? sim.HullIntegrity / sim.MaxHull * 100f : 0f;
        var gear = sim.ConsumableCounts.Count == 0
            ? "—"
            : string.Join("  ", sim.ConsumableCounts.Select(kv => $"{ShortConsumable(kv.Key)}×{kv.Value}"));
        var buoyKey = InputBindings.ActionKeyLabel("abyss_buoy");
        var decoyKey = InputBindings.ActionKeyLabel("abyss_decoy");
        var empKey = InputBindings.ActionKeyLabel("abyss_emp");
        _instruments.Text =
            $"FRAME {frameName}   DEPTH {sim.DepthMeters:F0} m\n" +
            $"HULL {sim.HullIntegrity:F0}/{sim.MaxHull:F0} ({hullPct:F0}%)   MARGIN {sim.PressureMargin:F1}\n" +
            $"PWR {sim.PowerDemand:F0}/{sim.PowerSupply:F0} PU  SHED {sim.PowerShedLevel}{(sim.BrownoutActive ? " BROWNOUT" : "")}\n" +
            $"NOISE {sim.Noise:F0}  THREAT {sim.Threat:F0}{(quiet ? "  QUIET" : "")}  SEALANT {sim.Sealant}  WINCH {(sim.WinchUsed ? "SPENT" : "READY")}\n" +
            $"GEAR {gear}  BUOY {(sim.BuoyCharges == 0 ? "—" : sim.BuoyFired ? "FIRED" : $"READY ({buoyKey})")}  DECOY {(sim.DecoyCharges == 0 ? "—" : $"{sim.DecoyChargesRemaining} ({decoyKey})")}  EMP {(sim.EmpCharges == 0 ? "—" : $"{sim.EmpChargesRemaining} ({empKey})")}\n" +
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

    /// <summary>Compact HUD token for a consumable ID (bound slot key + short name).</summary>
    private static string ShortConsumable(string id)
    {
        var (slot, name) = id switch
        {
            "consumable.sealant_canister" => (1, "SEAL"),
            "consumable.battery_pack" => (2, "BATT"),
            "consumable.hull_patch" => (3, "PATCH"),
            "consumable.decoy" => (4, "DECOY"),
            "consumable.flare" => (5, "FLARE"),
            "consumable.sonar_buoy" => (6, "BUOY"),
            "consumable.stim" => (7, "STIM"),
            "consumable.antifreeze" => (8, "ANTI"),
            _ => (0, id),
        };
        return slot == 0 ? name : $"{InputBindings.ActionKeyLabel("abyss_consumable_" + slot)}:{name}";
    }

    public void ShowMessage(string text, float seconds = 4f)
    {
        if (_message is null) return;
        _message.Text = InputBindings.RewriteHints(text);
        _messageTimer = seconds;
    }

    public void SetWarning(string text)
    {
        // Called every frame with mostly identical text: rewrite only on change.
        if (_warning is null || text == _warningSource) return;
        _warningSource = text;
        _warning.Text = InputBindings.RewriteHints(text);
    }

    /// <summary>Buddy callout line: steady text, amber only for critical.</summary>
    public void ShowBuddyCallout(string text, bool critical)
    {
        if (_buddy is null) return;
        _buddy.Text = (critical ? "BUDDY !! " : "BUDDY ") + InputBindings.RewriteHints(text);
        _buddy.AddThemeColorOverride("font_color", critical ? _theme.Alert : _theme.Accent);
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
