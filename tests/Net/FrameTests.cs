using AbyssScav.Protocol;

namespace AbyssScav.Net.Tests;

internal static class FrameTests
{
    public static int Run()
    {
        var cases = new (string Name, Action Test)[]
        {
            ("header_is_26_bytes_with_contiguous_offsets", HeaderSize),
            ("roundtrip_preserves_header_and_payload", Roundtrip),
            ("truncated_buffer_rejected", Truncated),
            ("protocol_mismatch_rejected", ProtocolMismatch),
            ("unknown_type_rejected", UnknownType),
            ("oversize_payload_rejected", Oversize),
            ("size_mismatch_rejected", SizeMismatch),
            ("sender_header_never_trusted", SenderMismatch),
            ("wrong_session_rejected", SessionMismatch),
            ("replayed_sequence_rejected", Replay),
            ("forward_jump_accepted", ForwardJump),
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

    private static byte[] Encode(MessageType type, uint seq, ulong session, ulong sender, byte[] payload)
    {
        var frame = new byte[NetFrame.HeaderSize + payload.Length];
        TestAssert.True(NetFrame.TryEncode(type, seq, session, sender, payload, frame, out var size), "encode");
        TestAssert.Equal(frame.Length, size, "encoded size");
        return frame;
    }

    private static void HeaderSize()
    {
        // u16 + u16 + u32 + u64 + u64 + u16 = 26, not 24. The codec must never
        // overwrite: an empty payload frame is exactly one header long.
        TestAssert.Equal(26, NetLimits.FrameHeaderSizeBytes, "header constant");
        TestAssert.Equal(26, NetFrame.HeaderSize, "codec header");
        var frame = Encode(MessageType.PlayerIntent, 1, 7, 2, Array.Empty<byte>());
        TestAssert.Equal(26, frame.Length, "empty frame length");
        TestAssert.Equal(FrameDecodeError.None, NetFrame.TryDecode(frame, out var header, out var length), "decode");
        TestAssert.Equal(26, length, "decoded length");
        TestAssert.Equal((ushort)0, header.PayloadSize, "empty payload size");
    }

    private static void Roundtrip()
    {
        var payload = new byte[] { 9, 8, 7 };
        var frame = Encode(MessageType.PlayerIntent, 41, 77, 2, payload);
        TestAssert.Equal(FrameDecodeError.None, NetFrame.TryDecode(frame, out var header, out _), "decode");
        TestAssert.Equal(MessageType.PlayerIntent, header.MessageType, "type");
        TestAssert.Equal(41u, header.Sequence, "sequence");
        TestAssert.Equal(77ul, header.SessionId, "session");
        TestAssert.Equal(2ul, header.SenderPeer, "sender");
        TestAssert.True(NetFrame.Payload(frame).SequenceEqual(payload), "payload bytes");
    }

    private static void Truncated()
    {
        var frame = Encode(MessageType.Snapshot, 1, 7, 2, new byte[] { 1 });
        TestAssert.Equal(FrameDecodeError.Truncated,
            NetFrame.TryDecode(frame[..^1], out _, out _), "short by one");
        TestAssert.Equal(FrameDecodeError.Truncated,
            NetFrame.TryDecode(frame[..10], out _, out _), "short header");
    }

    private static void ProtocolMismatch()
    {
        var frame = Encode(MessageType.Snapshot, 1, 7, 2, new byte[] { 1 });
        frame[0] = 0xFF;
        TestAssert.Equal(FrameDecodeError.ProtocolMismatch, NetFrame.TryDecode(frame, out _, out _), "bad protocol");
    }

    private static void UnknownType()
    {
        var frame = Encode(MessageType.Snapshot, 1, 7, 2, new byte[] { 1 });
        frame[2] = 0xFF;
        frame[3] = 0xFF;
        TestAssert.Equal(FrameDecodeError.UnknownType, NetFrame.TryDecode(frame, out _, out _), "unknown type");
    }

    private static void Oversize()
    {
        var tooBig = new byte[MessageBounds.MaxPayload(MessageType.PlayerIntent) + 1];
        var frame = new byte[NetFrame.HeaderSize + tooBig.Length];
        TestAssert.False(
            NetFrame.TryEncode(MessageType.PlayerIntent, 1, 7, 2, tooBig, frame, out _),
            "encode must refuse oversize");
    }

    private static void SizeMismatch()
    {
        var frame = Encode(MessageType.PlayerIntent, 1, 7, 2, new byte[] { 1, 2 });
        var padded = frame.Concat(new byte[] { 0 }).ToArray();
        TestAssert.Equal(FrameDecodeError.SizeMismatch, NetFrame.TryDecode(padded, out _, out _), "trailing byte");
    }

    private static void SenderMismatch()
    {
        var frame = Encode(MessageType.PlayerIntent, 1, 7, 2, new byte[] { 1 });
        TestAssert.Equal(FrameDecodeError.None, NetFrame.TryDecode(frame, out var header, out _), "decode");
        // Transport reports sender 3 while the header claims 2: must be dropped.
        TestAssert.Equal(FrameRouteError.SenderMismatch,
            NetFrame.ValidateRouted(header, 3, 7, new InboundSequenceFilter()), "spoofed sender");
        TestAssert.Equal(FrameRouteError.None,
            NetFrame.ValidateRouted(header, 2, 7, new InboundSequenceFilter()), "honest sender");
    }

    private static void SessionMismatch()
    {
        var frame = Encode(MessageType.PlayerIntent, 1, 7, 2, new byte[] { 1 });
        TestAssert.Equal(FrameDecodeError.None, NetFrame.TryDecode(frame, out var header, out _), "decode");
        TestAssert.Equal(FrameRouteError.SessionMismatch,
            NetFrame.ValidateRouted(header, 2, 999, new InboundSequenceFilter()), "wrong session");
    }

    private static void Replay()
    {
        var filter = new InboundSequenceFilter();
        var first = Encode(MessageType.Snapshot, 5, 7, 2, new byte[] { 1 });
        TestAssert.Equal(FrameDecodeError.None, NetFrame.TryDecode(first, out var h1, out _), "decode");
        TestAssert.Equal(FrameRouteError.None, NetFrame.ValidateRouted(h1, 2, 7, filter), "first accepted");
        var replay = Encode(MessageType.Snapshot, 5, 7, 2, new byte[] { 2 });
        TestAssert.Equal(FrameDecodeError.None, NetFrame.TryDecode(replay, out var h2, out _), "decode replay");
        TestAssert.Equal(FrameRouteError.SequenceReplay, NetFrame.ValidateRouted(h2, 2, 7, filter), "replay dropped");
    }

    private static void ForwardJump()
    {
        var filter = new InboundSequenceFilter();
        var first = Encode(MessageType.Snapshot, 5, 7, 2, new byte[] { 1 });
        TestAssert.Equal(FrameDecodeError.None, NetFrame.TryDecode(first, out var h1, out _), "decode");
        TestAssert.Equal(FrameRouteError.None, NetFrame.ValidateRouted(h1, 2, 7, filter), "first");
        var jump = Encode(MessageType.Snapshot, 9, 7, 2, new byte[] { 1 });
        TestAssert.Equal(FrameDecodeError.None, NetFrame.TryDecode(jump, out var h2, out _), "decode jump");
        TestAssert.Equal(FrameRouteError.None, NetFrame.ValidateRouted(h2, 2, 7, filter), "loss-tolerant jump");
    }
}
