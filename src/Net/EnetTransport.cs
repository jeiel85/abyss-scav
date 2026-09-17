using AbyssScav.Protocol;
using Godot;

namespace AbyssScav.Net;

/// <summary>
/// ENet transport adapter (docs/02 §1, docs/16 §1). Bounded to
/// <see cref="NetLimits.MaxPeers"/> peers. All Godot calls must happen on the
/// main thread; <see cref="Poll"/> is driven from the scene loop and only
/// raises data events (never mutates the scene).
/// Engine rules this adapter is built around: a standalone ENet peer is never
/// serviced (no connects, no packets), so a <see cref="SceneMultiplayer"/> must
/// be attached before Host/Join (fail fast otherwise); and raw
/// PutPacket/GetPacket must never be used on an assigned peer because the
/// engine consumes such bytes as RPC commands. All I/O therefore goes through
/// SendBytes and the peer_packet signal, which is the engine's custom-data
/// path. The receive side does not surface ENet channels, so the channel is
/// recovered from the frame's own type field (our stack owns both ends of the
/// frame format); unparseable datagrams are reported reliable and dropped by
/// frame decode.
/// </summary>
public sealed class EnetTransport : INetworkTransport, IDisposable
{
    private const int ReliableChannel = 0;
    private const int SnapshotChannel = 1;

    private ENetMultiplayerPeer? _peer;
    private SceneMultiplayer? _scene;
    private readonly Queue<NetMessage> _inbox = new();
    private TaskCompletionSource<bool>? _joinTcs;
    private bool _joinSeenConnected;
    private bool _subscribed;
    private bool _disposed;

    public bool IsHost { get; private set; }

    public ulong LocalPeerId { get; private set; }

    public event Action<PeerConnectedEvent>? PeerConnected;
    public event Action<PeerDisconnectedEvent>? PeerDisconnected;
    public event Action<NetMessage>? MessageReceived;

    /// <summary>
    /// Attaches the scene multiplayer that services the ENet host and carries
    /// custom-data bytes. Must be a <see cref="SceneMultiplayer"/> (the engine
    /// default). Safe to call before or after Host/Join.
    /// </summary>
    public void AttachMultiplayer(MultiplayerApi multiplayer)
    {
        ThrowIfDisposed();
        if (multiplayer is not SceneMultiplayer scene)
        {
            throw new ArgumentException(
                $"[{NetErrors.TransportCreate}] AttachMultiplayer requires the engine-default SceneMultiplayer.",
                nameof(multiplayer));
        }

        if (!ReferenceEquals(_scene, scene))
        {
            UnsubscribeScene();
            _scene = scene;
        }

        SubscribeScene();
        AssignToMultiplayer();
    }

    public Task HostAsync(SessionOptions options, CancellationToken ct)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();
        if (!SessionValidation.IsValidPort(options.Port))
        {
            throw new ArgumentException($"[{NetErrors.TransportCreate}] Game port out of range.", nameof(options));
        }

        if (options.MaxPeers is < 2 or > NetLimits.MaxPeers)
        {
            throw new ArgumentException(
                $"[{NetErrors.TransportCreate}] Host requires 2-{NetLimits.MaxPeers} peers; solo runs open no transport.",
                nameof(options));
        }

        if (options.SessionId == NetLimits.InvalidId)
        {
            throw new ArgumentException($"[{NetErrors.TransportCreate}] Session id is required.", nameof(options));
        }

        RequireMultiplayer(NetErrors.TransportCreate, "Host");

        ShutdownPeer();
        CancelPendingJoin();
        var peer = new ENetMultiplayerPeer();
        // Two channels are required: 0 reliable, 1 snapshot (docs/02 §4).
        const int channels = 2;
        var error = peer.CreateServer(options.Port, options.MaxPeers - 1, channels);
        if (error != Error.Ok)
        {
            throw new InvalidOperationException($"[{NetErrors.TransportCreate}] Cannot listen on UDP {options.Port}: {error}.");
        }

        _peer = peer;
        IsHost = true;
        LocalPeerId = (ulong)Math.Max(1, peer.GetUniqueId());
        AssignToMultiplayer();
        return Task.CompletedTask;
    }

    public async Task JoinAsync(SessionAddress address, CancellationToken ct)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();
        if (!SessionValidation.IsValidHostName(address.Host))
        {
            throw new ArgumentException($"[{NetErrors.TransportConnect}] Host address is invalid.", nameof(address));
        }

        if (!SessionValidation.IsValidPort(address.Port))
        {
            throw new ArgumentException($"[{NetErrors.TransportConnect}] Game port out of range.", nameof(address));
        }

        RequireMultiplayer(NetErrors.TransportConnect, "Join");

        ShutdownPeer();
        CancelPendingJoin();
        var peer = new ENetMultiplayerPeer();
        // Two channels are required: 0 reliable, 1 snapshot (docs/02 §4).
        const int channels = 2;
        var error = peer.CreateClient(address.Host.Trim(), address.Port, channels);
        if (error != Error.Ok)
        {
            throw new InvalidOperationException($"[{NetErrors.TransportConnect}] Cannot dial {address.Host}:{address.Port}: {error}.");
        }

        _peer = peer;
        IsHost = false;
        LocalPeerId = NetLimits.InvalidId;
        _joinSeenConnected = false;
        AssignToMultiplayer();
        _joinTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(address.EffectiveJoinTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        await using (linked.Token.Register(() => _joinTcs.TrySetCanceled()))
        {
            try
            {
                // Resumes on the caller's context: the continuation reads engine
                // state (GetUniqueId) and must stay on the main thread under Godot.
                await _joinTcs.Task;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                ShutdownPeer();
                CancelPendingJoin();
                throw new TimeoutException($"[{NetErrors.TransportConnect}] No route to {address.Host}:{address.Port} within the join window.");
            }
            catch (OperationCanceledException)
            {
                ShutdownPeer();
                CancelPendingJoin();
                throw;
            }

            if (_joinTcs.Task.Result != true)
            {
                ShutdownPeer();
                CancelPendingJoin();
                throw new InvalidOperationException($"[{NetErrors.TransportConnect}] Connection to {address.Host}:{address.Port} was refused.");
            }
        }

        _joinTcs = null;
        if (_peer is not null)
        {
            LocalPeerId = (ulong)Math.Max(1, _peer.GetUniqueId());
        }
    }

    public void SendReliable(ulong peerId, ReadOnlySpan<byte> payload) =>
        Send(peerId, payload, MultiplayerPeer.TransferModeEnum.Reliable, ReliableChannel);

    public void SendUnreliable(ulong peerId, ReadOnlySpan<byte> payload) =>
        // "Sequenced" snapshots (docs/02 §4) map to UnreliableOrdered: no
        // retransmit, per-channel ordering, latest state wins visually.
        Send(peerId, payload, MultiplayerPeer.TransferModeEnum.UnreliableOrdered, SnapshotChannel);

    /// <summary>
    /// Main-thread pump: completes pending joins and forwards queued
    /// peer_packet bytes as <see cref="NetMessage"/> events. The engine
    /// delivers peer_packet during its own multiplayer poll; this only drains
    /// the queue. No scene access.
    /// </summary>
    public void Poll()
    {
        var peer = _peer;
        if (peer is null || _disposed)
        {
            return;
        }

        var status = peer.GetConnectionStatus();
        if (_joinTcs is not null)
        {
            if (status == MultiplayerPeer.ConnectionStatus.Connected)
            {
                _joinSeenConnected = true;
                _joinTcs.TrySetResult(true);
            }
            else if (status == MultiplayerPeer.ConnectionStatus.Disconnected && !_joinSeenConnected)
            {
                _joinTcs.TrySetResult(false);
            }
        }

        while (_inbox.Count > 0)
        {
            MessageReceived?.Invoke(_inbox.Dequeue());
        }
    }

    public void Shutdown()
    {
        CancelPendingJoin();
        ShutdownPeer();
        _inbox.Clear();
        IsHost = false;
        LocalPeerId = NetLimits.InvalidId;
        _joinSeenConnected = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Shutdown();
        UnsubscribeScene();
        _scene = null;
    }

    private void Send(
        ulong peerId,
        ReadOnlySpan<byte> payload,
        MultiplayerPeer.TransferModeEnum mode,
        int channel)
    {
        ThrowIfDisposed();
        if (_peer is null)
        {
            throw new InvalidOperationException($"[{NetErrors.TransportCreate}] Transport is not running.");
        }

        var scene = _scene ?? throw new InvalidOperationException($"[{NetErrors.TransportCreate}] Transport is not running.");
        if (payload.Length == 0 ||
            payload.Length > NetLimits.AbsoluteMaxPayloadBytes + NetFrame.HeaderSize)
        {
            throw new ArgumentException("Payload exceeds the bounded frame size.", nameof(payload));
        }

        if (peerId != PeerIds.Broadcast && peerId > int.MaxValue)
        {
            throw new ArgumentException("Peer id is outside the engine-addressable range.", nameof(peerId));
        }

        try
        {
            scene.SendBytes(payload.ToArray(), (int)peerId, mode, channel);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"[{NetErrors.TransportCreate}] Send failed: {ex.Message}.", ex);
        }
    }

    private void SubscribeScene()
    {
        var scene = _scene;
        if (scene is null || _subscribed)
        {
            return;
        }

        scene.PeerConnected += OnSceneConnected;
        scene.PeerDisconnected += OnSceneDisconnected;
        scene.PeerPacket += OnScenePacket;
        _subscribed = true;
    }

    private void UnsubscribeScene()
    {
        if (!_subscribed)
        {
            return;
        }

        var scene = _scene;
        _subscribed = false;
        if (scene is null)
        {
            return;
        }

        try
        {
            scene.PeerConnected -= OnSceneConnected;
            scene.PeerDisconnected -= OnSceneDisconnected;
            scene.PeerPacket -= OnScenePacket;
        }
        catch
        {
            // Detach is best-effort and never throws.
        }
    }

    private void OnSceneConnected(long id)
    {
        if (id <= 0)
        {
            return;
        }

        PeerConnected?.Invoke(new PeerConnectedEvent((ulong)id));
    }

    private void OnSceneDisconnected(long id)
    {
        if (id <= 0)
        {
            return;
        }

        PeerDisconnected?.Invoke(new PeerDisconnectedEvent((ulong)id, "peer-disconnected"));
    }

    private void OnScenePacket(long id, byte[] packet)
    {
        if (_disposed || _peer is null || id <= 0 || packet is null)
        {
            return;
        }

        if (packet.Length == 0 ||
            packet.Length > NetLimits.AbsoluteMaxPayloadBytes + NetFrame.HeaderSize)
        {
            return;
        }

        _inbox.Enqueue(new NetMessage((ulong)id, RecoverChannel(packet), packet));
    }

    /// <summary>
    /// Recovers the channel from the frame's own type field: peer_packet does
    /// not surface ENet channels. Our stack owns the frame format on both ends,
    /// so the type byte is authoritative here; undecodable datagrams report
    /// reliable and are dropped by frame decode downstream.
    /// </summary>
    private static TransportChannel RecoverChannel(byte[] packet)
    {
        if (packet.Length >= NetFrame.HeaderSize)
        {
            var type = (MessageType)(packet[2] | (packet[3] << 8));
            if (MessageBounds.IsKnown(type))
            {
                return MessageBounds.IsReliable(type) ? TransportChannel.Reliable : TransportChannel.Unreliable;
            }
        }

        return TransportChannel.Reliable;
    }

    private void ShutdownPeer()
    {
        var peer = _peer;
        _peer = null;
        // Scene events stay subscribed for the attach lifetime (see Dispose):
        // Host/Join cycles must not Silence peer_packet delivery.
        DetachPeerFromScene(peer);
        if (peer is null)
        {
            return;
        }

        try
        {
            peer.Close();
        }
        catch
        {
            // Shutdown is best-effort and never throws.
        }
    }

    private void AssignToMultiplayer()
    {
        var peer = _peer;
        var scene = _scene;
        if (peer is null || scene is null)
        {
            return;
        }

        try
        {
            scene.MultiplayerPeer = peer;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"[{NetErrors.TransportCreate}] Cannot attach ENet peer to the scene multiplayer: {ex.Message}.", ex);
        }
    }

    private void DetachPeerFromScene(ENetMultiplayerPeer? peer)
    {
        var scene = _scene;
        if (scene is null)
        {
            return;
        }

        // Unsubscribe across the replace so peer teardown never surfaces as
        // session events, then restore the attach-lifetime subscription.
        UnsubscribeScene();
        try
        {
            if (peer is not null && ReferenceEquals(scene.MultiplayerPeer, peer))
            {
                scene.MultiplayerPeer = new OfflineMultiplayerPeer();
            }
        }
        catch
        {
            // Detach is best-effort and never throws.
        }
        finally
        {
            if (!_disposed)
            {
                SubscribeScene();
            }
        }
    }

    private void RequireMultiplayer(string code, string operation)
    {
        if (_scene is null)
        {
            throw new InvalidOperationException(
                $"[{code}] {operation} requires AttachMultiplayer first: a standalone ENet peer is never serviced by the engine.");
        }
    }

    private void CancelPendingJoin()
    {
        var pending = _joinTcs;
        _joinTcs = null;
        try
        {
            pending?.TrySetCanceled();
        }
        catch
        {
            // Gate release never throws.
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(EnetTransport));
        }
    }
}
