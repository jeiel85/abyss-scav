namespace AbyssScav.Protocol;

/// <summary>Wire message types. Unknown (0) is never valid on the wire.</summary>
public enum MessageType : ushort
{
    Unknown = 0,
    HandshakeRequest = 1,
    HandshakeResponse = 2,
    LobbyUpdate = 3,
    PlayerReady = 4,
    RunManifest = 5,
    ReconnectToken = 6,
    ReconnectClaim = 7,
    PlayerIntent = 10,
    GameEvent = 11,
    Snapshot = 20,
}

/// <summary>Fixed per-type payload bounds (docs/02 §5). Gameplay never uses JSON.</summary>
public static class MessageBounds
{
    public static int MaxPayload(MessageType type) => type switch
    {
        MessageType.HandshakeRequest => 256,
        MessageType.HandshakeResponse => 192,
        MessageType.LobbyUpdate => 1024,
        MessageType.PlayerReady => 64,
        MessageType.RunManifest => NetLimits.AbsoluteMaxPayloadBytes,
        MessageType.ReconnectToken => 192,
        MessageType.ReconnectClaim => 192,
        MessageType.PlayerIntent => 512,
        MessageType.GameEvent => 1024,
        MessageType.Snapshot => 1024,
        _ => -1,
    };

    public static bool IsKnown(MessageType type) => MaxPayload(type) >= 0;

    public static bool IsReliable(MessageType type) => type switch
    {
        MessageType.HandshakeRequest => true,
        MessageType.HandshakeResponse => true,
        MessageType.LobbyUpdate => true,
        MessageType.PlayerReady => true,
        MessageType.RunManifest => true,
        MessageType.ReconnectToken => true,
        MessageType.ReconnectClaim => true,
        MessageType.PlayerIntent => true,
        MessageType.GameEvent => true,
        MessageType.Snapshot => false,
        _ => false,
    };
}
