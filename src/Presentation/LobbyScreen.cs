using AbyssScav.App;
using AbyssScav.Domain;
using AbyssScav.Foundation;
using AbyssScav.Gameplay;
using AbyssScav.Net;
using AbyssScav.Protocol;
using Godot;
using ProtocolManifest = AbyssScav.Protocol.RunManifest;

namespace AbyssScav.Presentation;

/// <summary>
/// Co-op lobby (docs/02 §7-§8): player list with ready states, ready toggle for
/// clients, host contract staging + start, and leave. The host broadcasts the
/// run manifest; every peer (host included) stages the shared world and moves to
/// the run scene. In-run host-authoritative replication is the next batch — the
/// run scene itself is still solo per player on the shared world.
/// </summary>
public partial class LobbyScreen : Control
{
    private GodotNetworkSession? _session;
    private ContentCatalog? _catalog;
    private Label? _title;
    private VBoxContainer? _players;
    private Button? _readyButton;
    private Button? _chooseContract;
    private Button? _startButton;
    private Label? _status;
    private bool _localReady;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        _session = CoopLaunchContext.Session;
        if (_session is null)
        {
            // Defensive: the lobby is only reachable through a live session.
            BuildNoSession();
            return;
        }
        if (!ContentCatalog.TryBuild(out var catalog, out var errors) || catalog is null)
        {
            BuildError(Localization.T("Content catalog failed: {0}", (object)string.Join("; ", errors)));
            return;
        }
        _catalog = catalog;
        BuildUi();
        _session.LobbyChanged += OnLobbyChanged;
        _session.PeerJoined += OnPeerChanged;
        _session.PeerLeft += OnPeerChanged;
        _session.RunStarted += OnRunStarted;
        _session.SessionError += OnSessionError;
        _session.MappingStatus += OnMappingStatus;
        RefreshPlayers();
        RefreshHostControls();
    }

    public override void _ExitTree()
    {
        // The session node survives into the run scene; only detach this
        // screen's handlers so a freed screen never receives signals.
        if (_session is not null)
        {
            _session.LobbyChanged -= OnLobbyChanged;
            _session.PeerJoined -= OnPeerChanged;
            _session.PeerLeft -= OnPeerChanged;
            _session.RunStarted -= OnRunStarted;
            _session.SessionError -= OnSessionError;
            _session.MappingStatus -= OnMappingStatus;
        }
    }

    private static Color Ink() => new("#071622");
    private static Color Parchment() => new("#dae4df");
    private static Color Cyan() => new("#71d9d1");
    private static Color Amber() => new("#dfa44d");

    private void BuildNoSession()
    {
        var bg = new ColorRect { Color = Ink() };
        bg.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(bg);
        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(center);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 10);
        center.AddChild(box);
        var label = new Label
        {
            Text = Localization.T("No active co-op session. Return to the menu and host or join a lobby."),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(480, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        label.AddThemeColorOverride("font_color", Parchment());
        box.AddChild(label);
        var back = new Button { Text = Localization.T("Back to Menu"), CustomMinimumSize = new Vector2(200, 38) };
        back.Pressed += () => Navigate(AppScene.MainMenu);
        box.AddChild(back);
    }

    private void BuildError(string text)
    {
        var bg = new ColorRect { Color = Ink() };
        bg.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(bg);
        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(center);
        var label = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(480, 0) };
        label.AddThemeColorOverride("font_color", Parchment());
        center.AddChild(label);
    }

    private void BuildUi()
    {
        var session = _session!;
        var bg = new ColorRect { Color = Ink() };
        bg.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(bg);

        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(center);

        var panel = new PanelContainer();
        panel.CustomMinimumSize = new Vector2(560, 0);
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.03f, 0.09f, 0.12f, 1f),
            BorderColor = new Color("#29414b"),
            BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1,
            ContentMarginLeft = 20, ContentMarginRight = 20, ContentMarginTop = 16, ContentMarginBottom = 16,
        };
        panel.AddThemeStyleboxOverride("panel", style);
        center.AddChild(panel);

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        panel.AddChild(box);

        _title = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _title.AddThemeFontSizeOverride("font_size", 24);
        _title.AddThemeColorOverride("font_color", Parchment());
        box.AddChild(_title);

        var sub = new Label
        {
            Text = session.IsHost
                ? Localization.T("You are the host. Stage a contract, then start when everyone is ready.")
                : Localization.T("Waiting for the host to start. Toggle Ready when you are set."),
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        sub.AddThemeFontSizeOverride("font_size", 13);
        sub.AddThemeColorOverride("font_color", Cyan());
        box.AddChild(sub);

        var playersHeader = new Label { Text = Localization.T("PLAYERS") };
        playersHeader.AddThemeFontSizeOverride("font_size", 14);
        playersHeader.AddThemeColorOverride("font_color", Cyan());
        box.AddChild(playersHeader);

        _players = new VBoxContainer();
        _players.AddThemeConstantOverride("separation", 4);
        box.AddChild(_players);

        _status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _status.AddThemeFontSizeOverride("font_size", 13);
        _status.AddThemeColorOverride("font_color", Amber());
        box.AddChild(_status);

        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        row.AddThemeConstantOverride("separation", 10);
        box.AddChild(row);

        _readyButton = new Button { Text = Localization.T("Ready"), CustomMinimumSize = new Vector2(140, 38) };
        _readyButton.Pressed += OnReadyPressed;
        row.AddChild(_readyButton);

        _chooseContract = new Button { Text = Localization.T("Choose Contract"), CustomMinimumSize = new Vector2(180, 38) };
        _chooseContract.Pressed += () => Navigate(AppScene.Loadout);
        row.AddChild(_chooseContract);

        _startButton = new Button { Text = Localization.T("Start Run"), CustomMinimumSize = new Vector2(140, 38) };
        _startButton.Pressed += OnHostStart;
        row.AddChild(_startButton);

        var leave = new Button { Text = Localization.T("Leave Lobby"), CustomMinimumSize = new Vector2(140, 38) };
        leave.Pressed += OnLeave;
        row.AddChild(leave);

        RefreshTitle();
    }

    private void RefreshTitle()
    {
        if (_title is null || _session is null) return;
        var settings = _session.Settings;
        var name = settings?.LobbyName ?? Localization.T("Co-op Lobby");
        _title.Text = Localization.T("LOBBY — {0}", (object)name);
    }

    private void RefreshPlayers()
    {
        if (_players is null || _session is null) return;
        foreach (Node child in _players.GetChildren())
        {
            child.QueueFree();
        }
        foreach (var p in _session.Players)
        {
            var label = new Label
            {
                Text = Localization.T("{0} — {1}{2}", p.DisplayName, p.Ready ? Localization.T("Ready") : Localization.T("Not ready"), p.IsHost ? Localization.T(" (host)") : ""),
            };
            label.AddThemeColorOverride("font_color", Parchment());
            _players.AddChild(label);
        }
        RefreshHostControls();
    }

    private void RefreshHostControls()
    {
        var session = _session;
        if (session is null) return;
        var isHost = session.IsHost;
        if (_chooseContract is not null) _chooseContract.Visible = isHost;
        if (_startButton is not null)
        {
            _startButton.Visible = isHost;
            _startButton.Disabled = !isHost || !AllReady() || CoopLaunchContext.StagedOptions is null;
        }
        if (_readyButton is not null)
        {
            // The host's readiness is driven by staging a contract, not a toggle.
            _readyButton.Visible = !isHost;
            _readyButton.Text = _localReady ? Localization.T("Not ready") : Localization.T("Ready");
        }
    }

    private bool AllReady()
    {
        var session = _session;
        if (session is null) return false;
        var players = session.Players;
        return players.Count > 0 && players.All(p => p.Ready);
    }

    private void OnLobbyChanged() => RefreshPlayers();

    private void OnPeerChanged(ulong peerId) => RefreshPlayers();

    private void OnReadyPressed()
    {
        var session = _session;
        if (session is null || session.IsHost) return;
        _localReady = !_localReady;
        session.SetReady(_localReady);
        RefreshPlayers();
    }

    /// <summary>
    /// Host-only start: regenerate the staged world, broadcast the manifest,
    /// then let the RunStarted signal (fired for the host too) stage and move.
    /// </summary>
    private void OnHostStart()
    {
        var session = _session;
        if (session is null || !session.IsHost) return;
        var options = CoopLaunchContext.StagedOptions;
        if (options is null)
        {
            Say(Localization.T("Choose a contract first."));
            return;
        }
        if (_catalog is null)
        {
            Say(Localization.T("Content catalog unavailable."));
            return;
        }
        if (!options.TryValidate(_catalog, out var problems))
        {
            Say(Localization.T("Invalid selection: {0}", (object)string.Join("; ", problems)));
            return;
        }
        var request = new RunGenerationRequest(options.RunSeed, options.BiomeId, options.ContractId, _catalog);
        if (!TrenchGenerator.TryGenerate(request, out var world, out var verdict, out var reason) || world is null)
        {
            Say(Localization.T("Generation refused: {0}", (object)reason));
            return;
        }
        if (!verdict.IsValid)
        {
            Say(Localization.T("World invalid: {0}", (object)string.Join("; ", verdict.Errors)));
            return;
        }
        var domainManifest = world.ToManifest(GameVersion.Current, options.DifficultyId, options.ModifierIds);
        var manifest = new ProtocolManifest(
            domainManifest.ProtocolVersion, domainManifest.GameVersion, domainManifest.RunSeed,
            domainManifest.BiomeId, domainManifest.ContractId, domainManifest.DifficultyId,
            domainManifest.LayoutHash, domainManifest.CatalogHash, domainManifest.Modifiers);
        if (!session.StartRun(manifest, out var startError))
        {
            Say(startError);
            return;
        }
        // RunStarted fires for the host too; OnRunStarted stages + navigates.
    }

    private void OnRunStarted()
    {
        var session = _session;
        var manifest = session?.CurrentManifest;
        if (session is null || manifest is null) return;
        if (session.IsHost)
        {
            var options = CoopLaunchContext.StagedOptions;
            if (options is null)
            {
                Say(Localization.T("No staged contract; the run cannot start."));
                return;
            }
            RunLaunchContext.Pending = options;
        }
        else if (!TryStageClientRun(manifest, out var error))
        {
            Say(error);
            return;
        }
        Navigate(AppScene.RunLoading);
    }

    /// <summary>
    /// Client-side manifest consumption (docs/02 §8): regenerate the world from
    /// the manifest and refuse the join when the layout hash diverges.
    /// </summary>
    private bool TryStageClientRun(ProtocolManifest manifest, out string error)
    {
        error = string.Empty;
        if (_catalog is null)
        {
            error = Localization.T("Content catalog unavailable.");
            return false;
        }
        var frame = _catalog.Frames.ContainsKey("frame.skiff")
            ? "frame.skiff"
            : _catalog.Frames.Keys.OrderBy(x => x).First();
        var options = new RunLaunchOptions(
            manifest.BiomeId, manifest.ContractId, manifest.DifficultyId, frame,
            manifest.RunSeed, manifest.Modifiers, RunSimulation.InsuranceNone);
        if (!options.TryValidate(_catalog, out var problems))
        {
            error = Localization.T("Run refused: {0}", (object)string.Join("; ", problems));
            return false;
        }
        var request = new RunGenerationRequest(options.RunSeed, options.BiomeId, options.ContractId, _catalog);
        if (!TrenchGenerator.TryGenerate(request, out var world, out var verdict, out var reason) || world is null)
        {
            error = Localization.T("Generation refused: {0}", (object)reason);
            return false;
        }
        if (!verdict.IsValid)
        {
            error = Localization.T("World invalid: {0}", (object)string.Join("; ", verdict.Errors));
            return false;
        }
        if (!string.Equals(world.LayoutHash, manifest.LayoutHash, StringComparison.Ordinal))
        {
            error = Localization.T("World layout does not match the host ({0}). Join refused.", (object)manifest.LayoutHash);
            return false;
        }
        RunLaunchContext.Pending = options;
        return true;
    }

    private void OnLeave()
    {
        var session = _session;
        if (session is not null)
        {
            session.ShutdownSession();
            session.QueueFree();
        }
        CoopLaunchContext.Clear();
        Navigate(AppScene.MainMenu);
    }

    private void OnSessionError(string code, string message)
    {
        Say(Localization.T("Network error ({0}): {1}", (object)code, message));
    }

    private void OnMappingStatus(string message) => Say(message);

    private void Say(string text)
    {
        if (_status is not null && IsInstanceValid(_status))
        {
            _status.Text = text;
        }
    }

    private void Navigate(AppScene target)
    {
        if (!GameServices.IsInitialized)
        {
            GD.PushError("[BOOT-003] Services not initialized.");
            return;
        }
        var svc = GameServices.Registry.TryResolve<SceneFlowService>(out var s) ? s : null;
        if (svc is not null)
        {
            svc.Navigate(target);
            return;
        }
        if (GameServices.Flow.TryTransition(target, out var error))
        {
            var path = target switch
            {
                AppScene.Loadout => "res://scenes/contract_select.tscn",
                AppScene.RunLoading => "res://scenes/run.tscn",
                _ => "res://scenes/main_menu.tscn",
            };
            GetTree()?.CallDeferred(SceneTree.MethodName.ChangeSceneToFile, path);
        }
        else if (_status is not null)
        {
            _status.Text = error;
        }
    }
}