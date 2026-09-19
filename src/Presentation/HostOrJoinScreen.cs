using AbyssScav.App;
using AbyssScav.Domain;
using AbyssScav.Foundation;
using AbyssScav.Net;
using AbyssScav.Protocol;
using Godot;

namespace AbyssScav.Presentation;

/// <summary>
/// Co-op entry screen (docs/02 §2): host a LAN lobby, join by direct IP, or
/// pick a session from the LAN discovery list. Creates the root-parented
/// <see cref="GodotNetworkSession"/> (survives scene changes), then moves to the
/// lobby. Failures show reasons; a refused host/join never fakes success.
/// </summary>
public partial class HostOrJoinScreen : Control
{
    private LineEdit? _displayName;
    private LineEdit? _lobbyName;
    private SpinBox? _maxPlayers;
    private SpinBox? _hostPort;
    private LineEdit? _joinHost;
    private SpinBox? _joinPort;
    private ItemList? _lanList;
    private Label? _status;
    private CancellationTokenSource? _cts;
    private GodotNetworkSession? _session;
    private string _catalogHash = string.Empty;
    private bool _scanning;
    private float _lanRefresh;
    private List<DiscoveredLanSession> _lanSessions = new();

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        // Defensive: a session should never survive into this screen (the main
        // menu tears it down), but never leak one if it does.
        if (CoopLaunchContext.Session is { } stale)
        {
            stale.ShutdownSession();
            stale.QueueFree();
            CoopLaunchContext.Clear();
        }

        if (!ContentCatalog.TryBuild(out var catalog, out var errors) || catalog is null)
        {
            BuildError(Localization.T("Content catalog failed: {0}", (object)string.Join("; ", errors)));
            return;
        }
        _catalogHash = catalog.CatalogHash;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        BuildUi();
    }

    public override void _ExitTree()
    {
        try { _cts?.Cancel(); }
        catch { }
        finally { _cts?.Dispose(); _cts = null; }
        // The session node survives into the lobby; only detach this screen's
        // handlers so a freed screen never receives signals.
        if (_session is not null)
        {
            _session.SessionError -= OnSessionError;
            _session.MappingStatus -= OnMappingStatus;
        }
    }

    public override void _Process(double delta)
    {
        if (!_scanning || _session is null || _lanList is null) return;
        _lanRefresh -= (float)delta;
        if (_lanRefresh > 0f) return;
        _lanRefresh = 0.5f;
        RefreshLanList();
    }

    private static Color Ink() => new("#071622");
    private static Color Parchment() => new("#dae4df");
    private static Color Cyan() => new("#71d9d1");
    private static Color Amber() => new("#dfa44d");

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
        var bg = new ColorRect { Color = Ink() };
        bg.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(bg);

        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(center);

        var panel = new PanelContainer();
        panel.CustomMinimumSize = new Vector2(640, 0);
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
        box.AddThemeConstantOverride("separation", 6);
        panel.AddChild(box);

        var title = new Label { Text = Localization.T("CO-OP — HOST OR JOIN"), HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 24);
        title.AddThemeColorOverride("font_color", Parchment());
        box.AddChild(title);

        var sub = new Label { Text = Localization.T("LAN or direct IP. No central server."), HorizontalAlignment = HorizontalAlignment.Center };
        sub.AddThemeFontSizeOverride("font_size", 13);
        sub.AddThemeColorOverride("font_color", Cyan());
        box.AddChild(sub);

        var scroll = new ScrollContainer { FollowFocus = true };
        scroll.CustomMinimumSize = new Vector2(600, 380);
        box.AddChild(scroll);

        var content = new VBoxContainer();
        content.AddThemeConstantOverride("separation", 6);
        content.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(content);

        var nameLabel = new Label { Text = Localization.T("Display name (shown to other players)") };
        nameLabel.AddThemeColorOverride("font_color", Parchment());
        content.AddChild(nameLabel);
        _displayName = new LineEdit { Text = "Diver", MaxLength = 24, CustomMinimumSize = new Vector2(0, 32) };
        content.AddChild(_displayName);

        var hostHeader = new Label { Text = Localization.T("HOST A LOBBY"), HorizontalAlignment = HorizontalAlignment.Center };
        hostHeader.AddThemeFontSizeOverride("font_size", 16);
        hostHeader.AddThemeColorOverride("font_color", Cyan());
        content.AddChild(hostHeader);

        var lobbyLabel = new Label { Text = Localization.T("Lobby name") };
        lobbyLabel.AddThemeColorOverride("font_color", Parchment());
        content.AddChild(lobbyLabel);
        _lobbyName = new LineEdit { Text = "Abyss Scav Lobby", MaxLength = 32, CustomMinimumSize = new Vector2(0, 32) };
        content.AddChild(_lobbyName);

        var hostRow = new HBoxContainer();
        hostRow.AddThemeConstantOverride("separation", 8);
        content.AddChild(hostRow);
        _maxPlayers = MakeSpin(1, NetLimits.MaxPeers, 2, Localization.T("Max players"));
        _hostPort = MakeSpin(1024, 65535, NetLimits.GamePort, Localization.T("Game port"));
        hostRow.AddChild(_maxPlayers);
        hostRow.AddChild(_hostPort);
        var hostButton = new Button { Text = Localization.T("Host Lobby"), CustomMinimumSize = new Vector2(160, 36) };
        hostButton.Pressed += OnHostPressed;
        hostRow.AddChild(hostButton);

        var joinHeader = new Label { Text = Localization.T("JOIN BY DIRECT IP"), HorizontalAlignment = HorizontalAlignment.Center };
        joinHeader.AddThemeFontSizeOverride("font_size", 16);
        joinHeader.AddThemeColorOverride("font_color", Cyan());
        content.AddChild(joinHeader);

        var joinRow = new HBoxContainer();
        joinRow.AddThemeConstantOverride("separation", 8);
        content.AddChild(joinRow);
        _joinHost = new LineEdit { Text = "127.0.0.1", PlaceholderText = Localization.T("IPv4 or hostname"), CustomMinimumSize = new Vector2(220, 32) };
        _joinPort = MakeSpin(1024, 65535, NetLimits.GamePort, Localization.T("Port"));
        var joinButton = new Button { Text = Localization.T("Join"), CustomMinimumSize = new Vector2(120, 36) };
        joinButton.Pressed += () => JoinAsync(_joinHost?.Text.Trim() ?? "", (int)(_joinPort?.Value ?? NetLimits.GamePort));
        joinRow.AddChild(_joinHost);
        joinRow.AddChild(_joinPort);
        joinRow.AddChild(joinButton);

        var lanHeader = new Label { Text = Localization.T("LAN DISCOVERY"), HorizontalAlignment = HorizontalAlignment.Center };
        lanHeader.AddThemeFontSizeOverride("font_size", 16);
        lanHeader.AddThemeColorOverride("font_color", Cyan());
        content.AddChild(lanHeader);

        var lanRow = new HBoxContainer();
        lanRow.AddThemeConstantOverride("separation", 8);
        content.AddChild(lanRow);
        var scanButton = new Button { Text = Localization.T("Scan LAN"), CustomMinimumSize = new Vector2(140, 36) };
        scanButton.Pressed += OnScanPressed;
        var joinSelected = new Button { Text = Localization.T("Join Selected"), CustomMinimumSize = new Vector2(160, 36) };
        joinSelected.Pressed += OnJoinSelected;
        lanRow.AddChild(scanButton);
        lanRow.AddChild(joinSelected);
        content.AddChild(lanRow);

        _lanList = new ItemList { CustomMinimumSize = new Vector2(0, 120) };
        content.AddChild(_lanList);

        _status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _status.AddThemeFontSizeOverride("font_size", 13);
        _status.AddThemeColorOverride("font_color", Amber());
        content.AddChild(_status);

        var footer = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        footer.AddThemeConstantOverride("separation", 10);
        box.AddChild(footer);
        var back = new Button { Text = Localization.T("Back"), CustomMinimumSize = new Vector2(140, 38) };
        back.Pressed += () =>
        {
            TearDownSession();
            Navigate(AppScene.MainMenu);
        };
        footer.AddChild(back);
        hostButton.GrabFocus();
    }

    private static SpinBox MakeSpin(int min, int max, int value, string label)
    {
        var spin = new SpinBox
        {
            MinValue = min,
            MaxValue = max,
            Value = value,
            CustomMinimumSize = new Vector2(150, 36),
            TooltipText = label,
        };
        return spin;
    }

    private GodotNetworkSession CreateSession()
    {
        // Reuse a live session (e.g. one created for LAN scanning) instead of
        // stacking a second node; the discovery socket and ENet transport
        // coexist on the same session node.
        if (_session is not null) return _session;
        var session = new GodotNetworkSession { Name = "CoopSession" };
        GetTree()?.Root.AddChild(session);
        CoopLaunchContext.Session = session;
        session.ConfigureSessionIdentity(GameVersion.Current, _catalogHash);
        session.SessionError += OnSessionError;
        session.MappingStatus += OnMappingStatus;
        _session = session;
        return session;
    }

    private void StopScanIfActive(GodotNetworkSession session)
    {
        if (!_scanning) return;
        _scanning = false;
        session.StopScan();
    }

    private void TearDownSession()
    {
        var session = _session;
        _session = null;
        if (session is null) return;
        session.SessionError -= OnSessionError;
        session.MappingStatus -= OnMappingStatus;
        session.ShutdownSession();
        session.QueueFree();
        if (ReferenceEquals(CoopLaunchContext.Session, session)) CoopLaunchContext.Clear();
    }

    private async void OnHostPressed()
    {
        if (_status is null || !IsInstanceValid(_status)) return;
        var displayName = (_displayName?.Text ?? "").Trim();
        var lobbyName = (_lobbyName?.Text ?? "").Trim();
        if (displayName.Length == 0)
        {
            _status.Text = Localization.T("Enter a display name.");
            return;
        }
        if (lobbyName.Length == 0)
        {
            _status.Text = Localization.T("Enter a lobby name.");
            return;
        }
        var maxPlayers = (int)(_maxPlayers?.Value ?? 2);
        var port = (int)(_hostPort?.Value ?? NetLimits.GamePort);
        var session = CreateSession();
        StopScanIfActive(session);
        try
        {
            _status.Text = Localization.T("Hosting lobby…");
            await session.HostLobbyAsync(new LobbySettings(lobbyName, maxPlayers, true), displayName, port, _cts?.Token ?? CancellationToken.None);
            if (!IsInstanceValid(this)) return;
            session.StartAdvertise(new LanSessionAdvertisement(lobbyName, GameVersion.Current, DomainConstants.ProtocolVersion, 1, maxPlayers, port, session.SessionId));
            _ = MapPortWithStatusAsync(session, port);
            Navigate(AppScene.Lobby);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!IsInstanceValid(this)) return;
            _status.Text = Localization.T("Host failed: {0}", (object)ex.Message);
            TearDownSession();
        }
    }

    private async void JoinAsync(string host, int port)
    {
        if (_status is null || !IsInstanceValid(_status)) return;
        var displayName = (_displayName?.Text ?? "").Trim();
        if (displayName.Length == 0)
        {
            _status.Text = Localization.T("Enter a display name.");
            return;
        }
        if (host.Length == 0)
        {
            _status.Text = Localization.T("Enter a host address.");
            return;
        }
        var session = CreateSession();
        StopScanIfActive(session);
        try
        {
            _status.Text = Localization.T("Joining {0}:{1}…", (object)host, port);
            await session.JoinLobbyAsync(host, port, displayName, _cts?.Token ?? CancellationToken.None);
            if (!IsInstanceValid(this)) return;
            Navigate(AppScene.Lobby);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!IsInstanceValid(this)) return;
            _status.Text = Localization.T("Join failed: {0}", (object)ex.Message);
            TearDownSession();
        }
    }

    private void OnScanPressed()
    {
        var session = _session;
        if (session is null)
        {
            // Scanning needs a live session node (it owns the discovery socket).
            session = CreateSession();
        }
        _scanning = !_scanning;
        if (_scanning)
        {
            session.StartScan();
            _lanRefresh = 0f;
            RefreshLanList();
        }
        else
        {
            session.StopScan();
        }
    }

    private void RefreshLanList()
    {
        if (_lanList is null || _session is null) return;
        _lanSessions = _session.ScanSnapshot().ToList();
        _lanList.Clear();
        foreach (var s in _lanSessions)
        {
            _lanList.AddItem($"{s.LobbyName} ({s.Players}/{s.MaxPlayers}) — {s.SenderAddress}:{s.GamePort}");
            _lanList.SetItemMetadata(_lanList.ItemCount - 1, _lanSessions.Count - 1);
        }
    }

    private void OnJoinSelected()
    {
        if (_lanList is null || _lanList.GetSelectedItems().Length == 0) return;
        var idx = (int)_lanList.GetItemMetadata(_lanList.GetSelectedItems()[0]);
        if (idx < 0 || idx >= _lanSessions.Count) return;
        var s = _lanSessions[idx];
        JoinAsync(s.SenderAddress, s.GamePort);
    }

    private async Task MapPortWithStatusAsync(GodotNetworkSession session, int port)
    {
        try
        {
            await session.MapPortAsync(port, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // The mapping_status signal already carries the user-facing message.
            GD.PushWarning(ex.Message);
        }
    }

    private void OnSessionError(string code, string message)
    {
        if (_status is not null && IsInstanceValid(_status))
        {
            _status.Text = Localization.T("Network error ({0}): {1}", (object)code, message);
        }
    }

    private void OnMappingStatus(string message)
    {
        if (_status is not null && IsInstanceValid(_status))
        {
            _status.Text = message;
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
                AppScene.Lobby => "res://scenes/lobby.tscn",
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