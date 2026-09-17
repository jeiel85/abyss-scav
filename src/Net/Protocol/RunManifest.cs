using System.Text;

namespace AbyssScav.Protocol;

/// <summary>Host-authoritative run manifest (docs/16 §5, docs/02 §8).</summary>
public sealed record RunManifest(
    ushort ProtocolVersion,
    string GameVersion,
    ulong RunSeed,
    string BiomeId,
    string ContractId,
    string DifficultyId,
    string LayoutHash,
    string CatalogHash,
    IReadOnlyList<string> Modifiers);

public enum ManifestAcceptance
{
    Accepted,
    ProtocolMismatch,
    CatalogMismatch,
    LayoutMismatch,
}

public static class RunManifestCodec
{
    public static bool TryEncode(in RunManifest manifest, Span<byte> destination, out int written)
    {
        written = 0;
        var parts = new[]
        {
            manifest.GameVersion, manifest.BiomeId, manifest.ContractId,
            manifest.DifficultyId, manifest.LayoutHash, manifest.CatalogHash,
        };
        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part) || Encoding.UTF8.GetByteCount(part) > NetLimits.MaxIdBytes)
            {
                return false;
            }
        }

        if (manifest.Modifiers is null || manifest.Modifiers.Count > NetLimits.MaxModifiers)
        {
            return false;
        }

        foreach (var modifier in manifest.Modifiers)
        {
            if (string.IsNullOrEmpty(modifier) || Encoding.UTF8.GetByteCount(modifier) > NetLimits.MaxModifierBytes)
            {
                return false;
            }
        }

        var encoded = parts.Select(Encoding.UTF8.GetBytes).ToArray();
        var mods = manifest.Modifiers.Select(Encoding.UTF8.GetBytes).ToArray();
        var total = 2 + 8 + 1 + mods.Length;
        foreach (var bytes in encoded)
        {
            total += 1 + bytes.Length;
        }

        foreach (var bytes in mods)
        {
            total += 1 + bytes.Length;
        }

        if (total > MessageBounds.MaxPayload(MessageType.RunManifest) ||
            destination.Length < total)
        {
            return false;
        }

        destination[0] = (byte)(manifest.ProtocolVersion & 0xFF);
        destination[1] = (byte)((manifest.ProtocolVersion >> 8) & 0xFF);
        var at = 2;
        foreach (var bytes in encoded)
        {
            destination[at++] = (byte)bytes.Length;
            bytes.CopyTo(destination[at..]);
            at += bytes.Length;
        }

        BitConverter.TryWriteBytes(destination[at..], manifest.RunSeed);
        at += 8;
        destination[at++] = (byte)mods.Length;
        foreach (var bytes in mods)
        {
            destination[at++] = (byte)bytes.Length;
            bytes.CopyTo(destination[at..]);
            at += bytes.Length;
        }

        written = at;
        return true;
    }

    public static bool TryDecode(ReadOnlySpan<byte> payload, out RunManifest? manifest)
    {
        manifest = null;
        if (payload.Length < 2 + 6 + 8 + 1)
        {
            return false;
        }

        var protocol = (ushort)(payload[0] | (payload[1] << 8));
        var at = 2;
        var fields = new string[6];
        for (var i = 0; i < fields.Length; i++)
        {
            if (!TryReadString(payload, ref at, NetLimits.MaxIdBytes, out var value))
            {
                return false;
            }

            fields[i] = value;
        }

        if (at + 8 + 1 > payload.Length)
        {
            return false;
        }

        var seed = BitConverter.ToUInt64(payload.Slice(at, 8));
        at += 8;
        var modCount = payload[at++];
        if (modCount > NetLimits.MaxModifiers)
        {
            return false;
        }

        var modifiers = new string[modCount];
        for (var i = 0; i < modCount; i++)
        {
            if (!TryReadString(payload, ref at, NetLimits.MaxModifierBytes, out var value))
            {
                return false;
            }

            modifiers[i] = value;
        }

        if (at != payload.Length)
        {
            return false;
        }

        manifest = new RunManifest(protocol, fields[0], seed, fields[1], fields[2], fields[3], fields[4], fields[5], modifiers);
        return true;
    }

    /// <summary>
    /// Client-side manifest check (docs/02 §8): a layout mismatch fails the join
    /// with a diagnostic code instead of generating a divergent world.
    /// </summary>
    public static (ManifestAcceptance Acceptance, NetError? Error) Accept(
        in RunManifest manifest,
        ushort expectedProtocol,
        string expectedCatalogHash,
        string expectedLayoutHash)
    {
        if (manifest.ProtocolVersion != expectedProtocol)
        {
            return (ManifestAcceptance.ProtocolMismatch,
                new NetError(NetErrors.ManifestMismatch, "net.manifest.protocol",
                    $"Run manifest protocol v{manifest.ProtocolVersion} does not match v{expectedProtocol}."));
        }

        if (!string.Equals(manifest.CatalogHash, expectedCatalogHash, StringComparison.Ordinal))
        {
            return (ManifestAcceptance.CatalogMismatch,
                new NetError(NetErrors.ManifestMismatch, "net.manifest.catalog",
                    "Run catalog differs from the host; join refused."));
        }

        if (!string.Equals(manifest.LayoutHash, expectedLayoutHash, StringComparison.Ordinal))
        {
            return (ManifestAcceptance.LayoutMismatch,
                new NetError(NetErrors.ManifestMismatch, "net.manifest.layout",
                    $"Layout hash {manifest.LayoutHash} does not match local generation; join refused."));
        }

        return (ManifestAcceptance.Accepted, null);
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
