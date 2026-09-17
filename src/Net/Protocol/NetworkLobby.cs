using System.Security.Cryptography;

namespace AbyssScav.Protocol;

/// <summary>Host-authoritative lobby knobs synced to every client.</summary>
public sealed record LobbySettings(string LobbyName, int MaxPlayers, bool JoinAllowed);

/// <summary>One lobby seat. Identity is the session peer id, never an IP address.</summary>
public sealed record LobbyPlayer(ulong PeerId, string DisplayName, bool Ready, bool IsHost);

/// <summary>
/// Engine-independent lobby/session manager usable by UI (docs/02 §7-§8).
/// Owns framing, strict inbound validation, ready flow, run-start manifest
/// broadcast, and the reliable intent/event + unreliable snapshot channels.
/// Attaches to any <see cref="INetworkTransport"/>; domain wiring stays out.
/// </summary>
public sealed class NetworkSessionManager
{
    private readonly Func<DateTimeOffset> _clock;
    private readonly Dictionary<ulong, LobbyPlayer> _players = new();
    private readonly InboundSequenceFilter _sequences = new();
    private readonly PeerRateLimiter _rates = new();
    private readonly ReconnectTokenStore _tokens;
    private readonly Dictionary<ulong, string> _pendingNames = new();

    private INetworkTransport? _transport;
    private uint _nextSequence = 1;
    private TaskCompletionSource<HandshakeResponse>? _joinTcs;
    private string? _localToken;
    private bool _running;

    public NetworkSessionManager(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _tokens = new ReconnectTokenStore(_clock);
    }

    public event Action? LobbyChanged;
    public event Action<RunManifest>? RunStarted;
    public event Action<ulong, byte[]>? IntentReceived;
    public event Action<ulong, byte[]>? SnapshotReceived;
    public event Action<ulong, byte[]>? EventReceived;
    public event Action<ulong>? PeerJoined;
    public event Action<ulong>? PeerLeft;
    public event Action<NetError>? SessionError;

    public bool IsHost { get; private set; }
    public bool IsActive => _transport is not null;
    public bool HasStartedRun => _running;
    public ulong SessionId { get; private set; }
    public ulong LocalPeerId => _transport?.LocalPeerId ?? NetLimits.InvalidId;
    public LobbySettings? Settings { get; private set; }
    public RunManifest? ActiveManifest { get; private set; }
    public string? LocalReconnectToken => _localToken;
    public IReadOnlyList<LobbyPlayer> Players => _players.Values
        .OrderBy(p => p.PeerId).ToArray();

    /// <summary>Subscribes to a transport. At most one session per manager.</summary>
    public void AttachTransport(INetworkTransport transport)
    {
        if (_transport is not null)
        {
            throw new InvalidOperationException("A transport is already attached.");
        }

        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _transport.PeerConnected += OnPeerConnected;
        _transport.PeerDisconnected += OnPeerDisconnected;
        _transport.MessageReceived += OnMessage;
    }

    public async Task HostAsync(
        LobbySettings settings,
        string displayName,
        string gameVersion,
        string catalogHash,
        SessionOptions options,
        CancellationToken ct)
    {
        RequireTransport();
        ValidateLobbyInput(settings, displayName, gameVersion, catalogHash);
        if (!SessionValidation.IsValidPort(options.Port))
        {
            throw new ArgumentException("Game port out of range.", nameof(options));
        }

        IsHost = true;
        SessionId = options.SessionId != NetLimits.InvalidId ? options.SessionId : NewSessionId();
        Settings = Normalize(settings);
        ActiveManifest = null;
        _running = false;

        // No ConfigureAwait(false): continuations touch session state on the
        // caller's (main-thread, under Godot) context.
        await _transport!.HostAsync(
            options with { MaxPeers = Settings.MaxPlayers, SessionId = SessionId }, ct);

        _players.Clear();
        _sequences.Clear();
        _rates.Clear();
        _pendingNames.Clear();
        _tokens.EndRun();
        _players[LocalPeerId] = new LobbyPlayer(LocalPeerId, displayName.Trim(), false, true);
        BroadcastLobby();
    }

    public async Task JoinAsync(
        SessionAddress address,
        string displayName,
        string gameVersion,
        string catalogHash,
        CancellationToken ct,
        string? reconnectToken = null)
    {
        RequireTransport();
        if (!SessionValidation.IsValidHostName(address.Host))
        {
            throw new ArgumentException("Host address is invalid.", nameof(address));
        }

        if (!SessionValidation.IsValidPort(address.Port))
        {
            throw new ArgumentException("Game port out of range.", nameof(address));
        }

        if (!HandshakeCodec.IsValidDisplayName(displayName))
        {
            throw new ArgumentException("Display name is invalid.", nameof(displayName));
        }

        IsHost = false;
        SessionId = NetLimits.InvalidId;
        ActiveManifest = null;
        _running = false;
        _players.Clear();
        _sequences.Clear();
        _rates.Clear();
        _localToken = null;

        // No ConfigureAwait(false): continuations send on the transport and must
        // stay on the caller's (main-thread, under Godot) context.
        await _transport!.JoinAsync(address, ct);

        var request = new HandshakeRequest(NetLimits.ProtocolVersion, gameVersion, catalogHash, displayName.Trim());
        Span<byte> payload = stackalloc byte[MessageBounds.MaxPayload(MessageType.HandshakeRequest)];
        if (!HandshakeCodec.TryEncodeRequest(request, payload, out var requestSize))
        {
            throw new InvalidOperationException("Handshake request could not be encoded.");
        }

        // Pre-session handshake frames use session zero (docs/02 §6); the adopted
        // session id arrives in the host's response. Arm the gate before sending
        // so synchronously-delivered answers cannot be lost.
        _joinTcs = new TaskCompletionSource<HandshakeResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        SendFramed(MessageType.HandshakeRequest, PeerIds.Broadcast, NetLimits.InvalidId,
            payload[..requestSize], TransportChannel.Reliable);

        using var timeout = new CancellationTokenSource(address.EffectiveJoinTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        await using (linked.Token.Register(() => _joinTcs.TrySetCanceled()))
        {
            HandshakeResponse response;
            try
            {
                response = await _joinTcs.Task;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"[{NetErrors.HandshakeRejected}] Host did not answer the handshake in time.");
            }

            if (!response.Accepted)
            {
                throw new InvalidOperationException($"[{NetErrors.HandshakeRejected}] {response.Message}");
            }

            SessionId = response.SessionId;
        }

        _joinTcs = null;
        if (!string.IsNullOrEmpty(reconnectToken))
        {
            SendTokenMessage(MessageType.ReconnectClaim, PeerIds.Broadcast, reconnectToken);
        }
    }

    /// <summary>Client ready toggle; host applies locally. Broadcasts on host.</summary>
    public void SetLocalReady(bool ready)
    {
        RequireActiveSession();
        if (IsHost)
        {
            if (_players.TryGetValue(LocalPeerId, out var self))
            {
                _players[LocalPeerId] = self with { Ready = ready };
                BroadcastLobby();
            }

            return;
        }

        Span<byte> payload = stackalloc byte[8];
        if (LobbyCodecs.TryEncodeReady(ready, payload, out var size))
        {
            SendFramed(MessageType.PlayerReady, HostPeerId, SessionId, payload[..size], TransportChannel.Reliable);
        }
    }

    /// <summary>Host-only lobby update.</summary>
    public bool UpdateLobby(LobbySettings settings, out string error)
    {
        error = string.Empty;
        RequireActiveSession();
        if (!IsHost)
        {
            error = "Only the host can change lobby settings.";
            return false;
        }

        try
        {
            Settings = Normalize(settings);
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }

        BroadcastLobby();
        return true;
    }

    /// <summary>
    /// Host-only run start (docs/02 §8): every seat must be ready.
    /// Broadcasts the manifest; clients raise <see cref="RunStarted"/>.
    /// </summary>
    public bool TryStartRun(RunManifest manifest, out string error)
    {
        error = string.Empty;
        RequireActiveSession();
        if (!IsHost)
        {
            error = "Only the host can start the run.";
            return false;
        }

        if (_players.Count == 0 || _players.Values.Any(p => !p.Ready))
        {
            error = "Every player must be ready before the run starts.";
            return false;
        }

        Span<byte> payload = stackalloc byte[MessageBounds.MaxPayload(MessageType.RunManifest)];
        if (!RunManifestCodec.TryEncode(manifest, payload, out var size))
        {
            error = "Run manifest is too large or has empty fields.";
            return false;
        }

        ActiveManifest = manifest;
        _running = true;
        BroadcastFramed(MessageType.RunManifest, payload[..size], TransportChannel.Reliable);
        RunStarted?.Invoke(manifest);
        return true;
    }

    public void SendIntent(ulong peerId, ReadOnlySpan<byte> payload) =>
        SendGamePayload(MessageType.PlayerIntent, peerId, payload);

    public void BroadcastIntent(ReadOnlySpan<byte> payload) =>
        SendGamePayload(MessageType.PlayerIntent, PeerIds.Broadcast, payload);

    public void SendEvent(ulong peerId, ReadOnlySpan<byte> payload) =>
        SendGamePayload(MessageType.GameEvent, peerId, payload);

    public void BroadcastSnapshot(ReadOnlySpan<byte> payload) =>
        SendGamePayload(MessageType.Snapshot, PeerIds.Broadcast, payload);

    /// <summary>Main-thread pump. Never touches scene state; only raises events.</summary>
    public void Poll() => _transport?.Poll();

    public void Shutdown()
    {
        try
        {
            _joinTcs?.TrySetCanceled();
            _transport?.Shutdown();
        }
        finally
        {
            _joinTcs = null;
            _players.Clear();
            _pendingNames.Clear();
            _tokens.EndRun();
            SessionId = NetLimits.InvalidId;
            Settings = null;
            ActiveManifest = null;
            IsHost = false;
            _running = false;
            _localToken = null;
        }
    }

    internal ReconnectTokenStore Tokens => _tokens;

    private void SendGamePayload(MessageType type, ulong peerId, ReadOnlySpan<byte> payload)
    {
        RequireActiveSession();
        if (SessionId == NetLimits.InvalidId)
        {
            throw new InvalidOperationException("Join handshake has not completed.");
        }

        if (payload.Length > MessageBounds.MaxPayload(type))
        {
            throw new ArgumentException("Payload exceeds the per-type bound.", nameof(payload));
        }

        SendFramed(type, peerId, SessionId,
            payload, MessageBounds.IsReliable(type) ? TransportChannel.Reliable : TransportChannel.Unreliable);
    }

    private void SendFramed(
        MessageType type, ulong peerId, ulong sessionId, ReadOnlySpan<byte> payload, TransportChannel channel)
    {
        var frame = new byte[NetFrame.HeaderSize + payload.Length];
        if (!NetFrame.TryEncode(type, _nextSequence++, SessionIdOr(sessionId), LocalPeerId, payload, frame, out var size))
        {
            throw new InvalidOperationException("Frame could not be encoded.");
        }

        if (channel == TransportChannel.Reliable)
        {
            _transport!.SendReliable(peerId, frame.AsSpan(0, size));
        }
        else
        {
            _transport!.SendUnreliable(peerId, frame.AsSpan(0, size));
        }
    }

    private ulong SessionIdOr(ulong sessionId) => sessionId;

    private void SendTokenMessage(MessageType type, ulong peerId, string token)
    {
        Span<byte> payload = stackalloc byte[MessageBounds.MaxPayload(type)];
        if (LobbyCodecs.TryEncodeToken(token, payload, out var size))
        {
            SendFramed(type, peerId, SessionId, payload[..size], TransportChannel.Reliable);
        }
    }

    private void OnPeerConnected(PeerConnectedEvent e)
    {
        if (IsHost)
        {
            _pendingNames[e.PeerId] = string.Empty;
        }
    }

    private void OnPeerDisconnected(PeerDisconnectedEvent e)
    {
        _sequences.Forget(e.PeerId);
        _rates.Forget(e.PeerId);
        _pendingNames.Remove(e.PeerId);
        if (IsHost && _players.Remove(e.PeerId))
        {
            BroadcastLobby();
            PeerLeft?.Invoke(e.PeerId);
        }
        else if (!IsHost && e.PeerId == HostPeerId)
        {
            SessionError?.Invoke(new NetError(NetErrors.HostLost, "net.host.lost",
                "Host connection lost. Settle with the last secured cargo."));
        }
    }

    private void OnMessage(NetMessage message)
    {
        if (_transport is null)
        {
            return;
        }

        if (NetFrame.TryDecode(message.Payload, out var header, out _) != FrameDecodeError.None)
        {
            return;
        }

        if (MessageBounds.IsReliable(header.MessageType) != (message.Channel == TransportChannel.Reliable))
        {
            return;
        }

        if (header.SessionId == NetLimits.InvalidId)
        {
            // Pre-session handshake only; anything else on session zero is dropped.
            if (header.MessageType is not (MessageType.HandshakeRequest or MessageType.HandshakeResponse))
            {
                return;
            }

            if (header.SenderPeer != message.SenderPeerId ||
                !_sequences.Accept(message.SenderPeerId, header.Sequence))
            {
                return;
            }

            HandleHandshake(NetFrame.Payload(message.Payload).ToArray(), message.SenderPeerId, header);
            return;
        }

        if (SessionId != NetLimits.InvalidId &&
            NetFrame.ValidateRouted(header, message.SenderPeerId, SessionId, _sequences) != FrameRouteError.None)
        {
            return;
        }

        if (SessionId == NetLimits.InvalidId)
        {
            // Client has not adopted a session yet: only handshake responses apply.
            return;
        }

        if (!_rates.TryConsume(message.SenderPeerId, header.MessageType, _clock()))
        {
            return;
        }

        Dispatch(header, NetFrame.Payload(message.Payload).ToArray(), message.SenderPeerId);
    }

    private void HandleHandshake(byte[] payload, ulong sender, NetFrameHeader header)
    {
        if (IsHost)
        {
            if (header.MessageType != MessageType.HandshakeRequest ||
                !HandshakeCodec.TryDecodeRequest(payload, out var request) || request is null)
            {
                return;
            }

            var policy = new HostHandshakePolicy(
                NetLimits.ProtocolVersion, _hostCatalog, _players.Count, Settings?.MaxPlayers ?? 1,
                Settings?.JoinAllowed ?? false);
            var response = HandshakeValidator.Validate(request, policy, SessionId);
            Span<byte> responsePayload = stackalloc byte[MessageBounds.MaxPayload(MessageType.HandshakeResponse)];
            if (HandshakeCodec.TryEncodeResponse(response, responsePayload, out var responseSize))
            {
                SendFramed(MessageType.HandshakeResponse, sender, NetLimits.InvalidId,
                    responsePayload[..responseSize], TransportChannel.Reliable);
            }

            if (!response.Accepted)
            {
                return;
            }

            if (_players.Count >= (Settings?.MaxPlayers ?? NetLimits.MaxPeers))
            {
                return;
            }

            _players[sender] = new LobbyPlayer(sender, request.DisplayName.Trim(), false, false);
            var token = _tokens.Issue(sender, SessionId, request.DisplayName.Trim());
            SendTokenMessage(MessageType.ReconnectToken, sender, token);
            BroadcastLobby();
            PeerJoined?.Invoke(sender);
            return;
        }

        if (header.MessageType != MessageType.HandshakeResponse ||
            !HandshakeCodec.TryDecodeResponse(payload, out var handshake) || handshake is null)
        {
            return;
        }

        if (handshake.Accepted && handshake.SessionId != NetLimits.InvalidId)
        {
            // Adopt immediately so later frames in the same pump validate.
            SessionId = handshake.SessionId;
        }

        _joinTcs?.TrySetResult(handshake);
    }

    private void Dispatch(NetFrameHeader header, byte[] payload, ulong sender)
    {
        switch (header.MessageType)
        {
            case MessageType.LobbyUpdate:
                if (!IsHost && LobbyCodecs.TryDecodeLobby(payload, out var settings, out var players) &&
                    settings is not null && players is not null)
                {
                    Settings = settings;
                    _players.Clear();
                    foreach (var entry in players)
                    {
                        _players[entry.PeerId] = entry;
                    }

                    LobbyChanged?.Invoke();
                }

                break;
            case MessageType.PlayerReady:
                if (IsHost && LobbyCodecs.TryDecodeReady(payload, out var ready) &&
                    _players.TryGetValue(sender, out var player))
                {
                    _players[sender] = player with { Ready = ready };
                    BroadcastLobby();
                }

                break;
            case MessageType.RunManifest:
                if (!IsHost && RunManifestCodec.TryDecode(payload, out var manifest) && manifest is not null)
                {
                    ActiveManifest = manifest;
                    _running = true;
                    RunStarted?.Invoke(manifest);
                }

                break;
            case MessageType.ReconnectToken:
                if (!IsHost && LobbyCodecs.TryDecodeToken(payload, out var token))
                {
                    _localToken = token;
                }

                break;
            case MessageType.ReconnectClaim:
                if (IsHost && LobbyCodecs.TryDecodeToken(payload, out var claim) &&
                    _tokens.TryTakeover(claim, SessionId, out var oldPeer, out var oldName))
                {
                    // Take over the previous seat if it still exists; otherwise
                    // re-add it (disconnect cleans seats immediately, ready resets
                    // to false so a stale ready can never survive a reconnect).
                    if (_players.TryGetValue(oldPeer, out var seat))
                    {
                        _players.Remove(oldPeer);
                        _players[sender] = seat with { PeerId = sender };
                    }
                    else if (HandshakeCodec.IsValidDisplayName(oldName))
                    {
                        _players[sender] = new LobbyPlayer(sender, oldName, false, false);
                    }
                    else
                    {
                        break;
                    }

                    _tokens.RevokePeer(oldPeer, SessionId);
                    var rotated = _tokens.Issue(sender, SessionId, _players[sender].DisplayName);
                    SendTokenMessage(MessageType.ReconnectToken, sender, rotated);
                    BroadcastLobby();
                    PeerJoined?.Invoke(sender);
                }

                break;
            case MessageType.PlayerIntent:
                IntentReceived?.Invoke(sender, payload);
                break;
            case MessageType.GameEvent:
                EventReceived?.Invoke(sender, payload);
                break;
            case MessageType.Snapshot:
                SnapshotReceived?.Invoke(sender, payload);
                break;
        }
    }

    private void BroadcastLobby()
    {
        if (Settings is null)
        {
            return;
        }

        var players = Players;
        Span<byte> payload = stackalloc byte[MessageBounds.MaxPayload(MessageType.LobbyUpdate)];
        if (LobbyCodecs.TryEncodeLobby(players, Settings, payload, out var size))
        {
            BroadcastFramed(MessageType.LobbyUpdate, payload[..size], TransportChannel.Reliable);
        }

        LobbyChanged?.Invoke();
    }

    private void BroadcastFramed(MessageType type, ReadOnlySpan<byte> payload, TransportChannel channel)
    {
        foreach (var peerId in _players.Keys.ToArray())
        {
            if (peerId == LocalPeerId)
            {
                continue;
            }

            try
            {
                SendFramed(type, peerId, SessionId, payload, channel);
            }
            catch
            {
                // One unreachable peer must not break the broadcast loop.
            }
        }
    }

    private string _hostCatalog = string.Empty;

    private void ValidateLobbyInput(
        LobbySettings settings, string displayName, string gameVersion, string catalogHash)
    {
        Normalize(settings);
        if (!HandshakeCodec.IsValidDisplayName(displayName))
        {
            throw new ArgumentException("Display name is invalid.", nameof(displayName));
        }

        if (string.IsNullOrEmpty(gameVersion) || string.IsNullOrEmpty(catalogHash))
        {
            throw new ArgumentException("Game version and catalog hash are required.");
        }

        _hostCatalog = catalogHash;
    }

    private static LobbySettings Normalize(LobbySettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.LobbyName) ||
            settings.LobbyName.Trim().Length > NetLimits.MaxLobbyNameChars)
        {
            throw new ArgumentException("Lobby name is invalid.", nameof(settings));
        }

        if (!SessionValidation.IsValidPeerCount(settings.MaxPlayers))
        {
            throw new ArgumentException("Max players must be 1-4.", nameof(settings));
        }

        return settings with { LobbyName = settings.LobbyName.Trim() };
    }

    private void RequireTransport()
    {
        if (_transport is null)
        {
            throw new InvalidOperationException("No transport attached.");
        }
    }

    private void RequireActiveSession()
    {
        RequireTransport();
        if (SessionId == NetLimits.InvalidId && !IsHost)
        {
            throw new InvalidOperationException("No active session.");
        }
    }

    private static ulong HostPeerId => 1;

    private static ulong NewSessionId()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        var id = BitConverter.ToUInt64(bytes);
        return id == NetLimits.InvalidId ? 1 : id;
    }
}
