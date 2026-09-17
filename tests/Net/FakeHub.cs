using AbyssScav.Protocol;

namespace AbyssScav.Net.Tests;

/// <summary>In-memory transport hub for local defensive tests only.</summary>
internal sealed class FakeHub
{
    private readonly Dictionary<ulong, FakeTransport> _peers = new();
    private ulong _nextId = 1;

    public ulong ClaimId()
    {
        var id = _nextId++;
        if (id == NetLimits.InvalidId || id > 64)
        {
            throw new InvalidOperationException("Fake hub exhausted.");
        }

        return id;
    }

    public void Register(FakeTransport transport) => _peers[transport.LocalPeerId] = transport;

    public void Unregister(FakeTransport transport) => _peers.Remove(transport.LocalPeerId);

    public void AnnounceJoin(FakeTransport joined)
    {
        foreach (var peer in _peers.Values)
        {
            if (peer == joined)
            {
                continue;
            }

            if (peer.IsHost)
            {
                peer.RaiseConnected(joined.LocalPeerId);
            }
            else
            {
                peer.RaiseConnected(joined.LocalPeerId);
            }
        }

        joined.RaiseConnected(1);
    }

    public void AnnounceLeave(FakeTransport left)
    {
        foreach (var peer in _peers.Values)
        {
            if (peer != left)
            {
                peer.RaiseDisconnected(left.LocalPeerId);
            }
        }
    }

    public void Deliver(ulong senderId, ulong targetId, TransportChannel channel, byte[] payload)
    {
        if (targetId == PeerIds.Broadcast)
        {
            foreach (var peer in _peers.Values.ToArray())
            {
                if (peer.LocalPeerId != senderId)
                {
                    peer.Enqueue(senderId, channel, payload);
                }
            }

            return;
        }

        if (_peers.TryGetValue(targetId, out var target))
        {
            target.Enqueue(senderId, channel, payload);
        }
    }
}

internal sealed class FakeTransport : INetworkTransport
{
    private readonly FakeHub _hub;
    private readonly Queue<NetMessage> _inbox = new();
    private bool _running;

    public FakeTransport(FakeHub hub)
    {
        _hub = hub;
    }

    public bool IsHost { get; private set; }

    public ulong LocalPeerId { get; private set; }

    public event Action<PeerConnectedEvent>? PeerConnected;
    public event Action<PeerDisconnectedEvent>? PeerDisconnected;
    public event Action<NetMessage>? MessageReceived;

    public Task HostAsync(SessionOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        IsHost = true;
        LocalPeerId = 1;
        _running = true;
        _hub.Register(this);
        return Task.CompletedTask;
    }

    public Task JoinAsync(SessionAddress address, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        IsHost = false;
        LocalPeerId = _hub.ClaimId();
        if (LocalPeerId == 1)
        {
            LocalPeerId = _hub.ClaimId();
        }

        _running = true;
        _hub.Register(this);
        _hub.AnnounceJoin(this);
        return Task.CompletedTask;
    }

    public void SendReliable(ulong peerId, ReadOnlySpan<byte> payload)
    {
        RequireRunning();
        _hub.Deliver(LocalPeerId, peerId, TransportChannel.Reliable, payload.ToArray());
    }

    public void SendUnreliable(ulong peerId, ReadOnlySpan<byte> payload)
    {
        RequireRunning();
        _hub.Deliver(LocalPeerId, peerId, TransportChannel.Unreliable, payload.ToArray());
    }

    /// <summary>Test-only injection: bypasses send-side routing to simulate wire input.</summary>
    public void InjectRaw(ulong claimedSender, TransportChannel channel, byte[] payload) =>
        Enqueue(claimedSender, channel, payload);

    public void Poll()
    {
        while (_inbox.Count > 0)
        {
            MessageReceived?.Invoke(_inbox.Dequeue());
        }
    }

    public void Shutdown()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        _hub.AnnounceLeave(this);
        _hub.Unregister(this);
    }

    internal void Enqueue(ulong sender, TransportChannel channel, byte[] payload)
    {
        if (_running)
        {
            _inbox.Enqueue(new NetMessage(sender, channel, payload));
        }
    }

    internal void RaiseConnected(ulong peerId) => PeerConnected?.Invoke(new PeerConnectedEvent(peerId));

    internal void RaiseDisconnected(ulong peerId) => PeerDisconnected?.Invoke(new PeerDisconnectedEvent(peerId, "fake-leave"));

    private void RequireRunning()
    {
        if (!_running)
        {
            throw new InvalidOperationException("Fake transport is not running.");
        }
    }
}
