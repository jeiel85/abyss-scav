using AbyssScav.Protocol;

namespace AbyssScav.Net.Tests;

internal static class WorldSnapshotTests
{
    public static int Run()
    {
        var cases = new (string Name, Action Test)[]
        {
            ("snapshot_codec_roundtrip", SnapshotRoundtrip),
            ("snapshot_optional_fields_empty", SnapshotOptionals),
            ("snapshot_bounds_rejected", SnapshotBounds),
            ("intent_codec_roundtrip", IntentRoundtrip),
            ("intent_unknown_type_rejected", IntentUnknown),
            ("intent_bounds_rejected", IntentBounds),
        };

        var fail = 0;
        foreach (var (name, test) in cases)
        {
            try
            {
                test();
                Console.WriteLine($"  ok: {name}");
            }
            catch (Exception ex)
            {
                fail++;
                Console.WriteLine($"  FAIL: {name}: {ex.Message}");
            }
        }

        return fail;
    }

    private static WorldSnapshot Sample() => new(
        SessionId: 42,
        Phase: 1,
        FailureReason: "hull breached",
        MajorEventId: "event.acoustic_disturbance",
        MajorEventRemaining: 12.5f,
        SecuredSalvageValue: 340,
        SalvagedLootMask: new byte[] { 0b0000_0101 },
        ServicedNodesMask: new byte[] { 0b0000_0010 },
        SurveysDone: 2,
        PulsesUsed: 3,
        ObserveSeconds: 4.25f,
        DrillLootIndex: 7,
        DrillElapsedSeconds: 3.5f,
        Creatures: new[]
        {
            new CreatureWire(1f, 2f, 3f, 2, 4f, 0f, 15f),
            new CreatureWire(-5f, -6f, -7f, 1, 0.5f, 2f, 0f),
        });

    private static void SnapshotRoundtrip()
    {
        var snap = Sample();
        Span<byte> buffer = stackalloc byte[MessageBounds.MaxPayload(MessageType.Snapshot)];
        TestAssert.True(WorldSnapshotCodec.TryEncode(snap, buffer, out var size), "encode");
        TestAssert.True(WorldSnapshotCodec.TryDecode(buffer[..size].ToArray(), out var decoded), "decode");
        TestAssert.NotNull(decoded, "decoded");
        TestAssert.Equal(snap.SessionId, decoded!.SessionId, "session");
        TestAssert.Equal(snap.Phase, decoded.Phase, "phase");
        TestAssert.Equal(snap.FailureReason, decoded.FailureReason, "failure");
        TestAssert.Equal(snap.MajorEventId, decoded.MajorEventId, "event");
        TestAssert.True(Math.Abs(snap.MajorEventRemaining - decoded.MajorEventRemaining) < 0.001f, "event remaining");
        TestAssert.Equal(snap.SecuredSalvageValue, decoded.SecuredSalvageValue, "secured");
        TestAssert.True(snap.SalvagedLootMask.SequenceEqual(decoded.SalvagedLootMask), "loot mask");
        TestAssert.True(snap.ServicedNodesMask.SequenceEqual(decoded.ServicedNodesMask), "node mask");
        TestAssert.Equal(snap.SurveysDone, decoded.SurveysDone, "surveys");
        TestAssert.Equal(snap.PulsesUsed, decoded.PulsesUsed, "pulses");
        TestAssert.True(Math.Abs(snap.ObserveSeconds - decoded.ObserveSeconds) < 0.001f, "observe");
        TestAssert.Equal(snap.DrillLootIndex, decoded.DrillLootIndex, "drill index");
        TestAssert.True(Math.Abs(snap.DrillElapsedSeconds - decoded.DrillElapsedSeconds) < 0.001f, "drill elapsed");
        TestAssert.Equal(snap.Creatures.Count, decoded.Creatures.Count, "creature count");
        for (var i = 0; i < snap.Creatures.Count; i++)
        {
            TestAssert.True(Math.Abs(snap.Creatures[i].X - decoded.Creatures[i].X) < 0.001f, $"creature {i} x");
            TestAssert.Equal(snap.Creatures[i].State, decoded.Creatures[i].State, $"creature {i} state");
            TestAssert.True(Math.Abs(snap.Creatures[i].SonarExposedSeconds - decoded.Creatures[i].SonarExposedSeconds) < 0.001f, $"creature {i} exposed");
        }
    }

    private static void SnapshotOptionals()
    {
        var snap = Sample() with
        {
            FailureReason = null,
            MajorEventId = null,
            DrillLootIndex = -1,
            Creatures = Array.Empty<CreatureWire>(),
        };
        Span<byte> buffer = stackalloc byte[MessageBounds.MaxPayload(MessageType.Snapshot)];
        TestAssert.True(WorldSnapshotCodec.TryEncode(snap, buffer, out var size), "encode");
        TestAssert.True(WorldSnapshotCodec.TryDecode(buffer[..size].ToArray(), out var decoded), "decode");
        TestAssert.NotNull(decoded, "decoded");
        TestAssert.True(decoded!.FailureReason is null, "failure null");
        TestAssert.True(decoded.MajorEventId is null, "event null");
        TestAssert.Equal(-1, decoded.DrillLootIndex, "no drill");
        TestAssert.Equal(0, decoded.Creatures.Count, "no creatures");
    }

    private static void SnapshotBounds()
    {
        var tooMany = Sample() with
        {
            Creatures = Enumerable.Range(0, NetLimits.MaxCreatures + 1)
                .Select(i => new CreatureWire(i, 0f, 0f, 1, 0f, 0f, 0f)).ToList(),
        };
        Span<byte> buffer = stackalloc byte[MessageBounds.MaxPayload(MessageType.Snapshot)];
        TestAssert.False(WorldSnapshotCodec.TryEncode(tooMany, buffer, out _), "too many creatures refused");

        var badMask = Sample() with { SalvagedLootMask = new byte[NetLimits.MaxLootSpawns / 8 + 1] };
        TestAssert.False(WorldSnapshotCodec.TryEncode(badMask, buffer, out _), "oversized mask refused");

        var longReason = Sample() with { FailureReason = new string('x', NetLimits.MaxReasonBytes + 1) };
        TestAssert.False(WorldSnapshotCodec.TryEncode(longReason, buffer, out _), "long reason refused");
    }

    private static void IntentRoundtrip()
    {
        var intent = new PlayerIntent(42, IntentType.Salvage, "loot.007", 1f, 2f, 3f, 0);
        Span<byte> buffer = stackalloc byte[MessageBounds.MaxPayload(MessageType.PlayerIntent)];
        TestAssert.True(PlayerIntentCodec.TryEncode(intent, buffer, out var size), "encode");
        TestAssert.True(PlayerIntentCodec.TryDecode(buffer[..size].ToArray(), out var decoded), "decode");
        TestAssert.NotNull(decoded, "decoded");
        TestAssert.Equal(intent.SessionId, decoded!.SessionId, "session");
        TestAssert.Equal(intent.Type, decoded.Type, "type");
        TestAssert.Equal(intent.TargetId, decoded.TargetId, "target");
        TestAssert.True(Math.Abs(intent.PositionX - decoded.PositionX) < 0.001f, "x");
        TestAssert.Equal(intent.Extra, decoded.Extra, "extra");

        // Position-only intents (pulse) carry no target id.
        var pulse = new PlayerIntent(42, IntentType.Pulse, string.Empty, 1f, 2f, 3f, 0);
        TestAssert.True(PlayerIntentCodec.TryEncode(pulse, buffer, out var pulseSize), "pulse encode");
        TestAssert.True(PlayerIntentCodec.TryDecode(buffer[..pulseSize].ToArray(), out var pulseDecoded), "pulse decode");
        TestAssert.True(string.IsNullOrEmpty(pulseDecoded!.TargetId), "pulse target empty");
    }

    private static void IntentUnknown()
    {
        var intent = new PlayerIntent(42, IntentType.Unknown, "loot.007", 0f, 0f, 0f, 0);
        Span<byte> buffer = stackalloc byte[MessageBounds.MaxPayload(MessageType.PlayerIntent)];
        TestAssert.False(PlayerIntentCodec.TryEncode(intent, buffer, out _), "unknown type refused");
    }

    private static void IntentBounds()
    {
        var intent = new PlayerIntent(42, IntentType.Salvage, new string('x', NetLimits.MaxIdBytes + 1), 0f, 0f, 0f, 0);
        Span<byte> buffer = stackalloc byte[MessageBounds.MaxPayload(MessageType.PlayerIntent)];
        TestAssert.False(PlayerIntentCodec.TryEncode(intent, buffer, out _), "long target refused");
    }
}