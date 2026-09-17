namespace AbyssScav.Protocol;

/// <summary>UPnP port-mapping contract (docs/16 §3). Failure is never fatal.</summary>
public interface IPortMappingService
{
    Task<PortMappingResult> TryMapUdpAsync(int port, CancellationToken ct);
    Task RemoveMappingAsync(CancellationToken ct);
}

public enum PortMappingStatus
{
    Mapped,
    GatewayNotFound,
    MappingFailed,
    NotAttempted,
    Removed,
}

public sealed record PortMappingResult(
    PortMappingStatus Status,
    string ExternalAddress,
    string UserMessage);

public static class PortMappingCopy
{
    public const string Ready = "Automatic port mapping: Ready";
    public const string ManualRequired = "Internet requires router configuration: forward UDP 24857 manually, or play on LAN.";
    public const string CgnatNote = "Direct connection may not work behind CGNAT; a user-chosen VPN overlay is an alternative.";
    public const string LanOnly = "LAN only";
}
