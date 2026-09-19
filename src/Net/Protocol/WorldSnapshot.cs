using System.Text;

namespace AbyssScav.Protocol;

/// <summary>
/// Host-authoritative world snapshot (docs/02 §9). The world is deterministic
/// per manifest (LayoutHash-verified on join), so creatures and loot are matched
/// by index and loot/node state travels as compact bitmasks. Ship-local state
/// (hull, pressure, power, sonar, dock) is deliberately absent: each player's
/// sim owns its own ship.
/// </summary>
public sealed record WorldSnapshot(
    ulong SessionId,
    byte Phase,
    string? FailureReason,
    string? MajorEventId,
    float MajorEventRemaining,
    int SecuredSalvageValue,
    byte[] SalvagedLootMask,
    byte[] ServicedNodesMask,
    int SurveysDone,
    int PulsesUsed,
    float ObserveSeconds,
    int DrillLootIndex,
    float DrillElapsedSeconds,
    IReadOnlyList<CreatureWire> Creatures);

/// <summary>Per-creature wire state, index-aligned with the local world's creatures.</summary>
public sealed record CreatureWire(
    float X,
    float Y,
    float Z,
    byte State,
    float StateTime,
    float StunnedSeconds,
    float SonarExposedSeconds);

public static class WorldSnapshotCodec
{
    private const int MaxLootMaskBytes = (NetLimits.MaxLootSpawns + 7) / 8;
    private const int MaxNodeMaskBytes = (NetLimits.MaxWorldNodes + 7) / 8;

    public static bool TryEncode(in WorldSnapshot snap, Span<byte> destination, out int written)
    {
        written = 0;
        if (snap.SalvagedLootMask is null || snap.SalvagedLootMask.Length > MaxLootMaskBytes ||
            snap.ServicedNodesMask is null || snap.ServicedNodesMask.Length > MaxNodeMaskBytes ||
            snap.Creatures is null || snap.Creatures.Count > NetLimits.MaxCreatures)
        {
            return false;
        }

        if (snap.FailureReason is not null && Encoding.UTF8.GetByteCount(snap.FailureReason) > NetLimits.MaxReasonBytes)
        {
            return false;
        }

        if (snap.MajorEventId is not null && Encoding.UTF8.GetByteCount(snap.MajorEventId) > NetLimits.MaxIdBytes)
        {
            return false;
        }

        var total = 8 + 1 + 4 + 4 + 4 + 4 + 4 + 4 + 4 + 4 + 4;
        total += 1 + snap.SalvagedLootMask.Length;
        total += 1 + snap.ServicedNodesMask.Length;
        total += 1 + (snap.FailureReason is null ? 0 : Encoding.UTF8.GetByteCount(snap.FailureReason));
        total += 1 + (snap.MajorEventId is null ? 0 : Encoding.UTF8.GetByteCount(snap.MajorEventId));
        total += 1 + snap.Creatures.Count * 25;
        if (total > MessageBounds.MaxPayload(MessageType.Snapshot) || destination.Length < total)
        {
            return false;
        }

        var at = 0;
        BitConverter.TryWriteBytes(destination[at..], snap.SessionId);
        at += 8;
        destination[at++] = snap.Phase;
        WriteString(destination, ref at, snap.FailureReason, NetLimits.MaxReasonBytes);
        WriteString(destination, ref at, snap.MajorEventId, NetLimits.MaxIdBytes);
        BitConverter.TryWriteBytes(destination[at..], snap.MajorEventRemaining);
        at += 4;
        BitConverter.TryWriteBytes(destination[at..], snap.SecuredSalvageValue);
        at += 4;
        destination[at++] = (byte)snap.SalvagedLootMask.Length;
        snap.SalvagedLootMask.CopyTo(destination[at..]);
        at += snap.SalvagedLootMask.Length;
        destination[at++] = (byte)snap.ServicedNodesMask.Length;
        snap.ServicedNodesMask.CopyTo(destination[at..]);
        at += snap.ServicedNodesMask.Length;
        BitConverter.TryWriteBytes(destination[at..], snap.SurveysDone);
        at += 4;
        BitConverter.TryWriteBytes(destination[at..], snap.PulsesUsed);
        at += 4;
        BitConverter.TryWriteBytes(destination[at..], snap.ObserveSeconds);
        at += 4;
        BitConverter.TryWriteBytes(destination[at..], snap.DrillLootIndex);
        at += 4;
        BitConverter.TryWriteBytes(destination[at..], snap.DrillElapsedSeconds);
        at += 4;
        destination[at++] = (byte)snap.Creatures.Count;
        foreach (var c in snap.Creatures)
        {
            BitConverter.TryWriteBytes(destination[at..], c.X);
            at += 4;
            BitConverter.TryWriteBytes(destination[at..], c.Y);
            at += 4;
            BitConverter.TryWriteBytes(destination[at..], c.Z);
            at += 4;
            destination[at++] = c.State;
            BitConverter.TryWriteBytes(destination[at..], c.StateTime);
            at += 4;
            BitConverter.TryWriteBytes(destination[at..], c.StunnedSeconds);
            at += 4;
            BitConverter.TryWriteBytes(destination[at..], c.SonarExposedSeconds);
            at += 4;
        }

        written = at;
        return true;
    }

    public static bool TryDecode(ReadOnlySpan<byte> payload, out WorldSnapshot? snap)
    {
        snap = null;
        // Fixed fields + the two non-empty mask payloads (lootLen/nodeLen ≥ 1).
        if (payload.Length < 8 + 1 + 1 + 1 + 4 + 4 + 1 + 1 + 1 + 1 + 4 + 4 + 4 + 4 + 4 + 1)
        {
            return false;
        }

        var at = 0;
        var sessionId = BitConverter.ToUInt64(payload.Slice(at, 8));
        at += 8;
        var phase = payload[at++];
        if (!TryReadString(payload, ref at, NetLimits.MaxReasonBytes, out var failureReason))
        {
            return false;
        }

        if (!TryReadString(payload, ref at, NetLimits.MaxIdBytes, out var majorEventId))
        {
            return false;
        }

        var majorRemaining = BitConverter.ToSingle(payload.Slice(at, 4));
        at += 4;
        var secured = BitConverter.ToInt32(payload.Slice(at, 4));
        at += 4;
        var lootLen = payload[at++];
        if (lootLen == 0 || lootLen > MaxLootMaskBytes || at + lootLen > payload.Length)
        {
            return false;
        }

        var lootMask = payload.Slice(at, lootLen).ToArray();
        at += lootLen;
        var nodeLen = payload[at++];
        if (nodeLen == 0 || nodeLen > MaxNodeMaskBytes || at + nodeLen > payload.Length)
        {
            return false;
        }

        var nodeMask = payload.Slice(at, nodeLen).ToArray();
        at += nodeLen;
        var surveys = BitConverter.ToInt32(payload.Slice(at, 4));
        at += 4;
        var pulses = BitConverter.ToInt32(payload.Slice(at, 4));
        at += 4;
        var observe = BitConverter.ToSingle(payload.Slice(at, 4));
        at += 4;
        var drillIndex = BitConverter.ToInt32(payload.Slice(at, 4));
        at += 4;
        var drillElapsed = BitConverter.ToSingle(payload.Slice(at, 4));
        at += 4;
        var creatureCount = payload[at++];
        if (creatureCount > NetLimits.MaxCreatures || at + creatureCount * 25 > payload.Length)
        {
            return false;
        }

        var creatures = new CreatureWire[creatureCount];
        for (var i = 0; i < creatureCount; i++)
        {
            var x = BitConverter.ToSingle(payload.Slice(at, 4));
            at += 4;
            var y = BitConverter.ToSingle(payload.Slice(at, 4));
            at += 4;
            var z = BitConverter.ToSingle(payload.Slice(at, 4));
            at += 4;
            var state = payload[at++];
            var stateTime = BitConverter.ToSingle(payload.Slice(at, 4));
            at += 4;
            var stunned = BitConverter.ToSingle(payload.Slice(at, 4));
            at += 4;
            var exposed = BitConverter.ToSingle(payload.Slice(at, 4));
            at += 4;
            creatures[i] = new CreatureWire(x, y, z, state, stateTime, stunned, exposed);
        }

        if (at != payload.Length)
        {
            return false;
        }

        snap = new WorldSnapshot(sessionId, phase, failureReason, majorEventId, majorRemaining,
            secured, lootMask, nodeMask, surveys, pulses, observe, drillIndex, drillElapsed, creatures);
        return true;
    }

    private static void WriteString(Span<byte> destination, ref int at, string? value, int maxBytes)
    {
        if (string.IsNullOrEmpty(value))
        {
            destination[at++] = 0;
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        destination[at++] = (byte)bytes.Length;
        bytes.CopyTo(destination[at..]);
        at += bytes.Length;
    }

    private static bool TryReadString(ReadOnlySpan<byte> src, ref int at, int max, out string? value)
    {
        value = null;
        if (at >= src.Length)
        {
            return false;
        }

        var count = src[at++];
        if (count == 0)
        {
            return true;
        }

        if (count > max || at + count > src.Length)
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
        return true;
    }
}