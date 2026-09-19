using System.Text;

namespace AbyssScav.Protocol;

/// <summary>
/// Client interaction intent (docs/02 §3, §9). The client's local sim applies
/// the interaction as prediction; the host authorizes it against the requester
/// position and the shared world, and the next snapshot reconciles. Ship-local
/// interactions (repair, winch, consumables, decoy/EMP, dock, extract) are not
/// intents this batch: they stay client-local by design.
/// </summary>
public enum IntentType : byte
{
    Unknown = 0,
    Salvage = 1,
    DrillStart = 2,
    DrillCancel = 3,
    Survey = 4,
    Service = 5,
    Pulse = 6,
}

public sealed record PlayerIntent(
    ulong SessionId,
    IntentType Type,
    string TargetId,
    float PositionX,
    float PositionY,
    float PositionZ,
    int Extra);

public static class PlayerIntentCodec
{
    public static bool TryEncode(in PlayerIntent intent, Span<byte> destination, out int written)
    {
        written = 0;
        if (intent.Type == IntentType.Unknown ||
            string.IsNullOrEmpty(intent.TargetId) && intent.Type is not (IntentType.Pulse or IntentType.DrillCancel or IntentType.Survey) ||
            !string.IsNullOrEmpty(intent.TargetId) && Encoding.UTF8.GetByteCount(intent.TargetId) > NetLimits.MaxIdBytes)
        {
            return false;
        }

        var total = 8 + 1 + 12 + 4;
        if (!string.IsNullOrEmpty(intent.TargetId))
        {
            total += 1 + Encoding.UTF8.GetByteCount(intent.TargetId);
        }

        if (total > MessageBounds.MaxPayload(MessageType.PlayerIntent) || destination.Length < total)
        {
            return false;
        }

        var at = 0;
        BitConverter.TryWriteBytes(destination[at..], intent.SessionId);
        at += 8;
        destination[at++] = (byte)intent.Type;
        if (string.IsNullOrEmpty(intent.TargetId))
        {
            destination[at++] = 0;
        }
        else
        {
            var bytes = Encoding.UTF8.GetBytes(intent.TargetId);
            destination[at++] = (byte)bytes.Length;
            bytes.CopyTo(destination[at..]);
            at += bytes.Length;
        }

        BitConverter.TryWriteBytes(destination[at..], intent.PositionX);
        at += 4;
        BitConverter.TryWriteBytes(destination[at..], intent.PositionY);
        at += 4;
        BitConverter.TryWriteBytes(destination[at..], intent.PositionZ);
        at += 4;
        BitConverter.TryWriteBytes(destination[at..], intent.Extra);
        at += 4;
        written = at;
        return true;
    }

    public static bool TryDecode(ReadOnlySpan<byte> payload, out PlayerIntent? intent)
    {
        intent = null;
        if (payload.Length < 8 + 1 + 1 + 12 + 4)
        {
            return false;
        }

        var at = 0;
        var sessionId = BitConverter.ToUInt64(payload.Slice(at, 8));
        at += 8;
        var type = (IntentType)payload[at++];
        if (type == IntentType.Unknown)
        {
            return false;
        }

        var count = payload[at++];
        string targetId;
        if (count == 0)
        {
            targetId = string.Empty;
        }
        else
        {
            if (count > NetLimits.MaxIdBytes || at + count > payload.Length)
            {
                return false;
            }

            try
            {
                targetId = Encoding.UTF8.GetString(payload.Slice(at, count));
            }
            catch
            {
                return false;
            }

            at += count;
        }

        if (at + 12 + 4 > payload.Length)
        {
            return false;
        }

        var x = BitConverter.ToSingle(payload.Slice(at, 4));
        at += 4;
        var y = BitConverter.ToSingle(payload.Slice(at, 4));
        at += 4;
        var z = BitConverter.ToSingle(payload.Slice(at, 4));
        at += 4;
        var extra = BitConverter.ToInt32(payload.Slice(at, 4));
        at += 4;
        if (at != payload.Length)
        {
            return false;
        }

        intent = new PlayerIntent(sessionId, type, targetId, x, y, z, extra);
        return true;
    }
}