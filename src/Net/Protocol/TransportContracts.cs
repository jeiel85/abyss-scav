namespace AbyssScav.Protocol;

/// <summary>Network transport contract (docs/16 §1).</summary>
public interface INetworkTransport
{
    bool IsHost { get; }
    ulong LocalPeerId { get; }
    event Action<PeerConnectedEvent> PeerConnected;
    event Action<PeerDisconnectedEvent> PeerDisconnected;
    event Action<NetMessage> MessageReceived;

    Task HostAsync(SessionOptions options, CancellationToken ct);
    Task JoinAsync(SessionAddress address, CancellationToken ct);
    void SendReliable(ulong peerId, ReadOnlySpan<byte> payload);
    void SendUnreliable(ulong peerId, ReadOnlySpan<byte> payload);
    void Poll();
    void Shutdown();
}

/// <summary>Target every connected peer (host authoritative broadcast).</summary>
public static class PeerIds
{
    public const ulong Broadcast = 0;
}

public enum TransportChannel : byte
{
    Reliable = 0,
    Unreliable = 1,
}

public sealed record PeerConnectedEvent(ulong PeerId);

public sealed record PeerDisconnectedEvent(ulong PeerId, string Reason);

public sealed record NetMessage(ulong SenderPeerId, TransportChannel Channel, byte[] Payload);

public sealed record SessionOptions(
    int Port,
    int MaxPeers,
    ulong SessionId,
    TimeSpan? JoinTimeout = null)
{
    public TimeSpan EffectiveJoinTimeout =>
        JoinTimeout is { TotalSeconds: > 0 and <= 60 } t ? t
        : TimeSpan.FromSeconds(NetLimits.JoinTimeoutSeconds);
}

public sealed record SessionAddress(
    string Host,
    int Port,
    TimeSpan? JoinTimeout = null)
{
    public TimeSpan EffectiveJoinTimeout =>
        JoinTimeout is { TotalSeconds: > 0 and <= 60 } t ? t
        : TimeSpan.FromSeconds(NetLimits.JoinTimeoutSeconds);
}

public static class SessionValidation
{
    public static bool IsValidPort(int port) => port is >= 1024 and <= 65535;

    public static bool IsValidPeerCount(int count) => count is >= 1 and <= NetLimits.MaxPeers;

    public static bool IsValidHostName(string host) =>
        !string.IsNullOrWhiteSpace(host) && host.Trim().Length <= 253;
}
