using AbyssScav.Foundation;
using Godot;

namespace AbyssScav.App;

/// <summary>
/// Boot scene root (EPIC A-01): config, logging, crash marker, safe-mode, then MainMenu.
/// Acceptance: reaches main menu on a clean Windows PC with no network.
/// Fatal boot failures (storage, logger, config I/O, concurrent session) show a
/// visible error screen with Quit instead of a blank screen or a fake menu.
/// </summary>
public partial class GameBootstrap : Node
{
    public override void _Ready()
    {
        var allArgs = new List<string>(OS.GetCmdlineArgs());
        allArgs.AddRange(OS.GetCmdlineUserArgs());
        var bootOptions = BootOptions.Parse(allArgs);

        var paths = AppPaths.FromRoot(OS.GetUserDataDir());
        try
        {
            paths.EnsureDirectories();
        }
        catch (Exception ex)
        {
            GD.PushError($"[{ErrorCodes.BootSessionLock}] Cannot prepare local storage: {LocalLogger.Sanitize(ex.GetType().Name)}.");
            ShowBootError(
                ErrorCodes.BootSessionLock,
                "Local storage is unavailable, so the game cannot start.",
                bootOptions.SafeMode
                    ? "Check disk space and permissions, then relaunch."
                    : "Check disk space and permissions, then relaunch. You can also try --safe-mode (720p windowed, low graphics).");
            return;
        }

        LocalLogger logger;
        var sessionLock = new SessionLockService(paths);
        try
        {
            // SessionId is available before Acquire; the logger works even if the lock is contested.
            logger = new LocalLogger(paths.LogsDir, sessionLock.SessionId, GameVersion.Current);
        }
        catch (Exception ex)
        {
            GD.PushError($"[{ErrorCodes.BootLogInit}] Cannot initialize log file: {LocalLogger.Sanitize(ex.GetType().Name)}.");
            ShowBootError(
                ErrorCodes.BootLogInit,
                "Logging could not be initialized, so the game cannot start.",
                "Check disk space and permissions, then relaunch.");
            return;
        }

        try
        {
            sessionLock.Acquire();
        }
        catch (ConcurrentSessionException ex)
        {
            logger.Error(LocalLogger.Sanitize(ex.Message), ErrorCodes.BootSessionLock);
            logger.Info("Session ended: concurrent profile rejected.");
            logger.Dispose();
            GD.PushError(ex.Message);
            ShowBootError(
                ErrorCodes.BootSessionLock,
                "Another instance is already running with this profile.",
                "Close the other instance, then relaunch. No files were changed.");
            return;
        }
        catch (Exception ex)
        {
            logger.Error($"Cannot claim the session lock: {LocalLogger.Sanitize(ex.GetType().Name)}.", ErrorCodes.BootSessionLock);
            logger.Info("Session ended: session lock unavailable.");
            logger.Dispose();
            GD.PushError($"[{ErrorCodes.BootSessionLock}] Cannot claim the session lock.");
            ShowBootError(
                ErrorCodes.BootSessionLock,
                "The session marker could not be claimed, so the game cannot start.",
                "Check disk permissions, then relaunch.");
            return;
        }

        ConfigLoadResult load;
        try
        {
            load = ConfigLoader.Load(paths);
        }
        catch (Exception ex)
        {
            logger.Error($"Settings load failed: {LocalLogger.Sanitize(ex.GetType().Name)}.", ErrorCodes.BootConfigCorrupt);
            AttachSessionGuard(sessionLock, logger);
            GD.PushError($"[{ErrorCodes.BootConfigCorrupt}] Settings load failed.");
            ShowBootError(
                ErrorCodes.BootConfigCorrupt,
                "Settings could not be loaded, so the game cannot start.",
                bootOptions.SafeMode
                    ? "Check disk space and permissions, then relaunch."
                    : "Check disk space and permissions, then relaunch. You can also try --safe-mode (720p windowed, low graphics).");
            return;
        }

        switch (load.Status)
        {
            case ConfigLoadStatus.CreatedDefaults:
                logger.Info("Created default settings.", ErrorCodes.BootConfigCorrupt);
                break;
            case ConfigLoadStatus.RecoveredFromCorruption:
                logger.Warn(LocalLogger.Sanitize(load.Notice ?? "Corrupt settings were reset to defaults."), ErrorCodes.BootConfigCorrupt);
                break;
            case ConfigLoadStatus.FutureVersionReadOnly:
                logger.Warn(LocalLogger.Sanitize(load.Notice ?? "Future settings schema detected."), ErrorCodes.BootConfigFutureVersion);
                break;
            case ConfigLoadStatus.RecoveredInMemoryOnly:
            case ConfigLoadStatus.IoErrorInMemoryDefaults:
                logger.Error(LocalLogger.Sanitize(load.Notice ?? "Settings unavailable."), ErrorCodes.BootConfigCorrupt);
                AttachSessionGuard(sessionLock, logger);
                GD.PushError($"[{ErrorCodes.BootConfigCorrupt}] {load.Notice}");
                ShowBootError(
                    ErrorCodes.BootConfigCorrupt,
                    load.Notice ?? "Settings could not be loaded.",
                    bootOptions.SafeMode
                        ? "Check disk space and permissions, then relaunch."
                        : "Check disk space and permissions, then relaunch. You can also try --safe-mode (720p windowed, low graphics).");
                return;
        }

        var effective = bootOptions.SafeMode ? SafeModePolicy.Apply(load.Settings) : load.Settings;
        if (bootOptions.SafeMode)
        {
            // Safe-mode overrides stay in memory only: never persist them automatically.
            logger.Warn($"Safe mode active: running 1280x720 windowed / Low graphics in memory only. User preferences were not overwritten. Previous crash: {sessionLock.PreviousCrashDetected}.");
        }

        if (sessionLock.PreviousCrashDetected)
        {
            logger.Warn($"Previous session did not exit cleanly (marker left by {(sessionLock.PreviousSessionId ?? "unknown")}). Safe mode is recommended.");
        }

        var tree = GetTree();
        if (tree is null)
        {
            logger.Error("SceneTree unavailable at boot.", ErrorCodes.ContentSceneMissing);
            AttachSessionGuard(sessionLock, logger);
            GD.PushError($"[{ErrorCodes.ContentSceneMissing}] SceneTree unavailable at boot.");
            ShowBootError(
                ErrorCodes.ContentSceneMissing,
                "The engine scene tree is unavailable, so the game cannot start.",
                "Relaunch the game.");
            return;
        }

        try
        {
            SettingsAppliance.Apply(effective, tree.Root);
        }
        catch (Exception ex)
        {
            logger.Warn($"Could not apply display/audio settings: {LocalLogger.Sanitize(ex.GetType().Name)}.");
        }

        // SessionGuard outlives the boot scene (it is parented to the viewport root),
        // so the crash marker is released only on real quit, not on Boot -> MainMenu.
        AttachSessionGuard(sessionLock, logger);

        var registry = new Foundation.DependencyRegistry();
        registry.RegisterInstance(paths);
        registry.RegisterInstance(logger);
        registry.RegisterInstance(sessionLock);
        registry.RegisterInstance(new SceneFlow());
        registry.RegisterInstance(new AppSettingsHolder(
            load.Settings,
            bootOptions.SafeMode,
            load.Status == ConfigLoadStatus.FutureVersionReadOnly));
        registry.RegisterInstance(new SceneFlowService());
        registry.RegisterInstance(new ProfileSessionState());
        GameServices.Initialize(registry);

        // Flow bookkeeping: Boot -> MainMenu, then load the menu scene directly.
        var flow = GameServices.Flow;
        if (!flow.TryTransition(AppScene.MainMenu, out var flowError))
        {
            logger.Error(flowError, ErrorCodes.ContentSceneMissing);
            GD.PushError(flowError);
            ShowBootError(
                ErrorCodes.ContentSceneMissing,
                "The main menu could not be reached.",
                "Relaunch the game.");
            return;
        }

        logger.Info($"Boot complete. safeMode={bootOptions.SafeMode} previousCrash={sessionLock.PreviousCrashDetected} settings={effective.Graphics.Width}x{effective.Graphics.Height}/{effective.Graphics.WindowMode}/{effective.Graphics.Quality} vol={effective.MasterVolumePercent}.");

        tree.CallDeferred(SceneTree.MethodName.ChangeSceneToFile, "res://scenes/main_menu.tscn");
    }

    private void AttachSessionGuard(SessionLockService sessionLock, LocalLogger logger)
    {
        var tree = GetTree();
        if (tree?.Root is null)
        {
            return;
        }

        tree.Root.CallDeferred(Node.MethodName.AddChild, new SessionGuard(sessionLock, logger));
    }

    /// <summary>
    /// Visible fatal-error screen on the boot scene: code + human message + hint + Quit.
    /// Never transitions to the menu, so a failed boot is never reported as success.
    /// </summary>
    private void ShowBootError(string code, string message, string hint)
    {
        var layer = new CanvasLayer { Layer = 100 };
        AddChild(layer);

        var bg = new ColorRect
        {
            Color = new Color(0.01f, 0.03f, 0.06f, 1f),
        };
        bg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        layer.AddChild(bg);

        var center = new CenterContainer();
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        layer.AddChild(center);

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 10);
        center.AddChild(box);

        var title = new Label { Text = "ABYSS SCAV — Boot error", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 28);
        box.AddChild(title);

        var codeLabel = new Label { Text = code, HorizontalAlignment = HorizontalAlignment.Center };
        box.AddChild(codeLabel);

        var messageLabel = new Label
        {
            Text = message,
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(420, 0),
        };
        box.AddChild(messageLabel);

        var hintLabel = new Label
        {
            Text = hint,
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(420, 0),
        };
        box.AddChild(hintLabel);

        var quit = new Button { Text = "Quit", CustomMinimumSize = new Vector2(200, 36) };
        quit.Pressed += () => GetTree()?.Quit();
        var quitRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        quitRow.AddChild(quit);
        box.AddChild(quitRow);
    }

    /// <summary>
    /// Lives under the viewport root across scene changes; releases the crash
    /// marker and flushes the log only when the application really exits.
    /// </summary>
    private sealed partial class SessionGuard : Node
    {
        private SessionLockService? _lock;
        private LocalLogger? _logger;
        private bool _released;

        public SessionGuard(SessionLockService sessionLock, LocalLogger logger)
        {
            _lock = sessionLock;
            _logger = logger;
        }

        public override void _ExitTree() => Release();

        private void Release()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            try
            {
                if (_lock is not null && !_lock.Release())
                {
                    _logger?.Warn("session.lock could not be deleted on exit.", ErrorCodes.BootSessionLock);
                }
            }
            finally
            {
                _logger?.Info("Session ended normally.");
                _logger?.Dispose();
                _lock = null;
                _logger = null;
            }
        }
    }
}
