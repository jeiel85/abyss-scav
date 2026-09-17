using AbyssScav.Protocol;

namespace AbyssScav.Net.Tests;

internal static class ManifestTests
{
    public static RunManifest Sample() => new(
        NetLimits.ProtocolVersion, "0.1.0-a01", 987654321,
        "biome.trench", "contract.first_dive", "difficulty.standard",
        "layout-aaa", "catalog-hash-a01", new[] { "mod.night_ops" });

    public static int Run()
    {
        var cases = new (string Name, Action Test)[]
        {
            ("manifest_codec_roundtrip", CodecRoundtrip),
            ("layout_mismatch_fails_join", LayoutMismatch),
            ("catalog_mismatch_fails_join", CatalogMismatch),
            ("matching_manifest_accepted", Accepted),
            ("empty_fields_rejected", EmptyRejected),
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
        var manifest = Sample();
        Span<byte> buffer = stackalloc byte[4096];
        TestAssert.True(RunManifestCodec.TryEncode(manifest, buffer, out var size), "encode");
        TestAssert.True(RunManifestCodec.TryDecode(buffer[..size].ToArray(), out var decoded), "decode");
        TestAssert.NotNull(decoded, "decoded");
        TestAssert.Equal(manifest.ProtocolVersion, decoded!.ProtocolVersion, "protocol");
        TestAssert.Equal(manifest.RunSeed, decoded.RunSeed, "seed");
        TestAssert.Equal(manifest.LayoutHash, decoded.LayoutHash, "layout");
        TestAssert.True(manifest.Modifiers.SequenceEqual(decoded.Modifiers), "modifiers");
    }

    private static void LayoutMismatch()
    {
        var (acceptance, error) = RunManifestCodec.Accept(Sample(), NetLimits.ProtocolVersion, "catalog-hash-a01", "other-layout");
        TestAssert.Equal(ManifestAcceptance.LayoutMismatch, acceptance, "layout mismatch");
        TestAssert.NotNull(error, "diagnostic");
        TestAssert.Equal(NetErrors.ManifestMismatch, error!.Code, "NET code");
    }

    private static void CatalogMismatch()
    {
        var (acceptance, error) = RunManifestCodec.Accept(Sample(), NetLimits.ProtocolVersion, "other-catalog", "layout-aaa");
        TestAssert.Equal(ManifestAcceptance.CatalogMismatch, acceptance, "catalog mismatch");
        TestAssert.NotNull(error, "diagnostic");
    }

    private static void Accepted()
    {
        var (acceptance, error) = RunManifestCodec.Accept(Sample(), NetLimits.ProtocolVersion, "catalog-hash-a01", "layout-aaa");
        TestAssert.Equal(ManifestAcceptance.Accepted, acceptance, "accepted");
        TestAssert.True(error is null, "no error");
    }

    private static void EmptyRejected()
    {
        var bad = Sample() with { BiomeId = string.Empty };
        Span<byte> buffer = stackalloc byte[4096];
        TestAssert.False(RunManifestCodec.TryEncode(bad, buffer, out _), "empty field refused");
    }
}
