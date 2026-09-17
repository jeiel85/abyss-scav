using System.Buffers.Binary;
using System.Text;

namespace AbyssScav.Protocol;

/// <summary>Lobby + token payload codecs. Binary, length-capped, no JSON.</summary>
public static class LobbyCodecs
{
    public static bool TryEncodeReady(bool ready, Span<byte> destination, out int written)
    {
        written = 0;
        if (destination.Length < 1)
        {
            return false;
        }

        destination[0] = ready ? (byte)1 : (byte)0;
        written = 1;
        return true;
    }

    public static bool TryDecodeReady(ReadOnlySpan<byte> payload, out bool ready)
    {
        ready = false;
        if (payload.Length != 1 || payload[0] > 1)
        {
            return false;
        }

        ready = payload[0] == 1;
        return true;
    }

    public static bool TryEncodeToken(string token, Span<byte> destination, out int written)
    {
        written = 0;
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        var bytes = Encoding.UTF8.GetBytes(token);
        if (bytes.Length == 0 || bytes.Length > NetLimits.MaxReasonBytes ||
            destination.Length < 1 + bytes.Length)
        {
            return false;
        }

        destination[0] = (byte)bytes.Length;
        bytes.CopyTo(destination[1..]);
        written = 1 + bytes.Length;
        return true;
    }

    public static bool TryDecodeToken(ReadOnlySpan<byte> payload, out string token)
    {
        token = string.Empty;
        if (payload.Length < 2 || payload[0] == 0 ||
            payload[0] > NetLimits.MaxReasonBytes || 1 + payload[0] != payload.Length)
        {
            return false;
        }

        try
        {
            token = Encoding.UTF8.GetString(payload.Slice(1, payload[0]));
        }
        catch
        {
            return false;
        }

        return !string.IsNullOrEmpty(token);
    }

    public static bool TryEncodeLobby(
        IReadOnlyList<LobbyPlayer> players,
        in LobbySettings settings,
        Span<byte> destination,
        out int written)
    {
        written = 0;
        if (players.Count > NetLimits.MaxPeers)
        {
            return false;
        }

        var lobbyName = settings.LobbyName.Trim();
        if (string.IsNullOrEmpty(lobbyName) ||
            Encoding.UTF8.GetByteCount(lobbyName) > NetLimits.MaxLobbyNameBytes)
        {
            return false;
        }

        var names = new byte[players.Count][];
        for (var i = 0; i < players.Count; i++)
        {
            var name = players[i].DisplayName.Trim();
            if (!HandshakeCodec.IsValidDisplayName(name))
            {
                return false;
            }

            names[i] = Encoding.UTF8.GetBytes(name);
        }

        var nameBytes = Encoding.UTF8.GetBytes(lobbyName);
        var total = 1 + nameBytes.Length + 1 + 1 + 1;
        for (var i = 0; i < players.Count; i++)
        {
            total += 8 + 1 + 1 + 1 + names[i].Length;
        }

        if (total > MessageBounds.MaxPayload(MessageType.LobbyUpdate) ||
            destination.Length < total)
        {
            return false;
        }

        var at = 0;
        destination[at++] = (byte)nameBytes.Length;
        nameBytes.CopyTo(destination[at..]);
        at += nameBytes.Length;
        destination[at++] = (byte)settings.MaxPlayers;
        destination[at++] = settings.JoinAllowed ? (byte)1 : (byte)0;
        destination[at++] = (byte)players.Count;
        for (var i = 0; i < players.Count; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(destination[at..], players[i].PeerId);
            at += 8;
            destination[at++] = players[i].Ready ? (byte)1 : (byte)0;
            destination[at++] = players[i].IsHost ? (byte)1 : (byte)0;
            destination[at++] = (byte)names[i].Length;
            names[i].CopyTo(destination[at..]);
            at += names[i].Length;
        }

        written = at;
        return true;
    }

    public static bool TryDecodeLobby(
        ReadOnlySpan<byte> payload,
        out LobbySettings? settings,
        out List<LobbyPlayer>? players)
    {
        settings = null;
        players = null;
        var at = 0;
        if (!TryReadString(payload, ref at, NetLimits.MaxLobbyNameBytes, out var lobbyName))
        {
            return false;
        }

        if (at + 3 > payload.Length)
        {
            return false;
        }

        var maxPlayers = payload[at++];
        var joinAllowed = payload[at++];
        var count = payload[at++];
        if (joinAllowed > 1 || count > NetLimits.MaxPeers ||
            maxPlayers is < 1 or > NetLimits.MaxPeers || count > maxPlayers)
        {
            return false;
        }

        var list = new List<LobbyPlayer>(count);
        var hostSeen = false;
        for (var i = 0; i < count; i++)
        {
            if (at + 11 > payload.Length)
            {
                return false;
            }

            var peerId = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(at, 8));
            at += 8;
            var ready = payload[at++];
            var host = payload[at++];
            if (ready > 1 || host > 1 || peerId == NetLimits.InvalidId)
            {
                return false;
            }

            if (!TryReadString(payload, ref at, NetLimits.MaxDisplayNameBytes, out var name) ||
                !HandshakeCodec.IsValidDisplayName(name))
            {
                return false;
            }

            if (list.Any(p => p.PeerId == peerId))
            {
                return false;
            }

            var isHost = host == 1;
            hostSeen |= isHost;
            list.Add(new LobbyPlayer(peerId, name, ready == 1, isHost));
        }

        if (at != payload.Length || (list.Count > 0 && !hostSeen))
        {
            return false;
        }

        settings = new LobbySettings(lobbyName, maxPlayers, joinAllowed == 1);
        players = list;
        return true;
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
