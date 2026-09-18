using AbyssScav.App;
using AbyssScav.Domain;
using AbyssScav.Foundation;
using AbyssScav.Gameplay;
using AbyssScav.Infra.Logging;
using AbyssScav.Persistence;
using AbyssScav.Platform;
using Godot;

namespace AbyssScav.Presentation.Menus;

/// <summary>
/// Solo vertical-slice main menu: 720p-centered industrial layout on ink-blue,
/// Solo Dive launches contract/loadout selection, co-op stays visibly disabled
/// (Net integration is separate scope). Settings preserved. Save profile loads
/// here; corruption shows an explicit restore prompt instead of crashing or
/// silently resetting.
/// </summary>
public partial class MainMenu : Control
{
    private SettingsPanel? _settingsPanel;
    private Label? _statusLabel;
    private Label? _profileLabel;
    private Control? _savePrompt;
    private Label? _savePromptText;
    private FileSaveStore? _store;
    private ProfileSave _profile = ProfileSave.Default();
    private bool _saveUsable = true;
    private Button? _tutorialButton;
    private CancellationTokenSource? _cts;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        // Debug-only smoke: exercises actual run construction + ticks, then quits.
        if (OS.IsDebugBuild())
        {
            var args = new List<string>(OS.GetCmdlineArgs());
            args.AddRange(OS.GetCmdlineUserArgs());
            if (args.Any(a => string.Equals(a, "--run-smoke", StringComparison.OrdinalIgnoreCase)))
            {
                RunSmoke.Execute(this, args.ToArray());
                return;
            }
        }
        BuildUi();
        RefreshStatus();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        LoadProfileAsync(explicitRetry: false);
    }

    public override void _ExitTree()
    {
        try { _cts?.Cancel(); }
        catch { }
        finally { _cts?.Dispose(); _cts = null; }
    }

    private static Color Ink() => new("#071622");
    private static Color Metal() => new("#29414b");
    private static Color Parchment() => new("#dae4df");
    private static Color Cyan() => new("#71d9d1");
    private static Color Amber() => new("#dfa44d");

    private void BuildUi()
    {
        var bg = new ColorRect { Color = Ink() };
        bg.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(bg);

        // Depth rails: thin oxidized-metal side bars frame the centered column.
        var leftRail = new ColorRect { Color = Metal() };
        leftRail.SetAnchorsPreset(LayoutPreset.LeftWide);
        leftRail.OffsetRight = 26;
        leftRail.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(leftRail);
        var rightRail = new ColorRect { Color = Metal() };
        rightRail.SetAnchorsPreset(LayoutPreset.RightWide);
        rightRail.OffsetLeft = -26;
        rightRail.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(rightRail);

        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(center);

        var column = new VBoxContainer();
        column.Alignment = BoxContainer.AlignmentMode.Center;
        column.AddThemeConstantOverride("separation", 8);
        column.CustomMinimumSize = new Vector2(460, 0);
        center.AddChild(column);

        var kicker = new Label { Text = Localization.T("DARK INDUSTRIAL SALVAGE // SOLO VERTICAL SLICE"), HorizontalAlignment = HorizontalAlignment.Center };
        kicker.AddThemeFontSizeOverride("font_size", 13);
        kicker.AddThemeColorOverride("font_color", Cyan());
        column.AddChild(kicker);

        var title = new Label { Text = Localization.T("ABYSS SCAV"), HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 52);
        title.AddThemeColorOverride("font_color", Parchment());
        column.AddChild(title);

        var subtitle = new Label
        {
            Text = Localization.T("Submarine salvage protocol v{0}", (object)GameVersion.Current),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        subtitle.AddThemeFontSizeOverride("font_size", 14);
        subtitle.AddThemeColorOverride("font_color", new Color(0.62f, 0.72f, 0.70f));
        column.AddChild(subtitle);

        _profileLabel = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _profileLabel.AddThemeFontSizeOverride("font_size", 14);
        _profileLabel.AddThemeColorOverride("font_color", Parchment());
        column.AddChild(_profileLabel);

        _statusLabel = new Label { HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(460, 0) };
        _statusLabel.AddThemeFontSizeOverride("font_size", 13);
        _statusLabel.AddThemeColorOverride("font_color", new Color(0.62f, 0.72f, 0.70f));
        column.AddChild(_statusLabel);

        Button? first = null;
        var dive = AddMenuButton(column, Localization.T("Solo Dive — Contract & Loadout"), true, Localization.T("Pick waters, contract, and frame, then dive."));
        first = dive;
        dive.Pressed += () => Navigate(AppScene.Loadout, Localization.T("Solo dive requires the contract screen."));
        // T0 stays available after completion (replay resumes a complete checklist).
        _tutorialButton = AddMenuButton(column, Localization.T("Tutorial: The First Ping"), true, Localization.T("Guided first dive: fixed waters, contract, seed 4242, and skiff."));
        _tutorialButton.Pressed += LaunchTutorial;
        var researchButton = AddMenuButton(column, Localization.T("Research"), true, Localization.T("Spend research data on module blueprints (recorded; no in-run effect yet)."));
        researchButton.Pressed += () => Navigate(AppScene.Research, Localization.T("Research requires the research screen."));
        var codexButton = AddMenuButton(column, Localization.T("Codex"), true, Localization.T("Survey record: creatures, waters, and relic traits found on dives."));
        codexButton.Pressed += () => Navigate(AppScene.Codex, Localization.T("Codex requires the codex screen."));

        var settingsButton = AddMenuButton(column, Localization.T("Settings"), true, Localization.T("Adjust window, graphics and volume."));
        settingsButton.Pressed += () =>
        {
            var panel = _settingsPanel;
            if (panel is not null)
            {
                panel.Visible = !panel.Visible;
                if (panel.Visible) panel.Reload();
            }
        };

        var logsButton = AddMenuButton(column, Localization.T("Open Logs Folder"), true, Localization.T("Open the local logs folder."));
        logsButton.Pressed += () =>
        {
            if (!GameServices.IsInitialized)
            {
                GD.PushError("[BOOT-003] Services not initialized.");
                return;
            }
            new DesktopPlatformService(GameServices.Paths).OpenLogFolder();
        };

        var bundleButton = AddMenuButton(column, Localization.T("Create Support Bundle"), true, Localization.T("Package logs and settings into a support bundle for bug reports."));
        bundleButton.Pressed += () =>
        {
            if (!GameServices.IsInitialized)
            {
                GD.PushError("[BOOT-003] Services not initialized.");
                return;
            }
            try
            {
                var sessionId = GameServices.SessionLock.SessionId;
                var osInfo = new DesktopPlatformService(GameServices.Paths).DescribeOs();
                var bundlePath = SupportBundleWriter.Create(GameServices.Paths, GameVersion.Current, sessionId, osInfo);
                if (_statusLabel is not null) _statusLabel.Text = Localization.T("Support bundle written to {0}.", (object)bundlePath);
            }
            catch (Exception ex)
            {
                GD.PushError(ex.Message);
                if (_statusLabel is not null) _statusLabel.Text = Localization.T("Support bundle failed: {0}", (object)ex.Message);
            }
        };

        var quitButton = AddMenuButton(column, Localization.T("Quit"), true, Localization.T("Exit the game."));
        quitButton.Pressed += () => GetTree()?.Quit();

        // Keyboard focus order is linear; first item grabbed.
        first?.GrabFocus();

        var panel = new SettingsPanel();
        _settingsPanel = panel;
        panel.Visible = false;
        AddChild(panel);
        panel.Applied += RefreshStatus;

        BuildSavePrompt();
    }

    private void BuildSavePrompt()
    {
        _savePrompt = new PanelContainer();
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.03f, 0.09f, 0.12f, 0.98f),
            BorderColor = Amber(),
            BorderWidthLeft = 2, BorderWidthRight = 2, BorderWidthTop = 2, BorderWidthBottom = 2,
            ContentMarginLeft = 18, ContentMarginRight = 18, ContentMarginTop = 14, ContentMarginBottom = 14,
        };
        ((PanelContainer)_savePrompt).AddThemeStyleboxOverride("panel", style);
        ((PanelContainer)_savePrompt).SetAnchorsPreset(LayoutPreset.Center);
        ((PanelContainer)_savePrompt).Position = new Vector2(-250, -140);
        ((PanelContainer)_savePrompt).CustomMinimumSize = new Vector2(500, 0);
        ((PanelContainer)_savePrompt).Visible = false;
        AddChild(_savePrompt);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        _savePrompt.AddChild(box);
        var title = new Label { Text = Localization.T("SAVE PROFILE NEEDS ATTENTION"), HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 18);
        title.AddThemeColorOverride("font_color", Amber());
        box.AddChild(title);
        _savePromptText = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _savePromptText.AddThemeColorOverride("font_color", Parchment());
        box.AddChild(_savePromptText);
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        row.AddThemeConstantOverride("separation", 8);
        box.AddChild(row);
        var retry = new Button { Text = Localization.T("Retry load") };
        retry.Pressed += () => LoadProfileAsync(explicitRetry: true);
        var restore = new Button { Text = Localization.T("Restore backup") };
        restore.Pressed += () => RestoreBackupAsync(true);
        var play = new Button { Text = Localization.T("Continue unsaved") };
        play.Pressed += () =>
        {
            // Explicit player choice: session-only opt-out, registered in the
            // per-boot session state so it persists across scenes and menus.
            // Reads stay allowed; every production write path reports
            // "not saved by choice" instead of an error/retry.
            if (GameServices.IsInitialized
                && GameServices.Registry.TryResolve<ProfileSessionState>(out var session)
                && session is not null)
            {
                session.ContinueWithoutSaving = true;
            }
            if (_savePrompt is PanelContainer pc) pc.Visible = false;
            RefreshProfileLabel();
            if (_statusLabel is not null && IsInstanceValid(_statusLabel))
                _statusLabel.Text += "\n" + Localization.T("Running unsaved by choice: settlement credits, research, and tutorial progress will not persist.");
        };
        row.AddChild(retry);
        row.AddChild(restore);
        row.AddChild(play);
    }

    private static Button AddMenuButton(VBoxContainer parent, string text, bool enabled, string tooltip)
    {
        var button = new Button
        {
            Text = text,
            Disabled = !enabled,
            TooltipText = tooltip,
            CustomMinimumSize = new Vector2(460, 38),
            FocusMode = enabled ? Control.FocusModeEnum.All : Control.FocusModeEnum.None,
        };
        button.AddThemeFontSizeOverride("font_size", 15);
        parent.AddChild(button);
        return button;
    }

    private void Navigate(AppScene target, string failNote)
    {
        if (!GameServices.IsInitialized)
        {
            GD.PushError("[BOOT-003] Services not initialized.");
            return;
        }
        var svc = GameServices.Registry.TryResolve<SceneFlowService>(out var s) ? s : null;
        if (svc is not null)
        {
            if (!svc.Navigate(target) && _statusLabel is not null)
            {
                _statusLabel.Text = failNote + " " + Localization.T("Flow refused the transition; staying on menu.");
            }
            return;
        }
        // Registry without the service (tests): direct scene change, same bookkeeping.
        if (GameServices.Flow.TryTransition(target, out var error))
        {
            var path = target switch
            {
                AppScene.Loadout => "res://scenes/contract_select.tscn",
                AppScene.RunLoading => "res://scenes/run.tscn",
                AppScene.Research => "res://scenes/research.tscn",
                AppScene.Codex => "res://scenes/codex.tscn",
                _ => "res://scenes/main_menu.tscn",
            };
            GetTree()?.CallDeferred(SceneTree.MethodName.ChangeSceneToFile, path);
        }
        else if (_statusLabel is not null)
        {
            _statusLabel.Text = error;
        }
    }

    /// <summary>
    /// Direct tutorial launch with the fixed T0 options. Stages the options and
    /// navigates once to Loadout; ContractSelect consumes the pending tutorial
    /// at readiness and performs the single deferred Launch to RunLoading.
    /// Options are validated before anything is staged — never a blind hop.
    /// </summary>
    private void LaunchTutorial()
    {
        if (!GameServices.IsInitialized)
        {
            GD.PushError("[BOOT-003] Services not initialized.");
            return;
        }
        if (!ContentCatalog.TryBuild(out var catalog, out var errors) || catalog is null)
        {
            if (_statusLabel is not null) _statusLabel.Text = Localization.T("Tutorial unavailable: content catalog failed: {0}", (object)string.Join("; ", errors));
            return;
        }
        var options = RunLaunchOptions.Tutorial();
        if (!options.TryValidate(catalog, out var problems))
        {
            if (_statusLabel is not null) _statusLabel.Text = Localization.T("Tutorial unavailable: {0}", (object)string.Join("; ", problems));
            return;
        }
        RunLaunchContext.Pending = options;
        Navigate(AppScene.Loadout, Localization.T("Tutorial launch requires the contract screen."));
    }

    private void RefreshStatus()
    {
        var status = _statusLabel;
        if (status is null) return;
        if (!GameServices.IsInitialized)
        {
            status.Text = Localization.T("Boot services unavailable. Restart the game.");
            return;
        }
        var holder = GameServices.Registry.Resolve<AppSettingsHolder>();
        var sessionLock = GameServices.SessionLock;
        var parts = new List<string>();
        if (holder.IsSafeMode) parts.Add(Localization.T("SAFE MODE: 1280x720 windowed / Low (preferences untouched)."));
        if (sessionLock.PreviousCrashDetected) parts.Add(Localization.T("Previous session did not exit cleanly. Safe mode is recommended."));
        if (holder.IsFutureVersionReadOnly) parts.Add(Localization.T("Settings are from a newer version: running on defaults without overwriting your file."));
        if (parts.Count == 0)
        {
            var s = holder.Current;
            parts.Add(Localization.T("Ready — {0}x{1} {2} / {3}.", s.Graphics.Width, s.Graphics.Height, Localization.T(s.Graphics.WindowMode), Localization.T(s.Graphics.Quality)));
        }
        status.Text = string.Join("\n", parts);
        GodotLogBridge.Info(GameServices.Logger, "Main menu shown. " + string.Join(" ", parts));
        RefreshProfileLabel();
    }

    private void RefreshProfileLabel()
    {
        if (_profileLabel is null || !IsInstanceValid(_profileLabel)) return;
        var p = _profile;
        var suffix = GameServices.WritesSuspendedByChoice
            ? Localization.T(" (unsaved — not saved by choice)")
            : _saveUsable ? "" : Localization.T(" (unsaved session)");
        _profileLabel.Text = Localization.T("Settlement ledger — {0} cr · {1} research · {2} shards · {3}W/{4}L{5}", p.Currencies.Credits, p.Currencies.ResearchData, p.Currencies.AbyssShards, p.Stats.RunsCompleted, p.Stats.RunsFailed, suffix);
        if (_tutorialButton is not null)
        {
            _tutorialButton.Text = p.Tutorial.Completed
                ? Localization.T("Tutorial: The First Ping (completed — replay)")
                : Localization.T("Tutorial: The First Ping");
        }
    }

    /// <summary>
    /// Loads the live profile for display. Reads stay allowed in the unsaved
    /// session; only an explicit successful Retry/Restore clears the opt-out —
    /// an implicit menu reload never does. Load failures while opted out keep
    /// the "not saved by choice" notice instead of an error/retry prompt.
    /// </summary>
    private async void LoadProfileAsync(bool explicitRetry = false)
    {
        if (!GameServices.IsInitialized) return;
        var ct = _cts?.Token ?? CancellationToken.None;
        try
        {
            _store ??= new FileSaveStore(GameServices.Paths.SavesDir);
        }
        catch (Exception ex)
        {
            if (GameServices.WritesSuspendedByChoice)
            {
                RefreshProfileLabel();
                return;
            }
            ShowSavePrompt(Localization.T("Save folder is unavailable: {0}", (object)ex.Message));
            return;
        }
        try
        {
            _profile = await _store.LoadAsync(ct);
            if (ct.IsCancellationRequested || !IsInstanceValid(this)) return;
            _saveUsable = true;
            if (explicitRetry) ClearOptOut();
            if (_savePrompt is PanelContainer pc && IsInstanceValid(pc)) pc.Visible = false;
            RefreshProfileLabel();
        }
        catch (OperationCanceledException)
        {
            // Scene torn down; leave nodes alone.
        }
        catch (SaveFutureVersionException ex)
        {
            if (ct.IsCancellationRequested || !IsInstanceValid(this)) return;
            // Future-schema file preserved read-only; opt-out untouched.
            _saveUsable = false;
            if (GameServices.WritesSuspendedByChoice)
            {
                RefreshProfileLabel();
                return;
            }
            ShowSavePrompt(Localization.T("Profile is from a newer version (schema v{0}). The game runs read-only on defaults so nothing is overwritten. Install the newer build to use it.", ex.FoundVersion));
            RefreshProfileLabel();
        }
        catch (SaveCorruptException ex)
        {
            if (ct.IsCancellationRequested || !IsInstanceValid(this)) return;
            if (GameServices.WritesSuspendedByChoice)
            {
                RefreshProfileLabel();
                return;
            }
            ShowSavePrompt(Localization.T("Profile failed to load and was left untouched: {0}\nRetry, restore the newest backup, or continue without saving (explicit choice, never a silent reset).", (object)ex.Message));
        }
        catch (SaveException ex)
        {
            if (ct.IsCancellationRequested || !IsInstanceValid(this)) return;
            if (GameServices.WritesSuspendedByChoice)
            {
                RefreshProfileLabel();
                return;
            }
            ShowSavePrompt(Localization.T("Profile load failed [{0}] and was left untouched: {1}", (object)ex.Code, ex.Message));
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested || !IsInstanceValid(this)) return;
            if (GameServices.WritesSuspendedByChoice)
            {
                RefreshProfileLabel();
                return;
            }
            ShowSavePrompt(Localization.T("Profile load failed unexpectedly and was left untouched: {0}", (object)ex.GetType().Name));
        }
    }

    private async void RestoreBackupAsync(bool explicitRestore = true)
    {
        if (_store is null) return;
        var ct = _cts?.Token ?? CancellationToken.None;
        for (var gen = 1; gen <= FileSaveStore.BackupGenerations; gen++)
        {
            try
            {
                var result = await _store.RestoreBackupAsync(gen, ct);
                if (ct.IsCancellationRequested || !IsInstanceValid(this)) return;
                _profile = result.Save;
                _saveUsable = true;
                if (explicitRestore) ClearOptOut();
                if (_savePrompt is PanelContainer pc && IsInstanceValid(pc)) pc.Visible = false;
                RefreshProfileLabel();
                if (_statusLabel is not null && IsInstanceValid(_statusLabel)) _statusLabel.Text += "\n" + Localization.T("Restored backup generation {0}. Previous primary quarantined.", gen);
                GodotLogBridge.Info(GameServices.Logger, $"Profile restored from backup {gen}.");
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // Try the next generation; report only after all fail.
            }
        }
        if (ct.IsCancellationRequested || !IsInstanceValid(this)) return;
        ShowSavePrompt(Localization.T("No usable backup generation (1..3) was found. Retry, or continue without saving."));
    }

    /// <summary>Only an explicit successful Retry/Restore clears the opt-out.</summary>
    private static void ClearOptOut()
    {
        if (GameServices.IsInitialized
            && GameServices.Registry.TryResolve<ProfileSessionState>(out var session)
            && session is not null)
        {
            session.ContinueWithoutSaving = false;
        }
    }

    private void ShowSavePrompt(string text)
    {
        if (_savePromptText is not null) _savePromptText.Text = text;
        if (_savePrompt is PanelContainer pc) pc.Visible = true;
        GodotLogBridge.Warn(GameServices.Logger, "Save prompt shown: " + text);
    }
}
