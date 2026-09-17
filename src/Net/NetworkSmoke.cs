using System.Net;
using System.Net.Sockets;
using AbyssScav.Protocol;
using Godot;

namespace AbyssScav.Net;

/// <summary>
/// Headless real-ENet integration smoke, one instance per process (localhost
/// only, nothing destructive). Run once with --mode=host and once with
/// --mode=client --port=N --workdir=D, both pointed at the same scene:
/// the host publishes its ephemeral port plus a ready file, then both sides
/// walk the session flow over real ENet stacks and pumps: handshake, lobby
/// sync, ready, run-start manifest, intent, snapshot, settlement-like event,
/// graceful disconnect, and token reconnect. Each process prints SMOKE PASS
/// and quits 0 only if its own invariants hold, else SMOKE FAIL and quit 1.
/// Never touches the main menu or game scenes.
/// </summary>
public partial class NetworkSmoke : Node
{
    private const string GameVersion = "0.1.0-a01";
    private const string Catalog = "smoke-catalog-a01";
    private const ulong SessionSeed = 987654321987ul;
    private const int StepTimeoutMs = 20000;
    private const int TotalTimeoutMs = 120000;

    private readonly List<NetworkSessionManager> _pumped = new();
    private readonly List<ulong> _hostSeenJoins = new();

    public override void _Ready() => RunAsync();

    private async void RunAsync()
    {
        var code = 1;
        try
        {
            var args = ParseArgs(OS.GetCmdlineUserArgs());
            var start = Time.GetTicksMsec();
            if (args.Mode == "host")
            {
                await RunHost(args.WorkDir, start);
            }
            else
            {
                await RunClient(args.WorkDir, start);
            }

            GD.Print($"SMOKE PASS ({args.Mode}): all real-ENet invariants held.");
            code = 0;
        }
        catch (Exception ex)
        {
            GD.Print("SMOKE FAIL: " + ex.Message);
        }
        finally
        {
            _pumped.Clear();
            GetTree().Quit(code);
        }
    }

    private async Task RunHost(string workDir, ulong start)
    {
        var port = FreeLoopbackUdpPort();
        var transport = new EnetTransport();
        transport.AttachMultiplayer(Multiplayer);
        var host = new NetworkSessionManager();
        host.AttachTransport(transport);
        host.PeerJoined += id => _hostSeenJoins.Add(id);
        _pumped.Add(host);

        await host.HostAsync(
            new LobbySettings("smoke-lobby", 2, true), "smoke-host", GameVersion, Catalog,
            new SessionOptions(port, 2, SessionSeed), CancellationToken.None);
        Check(host.SessionId == SessionSeed, "host session id");
        Check(host.LocalPeerId == 1, $"host ENet id is 1, got {host.LocalPeerId}");
        File.WriteAllText(Path.Combine(workDir, "port"), port.ToString());
        File.WriteAllText(Path.Combine(workDir, "ready"), "ready");
        GD.Print($"SMOKE-PORT:{port}");
        GD.Print($"SMOKE (host): listening on 127.0.0.1:{port}.");

        await PumpUntil(() => host.Players.Count == 2, "guest join", start);
        var guestId = host.Players.First(p => !p.IsHost).PeerId;
        Check(_hostSeenJoins.Contains(guestId), "authoritative join routing for guest");
        GD.Print($"SMOKE (host): guest joined as ENet id {guestId}.");

        await PumpUntil(() => host.Players.Any(p => p.PeerId == guestId && p.Ready), "guest ready", start);
        host.SetLocalReady(true);

        var manifest = new RunManifest(
            NetLimits.ProtocolVersion, GameVersion, 424242,
            "biome.trench", "contract.first_dive", "difficulty.standard",
            "layout-smoke", Catalog, new[] { "mod.smoke" });
        Check(host.TryStartRun(manifest, out var startError), "run start: " + startError);

        (ulong Peer, byte[] Data)? intent = null;
        host.IntentReceived += (peer, data) => intent = (peer, data);
        await PumpUntil(() => intent is not null, "intent from guest", start);
        Check(intent!.Value.Peer == guestId, $"authoritative intent sender {guestId}, got {intent.Value.Peer}");
        Check(intent.Value.Data.SequenceEqual(new byte[] { 10, 20, 30, 40 }), "intent bytes intact");
        GD.Print("SMOKE (host): intent verified with authoritative sender.");

        host.BroadcastSnapshot(new byte[] { 7, 7, 7, 7, 7, 7, 7, 7 });
        host.SendEvent(guestId, BitConverter.GetBytes(1500u));
        GD.Print("SMOKE (host): snapshot and settlement event sent.");

        await PumpUntil(() => host.Players.Count == 1, "guest disconnect", start);
        GD.Print("SMOKE (host): disconnect removed the seat.");

        await PumpUntil(() => host.Players.Count == 2, "guest reconnect", start);
        var seat = host.Players.First(p => !p.IsHost);
        Check(seat.DisplayName == "smoke-guest", "reconnected seat keeps display name");
        Check(seat.PeerId != guestId, "reconnected leg has a fresh ENet id");
        Check(!seat.Ready, "ready resets on reconnect");
        GD.Print($"SMOKE (host): token reconnect took over seat as {seat.PeerId}.");

        host.Shutdown();
        transport.Shutdown();
        transport.Dispose();
        _pumped.Remove(host);
    }

    private async Task RunClient(string workDir, ulong start)
    {
        var port = await WaitForPort(workDir, start);
        GD.Print($"SMOKE (client): dialing 127.0.0.1:{port}.");

        var transport = new EnetTransport();
        transport.AttachMultiplayer(Multiplayer);
        var client = new NetworkSessionManager();
        client.AttachTransport(transport);
        _pumped.Add(client);

        var join = client.JoinAsync(
            new SessionAddress("127.0.0.1", port), "smoke-guest", GameVersion, Catalog,
            CancellationToken.None);
        await PumpUntil(() => join.IsCompleted, "join handshake", start);
        await join;
        Check(client.SessionId == SessionSeed, "adopted session id");
        Check(client.LocalPeerId != 0 && client.LocalPeerId != 1, "unique client ENet id");
        GD.Print($"SMOKE (client): handshake accepted as ENet id {client.LocalPeerId}.");

        await PumpUntil(() => client.Players.Count == 2 && client.LocalReconnectToken is not null, "lobby sync + token", start);
        GD.Print("SMOKE (client): lobby synced; reconnect token issued.");

        RunManifest? manifest = null;
        client.RunStarted += m => manifest = m;
        client.SetLocalReady(true);
        await PumpUntil(() => manifest is not null, "manifest delivery", start);
        Check(manifest!.LayoutHash == "layout-smoke", "manifest layout hash");

        (ulong Peer, byte[] Data)? snap = null;
        (ulong Peer, byte[] Data)? settled = null;
        client.SnapshotReceived += (peer, data) => snap = (peer, data);
        client.EventReceived += (peer, data) => settled = (peer, data);
        client.SendIntent(1, new byte[] { 10, 20, 30, 40 });
        await PumpUntil(() => snap is not null && settled is not null, "snapshot + settlement", start);
        Check(snap!.Value.Peer == 1, "snapshot sender is host");
        Check(snap.Value.Data.SequenceEqual(new byte[] { 7, 7, 7, 7, 7, 7, 7, 7 }), "snapshot bytes intact");
        Check(settled!.Value.Peer == 1, "settlement sender is host");
        Check(BitConverter.ToUInt32(settled.Value.Data) == 1500u, "settlement amount intact");
        GD.Print("SMOKE (client): snapshot and settlement event verified from host authority.");

        var token = client.LocalReconnectToken;
        Check(!string.IsNullOrEmpty(token), "token present before disconnect");
        _pumped.Remove(client);
        client.Shutdown();
        transport.Shutdown();
        transport.Dispose();

        var reTransport = new EnetTransport();
        reTransport.AttachMultiplayer(Multiplayer);
        var reClient = new NetworkSessionManager();
        reClient.AttachTransport(reTransport);
        _pumped.Add(reClient);
        var rejoin = reClient.JoinAsync(
            new SessionAddress("127.0.0.1", port), "smoke-guest", GameVersion, Catalog,
            CancellationToken.None, token);
        await PumpUntil(() => rejoin.IsCompleted, "reconnect handshake", start);
        await rejoin;
        Check(reClient.SessionId == SessionSeed, "re-adopted session id");
        await PumpUntil(() => reClient.Players.Count == 2, "lobby resync", start);
        GD.Print($"SMOKE (client): reconnected as ENet id {reClient.LocalPeerId}; seat restored.");
        reClient.Shutdown();
        reTransport.Shutdown();
        reTransport.Dispose();
        _pumped.Remove(reClient);
    }

    private async Task PumpUntil(Func<bool> done, string step, ulong start)
    {
        var stepStart = Time.GetTicksMsec();
        while (!done())
        {
            foreach (var manager in _pumped.ToArray())
            {
                try
                {
                    manager.Poll();
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"smoke poll failed at {step}: {ex.Message}", ex);
                }
            }

            if (Time.GetTicksMsec() - stepStart > StepTimeoutMs)
            {
                throw new TimeoutException($"smoke timeout at {step} after {StepTimeoutMs}ms.");
            }

            if (Time.GetTicksMsec() - start > TotalTimeoutMs)
            {
                throw new TimeoutException($"smoke exceeded total budget of {TotalTimeoutMs}ms at {step}.");
            }

            await ToSignal(GetTree(), "process_frame");
        }

        foreach (var manager in _pumped.ToArray())
        {
            manager.Poll();
        }
    }

    private async Task<int> WaitForPort(string workDir, ulong start)
    {
        var readyPath = Path.Combine(workDir, "ready");
        var portPath = Path.Combine(workDir, "port");
        while (!File.Exists(readyPath) || !File.Exists(portPath))
        {
            if (Time.GetTicksMsec() - start > TotalTimeoutMs)
            {
                throw new TimeoutException("smoke client: host never became ready.");
            }

            await ToSignal(GetTree(), "process_frame");
        }

        var text = File.ReadAllText(portPath).Trim();
        if (!int.TryParse(text, out var port) || !SessionValidation.IsValidPort(port))
        {
            throw new InvalidOperationException("smoke client: host published an invalid port.");
        }

        return port;
    }

    private static (string Mode, string WorkDir) ParseArgs(string[] args)
    {
        string? mode = null;
        string? workDir = null;
        foreach (var arg in args)
        {
            if (arg.StartsWith("--mode=", StringComparison.Ordinal))
            {
                mode = arg["--mode=".Length..].Trim().ToLowerInvariant();
            }
            else if (arg.StartsWith("--workdir=", StringComparison.Ordinal))
            {
                workDir = arg["--workdir=".Length..].Trim().Trim('"');
            }
        }

        if (mode is not ("host" or "client"))
        {
            throw new InvalidOperationException("smoke requires --mode=host|client.");
        }

        if (string.IsNullOrEmpty(workDir) || !Directory.Exists(workDir))
        {
            throw new InvalidOperationException("smoke requires an existing --workdir=directory.");
        }

        return (mode, workDir);
    }

    private static void Check(bool condition, string what)
    {
        if (!condition)
        {
            throw new InvalidOperationException("smoke invariant failed: " + what);
        }
    }

    private static int FreeLoopbackUdpPort()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }
}
