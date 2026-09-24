using System.Net;
using System.Net.Sockets;
using AbyssScav.App;
using AbyssScav.Domain;
using AbyssScav.Foundation;
using AbyssScav.Net;
using AbyssScav.Persistence;
using AbyssScav.Protocol;
using Godot;
using ProtocolManifest = AbyssScav.Protocol.RunManifest;

namespace AbyssScav.Gameplay;

/// <summary>
/// Headless two-process co-op <b>run</b> smoke (docs/02 §9.5, §11, §12): launch
/// once with --mode=host and once with --mode=client, both with the same
/// --workdir. Each process boots isolated services under workdir/&lt;mode&gt;
/// (own profile, own logs, no session lock), plays the real lobby handshake over
/// ENet on 127.0.0.1 and loads the real run scene. Verified in-engine:
/// remote proxies appear on both sides and follow the other ship, the teammate
/// sonar overlay exists, proxies are collider-less, a forced client link drop
/// is recovered by the in-run token reconnect inside the 8 s window (the host
/// re-keys the ship entity), and — after the host process exits — the client
/// settles host loss exactly once through the ledger (neither a completed nor
/// a failed run). Prints SMOKE PASS/FAIL and quits 0/1. Debug harness only.
/// </summary>
public partial class CoopRunSmoke : Node
{
    private const int StepTimeoutMs = 30000;
    private const string Contract = "contract.blackbox_recovery";
    private const ulong Seed = 20260924UL;

    private string _work = string.Empty;
    private GodotNetworkSession? _session;
    private ContentCatalog? _catalog;

    public override void _Ready() => RunAsync();

    private async void RunAsync()
    {
        var code = 1;
        var mode = "?";
        try
        {
            var args = OS.GetCmdlineUserArgs();
            mode = Arg(args, "--mode=") ?? throw new InvalidOperationException("--mode=host|client required.");
            _work = Arg(args, "--workdir=") ?? throw new InvalidOperationException("--workdir= required.");
            if (mode is not ("host" or "client") || !Directory.Exists(_work))
            {
                throw new InvalidOperationException("bad --mode or missing --workdir.");
            }

            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            InitServices(Path.Combine(_work, mode));
            if (!ContentCatalog.TryBuild(out var catalog, out var errors) || catalog is null)
            {
                throw new InvalidOperationException("catalog: " + string.Join("; ", errors));
            }

            _catalog = catalog;
            _session = new GodotNetworkSession { Name = "CoopSession" };
            GetTree().Root.AddChild(_session);
            _session.ConfigureSessionIdentity(GameVersion.Current, catalog.CatalogHash);
            CoopLaunchContext.Session = _session;
            if (mode == "host")
            {
                await RunHost();
            }
            else
            {
                await RunClient();
            }

            GD.Print($"SMOKE PASS ({mode}): co-op run invariants held.");
            code = 0;
        }
        catch (Exception ex)
        {
            GD.Print($"SMOKE FAIL ({mode}): {ex.Message}");
        }
        finally
        {
            GetTree().Quit(code);
        }
    }

    // ------------------------------------------------------------------ host

    private async Task RunHost()
    {
        var session = _session!;
        var port = FreeLoopbackUdpPort();
        await session.HostLobbyAsync(new LobbySettings("coop-smoke", 2, true), "smoke-host", port, CancellationToken.None);
        File.WriteAllText(Path.Combine(_work, "port"), port.ToString());
        Say("host", $"lobby on 127.0.0.1:{port}");

        await Until(() => session.Players.Count == 2 && session.Players.All(p => p.IsHost || p.Ready), "guest joined + ready");
        var guest = session.Players.First(p => !p.IsHost).PeerId;
        var options = Options();
        var manifest = Manifest(options);
        session.SetReady(true);
        Check(session.StartRun(manifest, out var startError), "run start: " + startError);
        RunLaunchContext.Pending = options;
        var run = LoadRun();
        var sub = await LocalSub(run);
        sub.TestDriveWorld = new Vector3(0f, 0f, -6f);

        var proxy = await VisibleProxy(run, guest, "host sees the guest ship");
        Check(proxy is not PhysicsBody3D && proxy.FindChildren("*", nameof(CollisionShape3D), true, false).Count == 0,
            "proxy is collider-less");
        await Moves(proxy, "guest proxy follows the guest ship");
        File.WriteAllText(Path.Combine(_work, "host_saw_guest"), guest.ToString());

        // The guest now drops its own link and must come back on a new peer id.
        await UntilFile("client_reconnected", "guest reconnect report");
        var newGuest = ulong.Parse(File.ReadAllText(Path.Combine(_work, "client_reconnected")).Trim());
        Check(newGuest != guest, "guest came back on a fresh peer id");
        await Until(() => session.Players.Any(p => p.PeerId == newGuest), "seat taken over by the new leg");
        var rekeyed = await VisibleProxy(run, newGuest, "ship entity re-keyed to the new peer");
        Check(ReferenceEquals(rekeyed, proxy), "the same proxy entity was taken over (no duplicate)");
        File.WriteAllText(Path.Combine(_work, "host_done"), "done");
        Say("host", "exiting mid-run to trigger host loss on the guest");
    }

    // ---------------------------------------------------------------- client

    private async Task RunClient()
    {
        var session = _session!;
        var port = int.Parse(await ReadFile("port"));
        await session.JoinLobbyAsync("127.0.0.1", port, "smoke-guest", CancellationToken.None);
        session.SetReady(true);
        await Until(() => session.CurrentManifest is not null, "manifest");
        var options = StageClient(session.CurrentManifest!);
        RunLaunchContext.Pending = options;
        var run = LoadRun();
        var sub = await LocalSub(run);
        sub.TestDriveWorld = new Vector3(5f, 0f, 0f);

        var proxy = await VisibleProxy(run, 1, "guest sees the host ship");
        await Moves(proxy, "host proxy follows the host ship");
        Check(run.GetNodeOrNull("RunHud/Sonar/TeammateOverlay") is not null, "teammate sonar overlay attached");
        await UntilFile("host_saw_guest", "host saw guest");

        // Forced link drop: close the ENet peer underneath the session.
        var oldPeer = session.LocalPeerId;
        var link = run.GetNode<CoopRunLink>("CoopRunLink");
        ((SceneMultiplayer)session.Multiplayer).MultiplayerPeer.Close();
        await Until(() => link.IsReconnecting, "reconnect window opened by the drop");
        Say("client", "link dropped; reconnect window open");
        await Until(() => !link.IsReconnecting && session.LocalPeerId != oldPeer && session.LocalPeerId != 0, "token reconnect inside the window");
        Check(session.SessionId != NetLimits.InvalidId, "same session re-adopted");
        await Moves(await VisibleProxy(run, 1, "host ship visible after reconnect"), "host proxy still live after reconnect");
        File.WriteAllText(Path.Combine(_work, "client_reconnected"), session.LocalPeerId.ToString());

        await UntilFile("host_done", "host finished its checks");
        var store = new FileSaveStore(GameServices.Paths.SavesDir);
        ProfileSave? profile = null;
        await Until(() =>
        {
            profile = store.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();
            return profile.AppliedSettlementIds.Count > 0;
        }, "host-loss settlement applied (8 s window + write)", 60000);
        Check(profile!.AppliedSettlementIds.Count == 1, "exactly one settlement applied");
        Check(profile.Stats.RunsCompleted == 0 && profile.Stats.RunsFailed == 0, "host loss is neither completed nor failed");
        Say("client", $"host-loss settled once: {profile.AppliedSettlementIds[0]} credits={profile.Currencies.Credits}");
    }

    // --------------------------------------------------------------- helpers

    private static void InitServices(string root)
    {
        var localization = new LocalizationService();
        Localization.Initialize(localization);
        Localization.SetLanguage("en");
        var paths = AppPaths.FromRoot(root);
        paths.EnsureDirectories();
        var registry = new Foundation.DependencyRegistry();
        registry.RegisterInstance(localization);
        registry.RegisterInstance(paths);
        registry.RegisterInstance(new LocalLogger(paths.LogsDir, "coop-smoke", GameVersion.Current));
        registry.RegisterInstance(new SceneFlow());
        registry.RegisterInstance(new AppSettingsHolder(AppSettings.Default(), false, false));
        registry.RegisterInstance(new ProfileSessionState());
        GameServices.Initialize(registry);
    }

    private RunLaunchOptions Options() =>
        new(_catalog!.Contracts[Contract].AllowedBiomeIds[0], Contract, "difficulty.standard", "frame.skiff",
            Seed, Array.Empty<string>(), RunSimulation.InsuranceNone);

    private ProtocolManifest Manifest(RunLaunchOptions options)
    {
        var world = Generate(options);
        var m = world.ToManifest(GameVersion.Current, options.DifficultyId, options.ModifierIds);
        return new ProtocolManifest(m.ProtocolVersion, m.GameVersion, m.RunSeed, m.BiomeId, m.ContractId,
            m.DifficultyId, m.LayoutHash, m.CatalogHash, m.Modifiers);
    }

    private RunLaunchOptions StageClient(ProtocolManifest manifest)
    {
        var options = new RunLaunchOptions(manifest.BiomeId, manifest.ContractId, manifest.DifficultyId, "frame.skiff",
            manifest.RunSeed, manifest.Modifiers, RunSimulation.InsuranceNone);
        Check(Generate(options).LayoutHash == manifest.LayoutHash, "client layout matches the host");
        return options;
    }

    private GeneratedWorld Generate(RunLaunchOptions options)
    {
        var request = new RunGenerationRequest(options.RunSeed, options.BiomeId, options.ContractId, _catalog!);
        if (!TrenchGenerator.TryGenerate(request, out var world, out var verdict, out var reason) || world is null || !verdict.IsValid)
        {
            throw new InvalidOperationException("generation: " + reason);
        }

        return world;
    }

    private Node LoadRun()
    {
        var run = GD.Load<PackedScene>("res://scenes/run.tscn").Instantiate();
        GetTree().Root.AddChild(run);
        return run;
    }

    private async Task<SubmarineController> LocalSub(Node run)
    {
        SubmarineController? sub = null;
        await Until(() => (sub = run.GetNodeOrNull<SubmarineController>("Submarine")) is not null &&
                          run.GetNodeOrNull("CoopRunLink") is not null, "run scene built in co-op mode");
        return sub!;
    }

    private async Task<Node3D> VisibleProxy(Node run, ulong peer, string what)
    {
        Node3D? proxy = null;
        await Until(() => (proxy = run.GetNodeOrNull<Node3D>($"Teammate_{peer}")) is { Visible: true }, what);
        return proxy!;
    }

    private async Task Moves(Node3D proxy, string what)
    {
        var start = proxy.GlobalPosition;
        await Until(() => proxy.GlobalPosition.DistanceTo(start) > 1.0f && float.IsFinite(proxy.GlobalPosition.X), what);
    }

    private async Task Until(Func<bool> done, string step, int timeoutMs = StepTimeoutMs)
    {
        var started = Time.GetTicksMsec();
        while (!done())
        {
            if (Time.GetTicksMsec() - started > (ulong)timeoutMs)
            {
                throw new TimeoutException($"timeout at: {step}");
            }

            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        Say("step", step);
    }

    private async Task UntilFile(string name, string step) =>
        await Until(() => File.Exists(Path.Combine(_work, name)), step, 90000);

    private async Task<string> ReadFile(string name)
    {
        await UntilFile(name, name + " published");
        return File.ReadAllText(Path.Combine(_work, name)).Trim();
    }

    private static void Say(string who, string text) => GD.Print($"SMOKE ({who}): {text}");

    private static void Check(bool condition, string what)
    {
        if (!condition)
        {
            throw new InvalidOperationException("invariant failed: " + what);
        }
    }

    private static string? Arg(string[] args, string prefix) =>
        args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..].Trim().Trim('"');

    private static int FreeLoopbackUdpPort()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }
}
