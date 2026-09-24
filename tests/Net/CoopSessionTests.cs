using AbyssScav.Protocol;

namespace AbyssScav.Net.Tests;

/// <summary>
/// Protocol v2 co-op run features (docs/02 §9.5-§12): ship pose and run-event
/// codecs, ships inside the world snapshot, reconnect token in the handshake,
/// mid-run seat reservation + takeover, join-in-progress admission, per-channel
/// replay windows, host-only routing, and the host-loss window timer.
/// </summary>
internal static class CoopSessionTests
{
    private const string GameVersion = "0.1.0-a01";
    private const string Catalog = "catalog-hash-a01";
    private const ulong Session = 4242;

    public static int Run()
    {
        var cases = new (string Name, Func<Task> Test)[]
        {
            ("ship_pose_codec_roundtrip", Sync(ShipPoseRoundtrip)),
            ("ship_pose_rejects_garbage", Sync(ShipPoseGarbage)),
            ("snapshot_carries_ships", Sync(SnapshotShips)),
            ("coop_event_codec_roundtrip_and_bounds", Sync(CoopEventCodecRoundtrip)),
            ("handshake_carries_reconnect_token", Sync(HandshakeToken)),
            ("manifest_carries_real_sha256_hashes", Sync(ManifestRealHashes)),
            ("admission_rules_for_running_dive", Sync(AdmissionRules)),
            ("token_refresh_and_liveness", Sync(TokenRefresh)),
            ("mid_run_drop_reserves_seat_then_takeover", MidRunTakeover),
            ("reserved_seat_released_after_grace", ReservedSeatExpires),
            ("active_peer_token_outlives_mint_grace", ActiveTokenStaysLive),
            ("join_in_progress_gets_manifest", JoinInProgress),
            ("join_in_progress_refused_when_off", JoinInProgressOff),
            ("join_in_progress_refused_in_final_sequence", JoinInProgressFinal),
            ("run_end_closes_reconnect", RunEndClosesReconnect),
            ("ship_pose_routed_to_host", ShipPoseRouted),
            ("reliable_not_dropped_behind_unreliable", CrossChannelReplay),
            ("client_drops_host_messages_from_non_host", RelaySpoofDropped),
            ("host_link_window_and_restore", Sync(HostLinkWindow)),
            ("host_link_silence_and_abandon", Sync(HostLinkSilence)),
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

    private static Func<Task> Sync(Action action) => () =>
    {
        action();
        return Task.CompletedTask;
    };

    // ------------------------------------------------------------------ codecs

    private static ShipPoseWire SamplePose(uint peer = 2) =>
        new(peer, 1f, -2f, 3f, 0f, 0.7071f, 0f, 0.7071f, 4f, 0f, -1f, 87, ShipPoseFlags.Quiet | ShipPoseFlags.Drilling);

    private static void ShipPoseRoundtrip()
    {
        var pose = SamplePose();
        Span<byte> buffer = stackalloc byte[MessageBounds.MaxPayload(MessageType.ShipPose)];
        TestAssert.True(ShipPoseCodec.TryEncodeMessage(Session, pose, buffer, out var size), "encode");
        TestAssert.Equal(ShipPoseCodec.MessageBytes, size, "fixed size");
        TestAssert.True(ShipPoseCodec.TryDecodeMessage(buffer[..size].ToArray(), out var session, out var decoded), "decode");
        TestAssert.Equal(Session, session, "session");
        TestAssert.Equal(pose, decoded!, "roundtrip");
        TestAssert.False(MessageBounds.IsReliable(MessageType.ShipPose), "poses are unreliable");
    }

    private static void ShipPoseGarbage()
    {
        Span<byte> buffer = stackalloc byte[MessageBounds.MaxPayload(MessageType.ShipPose)];
        TestAssert.False(ShipPoseCodec.TryEncodeMessage(Session, SamplePose() with { X = float.NaN }, buffer, out _), "NaN refused");
        TestAssert.False(ShipPoseCodec.TryEncodeMessage(Session, SamplePose() with { HullPercent = 101 }, buffer, out _), "hull > 100 refused");
        TestAssert.False(ShipPoseCodec.TryEncodeMessage(NetLimits.InvalidId, SamplePose(), buffer, out _), "session zero refused");

        TestAssert.True(ShipPoseCodec.TryEncodeMessage(Session, SamplePose(), buffer, out var size), "baseline");
        var bytes = buffer[..size].ToArray();
        var badFlags = (byte[])bytes.Clone();
        badFlags[^1] = 0x80;
        TestAssert.False(ShipPoseCodec.TryDecodeMessage(badFlags, out _, out _), "unknown flag bit refused");
        var nan = (byte[])bytes.Clone();
        BitConverter.TryWriteBytes(nan.AsSpan(8 + 4), float.PositiveInfinity);
        TestAssert.False(ShipPoseCodec.TryDecodeMessage(nan, out _, out _), "non-finite refused on decode");
        TestAssert.False(ShipPoseCodec.TryDecodeMessage(bytes[..^1], out _, out _), "truncated refused");
    }

    private static WorldSnapshot BaseSnapshot(IReadOnlyList<ShipPoseWire>? ships) => new(
        Session, 0, null, null, 0f, 120, new byte[] { 1 }, new byte[] { 0 }, 1, 2, 0f, -1, 0f,
        new[] { new CreatureWire(1f, 2f, 3f, 1, 0f, 0f, 0f) }, ships);

    private static void SnapshotShips()
    {
        var ships = new[] { SamplePose(1), SamplePose(5) with { Flags = ShipPoseFlags.Extracted } };
        Span<byte> buffer = stackalloc byte[MessageBounds.MaxPayload(MessageType.Snapshot)];
        TestAssert.True(WorldSnapshotCodec.TryEncode(BaseSnapshot(ships), buffer, out var size), "encode");
        TestAssert.True(WorldSnapshotCodec.TryDecode(buffer[..size].ToArray(), out var decoded), "decode");
        TestAssert.Equal(2, decoded!.ShipList.Count, "ship count");
        TestAssert.Equal(ships[1], decoded.ShipList[1], "ship roundtrip");

        TestAssert.True(WorldSnapshotCodec.TryEncode(BaseSnapshot(null), buffer, out var emptySize), "null ships encode as empty");
        TestAssert.True(WorldSnapshotCodec.TryDecode(buffer[..emptySize].ToArray(), out var none) && none!.ShipList.Count == 0, "empty list");

        var tooMany = Enumerable.Range(1, NetLimits.MaxPeers + 1).Select(i => SamplePose((uint)i)).ToArray();
        TestAssert.False(WorldSnapshotCodec.TryEncode(BaseSnapshot(tooMany), buffer, out _), "more ships than seats refused");
        TestAssert.False(WorldSnapshotCodec.TryEncode(BaseSnapshot(new[] { SamplePose() with { Qw = float.NaN } }), buffer, out _), "malformed ship refused");

        // Worst case (all caps) still fits the fixed snapshot bound.
        var worst = BaseSnapshot(Enumerable.Range(1, NetLimits.MaxPeers).Select(i => SamplePose((uint)i)).ToArray()) with
        {
            FailureReason = new string('r', NetLimits.MaxReasonBytes),
            MajorEventId = new string('e', NetLimits.MaxIdBytes),
            SalvagedLootMask = new byte[NetLimits.MaxLootSpawns / 8],
            ServicedNodesMask = new byte[NetLimits.MaxWorldNodes / 8],
            Creatures = Enumerable.Range(0, NetLimits.MaxCreatures).Select(i => new CreatureWire(i, 0f, 0f, 1, 0f, 0f, 0f)).ToArray(),
        };
        TestAssert.True(WorldSnapshotCodec.TryEncode(worst, buffer, out var worstSize), "worst case encodes");
        Span<byte> evtBuffer = stackalloc byte[MessageBounds.MaxPayload(MessageType.GameEvent)];
        TestAssert.True(AbyssScav.Protocol.CoopEventCodec.TryEncode(new CoopEvent(Session, CoopEventType.FinalSnapshot, null, buffer[..worstSize].ToArray()), evtBuffer, out _),
            "worst-case snapshot fits a reliable final-snapshot event");
    }

    private static void CoopEventCodecRoundtrip()
    {
        Span<byte> buffer = stackalloc byte[MessageBounds.MaxPayload(MessageType.GameEvent)];
        var refused = new CoopEvent(Session, CoopEventType.ExtractRefused, "Not in the extraction zone.", null);
        TestAssert.True(AbyssScav.Protocol.CoopEventCodec.TryEncode(refused, buffer, out var size), "encode refused");
        TestAssert.True(AbyssScav.Protocol.CoopEventCodec.TryDecode(buffer[..size].ToArray(), out var decoded), "decode refused");
        TestAssert.Equal(refused.Text, decoded!.Text, "reason text");
        TestAssert.True(decoded.Body is null, "no body");

        var body = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();
        var final = new CoopEvent(Session, CoopEventType.FinalSnapshot, null, body);
        TestAssert.True(AbyssScav.Protocol.CoopEventCodec.TryEncode(final, buffer, out var finalSize), "encode final");
        TestAssert.True(AbyssScav.Protocol.CoopEventCodec.TryDecode(buffer[..finalSize].ToArray(), out var finalDecoded), "decode final");
        TestAssert.True(finalDecoded!.Body!.SequenceEqual(body), "body intact");

        TestAssert.False(AbyssScav.Protocol.CoopEventCodec.TryEncode(final with { Body = null }, buffer, out _), "final snapshot needs a body");
        TestAssert.False(AbyssScav.Protocol.CoopEventCodec.TryEncode(refused with { Text = new string('x', NetLimits.MaxReasonBytes + 1) }, buffer, out _), "long reason refused");
        TestAssert.False(AbyssScav.Protocol.CoopEventCodec.TryEncode(refused with { Type = CoopEventType.Unknown }, buffer, out _), "unknown type refused");
        var badType = buffer[..size].ToArray();
        badType[8] = 99;
        TestAssert.False(AbyssScav.Protocol.CoopEventCodec.TryDecode(badType, out _), "undefined type refused on decode");
        TestAssert.False(AbyssScav.Protocol.CoopEventCodec.TryDecode(buffer[..(finalSize - 1)].ToArray(), out _), "truncated body refused");
    }

    private static void HandshakeToken()
    {
        var request = new HandshakeRequest(NetLimits.ProtocolVersion, GameVersion, Catalog, "diver", "dG9rZW4tdG9rZW4tdG9rZW4=");
        Span<byte> buffer = stackalloc byte[MessageBounds.MaxPayload(MessageType.HandshakeRequest)];
        TestAssert.True(HandshakeCodec.TryEncodeRequest(request, buffer, out var size), "encode");
        TestAssert.True(HandshakeCodec.TryDecodeRequest(buffer[..size].ToArray(), out var decoded), "decode");
        TestAssert.Equal(request, decoded!, "token roundtrip");

        var worst = new HandshakeRequest(NetLimits.ProtocolVersion, new string('v', NetLimits.MaxGameVersionBytes),
            new string('c', NetLimits.MaxCatalogHashBytes), new string('n', NetLimits.MaxDisplayNameChars),
            new string('t', NetLimits.MaxReconnectTokenBytes));
        TestAssert.True(HandshakeCodec.TryEncodeRequest(worst, buffer, out _), "worst case fits the handshake bound");
        TestAssert.False(HandshakeCodec.TryEncodeRequest(worst with { ReconnectToken = new string('t', NetLimits.MaxReconnectTokenBytes + 1) }, buffer, out _), "oversized token refused");
    }

    private static void ManifestRealHashes()
    {
        // Domain hashes are "sha256:" + 64 hex = 71 bytes (over the 64-byte id cap).
        var layout = "sha256:" + new string('a', 64);
        var catalog = "sha256:" + new string('b', 64);
        var manifest = ManifestTests.Sample() with { LayoutHash = layout, CatalogHash = catalog };
        Span<byte> buffer = stackalloc byte[MessageBounds.MaxPayload(MessageType.RunManifest)];
        TestAssert.True(RunManifestCodec.TryEncode(manifest, buffer, out var size), "real hashes encode");
        TestAssert.True(RunManifestCodec.TryDecode(buffer[..size].ToArray(), out var decoded), "decode");
        TestAssert.Equal(layout, decoded!.LayoutHash, "layout hash intact");
        TestAssert.Equal(catalog, decoded.CatalogHash, "catalog hash intact");
        TestAssert.False(RunManifestCodec.TryEncode(manifest with { BiomeId = new string('x', NetLimits.MaxIdBytes + 1) }, buffer, out _),
            "id fields keep the id cap");
    }

    private static void AdmissionRules()
    {
        var request = new HandshakeRequest(NetLimits.ProtocolVersion, GameVersion, Catalog, "late");
        var running = new HostHandshakePolicy(NetLimits.ProtocolVersion, Catalog, 2, 4, true, RunInProgress: true);
        TestAssert.Equal(HandshakeRejectReason.JoinClosed, HandshakeValidator.Validate(request, running, 7).Reason, "late join refused unless the host opened it");
        TestAssert.True(HandshakeValidator.Validate(request, running with { JoinInProgressAllowed = true }, 7).Accepted, "late join admitted when opened");
        TestAssert.Equal(HandshakeRejectReason.RunFinalSequence,
            HandshakeValidator.Validate(request, running with { JoinInProgressAllowed = true, RunFinalSequence = true }, 7).Reason,
            "final extraction sequence closes late join");

        var reconnect = running with { CurrentPlayers = 4, JoinAllowed = false, RunFinalSequence = true, ReconnectTokenValid = true, ReconnectSeatHeld = true };
        TestAssert.True(HandshakeValidator.Validate(request, reconnect, 7).Accepted, "held seat restores through full/closed/final");
        TestAssert.False(HandshakeValidator.Validate(request with { ProtocolVersion = 1 }, reconnect, 7).Accepted, "protocol still checked for reconnects");
        TestAssert.False(HandshakeValidator.Validate(request, reconnect with { ReconnectSeatHeld = false }, 7).Accepted, "released seat + full lobby refused");
    }

    private static void TokenRefresh()
    {
        var clock = new ManualClock();
        var store = new ReconnectTokenStore(() => clock.Now);
        var token = store.Issue(2, 77, "diver");
        clock.Advance(TimeSpan.FromSeconds(NetLimits.ReconnectGraceSeconds - 10));
        TestAssert.True(store.Refresh(2, 77), "refresh live token");
        clock.Advance(TimeSpan.FromSeconds(NetLimits.ReconnectGraceSeconds - 10));
        TestAssert.True(store.IsLive(2, 77), "grace restarted by refresh");
        TestAssert.True(store.TryTakeover(token, 77, out _, out _), "takeover after refresh");
        TestAssert.False(store.Refresh(3, 77), "unknown peer has nothing to refresh");
        clock.Advance(TimeSpan.FromSeconds(NetLimits.ReconnectGraceSeconds + 1));
        TestAssert.False(store.IsLive(2, 77), "expired without refresh");
        TestAssert.Equal(0, store.ActiveCount, "expired token dropped");
    }

    // ------------------------------------------------------- session manager

    private sealed class Rig
    {
        public readonly ManualClock Clock = new();
        public readonly FakeHub Hub = new();
        public readonly NetworkSessionManager Host;
        public readonly FakeTransport HostTransport;

        public Rig()
        {
            Host = new NetworkSessionManager(() => Clock.Now);
            HostTransport = new FakeTransport(Hub);
            Host.AttachTransport(HostTransport);
        }

        public async Task StartHost(LobbySettings settings)
        {
            await Host.HostAsync(settings, "host-diver", GameVersion, Catalog,
                new SessionOptions(NetLimits.GamePort, settings.MaxPlayers, Session), CancellationToken.None);
        }

        public async Task<(NetworkSessionManager Client, FakeTransport Transport)> Join(string name, string? token = null)
        {
            var transport = new FakeTransport(Hub);
            var client = new NetworkSessionManager(() => Clock.Now);
            client.AttachTransport(transport);
            var join = client.JoinAsync(new SessionAddress("127.0.0.1", NetLimits.GamePort), name, GameVersion, Catalog,
                CancellationToken.None, token);
            for (var i = 0; i < 4 && !join.IsCompleted; i++)
            {
                Host.Poll();
                client.Poll();
            }

            await join;
            Pump(client);
            return (client, transport);
        }

        public void Pump(params NetworkSessionManager[] clients)
        {
            for (var i = 0; i < 3; i++)
            {
                Host.Poll();
                foreach (var c in clients)
                {
                    c.Poll();
                }
            }
        }

        public void StartRun(params NetworkSessionManager[] clients)
        {
            foreach (var c in clients)
            {
                c.SetLocalReady(true);
            }

            Pump(clients);
            Host.SetLocalReady(true);
            if (!Host.TryStartRun(ManifestTests.Sample(), out var error))
            {
                throw new Exception("run start failed: " + error);
            }

            Pump(clients);
        }
    }

    private static async Task MidRunTakeover()
    {
        var rig = new Rig();
        await rig.StartHost(new LobbySettings("abyss", 2, true));
        var (client, transport) = await rig.Join("guest");
        rig.StartRun(client);
        var oldPeer = client.LocalPeerId;
        var token = client.LocalReconnectToken;
        TestAssert.NotNull(token, "token issued");

        (ulong Old, ulong New)? takeover = null;
        rig.Host.PeerReconnected += (o, n) => takeover = (o, n);
        client.Shutdown();
        rig.Pump();
        TestAssert.Equal(1, rig.Host.Players.Count, "dropped seat leaves the live list");
        TestAssert.Equal(1, rig.Host.ReservedSeats.Count, "seat held for reconnect");

        // Joins are now closed and the 2-seat lobby is full with the held seat:
        // only the token may restore it.
        TestAssert.True(rig.Host.UpdateLobby(new LobbySettings("abyss", 2, false), out _), "close joins");
        var (back, _) = await rig.Join("guest", token);
        rig.Pump(back);
        TestAssert.Equal(2, rig.Host.Players.Count, "seat restored");
        TestAssert.Equal(0, rig.Host.ReservedSeats.Count, "reservation consumed");
        TestAssert.True(takeover is { } t && t.Old == oldPeer && t.New == back.LocalPeerId, "takeover reported old→new");
        TestAssert.True(back.ActiveManifest is not null && back.ActiveManifest.LayoutHash == ManifestTests.Sample().LayoutHash,
            "manifest re-sent to the reconnected peer");
        TestAssert.True(back.LocalReconnectToken is not null && back.LocalReconnectToken != token, "token rotated");
        TestAssert.False(rig.Host.Players.Single(p => p.PeerId == back.LocalPeerId).Ready, "ready resets on takeover");

        var stranger = new NetworkSessionManager(() => rig.Clock.Now);
        stranger.AttachTransport(new FakeTransport(rig.Hub));
        var strangerJoin = stranger.JoinAsync(new SessionAddress("127.0.0.1", NetLimits.GamePort), "x", GameVersion, Catalog,
            CancellationToken.None, token);
        rig.Pump(stranger);
        await ExpectRejected(strangerJoin, "a consumed token cannot be replayed");
        transport.Shutdown();
    }

    private static async Task ReservedSeatExpires()
    {
        var rig = new Rig();
        await rig.StartHost(new LobbySettings("abyss", 2, true, JoinInProgress: true));
        var (client, _) = await rig.Join("guest");
        rig.StartRun(client);
        var token = client.LocalReconnectToken;
        client.Shutdown();
        rig.Pump();
        TestAssert.Equal(1, rig.Host.ReservedSeats.Count, "held");

        rig.Clock.Advance(TimeSpan.FromSeconds(NetLimits.ReconnectGraceSeconds + 1));
        rig.Host.Poll();
        TestAssert.Equal(0, rig.Host.ReservedSeats.Count, "released after grace");

        // The seat is free again for a late joiner; the stale token grants nothing special.
        var (late, _) = await rig.Join("late");
        TestAssert.Equal(2, rig.Host.Players.Count, "late joiner took the freed seat");
        TestAssert.True(rig.Host.Players.Any(p => p.PeerId == late.LocalPeerId && p.DisplayName == "late"), "fresh seat, fresh name");
        TestAssert.NotNull(token, "token existed");
    }

    private static async Task ActiveTokenStaysLive()
    {
        var rig = new Rig();
        await rig.StartHost(new LobbySettings("abyss", 2, true));
        var (client, _) = await rig.Join("guest");
        rig.StartRun(client);
        var token = client.LocalReconnectToken;
        for (var i = 0; i < 3; i++)
        {
            rig.Clock.Advance(TimeSpan.FromSeconds(NetLimits.ReconnectGraceSeconds - 20));
            client.SendIntent(1, new byte[] { 1 });
            rig.Pump(client);
        }

        client.Shutdown();
        rig.Pump();
        TestAssert.Equal(1, rig.Host.ReservedSeats.Count, "long-running peer's seat is still held at disconnect");
        TestAssert.True(rig.Host.UpdateLobby(new LobbySettings("abyss", 2, false), out _), "close joins");
        var (back, _) = await rig.Join("guest", token);
        TestAssert.True(rig.Host.Players.Any(p => p.PeerId == back.LocalPeerId), "reconnect after a run longer than the grace");
    }

    private static async Task JoinInProgress()
    {
        var rig = new Rig();
        await rig.StartHost(new LobbySettings("abyss", 4, true, JoinInProgress: true));
        var (first, _) = await rig.Join("first");
        rig.StartRun(first);

        var (late, _) = await rig.Join("late");
        TestAssert.Equal(3, rig.Host.Players.Count, "late joiner seated");
        TestAssert.True(late.HasStartedRun, "late joiner is in the run");
        TestAssert.True(late.ActiveManifest is not null && late.ActiveManifest.RunSeed == ManifestTests.Sample().RunSeed, "manifest delivered after the handshake");
        TestAssert.NotNull(late.LocalReconnectToken, "late joiner holds a token too");
    }

    private static async Task JoinInProgressOff()
    {
        var rig = new Rig();
        await rig.StartHost(new LobbySettings("abyss", 4, true));
        var (first, _) = await rig.Join("first");
        rig.StartRun(first);
        await ExpectRejected(JoinOnly(rig, "late"), "running dive closed to late joiners by default");
    }

    private static async Task JoinInProgressFinal()
    {
        var rig = new Rig();
        await rig.StartHost(new LobbySettings("abyss", 4, true, JoinInProgress: true));
        var (first, _) = await rig.Join("first");
        rig.StartRun(first);
        rig.Host.SetRunFinalSequence();
        await ExpectRejected(JoinOnly(rig, "late"), "final extraction sequence refuses late joiners");
        TestAssert.True(rig.Host.RunFinalSequence, "flag set");
    }

    private static async Task RunEndClosesReconnect()
    {
        var rig = new Rig();
        await rig.StartHost(new LobbySettings("abyss", 2, true));
        var (client, _) = await rig.Join("guest");
        rig.StartRun(client);
        var token = client.LocalReconnectToken;
        client.Shutdown();
        rig.Pump();
        rig.Host.EndRunAdmission();
        TestAssert.Equal(0, rig.Host.ReservedSeats.Count, "held seats released at run end");
        await ExpectRejected(JoinOnly(rig, "guest", token), "tokens discarded at run end");
    }

    private static async Task ShipPoseRouted()
    {
        var rig = new Rig();
        await rig.StartHost(new LobbySettings("abyss", 2, true));
        var (client, _) = await rig.Join("guest");
        (ulong Peer, byte[] Data)? received = null;
        rig.Host.ShipPoseReceived += (peer, data) => received = (peer, data);
        Span<byte> buffer = stackalloc byte[ShipPoseCodec.MessageBytes];
        TestAssert.True(ShipPoseCodec.TryEncodeMessage(client.SessionId, SamplePose(999), buffer, out var size), "encode");
        client.SendShipPose(buffer[..size]);
        rig.Pump(client);
        TestAssert.True(received is { } r && r.Peer == client.LocalPeerId, "authoritative sender attached");
        var threw = false;
        try
        {
            rig.Host.SendShipPose(buffer[..size].ToArray());
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        TestAssert.True(threw, "the host never sends a standalone pose");
    }

    private static async Task CrossChannelReplay()
    {
        var rig = new Rig();
        await rig.StartHost(new LobbySettings("abyss", 2, true));
        var (client, _) = await rig.Join("guest");
        var intents = 0;
        var poses = 0;
        rig.Host.IntentReceived += (_, _) => intents++;
        rig.Host.ShipPoseReceived += (_, _) => poses++;
        var sender = client.LocalPeerId;

        Span<byte> pose = stackalloc byte[ShipPoseCodec.MessageBytes];
        ShipPoseCodec.TryEncodeMessage(Session, SamplePose(), pose, out var poseSize);
        var unreliable = new byte[NetFrame.HeaderSize + poseSize];
        TestAssert.True(NetFrame.TryEncode(MessageType.ShipPose, 5001, Session, sender, pose[..poseSize], unreliable, out _), "pose frame");
        var reliable = new byte[NetFrame.HeaderSize + 3];
        TestAssert.True(NetFrame.TryEncode(MessageType.PlayerIntent, 5000, Session, sender, new byte[] { 1, 2, 3 }, reliable, out _), "intent frame");

        // The newer unreliable frame overtakes the older reliable one (different ENet channels).
        rig.HostTransport.InjectRaw(sender, TransportChannel.Unreliable, unreliable);
        rig.HostTransport.InjectRaw(sender, TransportChannel.Reliable, reliable);
        rig.Host.Poll();
        TestAssert.Equal(1, poses, "pose accepted");
        TestAssert.Equal(1, intents, "older reliable frame still accepted");

        rig.HostTransport.InjectRaw(sender, TransportChannel.Reliable, reliable);
        rig.Host.Poll();
        TestAssert.Equal(1, intents, "exact replay still dropped");
    }

    private static async Task RelaySpoofDropped()
    {
        var rig = new Rig();
        await rig.StartHost(new LobbySettings("abyss", 3, true));
        var (a, aTransport) = await rig.Join("a");
        var (b, _) = await rig.Join("b");
        rig.Pump(a, b);
        var snapshots = 0;
        a.SnapshotReceived += (_, _) => snapshots++;
        // Another client relaying a "snapshot" through the server: consistent header, wrong authority.
        var frame = new byte[NetFrame.HeaderSize + 2];
        TestAssert.True(NetFrame.TryEncode(MessageType.Snapshot, 9000, Session, b.LocalPeerId, new byte[] { 1, 2 }, frame, out _), "encode");
        aTransport.InjectRaw(b.LocalPeerId, TransportChannel.Unreliable, frame);
        a.Poll();
        TestAssert.Equal(0, snapshots, "snapshots only from the host");
    }

    // ------------------------------------------------------------ host link

    private static void HostLinkWindow()
    {
        var monitor = new HostLinkMonitor();
        monitor.OnHostHeard(1.0);
        TestAssert.Equal(HostLinkState.Connected, monitor.Update(2.0), "connected");
        monitor.OnHostDisconnected(2.0);
        TestAssert.Equal(HostLinkState.Reconnecting, monitor.Update(2.1), "window open");
        TestAssert.True(Math.Abs(monitor.WindowRemaining(4.0) - (NetLimits.HostLossWindowSeconds - 2.0)) < 1e-9, "remaining");
        TestAssert.True(monitor.OnReconnected(5.0), "restored inside the window");
        TestAssert.Equal(HostLinkState.Connected, monitor.Update(5.1), "back to connected");

        monitor.OnHostDisconnected(20.0);
        TestAssert.Equal(HostLinkState.Reconnecting, monitor.Update(20.0 + NetLimits.HostLossWindowSeconds - 0.01), "still inside");
        TestAssert.Equal(HostLinkState.Lost, monitor.Update(20.0 + NetLimits.HostLossWindowSeconds), "expired at 8 s");
        TestAssert.False(monitor.OnReconnected(30.0), "a late reconnect cannot resume");
        monitor.OnHostHeard(31.0);
        TestAssert.Equal(HostLinkState.Lost, monitor.Update(31.0), "lost is terminal");
        TestAssert.Equal("disconnect", monitor.LossCause, "cause kept");
    }

    private static void HostLinkSilence()
    {
        var monitor = new HostLinkMonitor();
        TestAssert.Equal(HostLinkState.Connected, monitor.Update(1000.0), "no silence alarm before the host was ever heard");
        monitor.OnHostHeard(1000.0);
        TestAssert.Equal(HostLinkState.Connected, monitor.Update(1000.0 + NetLimits.HostSilenceSeconds - 0.1), "quiet but alive");
        TestAssert.Equal(HostLinkState.Reconnecting, monitor.Update(1000.0 + NetLimits.HostSilenceSeconds), "silence opens the window");
        TestAssert.Equal("silence", monitor.LossCause, "silence cause");

        var abandoned = new HostLinkMonitor();
        abandoned.OnHostHeard(1.0);
        abandoned.Abandon("manifest-mismatch");
        TestAssert.Equal(HostLinkState.Lost, abandoned.Update(1.1), "abandon ends the window");
    }

    // --------------------------------------------------------------- helpers

    private static async Task JoinOnly(Rig rig, string name, string? token = null)
    {
        var client = new NetworkSessionManager(() => rig.Clock.Now);
        client.AttachTransport(new FakeTransport(rig.Hub));
        var join = client.JoinAsync(new SessionAddress("127.0.0.1", NetLimits.GamePort), name, GameVersion, Catalog,
            CancellationToken.None, token);
        for (var i = 0; i < 4 && !join.IsCompleted; i++)
        {
            rig.Host.Poll();
            client.Poll();
        }

        await join;
    }

    private static async Task ExpectRejected(Task join, string what)
    {
        try
        {
            await join;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains(NetErrors.HandshakeRejected, StringComparison.Ordinal))
        {
            return;
        }

        throw new Exception("Expected handshake rejection: " + what);
    }
}
