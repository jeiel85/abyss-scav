using System.Text;

namespace AbyssScav.Protocol;

/// <summary>Reliable host → client run events (docs/02 §9.3, §9.6).</summary>
public enum CoopEventType : byte
{
    Unknown = 0,

    /// <summary>The host authorized this client's extraction request.</summary>
    ExtractApproved = 1,

    /// <summary>The host refused this client's extraction request; <see cref="CoopEvent.Text"/> carries the reason.</summary>
    ExtractRefused = 2,

    /// <summary>
    /// The host's run ended; <see cref="CoopEvent.Body"/> is the final encoded
    /// <see cref="WorldSnapshot"/>, delivered reliably so a lost unreliable
    /// datagram can never leave a client unaware the run is over.
    /// </summary>
    FinalSnapshot = 3,
}

/// <summary>
/// Reliable run event. <see cref="Text"/> is a short human-readable reason
/// (≤ <see cref="NetLimits.MaxReasonBytes"/> UTF-8 bytes) and <see cref="Body"/>
/// an opaque bounded payload whose meaning depends on <see cref="Type"/>.
/// </summary>
public sealed record CoopEvent(ulong SessionId, CoopEventType Type, string? Text, byte[]? Body);

public static class CoopEventCodec
{
    /// <summary>Body ceiling: a final snapshot must fit (see <see cref="MessageType.Snapshot"/>).</summary>
    public static int MaxBodyBytes => MessageBounds.MaxPayload(MessageType.Snapshot);

    public static bool TryEncode(CoopEvent evt, Span<byte> destination, out int written)
    {
        written = 0;
        if (evt is null || evt.SessionId == NetLimits.InvalidId || !Enum.IsDefined(evt.Type) || evt.Type == CoopEventType.Unknown)
        {
            return false;
        }

        var text = string.IsNullOrEmpty(evt.Text) ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(evt.Text);
        var body = evt.Body ?? Array.Empty<byte>();
        if (text.Length > NetLimits.MaxReasonBytes || body.Length > MaxBodyBytes)
        {
            return false;
        }

        if (evt.Type == CoopEventType.FinalSnapshot && body.Length == 0)
        {
            return false;
        }

        var total = 8 + 1 + 1 + text.Length + 2 + body.Length;
        if (total > MessageBounds.MaxPayload(MessageType.GameEvent) || destination.Length < total)
        {
            return false;
        }

        var at = 0;
        BitConverter.TryWriteBytes(destination[at..], evt.SessionId);
        at += 8;
        destination[at++] = (byte)evt.Type;
        destination[at++] = (byte)text.Length;
        text.CopyTo(destination[at..]);
        at += text.Length;
        BitConverter.TryWriteBytes(destination[at..], (ushort)body.Length);
        at += 2;
        body.CopyTo(destination[at..]);
        at += body.Length;
        written = at;
        return true;
    }

    public static bool TryDecode(ReadOnlySpan<byte> payload, out CoopEvent? evt)
    {
        evt = null;
        if (payload.Length < 8 + 1 + 1 + 2 || payload.Length > MessageBounds.MaxPayload(MessageType.GameEvent))
        {
            return false;
        }

        var at = 0;
        var session = BitConverter.ToUInt64(payload[..8]);
        at += 8;
        var type = (CoopEventType)payload[at++];
        if (session == NetLimits.InvalidId || type == CoopEventType.Unknown || !Enum.IsDefined(type))
        {
            return false;
        }

        var textLength = payload[at++];
        if (textLength > NetLimits.MaxReasonBytes || at + textLength + 2 > payload.Length)
        {
            return false;
        }

        string? text = null;
        if (textLength > 0)
        {
            try
            {
                text = new UTF8Encoding(false, true).GetString(payload.Slice(at, textLength));
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        at += textLength;
        var bodyLength = BitConverter.ToUInt16(payload.Slice(at, 2));
        at += 2;
        if (bodyLength > MaxBodyBytes || at + bodyLength != payload.Length)
        {
            return false;
        }

        if (type == CoopEventType.FinalSnapshot && bodyLength == 0)
        {
            return false;
        }

        var body = bodyLength == 0 ? null : payload.Slice(at, bodyLength).ToArray();
        evt = new CoopEvent(session, type, text, body);
        return true;
    }
}
