using AbyssScav.Protocol;

namespace AbyssScav.Net.Tests;

internal static class HandshakeTests
{
    private const string Catalog = "catalog-hash-a01";
    private static readonly HostHandshakePolicy OpenPolicy = new(NetLimits.ProtocolVersion, Catalog, 1, 4, true);

    public static int Run()
    {
        var cases = new (string Name, Action Test)[]
        {
            ("request_codec_roundtrip", CodecRoundtrip),
            ("overlong_name_rejected", LongName),
            ("protocol_mismatch_rejected_with_copy", ProtocolMismatch),
            ("catalog_mismatch_rejected_with_copy", CatalogMismatch),
            ("full_lobby_rejected", FullLobby),
            ("closed_run_rejected", ClosedRun),
            ("blank_name_rejected", BlankName),
            ("response_codec_roundtrip", ResponseRoundtrip),
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

    private static void CodecRoundtrip()
    {
        var request = new HandshakeRequest(NetLimits.ProtocolVersion, "0.1.0-a01", Catalog, "diver-one");
        Span<byte> buffer = stackalloc byte[256];
        TestAssert.True(HandshakeCodec.TryEncodeRequest(request, buffer, out var size), "encode");
        TestAssert.True(HandshakeCodec.TryDecodeRequest(buffer[..size].ToArray(), out var decoded), "decode");
        TestAssert.NotNull(decoded, "decoded");
        TestAssert.Equal(request, decoded!, "roundtrip");
    }

    private static void OverlongName()
    {
        var request = new HandshakeRequest(NetLimits.ProtocolVersion, "0.1.0-a01", Catalog, new string('x', 25));
        Span<byte> buffer = stackalloc byte[256];
        TestAssert.False(HandshakeCodec.TryEncodeRequest(request, buffer, out _), "25 chars refused");
    }

    private static void LongName() => OverlongName();

    private static void ProtocolMismatch()
    {
        var request = new HandshakeRequest(99, "0.1.0-a01", Catalog, "diver");
        var response = HandshakeValidator.Validate(request, OpenPolicy, 55);
        TestAssert.False(response.Accepted, "rejected");
        TestAssert.Equal(HandshakeRejectReason.ProtocolMismatch, response.Reason, "reason");
        TestAssert.True(response.Message.Length > 0, "human-readable copy");
    }

    private static void CatalogMismatch()
    {
        var request = new HandshakeRequest(NetLimits.ProtocolVersion, "0.1.0-a01", "other-catalog", "diver");
        var response = HandshakeValidator.Validate(request, OpenPolicy, 55);
        TestAssert.False(response.Accepted, "rejected");
        TestAssert.Equal(HandshakeRejectReason.CatalogMismatch, response.Reason, "reason");
    }

    private static void FullLobby()
    {
        var request = new HandshakeRequest(NetLimits.ProtocolVersion, "0.1.0-a01", Catalog, "diver");
        var policy = OpenPolicy with { CurrentPlayers = 4 };
        var response = HandshakeValidator.Validate(request, policy, 55);
        TestAssert.False(response.Accepted, "rejected");
        TestAssert.True(
            response.Reason is HandshakeRejectReason.LobbyFull or HandshakeRejectReason.JoinClosed,
            "full reason");
    }

    private static void ClosedRun()
    {
        var request = new HandshakeRequest(NetLimits.ProtocolVersion, "0.1.0-a01", Catalog, "diver");
        var policy = OpenPolicy with { JoinAllowed = false };
        var response = HandshakeValidator.Validate(request, policy, 55);
        TestAssert.False(response.Accepted, "rejected");
        TestAssert.Equal(HandshakeRejectReason.JoinClosed, response.Reason, "reason");
    }

    private static void BlankName()
    {
        var request = new HandshakeRequest(NetLimits.ProtocolVersion, "0.1.0-a01", Catalog, "   ");
        var response = HandshakeValidator.Validate(request, OpenPolicy, 55);
        TestAssert.False(response.Accepted, "rejected");
        TestAssert.Equal(HandshakeRejectReason.NameInvalid, response.Reason, "reason");
    }

    private static void ResponseRoundtrip()
    {
        var response = new HandshakeResponse(true, HandshakeRejectReason.None, 12345, "Welcome aboard.");
        Span<byte> buffer = stackalloc byte[192];
        TestAssert.True(HandshakeCodec.TryEncodeResponse(response, buffer, out var size), "encode");
        TestAssert.True(HandshakeCodec.TryDecodeResponse(buffer[..size].ToArray(), out var decoded), "decode");
        TestAssert.Equal(response, decoded!, "roundtrip");
    }
}
