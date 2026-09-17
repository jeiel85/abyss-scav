using System.Net;
using System.Net.Sockets;
using AbyssScav.Protocol;

namespace AbyssScav.Net.Tests;

internal static class BeaconTests
{
    public static int Run()
    {
        var cases = new (string Name, Func<Task> Test)[]
        {
            ("beacon_codec_roundtrip", BeaconRoundtrip),
            ("oversize_lobby_name_refused", OversizeName),
            ("stale_beacon_dropped", StaleBeacon),
            ("future_stamped_beacon_dropped", FutureBeacon),
            ("wrong_magic_dropped", WrongMagic),
            ("session_table_expires_stale_entries", TableExpiry),
            ("beacon_survives_local_udp_loopback", LoopbackAsync),
        };

        var fail = 0;
        foreach (var (name, test) in cases)
        {
            try
            {
                test().GetAwaiter().GetResult();
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

    private static LanSessionAdvertisement Sample() => new(
        "abyss-lobby", "0.1.0-a01", NetLimits.ProtocolVersion, 1, 4, NetLimits.GamePort, 4242);

    private static Task BeaconRoundtrip()
    {
        var info = Sample();
        Span<byte> buffer = stackalloc byte[NetLimits.BeaconMaxBytes];
        TestAssert.True(LanBeaconCodec.TryEncode(info, 1_700_000_000, buffer, out var size), "encode");
        TestAssert.True(buffer[..size].ToArray().Length <= NetLimits.BeaconMaxBytes, "bounded");
        TestAssert.True(LanBeaconCodec.TryDecode(buffer[..size].ToArray(), 1_700_000_000, out var decoded), "decode");
        TestAssert.Equal(info, decoded!, "roundtrip");
        return Task.CompletedTask;
    }

    private static Task OversizeName()
    {
        var info = Sample() with { LobbyName = new string('x', NetLimits.MaxLobbyNameChars + 1) };
        TestAssert.False(LanBeaconCodec.IsAdvertisable(info), "oversize lobby refused");
        Span<byte> buffer = stackalloc byte[NetLimits.BeaconMaxBytes];
        TestAssert.False(LanBeaconCodec.TryEncode(info, 1_700_000_000, buffer, out _), "encode refused");
        return Task.CompletedTask;
    }

    private static Task StaleBeacon()
    {
        Span<byte> buffer = stackalloc byte[NetLimits.BeaconMaxBytes];
        TestAssert.True(LanBeaconCodec.TryEncode(Sample(), 1_000, buffer, out var size), "encode");
        TestAssert.False(
            LanBeaconCodec.TryDecode(buffer[..size].ToArray(), 1_000 + NetLimits.BeaconTtlSeconds + 1, out _),
            "stale dropped");
        return Task.CompletedTask;
    }

    private static Task FutureBeacon()
    {
        Span<byte> buffer = stackalloc byte[NetLimits.BeaconMaxBytes];
        TestAssert.True(LanBeaconCodec.TryEncode(Sample(), 5_000, buffer, out var size), "encode");
        TestAssert.False(
            LanBeaconCodec.TryDecode(buffer[..size].ToArray(), 5_000 - NetLimits.BeaconFutureSkewSeconds - 1, out _),
            "future skew dropped");
        return Task.CompletedTask;
    }

    private static Task WrongMagic()
    {
        Span<byte> buffer = stackalloc byte[NetLimits.BeaconMaxBytes];
        TestAssert.True(LanBeaconCodec.TryEncode(Sample(), 1_000, buffer, out var size), "encode");
        var bytes = buffer[..size].ToArray();
        bytes[0] = (byte)'X';
        TestAssert.False(LanBeaconCodec.TryDecode(bytes, 1_000, out _), "bad magic dropped");
        return Task.CompletedTask;
    }

    private static Task TableExpiry()
    {
        var clock = new ManualClock();
        var table = new LanSessionTable(() => clock.Now);
        table.Upsert(new DiscoveredLanSession("a", "0.1.0-a01", 1, 1, 4, NetLimits.GamePort, 9, "127.0.0.1", clock.Now));
        TestAssert.Equal(1, table.Snapshot().Count, "visible");
        clock.Advance(TimeSpan.FromSeconds(NetLimits.BeaconTtlSeconds * 2 + 1));
        TestAssert.Equal(0, table.Snapshot().Count, "expired");
        return Task.CompletedTask;
    }

    private static async Task LoopbackAsync()
    {
        // Local-only wire check on loopback with OS-assigned ports; never touches
        // the real discovery port and never leaves the machine.
        var info = Sample();
        Span<byte> buffer = stackalloc byte[NetLimits.BeaconMaxBytes];
        TestAssert.True(LanBeaconCodec.TryEncode(info, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), buffer, out var size), "encode");
        var wire = buffer[..size].ToArray();

        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var sender = new UdpClient();
        var endpoint = (IPEndPoint)receiver.Client.LocalEndPoint!;
        await sender.SendAsync(wire, new IPEndPoint(IPAddress.Loopback, endpoint.Port));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = await receiver.ReceiveAsync(timeout.Token);
        TestAssert.True(
            LanBeaconCodec.TryDecode(received.Buffer, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), out var decoded),
            "loopback decode");
        TestAssert.Equal(info.LobbyName, decoded!.LobbyName, "lobby name");
        TestAssert.Equal(info.SessionId, decoded.SessionId, "session");
    }
}
