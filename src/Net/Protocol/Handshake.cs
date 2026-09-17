using System.Text;

namespace AbyssScav.Protocol;

/// <summary>Human-readable handshake rejection reasons.</summary>
public enum HandshakeRejectReason : byte
{
    None = 0,
    ProtocolMismatch = 1,
    CatalogMismatch = 2,
    LobbyFull = 3,
    JoinClosed = 4,
    NameInvalid = 5,
    Malformed = 6,
}

/// <summary>Client -&gt; host handshake (docs/02 §6). Binary, length-capped.</summary>
public sealed record HandshakeRequest(
    ushort ProtocolVersion,
    string GameVersion,
    string CatalogHash,
    string DisplayName);

/// <summary>Host -&gt; client handshake answer with a human-readable reason.
/// Carries the session id so the client can adopt it for strict frame validation.</summary>
public sealed record HandshakeResponse(
    bool Accepted,
    HandshakeRejectReason Reason,
    ulong SessionId,
    string Message);

public sealed record HostHandshakePolicy(
    ushort ExpectedProtocol,
    string ExpectedCatalogHash,
    int CurrentPlayers,
    int MaxPlayers,
    bool JoinAllowed);

public static class HandshakeCodec
{
    public static bool TryEncodeRequest(in HandshakeRequest request, Span<byte> destination, out int written)
    {
        written = 0;
        if (!IsValidDisplayName(request.DisplayName) ||
            !IsBoundedAscii(request.GameVersion, NetLimits.MaxGameVersionBytes) ||
            !IsBoundedAscii(request.CatalogHash, NetLimits.MaxCatalogHashBytes))
        {
            return false;
        }

        var game = Encoding.UTF8.GetBytes(request.GameVersion);
        var catalog = Encoding.UTF8.GetBytes(request.CatalogHash);
        var name = Encoding.UTF8.GetBytes(request.DisplayName);
        var total = 2 + 1 + game.Length + 1 + catalog.Length + 1 + name.Length;
        if (total > MessageBounds.MaxPayload(MessageType.HandshakeRequest) ||
            destination.Length < total)
        {
            return false;
        }

        destination[0] = (byte)(request.ProtocolVersion & 0xFF);
        destination[1] = (byte)((request.ProtocolVersion >> 8) & 0xFF);
        var at = 2;
        at = WritePrefixed(destination, at, game);
        at = WritePrefixed(destination, at, catalog);
        at = WritePrefixed(destination, at, name);
        written = at;
        return true;
    }

    public static bool TryDecodeRequest(ReadOnlySpan<byte> payload, out HandshakeRequest? request)
    {
        request = null;
        if (payload.Length < 5)
        {
            return false;
        }

        var protocol = (ushort)(payload[0] | (payload[1] << 8));
        var at = 2;
        if (!TryReadPrefixed(payload, ref at, NetLimits.MaxGameVersionBytes, out var game) ||
            !TryReadPrefixed(payload, ref at, NetLimits.MaxCatalogHashBytes, out var catalog) ||
            !TryReadPrefixed(payload, ref at, NetLimits.MaxDisplayNameBytes, out var name) ||
            at != payload.Length)
        {
            return false;
        }

        string gameVersion;
        string catalogHash;
        string displayName;
        try
        {
            gameVersion = Encoding.UTF8.GetString(game);
            catalogHash = Encoding.UTF8.GetString(catalog);
            displayName = Encoding.UTF8.GetString(name);
        }
        catch
        {
            return false;
        }

        if (gameVersion.Length == 0 || catalogHash.Length == 0 ||
            !IsValidDisplayName(displayName))
        {
            return false;
        }

        request = new HandshakeRequest(protocol, gameVersion, catalogHash, displayName);
        return true;
    }

    public static bool TryEncodeResponse(in HandshakeResponse response, Span<byte> destination, out int written)
    {
        written = 0;
        var message = response.Message ?? string.Empty;
        var bytes = Encoding.UTF8.GetBytes(message);
        if (bytes.Length > NetLimits.MaxReasonBytes)
        {
            return false;
        }

        var total = 1 + 1 + 8 + 1 + bytes.Length;
        if (total > MessageBounds.MaxPayload(MessageType.HandshakeResponse) ||
            destination.Length < total)
        {
            return false;
        }

        destination[0] = response.Accepted ? (byte)1 : (byte)0;
        destination[1] = (byte)response.Reason;
        BitConverter.TryWriteBytes(destination[2..], response.SessionId);
        destination[10] = (byte)bytes.Length;
        bytes.CopyTo(destination[11..]);
        written = total;
        return true;
    }

    public static bool TryDecodeResponse(ReadOnlySpan<byte> payload, out HandshakeResponse? response)
    {
        response = null;
        if (payload.Length < 11 || !Enum.IsDefined(typeof(HandshakeRejectReason), payload[1]))
        {
            return false;
        }

        var sessionId = BitConverter.ToUInt64(payload.Slice(2, 8));
        var count = payload[10];
        if (11 + count != payload.Length || count > NetLimits.MaxReasonBytes)
        {
            return false;
        }

        string message;
        try
        {
            message = Encoding.UTF8.GetString(payload.Slice(11, count));
        }
        catch
        {
            return false;
        }

        response = new HandshakeResponse(payload[0] == 1, (HandshakeRejectReason)payload[1], sessionId, message);
        return true;
    }

    public static bool IsValidDisplayName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var trimmed = name.Trim();
        if (trimmed.Length == 0 || trimmed.Length > NetLimits.MaxDisplayNameChars)
        {
            return false;
        }

        return Encoding.UTF8.GetByteCount(trimmed) <= NetLimits.MaxDisplayNameBytes;
    }

    private static bool IsBoundedAscii(string value, int maxBytes)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        return Encoding.UTF8.GetByteCount(value) is var n && n > 0 && n <= maxBytes;
    }

    private static int WritePrefixed(Span<byte> dest, int at, byte[] bytes)
    {
        dest[at] = (byte)bytes.Length;
        bytes.CopyTo(dest[(at + 1)..]);
        return at + 1 + bytes.Length;
    }

    private static bool TryReadPrefixed(ReadOnlySpan<byte> src, ref int at, int max, out ReadOnlySpan<byte> slice)
    {
        slice = default;
        if (at >= src.Length)
        {
            return false;
        }

        var count = src[at++];
        if (count > max || at + count > src.Length)
        {
            return false;
        }

        slice = src.Slice(at, count);
        at += count;
        return true;
    }
}

/// <summary>Host-side handshake validation: exact protocol and catalog match,
/// player count bound, and run join policy (docs/02 §6).</summary>
public static class HandshakeValidator
{
    public static HandshakeResponse Validate(in HandshakeRequest request, in HostHandshakePolicy policy, ulong sessionId)
    {
        if (request.ProtocolVersion != policy.ExpectedProtocol)
        {
            return Reject(HandshakeRejectReason.ProtocolMismatch,
                $"Protocol mismatch: session uses v{policy.ExpectedProtocol}. Update the game so both sides match.");
        }

        if (!string.Equals(request.CatalogHash, policy.ExpectedCatalogHash, StringComparison.Ordinal))
        {
            return Reject(HandshakeRejectReason.CatalogMismatch,
                "Content catalog differs from the host. Use the same game build and mods.");
        }

        if (!HandshakeCodec.IsValidDisplayName(request.DisplayName))
        {
            return Reject(HandshakeRejectReason.NameInvalid,
                $"Display name must be 1-{NetLimits.MaxDisplayNameChars} characters.");
        }

        if (policy.CurrentPlayers >= policy.MaxPlayers)
        {
            return Reject(HandshakeRejectReason.JoinClosed,
                "Lobby is full. Try again when a slot frees up.");
        }

        if (!policy.JoinAllowed)
        {
            return Reject(HandshakeRejectReason.JoinClosed,
                "This run is closed to new players right now.");
        }

        return new HandshakeResponse(true, HandshakeRejectReason.None, sessionId, "Welcome aboard.");
    }

    private static HandshakeResponse Reject(HandshakeRejectReason reason, string message) =>
        new(false, reason, NetLimits.InvalidId, message);
}
