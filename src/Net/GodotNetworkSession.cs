using AbyssScav.Protocol;
using Godot;

namespace AbyssScav.Net;

/// <summary>
/// UI-facing network session facade (docs/02 §7-§8). Composes the ENet
/// transport, the engine-independent <see cref="NetworkSessionManager"/>, LAN
/// discovery, and UPnP mapping. Emits signals only; never manipulates scenes.
/// Drive it from <c>_Process</c> (calls <see cref="Poll"/>); all callbacks
/// arrive on the main thread.
/// </summary>
public partial class GodotNetworkSession : Node
{
    [Signal] public delegate void LobbyChangedEventHandler();
    [Signal] public delegate void RunStartedEventHandler();
    [Signal] public delegate void PeerJoinedEventHandler(ulong peerId);
    [Signal] public delegate void PeerLeftEventHandler(ulong peerId);
    [Signal] public delegate void IntentReceivedEventHandler(ulong peerId, byte[] payload);
    [Signal] public delegate void SnapshotReceivedEventHandler(ulong peerId, byte[] payload);
    [Signal] public delegate void SessionErrorEventHandler(string code, string message);
    [Signal] public delegate void MappingStatusEventHandler(string message);

    private EnetTransport? _transport;
    private NetworkSessionManager? _manager;
    private LanDiscoveryService? _discovery;
    private UpnpPortMappingService? _upnp;
    private string _gameVersion = string.Empty;
    private string _catalogHash = string.Empty;
    private bool _built;

    public bool IsActive => _manager?.IsActive ?? false;
    public bool IsHost => _manager?.IsHost ?? false;
    public ulong SessionId => _manager?.SessionId ?? NetLimits.InvalidId;
    public ulong LocalPeerId => _manager?.LocalPeerId ?? NetLimits.InvalidId;

    /// <summary>Identifies this build for handshake validation. Call once before host/join.</summary>
    public void ConfigureSessionIdentity(string gameVersion, string catalogHash)
    {
        _gameVersion = gameVersion?.Trim() ?? string.Empty;
        _catalogHash = catalogHash?.Trim() ?? string.Empty;
    }

    public IReadOnlyList<LobbyPlayer> Players =>
        _manager?.Players ?? Array.Empty<LobbyPlayer>();

    public LobbySettings? Settings => _manager?.Settings;

    /// <summary>Manifest of the started run (null until the host starts).</summary>
    public RunManifest? CurrentManifest => _manager?.ActiveManifest;

    public async Task HostLobbyAsync(LobbySettings settings, string displayName, int port, CancellationToken ct)
    {
        var manager = EnsureBuilt();
        RequireIdentity();
        await manager.HostAsync(
            settings, displayName, _gameVersion, _catalogHash,
            new SessionOptions(port, settings.MaxPlayers, NetLimits.InvalidId), ct).ConfigureAwait(false);
    }

    public async Task JoinLobbyAsync(string host, int port, string displayName, CancellationToken ct)
    {
        var manager = EnsureBuilt();
        RequireIdentity();
        await manager.JoinAsync(
            new SessionAddress(host, port), displayName, _gameVersion, _catalogHash, ct).ConfigureAwait(false);
    }

    public void SetReady(bool ready) => _manager?.SetLocalReady(ready);

    public bool UpdateLobby(LobbySettings settings, out string error)
    {
        error = string.Empty;
        var manager = _manager;
        if (manager is null)
        {
            error = "No active session.";
            return false;
        }

        return manager.UpdateLobby(settings, out error);
    }

    public bool StartRun(RunManifest manifest, out string error)
    {
        error = string.Empty;
        var manager = _manager;
        if (manager is null)
        {
            error = "No active session.";
            return false;
        }

        return manager.TryStartRun(manifest, out error);
    }

    public void SendIntent(ulong peerId, byte[] payload) =>
        _manager?.SendIntent(peerId, payload);

    public void BroadcastIntent(byte[] payload) =>
        _manager?.BroadcastIntent(payload);

    public void BroadcastSnapshot(byte[] payload) =>
        _manager?.BroadcastSnapshot(payload);

    public void StartAdvertise(LanSessionAdvertisement info)
    {
        EnsureBuilt();
        _discovery!.StartAdvertise(info);
    }

    public void StopAdvertise() => _discovery?.StopAdvertise();

    public void StartScan()
    {
        EnsureBuilt();
        _discovery!.StartScan();
    }

    public IReadOnlyList<DiscoveredLanSession> ScanSnapshot() =>
        _discovery?.Snapshot() ?? Array.Empty<DiscoveredLanSession>();

    public void StopScan() => _discovery?.StopScan();

    public async Task MapPortAsync(int port, CancellationToken ct)
    {
        EnsureBuilt();
        var result = await _upnp!.TryMapUdpAsync(port, ct);
        EmitSignal("mapping_status", result.UserMessage);
    }

    public Task UnmapPortAsync(CancellationToken ct) =>
        _upnp?.RemoveMappingAsync(ct) ?? Task.CompletedTask;

    /// <summary>Main-loop pump for transport and discovery. No scene access.</summary>
    public void Poll()
    {
        _manager?.Poll();
        _discovery?.Poll();
    }

    public override void _Process(double delta) => Poll();

    public void ShutdownSession()
    {
        try
        {
            _discovery?.StopAdvertise();
            _discovery?.StopScan();
            _manager?.Shutdown();
            _transport?.Shutdown();
        }
        catch
        {
            // Shutdown never throws out of the scene loop.
        }
    }

    protected void DisposeServices(bool disposing)
    {
        if (!disposing)
        {
            return;
        }

        ShutdownSession();
        _discovery?.Dispose();
        _transport?.Dispose();
        _upnp?.Dispose();
        _discovery = null;
        _transport = null;
        _upnp = null;
        _manager = null;
        _built = false;
    }

    public override void _ExitTree() => DisposeServices(true);

    private NetworkSessionManager EnsureBuilt()
    {
        if (_built && _manager is not null)
        {
            return _manager;
        }

        _transport = new EnetTransport();
        var tree = GetTree();
        if (tree is null)
        {
            throw new InvalidOperationException("GodotNetworkSession must be inside the scene tree before hosting or joining.");
        }

        _transport.AttachMultiplayer(Multiplayer);
        var manager = new NetworkSessionManager();
        manager.AttachTransport(_transport);
        manager.LobbyChanged += () => EmitSignal("lobby_changed");
        manager.RunStarted += _ => EmitSignal("run_started");
        manager.PeerJoined += id => EmitSignal("peer_joined", id);
        manager.PeerLeft += id => EmitSignal("peer_left", id);
        manager.IntentReceived += (id, payload) => EmitSignal("intent_received", id, payload);
        manager.SnapshotReceived += (id, payload) => EmitSignal("snapshot_received", id, payload);
        manager.SessionError += error => EmitSignal("session_error", error.Code, error.Detail);
        _manager = manager;
        _discovery = new LanDiscoveryService();
        _upnp = new UpnpPortMappingService();
        _built = true;
        return manager;
    }

    private void RequireIdentity()
    {
        if (string.IsNullOrEmpty(_gameVersion) || string.IsNullOrEmpty(_catalogHash))
        {
            throw new InvalidOperationException("ConfigureSessionIdentity must be called before host/join.");
        }
    }
}
