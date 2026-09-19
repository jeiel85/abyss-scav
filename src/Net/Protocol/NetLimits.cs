namespace AbyssScav.Protocol;

/// <summary>Bounded backendless networking constants (docs/02).</summary>
public static class NetLimits
{
    /// <summary>Default gameplay UDP port (docs/02 §2C).</summary>
    public const int GamePort = 24857;

    /// <summary>Discovery beacon UDP port, separate from the game port (docs/16 §2).</summary>
    public const int DiscoveryPort = 24858;

    /// <summary>Total players in a session including the host (docs/02 §1).</summary>
    public const int MaxPeers = 4;

    public const ushort ProtocolVersion = 1;

    /// <summary>
    /// Binary frame header size: u16 + u16 + u32 + u64 + u64 + u16 = 26 bytes.
    /// <see cref="NetFrame"/> offsets must stay contiguous and exactly this long.
    /// </summary>
    public const int FrameHeaderSizeBytes = 2 + 2 + 4 + 8 + 8 + 2;

    /// <summary>Absolute payload ceiling; every message type has a smaller fixed bound.</summary>
    public const int AbsoluteMaxPayloadBytes = 4096;

    public const int MaxDisplayNameChars = 24;
    public const int MaxDisplayNameBytes = 96;
    public const int MaxGameVersionBytes = 32;
    public const int MaxCatalogHashBytes = 128;
    public const int MaxReasonBytes = 128;
    public const int MaxLobbyNameChars = 48;
    public const int MaxLobbyNameBytes = 144;
    public const int MaxModifiers = 8;
    public const int MaxModifierBytes = 48;
    public const int MaxIdBytes = 64;

    /// <summary>World snapshot ceilings (docs/02 §9). The world is deterministic
    /// per manifest, so the snapshot matches creatures/loot/nodes by index and
    /// sends compact bitmasks; these caps bound the wire size.</summary>
    public const int MaxLootSpawns = 32;
    public const int MaxCreatures = 16;
    public const int MaxWorldNodes = 32;
    public const int MaxObjectives = 8;

    /// <summary>LAN beacon ceiling (docs/16 §2: discovery size is bounded).</summary>
    public const int BeaconMaxBytes = 256;

    /// <summary>Beacons older than this are dropped (seconds).</summary>
    public const int BeaconTtlSeconds = 5;

    /// <summary>Beacons stamped this far in the future are dropped (clock-skew guard, seconds).</summary>
    public const int BeaconFutureSkewSeconds = 30;

    /// <summary>Beacon broadcast cadence (seconds).</summary>
    public const int BeaconIntervalSeconds = 1;

    /// <summary>Per-peer reconnect grace window (docs/02 §11, seconds).</summary>
    public const int ReconnectGraceSeconds = 120;

    /// <summary>Host-loss reconnect window before settlement (docs/02 §12, seconds).</summary>
    public const int HostLossWindowSeconds = 8;

    /// <summary>Default join handshake timeout (seconds).</summary>
    public const int JoinTimeoutSeconds = 10;

    /// <summary>Inbound reliable packets per peer per second.</summary>
    public const double ReliablePerSecond = 30.0;

    public const double ReliableBurst = 32.0;

    /// <summary>Inbound unreliable packets per peer per second.</summary>
    public const double UnreliablePerSecond = 20.0;

    public const double UnreliableBurst = 24.0;

    /// <summary>Zero ids are never valid on the wire.</summary>
    public const ulong InvalidId = 0;
}
