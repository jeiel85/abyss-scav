using System.Buffers.Binary;

namespace AbyssScav.Protocol;

/// <summary>Decoded binary frame header (docs/02 §5).</summary>
public readonly record struct NetFrameHeader(
    ushort ProtocolVersion,
    MessageType MessageType,
    uint Sequence,
    ulong SessionId,
    ulong SenderPeer,
    ushort PayloadSize);

/// <summary>Frame decode outcome. Oversize/unknown are dropped, never applied.</summary>
public enum FrameDecodeError
{
    None,
    Truncated,
    SizeMismatch,
    Oversize,
    ProtocolMismatch,
    UnknownType,
}

/// <summary>Routed (post-decode) validation outcome. The sender header is never trusted:
/// it must equal the transport-reported sender id.</summary>
public enum FrameRouteError
{
    None,
    SessionMismatch,
    SenderMismatch,
    SequenceReplay,
    RateLimited,
}

/// <summary>Binary envelope codec: protocol_version u16, message_type u16,
/// sequence u32, session_id u64, sender_peer u64, payload_size u16, payload.
/// Little-endian. No JSON on the wire.</summary>
public static class NetFrame
{
    public const int HeaderSize = NetLimits.FrameHeaderSizeBytes;

    public static bool TryEncode(
        MessageType type,
        uint sequence,
        ulong sessionId,
        ulong senderPeer,
        ReadOnlySpan<byte> payload,
        Span<byte> destination,
        out int written)
    {
        written = 0;
        if (!MessageBounds.IsKnown(type) || payload.Length > MessageBounds.MaxPayload(type))
        {
            return false;
        }

        if (sessionId == NetLimits.InvalidId &&
            type is not (MessageType.HandshakeRequest or MessageType.HandshakeResponse))
        {
            // Session zero is reserved for the pre-session handshake only;
            // every post-accept frame carries the adopted session id.
            return false;
        }

        if (senderPeer == NetLimits.InvalidId)
        {
            return false;
        }

        if (destination.Length < HeaderSize + payload.Length)
        {
            return false;
        }

        BinaryPrimitives.WriteUInt16LittleEndian(destination[0..2], NetLimits.ProtocolVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[2..4], (ushort)type);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..8], sequence);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[8..16], sessionId);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[16..24], senderPeer);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[24..26], (ushort)payload.Length);
        payload.CopyTo(destination[HeaderSize..]);
        written = HeaderSize + payload.Length;
        return true;
    }

    public static FrameDecodeError TryDecode(
        ReadOnlySpan<byte> buffer,
        out NetFrameHeader header,
        out int frameLength)
    {
        header = default;
        frameLength = 0;
        if (buffer.Length < HeaderSize)
        {
            return FrameDecodeError.Truncated;
        }

        var protocol = BinaryPrimitives.ReadUInt16LittleEndian(buffer[0..2]);
        var rawType = BinaryPrimitives.ReadUInt16LittleEndian(buffer[2..4]);
        var sequence = BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..8]);
        var sessionId = BinaryPrimitives.ReadUInt64LittleEndian(buffer[8..16]);
        var sender = BinaryPrimitives.ReadUInt64LittleEndian(buffer[16..24]);
        var size = BinaryPrimitives.ReadUInt16LittleEndian(buffer[24..26]);

        if (protocol != NetLimits.ProtocolVersion)
        {
            return FrameDecodeError.ProtocolMismatch;
        }

        var type = (MessageType)rawType;
        if (!MessageBounds.IsKnown(type))
        {
            return FrameDecodeError.UnknownType;
        }

        var cap = MessageBounds.MaxPayload(type);
        if (size > cap || size > NetLimits.AbsoluteMaxPayloadBytes)
        {
            return FrameDecodeError.Oversize;
        }

        if (buffer.Length < HeaderSize + size)
        {
            return FrameDecodeError.Truncated;
        }

        if (buffer.Length > HeaderSize + size)
        {
            return FrameDecodeError.SizeMismatch;
        }

        header = new NetFrameHeader(protocol, type, sequence, sessionId, sender, size);
        frameLength = HeaderSize + size;
        return FrameDecodeError.None;
    }

    public static ReadOnlySpan<byte> Payload(ReadOnlySpan<byte> frame) =>
        frame[HeaderSize..];

    /// <summary>
    /// Strict routed validation: session must match, the header sender must equal
    /// the transport-reported sender (never trust the header), and the sequence
    /// must be newer than the last accepted one for that sender.
    /// </summary>
    public static FrameRouteError ValidateRouted(
        in NetFrameHeader header,
        ulong transportSenderId,
        ulong expectedSessionId,
        InboundSequenceFilter sequences)
    {
        if (header.SessionId != expectedSessionId || header.SessionId == NetLimits.InvalidId)
        {
            return FrameRouteError.SessionMismatch;
        }

        if (transportSenderId == NetLimits.InvalidId ||
            header.SenderPeer != transportSenderId)
        {
            return FrameRouteError.SenderMismatch;
        }

        if (sequences is not null && !sequences.Accept(transportSenderId, header.Sequence))
        {
            return FrameRouteError.SequenceReplay;
        }

        return FrameRouteError.None;
    }
}

/// <summary>Per-sender last-sequence tracker. Rejects duplicates and stale replays;
/// forward jumps are accepted (loss/reorder tolerance). Bounded entry count.</summary>
public sealed class InboundSequenceFilter
{
    private readonly Dictionary<ulong, uint> _last = new();
    private readonly int _maxEntries;

    public InboundSequenceFilter(int maxEntries = NetLimits.MaxPeers * 2)
    {
        _maxEntries = Math.Max(NetLimits.MaxPeers, maxEntries);
    }

    public bool Accept(ulong sender, uint sequence)
    {
        if (sender == NetLimits.InvalidId)
        {
            return false;
        }

        if (_last.TryGetValue(sender, out var last))
        {
            if (sequence <= last)
            {
                return false;
            }

            _last[sender] = sequence;
            return true;
        }

        if (_last.Count >= _maxEntries)
        {
            return false;
        }

        _last[sender] = sequence;
        return true;
    }

    public void Forget(ulong sender) => _last.Remove(sender);

    public void Clear() => _last.Clear();
}
