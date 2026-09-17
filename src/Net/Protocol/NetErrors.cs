namespace AbyssScav.Protocol;

/// <summary>Error domain codes for networking (docs/16 §11: NET-*).</summary>
public static class NetErrors
{
    public const string TransportCreate = "NET-001";
    public const string TransportConnect = "NET-002";
    public const string HandshakeRejected = "NET-003";
    public const string FrameRejected = "NET-004";
    public const string LobbyFull = "NET-005";
    public const string ManifestMismatch = "NET-006";
    public const string RateLimited = "NET-007";
    public const string ReconnectFailed = "NET-008";
    public const string HostLost = "NET-009";
    public const string DiscoveryMalformed = "NET-010";
    public const string PortMappingUnavailable = "NET-011";
}

/// <summary>Structured network error: code + localization key + diagnostic context.</summary>
public sealed record NetError(string Code, string LocalizationKey, string Detail);
