using System.Buffers.Binary;
using System.Text;

namespace AbyssScav.Protocol;

/// <summary>LAN discovery contract (docs/16 §2).</summary>
public interface ILanDiscoveryService
{
    void StartAdvertise(LanSessionAdvertisement info);
    void StopAdvertise();
    void StartScan();
    IReadOnlyList<DiscoveredLanSession> Snapshot();
    void StopScan();
}

public sealed record LanSessionAdvertisement(
    string LobbyName,
    string GameVersion,
    ushort ProtocolVersion,
    int Players,
    int MaxPlayers,
    int GamePort,
    ulong SessionId);

public sealed record DiscoveredLanSession(
    string LobbyName,
    string GameVersion,
    ushort ProtocolVersion,
    int Players,
    int MaxPlayers,
    int GamePort,
    ulong SessionId,
    string SenderAddress,
    DateTimeOffset LastSeen);

/// <summary>
/// Bounded LAN beacon schema on the dedicated discovery port (docs/02 §2B).
/// Layout: magic "ABYSS1" (6) + protocol u16 + gamePort u16 + players u8 +
/// maxPlayers u8 + sessionId u64 + sentUnixSec u64 + gameVersion u8+bytes +
/// lobby u8+bytes. Total never exceeds <see cref="NetLimits.BeaconMaxBytes"/>.
/// </summary>
public static class LanBeaconCodec
{
    private static readonly byte[] Magic = "ABYSS1"u8.ToArray();
    private const int FixedSize = 6 + 2 + 2 + 1 + 1 + 8 + 8;

    public static bool TryEncode(in LanSessionAdvertisement info, long sentUnixSeconds, Span<byte> destination, out int written)
    {
        written = 0;
        if (!IsAdvertisable(info))
        {
            return false;
        }

        var game = Encoding.UTF8.GetBytes(info.GameVersion);
        var lobby = Encoding.UTF8.GetBytes(info.LobbyName.Trim());
        var total = FixedSize + 1 + game.Length + 1 + lobby.Length;
        if (total > NetLimits.BeaconMaxBytes || destination.Length < total)
        {
            return false;
        }

        Magic.CopyTo(destination);
        var at = 6;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[at..], info.ProtocolVersion);
        at += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[at..], (ushort)info.GamePort);
        at += 2;
        destination[at++] = (byte)info.Players;
        destination[at++] = (byte)info.MaxPlayers;
        BinaryPrimitives.WriteUInt64LittleEndian(destination[at..], info.SessionId);
        at += 8;
        BinaryPrimitives.WriteInt64LittleEndian(destination[at..], sentUnixSeconds);
        at += 8;
        destination[at++] = (byte)game.Length;
        game.CopyTo(destination[at..]);
        at += game.Length;
        destination[at++] = (byte)lobby.Length;
        lobby.CopyTo(destination[at..]);
        at += lobby.Length;
        written = at;
        return true;
    }

    public static bool TryDecode(ReadOnlySpan<byte> packet, long nowUnixSeconds, out LanSessionAdvertisement? info)
    {
        info = null;
        if (packet.Length < FixedSize + 2 || packet.Length > NetLimits.BeaconMaxBytes)
        {
            return false;
        }

        if (!packet[..6].SequenceEqual(Magic))
        {
            return false;
        }

        var at = 6;
        var protocol = BinaryPrimitives.ReadUInt16LittleEndian(packet[at..]);
        at += 2;
        var gamePort = BinaryPrimitives.ReadUInt16LittleEndian(packet[at..]);
        at += 2;
        var players = packet[at++];
        var maxPlayers = packet[at++];
        var sessionId = BinaryPrimitives.ReadUInt64LittleEndian(packet[at..]);
        at += 8;
        var sent = BinaryPrimitives.ReadInt64LittleEndian(packet[at..]);
        at += 8;

        // TTL validation: drop stale beacons and absurd future stamps.
        var age = nowUnixSeconds - sent;
        if (age < -NetLimits.BeaconFutureSkewSeconds || age > NetLimits.BeaconTtlSeconds)
        {
            return false;
        }

        if (!TryReadString(packet, ref at, NetLimits.MaxGameVersionBytes, out var gameVersion) ||
            !TryReadString(packet, ref at, NetLimits.MaxLobbyNameBytes, out var lobbyName) ||
            at != packet.Length)
        {
            return false;
        }

        if (sessionId == NetLimits.InvalidId ||
            !SessionValidation.IsValidPort(gamePort) ||
            players > maxPlayers ||
            maxPlayers is < 1 or > NetLimits.MaxPeers)
        {
            return false;
        }

        info = new LanSessionAdvertisement(
            lobbyName, gameVersion, protocol, players, maxPlayers, gamePort, sessionId);
        return true;
    }

    public static bool IsAdvertisable(in LanSessionAdvertisement info)
    {
        if (string.IsNullOrWhiteSpace(info.LobbyName) ||
            info.LobbyName.Trim().Length > NetLimits.MaxLobbyNameChars ||
            Encoding.UTF8.GetByteCount(info.LobbyName.Trim()) > NetLimits.MaxLobbyNameBytes)
        {
            return false;
        }

        if (string.IsNullOrEmpty(info.GameVersion) ||
            Encoding.UTF8.GetByteCount(info.GameVersion) > NetLimits.MaxGameVersionBytes)
        {
            return false;
        }

        return info.SessionId != NetLimits.InvalidId &&
            SessionValidation.IsValidPort(info.GamePort) &&
            info.Players >= 0 &&
            info.Players <= info.MaxPlayers &&
            info.MaxPlayers is >= 1 and <= NetLimits.MaxPeers;
    }

    private static bool TryReadString(ReadOnlySpan<byte> src, ref int at, int max, out string value)
    {
        value = string.Empty;
        if (at >= src.Length)
        {
            return false;
        }

        var count = src[at++];
        if (count == 0 || count > max || at + count > src.Length)
        {
            return false;
        }

        try
        {
            value = Encoding.UTF8.GetString(src.Slice(at, count));
        }
        catch
        {
            return false;
        }

        at += count;
        return !string.IsNullOrEmpty(value);
    }
}

/// <summary>Engine-independent discovered-session table with TTL expiry.</summary>
public sealed class LanSessionTable
{
    private readonly Dictionary<string, DiscoveredLanSession> _sessions = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _clock;

    public LanSessionTable(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public void Upsert(DiscoveredLanSession session)
    {
        if (session.SessionId == NetLimits.InvalidId)
        {
            return;
        }

        _sessions[Key(session)] = session;
    }

    public IReadOnlyList<DiscoveredLanSession> Snapshot()
    {
        Expire();
        return _sessions.Values
            .OrderBy(s => s.LobbyName, StringComparer.Ordinal)
            .ToArray();
    }

    public void Clear() => _sessions.Clear();

    private void Expire()
    {
        var now = _clock();
        List<string>? stale = null;
        foreach (var (key, session) in _sessions)
        {
            if ((now - session.LastSeen).TotalSeconds > NetLimits.BeaconTtlSeconds * 2)
            {
                (stale ??= new()).Add(key);
            }
        }

        if (stale is not null)
        {
            foreach (var key in stale)
            {
                _sessions.Remove(key);
            }
        }
    }

    private static string Key(DiscoveredLanSession session) =>
        $"{session.SenderAddress}:{session.GamePort}:{session.SessionId}";
}
