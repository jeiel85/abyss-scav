using AbyssScav.App;
using AbyssScav.Domain;
using AbyssScav.Foundation;
using AbyssScav.Infra.Logging;
using AbyssScav.Net;
using AbyssScav.Presentation;
using AbyssScav.Protocol;
using Godot;
using SysQuat = System.Numerics.Quaternion;
using SysVec = System.Numerics.Vector3;

namespace AbyssScav.Gameplay;

/// <summary>
/// In-run co-op link (docs/02 §9.5, §11, §12), owned by <see cref="RunController"/>.
/// <list type="bullet">
/// <item>Ship transform replication: clients send their own pose to the host at
/// 15 Hz (unreliable); the host validates each pose (<see cref="ShipPoseGate"/>)
/// and republishes every player's latest pose inside the world snapshot.</item>
/// <item>Remote subs render as collider-less <see cref="RemoteSubmarineProxy"/>
/// nodes driven by <see cref="PoseInterpolationBuffer"/> (120 ms delay, velocity
/// extrapolation ≤ 250 ms, large-correction blend, teleport snap) with a name
/// tag, plus teammate markers on the sonar scope.</item>
/// <item>Client host-link watch: a <see cref="HostLinkMonitor"/> opens the 8 s
/// reconnect window on host loss, retries a token reconnect inside it, and
/// raises <see cref="HostLinkLost"/> when it expires so the controller settles.</item>
/// </list>
/// Scene-only glue: all rules live in the engine-independent Domain/Protocol types.
/// </summary>
public partial class CoopRunLink : Node
{
    /// <summary>A proxy with no fresh pose for this long is hidden.</summary>
    public const double HideAfterSeconds = 3.0;

    /// <summary>A proxy with no fresh pose for this long is freed (the reconnect window is far shorter on the client side).</summary>
    public const double ForgetAfterSeconds = 30.0;

    private sealed class RemoteShip
    {
        public readonly PoseInterpolationBuffer Buffer = new();
        public RemoteSubmarineProxy? Proxy;
        public ShipPoseWire? Latest;
        public double LatestTime;
        public bool LinkLost;
        public bool HasDisplayed;
        public SysVec Displayed;
    }

    private readonly Dictionary<ulong, RemoteShip> _remotes = new();
    private readonly byte[] _poseBuffer = new byte[ShipPoseCodec.MessageBytes];
    private readonly List<(Vector3 Position, string Name, bool LinkLost)> _sonarMates = new();

    private GodotNetworkSession? _session;
    private GeneratedWorld? _world;
    private SubmarineController? _localSub;
    private Func<ShipPoseFlags>? _localFlags;
    private Func<byte>? _localHull;
    private Func<float>? _sonarRange;
    private TeammateSonarOverlay? _overlay;
    private ShipPoseGate? _gate;
    private HostLinkMonitor? _monitor;
    private bool _isHost;
    private bool _monitoring;
    private bool _lostRaised;
    private bool _reconnectInFlight;
    private double _nextReconnectAttempt;
    private bool _teleportPending;
    private int _rejectedPoses;
    private CancellationTokenSource? _reconnectCts;

    /// <summary>Client: the reconnect window expired; settle host loss now.</summary>
    public event Action? HostLinkLost;

    /// <summary>Client: a token reconnect restored the session inside the window.</summary>
    public event Action? HostLinkRestored;

    /// <summary>Client: the reconnect window is running.</summary>
    public bool IsReconnecting => _monitor?.State == HostLinkState.Reconnecting;

    /// <summary>Client: seconds left in the reconnect window.</summary>
    public double ReconnectSecondsLeft => _monitor?.WindowRemaining(Now) ?? 0.0;

    private static double Now => Time.GetTicksUsec() / 1_000_000.0;

    /// <summary>Wires the link to the session, the local ship and the sonar scope.</summary>
    public void Setup(
        GodotNetworkSession session,
        GeneratedWorld world,
        SubmarineController localSub,
        Func<ShipPoseFlags> localFlags,
        Func<byte> localHull,
        Func<float> sonarRange,
        SonarDisplay? sonar)
    {
        _session = session;
        _world = world;
        _localSub = localSub;
        _localFlags = localFlags;
        _localHull = localHull;
        _sonarRange = sonarRange;
        _isHost = session.IsHost;
        if (sonar is not null)
        {
            _overlay = new TeammateSonarOverlay { Name = "TeammateOverlay" };
            sonar.AddChild(_overlay);
        }

        if (_isHost)
        {
            _gate = new ShipPoseGate(world);
            session.ShipPoseReceived += OnShipPose;
            session.PeerLeft += OnPeerLeft;
            session.PeerReconnected += OnPeerReconnected;
        }
        else
        {
            _monitor = new HostLinkMonitor();
            _monitoring = true;
            session.SessionError += OnSessionError;
            session.RunStarted += OnManifestAgain;
        }
    }

    public override void _ExitTree()
    {
        _reconnectCts?.Cancel();
        _reconnectCts?.Dispose();
        _reconnectCts = null;
        var session = _session;
        if (session is null || !IsInstanceValid(session))
        {
            return;
        }

        if (_isHost)
        {
            session.ShipPoseReceived -= OnShipPose;
            session.PeerLeft -= OnPeerLeft;
            session.PeerReconnected -= OnPeerReconnected;
        }
        else
        {
            session.SessionError -= OnSessionError;
            session.RunStarted -= OnManifestAgain;
        }
    }

    /// <summary>Client: the local run ended; stop watching the host link.</summary>
    public void StopMonitoring()
    {
        _monitoring = false;
        _reconnectCts?.Cancel();
    }

    /// <summary>Client: a valid snapshot or event from the host arrived.</summary>
    public void NotifyHostHeard() => _monitor?.OnHostHeard(Now);

    /// <summary>Client: the emergency winch relocated the ship; flag the next pose.</summary>
    public void MarkTeleported() => _teleportPending = true;

    /// <summary>Client: sends this ship's pose to the host (caller paces at 15 Hz).</summary>
    public void SendLocalPose()
    {
        var session = _session;
        if (_isHost || session is null || !session.IsActive || session.SessionId == NetLimits.InvalidId ||
            _monitor?.State != HostLinkState.Connected)
        {
            return;
        }

        var pose = BuildLocalPose(0);
        if (pose is null)
        {
            return;
        }

        if (!ShipPoseCodec.TryEncodeMessage(session.SessionId, pose, _poseBuffer, out var written))
        {
            return;
        }

        try
        {
            session.SendShipPose(_poseBuffer.AsSpan(0, written).ToArray());
            _teleportPending = false;
        }
        catch (InvalidOperationException ex)
        {
            // Transport already down: the host-link monitor owns the recovery path.
            GodotLogBridge.Warn(GameServices.Logger, "[coop] pose send skipped: " + ex.Message, NetErrors.TransportCreate);
        }
    }

    /// <summary>
    /// Host: every player's latest validated pose for the world snapshot — the
    /// host's own ship plus each client heard within <see cref="HideAfterSeconds"/>.
    /// </summary>
    public IReadOnlyList<ShipPoseWire> BuildShipList()
    {
        var list = new List<ShipPoseWire>(NetLimits.MaxPeers);
        var session = _session;
        if (!_isHost || session is null)
        {
            return list;
        }

        var own = BuildLocalPose(session.LocalPeerId);
        if (own is not null)
        {
            list.Add(own);
        }

        var now = Now;
        foreach (var (_, remote) in _remotes)
        {
            if (list.Count >= NetLimits.MaxPeers)
            {
                break;
            }

            if (remote.Latest is not null && !remote.LinkLost && now - remote.LatestTime <= HideAfterSeconds)
            {
                list.Add(remote.Latest);
            }
        }

        return list;
    }

    /// <summary>Freshness bound for authority checks against an accepted pose (15 Hz sends).</summary>
    public const double AcceptedPoseMaxAgeSeconds = 1.5;

    /// <summary>
    /// Host: the requester's last host-validated position (domain space), if
    /// fresh. Extraction is authorized against this, never against a position
    /// the client only claims in its intent.
    /// </summary>
    public bool TryGetAcceptedPosition(ulong peerId, out SysVec position)
    {
        position = default;
        return _isHost && _gate is not null && _gate.TryGetAccepted(peerId, Now, AcceptedPoseMaxAgeSeconds, out position);
    }

    /// <summary>
    /// Client: feeds the ship poses of an applied host snapshot. The host sends
    /// the full live ship list every snapshot, so a teammate missing from it
    /// (left, or re-keyed to a new peer id by a reconnect takeover) is retired
    /// at once instead of lingering as a phantom proxy.
    /// </summary>
    public void ApplyShips(IReadOnlyList<ShipPoseWire> ships)
    {
        var session = _session;
        if (_isHost || session is null)
        {
            return;
        }

        var now = Now;
        var present = new HashSet<ulong>();
        foreach (var ship in ships)
        {
            if (ship.PeerId == session.LocalPeerId || ship.PeerId == NetLimits.InvalidId)
            {
                continue;
            }

            present.Add(ship.PeerId);
            Ingest(ship.PeerId, ship, now);
        }

        foreach (var peerId in _remotes.Keys.Where(id => !present.Contains(id)).ToList())
        {
            var remote = _remotes[peerId];
            if (remote.Proxy is not null && IsInstanceValid(remote.Proxy))
            {
                remote.Proxy.QueueFree();
            }

            _remotes.Remove(peerId);
        }
    }

    public override void _Process(double delta)
    {
        var now = Now;
        UpdateProxies(now, (float)delta);
        if (!_isHost && _monitoring)
        {
            WatchHostLink(now);
        }
    }

    // ------------------------------------------------------------------ host

    private void OnShipPose(ulong peerId, byte[] payload)
    {
        var session = _session;
        if (session is null || _gate is null || peerId > uint.MaxValue)
        {
            return;
        }

        if (!ShipPoseCodec.TryDecodeMessage(payload, out var sessionId, out var pose) || pose is null ||
            sessionId != session.SessionId)
        {
            return;
        }

        var now = Now;
        var verdict = _gate.Evaluate(peerId, now,
            new SysVec(pose.X, pose.Y, pose.Z),
            new SysQuat(pose.Qx, pose.Qy, pose.Qz, pose.Qw),
            new SysVec(pose.Vx, pose.Vy, pose.Vz),
            (pose.Flags & ShipPoseFlags.Teleported) != 0);
        if (verdict != PoseVerdict.Accepted)
        {
            // Bounded log: the first refusals are diagnostic, a flood is not.
            if (_rejectedPoses++ < 20 && verdict != PoseVerdict.TooFrequent)
            {
                GodotLogBridge.Warn(GameServices.Logger, $"[coop] pose from peer {peerId} refused: {verdict}.", NetErrors.FrameRejected);
            }

            return;
        }

        // Never trust the claimed owner: the transport sender is authoritative.
        Ingest(peerId, pose with { PeerId = (uint)peerId }, now);
        if (_remotes.TryGetValue(peerId, out var remote))
        {
            remote.LinkLost = false;
        }
    }

    private void OnPeerLeft(ulong peerId)
    {
        if (_remotes.TryGetValue(peerId, out var remote))
        {
            remote.LinkLost = true;
        }
    }

    private void OnPeerReconnected(ulong oldPeer, ulong newPeer)
    {
        _gate?.Rekey(oldPeer, newPeer);
        if (_remotes.Remove(oldPeer, out var remote))
        {
            remote.LinkLost = false;
            if (_remotes.Remove(newPeer, out var duplicate) && duplicate.Proxy is not null && IsInstanceValid(duplicate.Proxy))
            {
                duplicate.Proxy.QueueFree();
            }

            if (remote.Proxy is not null && IsInstanceValid(remote.Proxy))
            {
                remote.Proxy.Name = $"Teammate_{newPeer}";
            }

            _remotes[newPeer] = remote;
        }

        GodotLogBridge.Info(GameServices.Logger, $"[coop] peer {oldPeer} reconnected as {newPeer}; ship entity taken over.");
    }

    // ---------------------------------------------------------------- client

    private void OnSessionError(string code, string message)
    {
        if (code == NetErrors.HostLost)
        {
            _monitor?.OnHostDisconnected(Now);
            GodotLogBridge.Warn(GameServices.Logger, "[coop] host link lost; reconnect window open.", NetErrors.HostLost);
        }
    }

    /// <summary>
    /// A manifest arriving mid-run (after a reconnect takeover) must describe
    /// this very run; anything else means the host moved on and the window ends.
    /// </summary>
    private void OnManifestAgain()
    {
        var manifest = _session?.CurrentManifest;
        var world = _world;
        if (manifest is null || world is null || !_monitoring)
        {
            return;
        }

        if (manifest.RunSeed != world.RunSeed ||
            !string.Equals(manifest.LayoutHash, world.LayoutHash, StringComparison.Ordinal))
        {
            GodotLogBridge.Warn(GameServices.Logger, "[coop] host re-sent a different run manifest; treating the host as lost.", NetErrors.ManifestMismatch);
            _monitor?.Abandon("manifest-mismatch");
        }
    }

    private void WatchHostLink(double now)
    {
        var monitor = _monitor;
        if (monitor is null)
        {
            return;
        }

        var state = monitor.Update(now);
        if (state == HostLinkState.Lost)
        {
            if (!_lostRaised)
            {
                _lostRaised = true;
                _monitoring = false;
                _reconnectCts?.Cancel();
                GodotLogBridge.Warn(GameServices.Logger, $"[coop] host lost ({monitor.LossCause}); reconnect window expired.", NetErrors.HostLost);
                HostLinkLost?.Invoke();
            }

            return;
        }

        if (state == HostLinkState.Reconnecting && !_reconnectInFlight && now >= _nextReconnectAttempt &&
            _session is { CanReconnect: true })
        {
            TryReconnectAsync(monitor.WindowRemaining(now));
        }
    }

    private async void TryReconnectAsync(double windowSeconds)
    {
        var session = _session;
        if (session is null || windowSeconds <= 0.05)
        {
            return;
        }

        _reconnectInFlight = true;
        _reconnectCts?.Dispose();
        _reconnectCts = new CancellationTokenSource();
        var ct = _reconnectCts.Token;
        try
        {
            GodotLogBridge.Info(GameServices.Logger, $"[coop] reconnect attempt ({windowSeconds:F1}s left in the window).");
            await session.ReconnectAsync(TimeSpan.FromSeconds(windowSeconds), ct);
            if (!IsInstanceValid(this) || ct.IsCancellationRequested)
            {
                return;
            }

            if (_monitor is not null && _monitor.OnReconnected(Now))
            {
                GodotLogBridge.Info(GameServices.Logger, "[coop] host link restored by token reconnect.");
                HostLinkRestored?.Invoke();
            }
            else
            {
                // Settlement already began: a late success must not resume play.
                session.ShutdownSession();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!IsInstanceValid(this))
            {
                return;
            }

            GodotLogBridge.Warn(GameServices.Logger, "[coop] reconnect attempt failed: " + ex.Message, NetErrors.ReconnectFailed);
            _nextReconnectAttempt = Now + 1.0;
        }
        finally
        {
            _reconnectInFlight = false;
        }
    }

    // ----------------------------------------------------------------- shared

    private ShipPoseWire? BuildLocalPose(ulong peerId)
    {
        var sub = _localSub;
        if (sub is null || !IsInstanceValid(sub) || peerId > uint.MaxValue)
        {
            return null;
        }

        var p = sub.GlobalPosition;
        var q = sub.GlobalTransform.Basis.GetRotationQuaternion();
        var v = sub.LinearVelocity;
        var flags = _localFlags?.Invoke() ?? ShipPoseFlags.None;
        if (_teleportPending)
        {
            flags |= ShipPoseFlags.Teleported;
        }

        var pose = new ShipPoseWire((uint)peerId, p.X, p.Y, p.Z, q.X, q.Y, q.Z, q.W, v.X, v.Y, v.Z,
            _localHull?.Invoke() ?? 100, flags);
        return ShipPoseCodec.IsWellFormed(pose) ? pose : null;
    }

    private void Ingest(ulong peerId, ShipPoseWire pose, double now)
    {
        if (!_remotes.TryGetValue(peerId, out var remote))
        {
            if (_remotes.Count >= NetLimits.MaxPeers * 2)
            {
                return;
            }

            remote = new RemoteShip();
            _remotes[peerId] = remote;
        }

        remote.Latest = pose;
        remote.LatestTime = now;
        remote.Buffer.Push(new ShipPoseSample(now,
            new SysVec(pose.X, pose.Y, pose.Z),
            new SysQuat(pose.Qx, pose.Qy, pose.Qz, pose.Qw),
            new SysVec(pose.Vx, pose.Vy, pose.Vz)));
    }

    private void UpdateProxies(double now, float dt)
    {
        _sonarMates.Clear();
        foreach (var peerId in _remotes.Keys.ToArray())
        {
            var remote = _remotes[peerId];
            var age = now - remote.LatestTime;
            if (remote.Latest is null || age > ForgetAfterSeconds)
            {
                if (remote.Proxy is not null && IsInstanceValid(remote.Proxy))
                {
                    remote.Proxy.QueueFree();
                }

                _remotes.Remove(peerId);
                continue;
            }

            var gone = (remote.Latest.Flags & (ShipPoseFlags.Extracted | ShipPoseFlags.Failed)) != 0;
            var visible = !gone && age <= HideAfterSeconds;
            if (!visible)
            {
                if (remote.Proxy is not null && IsInstanceValid(remote.Proxy))
                {
                    remote.Proxy.Visible = false;
                }

                continue;
            }

            if (!remote.Buffer.TrySample(now, out var sample))
            {
                continue;
            }

            var proxy = remote.Proxy;
            if (proxy is null || !IsInstanceValid(proxy))
            {
                proxy = new RemoteSubmarineProxy { Name = $"Teammate_{peerId}" };
                GetParent()?.AddChild(proxy);
                remote.Proxy = proxy;
                remote.HasDisplayed = false;
            }

            var snap = remote.Buffer.ConsumeSnap() || !remote.HasDisplayed;
            remote.Displayed = snap ? sample.Position : PoseCorrection.Step(remote.Displayed, sample.Position, dt);
            remote.HasDisplayed = true;
            var rotation = new Quaternion(sample.Rotation.X, sample.Rotation.Y, sample.Rotation.Z, sample.Rotation.W).Normalized();
            proxy.GlobalTransform = new Transform3D(new Basis(rotation), WorldBuilder.ToG(remote.Displayed));
            proxy.Visible = true;
            var name = DisplayNameOf(peerId);
            proxy.SetDisplayName(name);
            proxy.SetLinkLost(remote.LinkLost);
            _sonarMates.Add((proxy.GlobalPosition, name, remote.LinkLost));
        }

        var sub = _localSub;
        if (_overlay is not null && IsInstanceValid(_overlay) && sub is not null && IsInstanceValid(sub))
        {
            // Same heading convention as RunController.RefreshHud (bow = -Z).
            var heading = MathF.Atan2(-sub.GlobalTransform.Basis.Z.X, -sub.GlobalTransform.Basis.Z.Z);
            _overlay.SetTeammates(_sonarMates, sub.GlobalPosition, heading, _sonarRange?.Invoke() ?? 450f);
        }
    }

    private string DisplayNameOf(ulong peerId)
    {
        var session = _session;
        if (session is not null)
        {
            foreach (var p in session.Players)
            {
                if (p.PeerId == peerId)
                {
                    return p.DisplayName;
                }
            }
        }

        return peerId == 1 ? Localization.T("Host") : Localization.T("Diver {0}", peerId);
    }
}
