using AbyssScav.Protocol;

namespace AbyssScav.Net.Tests;

internal static class LobbyTests
{
    private const string GameVersion = "0.1.0-a01";
    private const string Catalog = "catalog-hash-a01";

    public static int Run()
    {
        var cases = new (string Name, Func<Task> Test)[]
        {
            ("lobby_codec_roundtrip", LobbyCodecRoundtrip),
            ("host_and_join_handshake_flow", HostJoinFlow),
            ("handshake_reject_on_catalog_mismatch", CatalogReject),
            ("ready_and_run_start_flow", ReadyStartFlow),
            ("intent_and_snapshot_channels_routed", ChannelsRouted),
            ("spoofed_and_wrong_session_frames_dropped", FramesDropped),
            ("flood_is_rate_limited", FloodLimited),
            ("reconnect_claim_takes_over_seat", ReconnectFlow),
            ("snapshot_on_reliable_channel_dropped", ChannelMismatchDropped),
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

    private static Task LobbyCodecRoundtrip()
    {
        var players = new List<LobbyPlayer>
        {
            new(1, "host-diver", true, true),
            new(2, "guest", false, false),
        };
        var settings = new LobbySettings("abyss", 4, true);
        Span<byte> buffer = stackalloc byte[1024];
        TestAssert.True(LobbyCodecs.TryEncodeLobby(players, settings, buffer, out var size), "encode");
        TestAssert.True(LobbyCodecs.TryDecodeLobby(buffer[..size].ToArray(), out var decodedSettings, out var decodedPlayers), "decode");
        TestAssert.Equal(settings, decodedSettings!, "settings");
        TestAssert.True(players.SequenceEqual(decodedPlayers!), "players");
        return Task.CompletedTask;
    }

    private sealed class Session
    {
        public FakeHub Hub = new();
        public NetworkSessionManager Host = new();
        public NetworkSessionManager Client = new();
        public FakeTransport HostTransport;
        public FakeTransport ClientTransport;

        public Session()
        {
            HostTransport = new FakeTransport(Hub);
            ClientTransport = new FakeTransport(Hub);
            Host.AttachTransport(HostTransport);
            Client.AttachTransport(ClientTransport);
        }
    }

    private static async Task<Session> ConnectedSession(string clientName = "guest")
    {
        var session = new Session();
        await session.Host.HostAsync(
            new LobbySettings("abyss", 4, true), "host-diver", GameVersion, Catalog,
            new SessionOptions(NetLimits.GamePort, 4, 4242), CancellationToken.None);
        session.Host.Poll();
        var join = session.Client.JoinAsync(
            new SessionAddress("127.0.0.1", NetLimits.GamePort), clientName, GameVersion, Catalog,
            CancellationToken.None);
        session.Host.Poll();
        session.Client.Poll();
        session.Host.Poll();
        session.Client.Poll();
        await join;
        session.Host.Poll();
        session.Client.Poll();
        return session;
    }

    private static async Task HostJoinFlow()
    {
        var session = await ConnectedSession();
        TestAssert.True(session.Client.SessionId == 4242, "client adopted session");
        TestAssert.Equal(2, session.Host.Players.Count, "host sees two seats");
        TestAssert.Equal(2, session.Client.Players.Count, "client lobby synced");
        TestAssert.True(session.Client.LocalReconnectToken is not null, "reconnect token issued");
        session.Host.Shutdown();
        session.Client.Shutdown();
    }

    private static async Task CatalogReject()
    {
        var session = new Session();
        await session.Host.HostAsync(
            new LobbySettings("abyss", 4, true), "host-diver", GameVersion, Catalog,
            new SessionOptions(NetLimits.GamePort, 4, 4242), CancellationToken.None);
        try
        {
            var join = session.Client.JoinAsync(
                new SessionAddress("127.0.0.1", NetLimits.GamePort), "guest", GameVersion, "other-catalog",
                CancellationToken.None);
            session.Host.Poll();
            session.Client.Poll();
            session.Host.Poll();
            session.Client.Poll();
            await join;
            TestAssert.True(false, "catalog mismatch must reject");
        }
        catch (InvalidOperationException ex)
        {
            TestAssert.True(ex.Message.Contains(NetErrors.HandshakeRejected), "NET-003 surfaced: " + ex.Message);
        }
        finally
        {
            session.Host.Shutdown();
            session.Client.Shutdown();
        }
    }

    private static async Task ReadyStartFlow()
    {
        var session = await ConnectedSession();
        RunManifest? clientManifest = null;
        session.Client.RunStarted += manifest => clientManifest = manifest;

        session.Client.SetLocalReady(true);
        session.Host.Poll();
        session.Client.Poll();
        TestAssert.True(session.Host.Players.All(p => p.PeerId == 1 || p.Ready), "guest ready on host");

        // Start must wait for every seat.
        TestAssert.False(session.Host.TryStartRun(ManifestTests.Sample(), out _), "host not ready blocks start");
        session.Host.SetLocalReady(true);
        TestAssert.True(session.Host.TryStartRun(ManifestTests.Sample(), out var error), "start " + error);
        session.Client.Poll();
        TestAssert.NotNull(clientManifest, "client received manifest");
        TestAssert.Equal("layout-aaa", clientManifest!.LayoutHash, "layout hash");
        session.Host.Shutdown();
        session.Client.Shutdown();
    }

    private static async Task ChannelsRouted()
    {
        var session = await ConnectedSession();
        (ulong Peer, byte[] Data)? intent = null;
        (ulong Peer, byte[] Data)? snapshot = null;
        session.Host.IntentReceived += (peer, data) => intent = (peer, data);
        session.Client.SnapshotReceived += (peer, data) => snapshot = (peer, data);

        session.Client.SendIntent(1, new byte[] { 1, 2, 3 });
        session.Host.BroadcastSnapshot(new byte[] { 7, 7 });
        session.Host.Poll();
        session.Client.Poll();

        TestAssert.NotNull(intent, "intent arrived");
        TestAssert.Equal(session.Client.LocalPeerId, intent!.Value.Peer, "intent sender");
        TestAssert.True(intent.Value.Data.SequenceEqual(new byte[] { 1, 2, 3 }), "intent bytes");
        TestAssert.NotNull(snapshot, "snapshot arrived");
        TestAssert.Equal(1ul, snapshot!.Value.Peer, "snapshot sender");
        session.Host.Shutdown();
        session.Client.Shutdown();
    }

    private static async Task FramesDropped()
    {
        var session = await ConnectedSession();
        var intents = 0;
        session.Host.IntentReceived += (_, _) => intents++;

        // Spoofed sender header: transport reports the client, header claims host.
        var spoof = new byte[NetFrame.HeaderSize + 1];
        TestAssert.True(NetFrame.TryEncode(MessageType.PlayerIntent, 500, 4242, 1, new byte[] { 9 }, spoof, out _), "spoof encode");
        session.HostTransport.InjectRaw(session.Client.LocalPeerId, TransportChannel.Reliable, spoof);

        // Wrong session id with a valid sender.
        var alien = new byte[NetFrame.HeaderSize + 1];
        TestAssert.True(NetFrame.TryEncode(MessageType.PlayerIntent, 501, 9999, session.Client.LocalPeerId, new byte[] { 9 }, alien, out _), "alien encode");
        session.HostTransport.InjectRaw(session.Client.LocalPeerId, TransportChannel.Reliable, alien);

        session.Host.Poll();
        TestAssert.Equal(0, intents, "both forged frames dropped");
        session.Host.Shutdown();
        session.Client.Shutdown();
    }

    private static async Task FloodLimited()
    {
        var clock = new ManualClock();
        var session = new Session();
        session.Host = new NetworkSessionManager(() => clock.Now);
        session.Host.AttachTransport(session.HostTransport);
        await session.Host.HostAsync(
            new LobbySettings("abyss", 4, true), "host-diver", GameVersion, Catalog,
            new SessionOptions(NetLimits.GamePort, 4, 4242), CancellationToken.None);
        var join = session.Client.JoinAsync(
            new SessionAddress("127.0.0.1", NetLimits.GamePort), "guest", GameVersion, Catalog,
            CancellationToken.None);
        session.Host.Poll();
        session.Client.Poll();
        session.Host.Poll();
        session.Client.Poll();
        await join;

        var intents = 0;
        session.Host.IntentReceived += (_, _) => intents++;
        for (var i = 0; i < 200; i++)
        {
            session.Client.SendIntent(1, new byte[] { 1 });
        }

        session.Host.Poll();
        // One handshake-adjacent reliable packet may precede the burst; the
        // bound is the bucket size plus that small constant, never 200.
        TestAssert.True(intents <= (int)NetLimits.ReliableBurst + 4, $"flood capped, got {intents}");
        TestAssert.True(intents >= (int)NetLimits.ReliableBurst, $"burst admitted, got {intents}");
        session.Host.Shutdown();
        session.Client.Shutdown();
    }

    private static async Task ReconnectFlow()
    {
        var session = await ConnectedSession();
        var token = session.Client.LocalReconnectToken;
        TestAssert.NotNull(token, "token issued");

        // Drop the first client leg entirely.
        session.Client.Shutdown();
        session.ClientTransport.Shutdown();

        var client2Transport = new FakeTransport(session.Hub);
        var client2 = new NetworkSessionManager();
        client2.AttachTransport(client2Transport);
        var join = client2.JoinAsync(
            new SessionAddress("127.0.0.1", NetLimits.GamePort), "guest", GameVersion, Catalog,
            CancellationToken.None, token);
        for (var i = 0; i < 4; i++)
        {
            session.Host.Poll();
            client2.Poll();
        }

        await join;
        for (var i = 0; i < 4; i++)
        {
            session.Host.Poll();
            client2.Poll();
        }

        TestAssert.Equal(2, session.Host.Players.Count, "seat taken over, not duplicated");
        TestAssert.True(session.Host.Players.Any(p => p.PeerId == client2.LocalPeerId), "new leg holds seat");
        session.Host.Shutdown();
        client2.Shutdown();
    }

    private static async Task ChannelMismatchDropped()
    {
        var session = await ConnectedSession();
        var snapshots = 0;
        session.Client.SnapshotReceived += (_, _) => snapshots++;

        // Snapshot framing delivered over the reliable channel must be dropped.
        var frame = new byte[NetFrame.HeaderSize + 1];
        TestAssert.True(NetFrame.TryEncode(MessageType.Snapshot, 600, 4242, 1, new byte[] { 5 }, frame, out _), "encode");
        session.HostTransport.InjectRaw(1, TransportChannel.Reliable, frame);
        session.Client.Poll();
        TestAssert.Equal(0, snapshots, "channel mismatch dropped");
        session.Host.Shutdown();
        session.Client.Shutdown();
    }
}
