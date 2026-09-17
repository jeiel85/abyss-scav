using AbyssScav.Foundation;

namespace AbyssScav.Foundation.Tests;

internal sealed class FakeService
{
    public string Name { get; }
    public FakeService(string name) => Name = name;
}

internal static class RegistryFlowTests
{
    public static int Run()
    {
        var cases = new (string Name, Action Test)[]
        {
            ("register_resolve_and_missing", RegistryBasics),
            ("boot_args_safe_mode", BootArgsParse),
            ("scene_flow_boot_to_menu", FlowBootMenu),
            ("scene_flow_rejects_illegal_jump", FlowRejects),
            ("scene_flow_full_run_path", FlowFullPath),
            ("scene_flow_menu_to_research_and_back", FlowResearch),
            ("scene_flow_menu_to_codex_and_back", FlowCodex),
            ("scene_flow_research_codex_isolated", FlowResearchCodexIsolated),
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

    private static void RegistryBasics()
    {
        var registry = new DependencyRegistry();
        TestAssert.True(!registry.IsRegistered<FakeService>(), "not registered initially");
        registry.RegisterInstance(new FakeService("alpha"));
        TestAssert.True(registry.IsRegistered<FakeService>(), "registered");
        TestAssert.Equal("alpha", registry.Resolve<FakeService>().Name, "resolved");
        TestAssert.True(registry.TryResolve<FakeService>(out var s) && s!.Name == "alpha", "try resolve");

        var empty = new DependencyRegistry();
        TestAssert.True(!empty.TryResolve<FakeService>(out _), "missing try-resolve false");
        try
        {
            empty.Resolve<FakeService>();
            throw new Exception("Resolve of missing service should throw.");
        }
        catch (InvalidOperationException)
        {
            // Expected.
        }
    }

    private static void BootArgsParse()
    {
        TestAssert.True(BootOptions.Parse(["--safe-mode"]).SafeMode, "exact flag");
        TestAssert.True(BootOptions.Parse(["--headless", "--safe-mode", "--resolution", "1280x720"]).SafeMode, "among others");
        TestAssert.True(!BootOptions.Parse([]).SafeMode, "default off");
        TestAssert.True(!BootOptions.Parse(["--safe-mode-fast"]).SafeMode, "prefix alone must not match");
    }

    private static void FlowBootMenu()
    {
        var flow = new SceneFlow();
        TestAssert.Equal(AppScene.Boot, flow.Current, "starts at boot");
        TestAssert.True(flow.CanTransition(AppScene.MainMenu), "boot->menu allowed");
        TestAssert.True(flow.TryTransition(AppScene.MainMenu, out _), "transition ok");
        TestAssert.Equal(AppScene.MainMenu, flow.Current, "now menu");
    }

    private static void FlowRejects()
    {
        var flow = new SceneFlow();
        TestAssert.True(!flow.CanTransition(AppScene.InRun), "boot->inrun illegal");
        TestAssert.True(!flow.TryTransition(AppScene.InRun, out var err) && err.Length > 0, "illegal returns error");
        TestAssert.Equal(AppScene.Boot, flow.Current, "state unchanged after illegal");
    }

    private static void FlowFullPath()
    {
        var flow = new SceneFlow();
        foreach (var next in new[] { AppScene.MainMenu, AppScene.HostOrJoin, AppScene.Lobby, AppScene.Loadout, AppScene.RunLoading, AppScene.InRun, AppScene.Settlement, AppScene.MainMenu })
        {
            TestAssert.True(flow.TryTransition(next, out var err), $"step to {next}: {err}");
        }

        TestAssert.Equal(AppScene.MainMenu, flow.Current, "ends at menu");
    }

    private static void FlowResearch()
    {
        var flow = new SceneFlow();
        TestAssert.True(flow.TryTransition(AppScene.MainMenu, out _), "boot->menu");
        TestAssert.True(flow.CanTransition(AppScene.Research), "menu->research allowed");
        TestAssert.True(flow.TryTransition(AppScene.Research, out var err), $"menu->research: {err}");
        TestAssert.Equal(AppScene.Research, flow.Current, "now research");
        TestAssert.True(flow.TryTransition(AppScene.MainMenu, out err), $"research->menu: {err}");
        TestAssert.Equal(AppScene.MainMenu, flow.Current, "back at menu");
    }

    private static void FlowCodex()
    {
        var flow = new SceneFlow();
        TestAssert.True(flow.TryTransition(AppScene.MainMenu, out _), "boot->menu");
        TestAssert.True(flow.CanTransition(AppScene.Codex), "menu->codex allowed");
        TestAssert.True(flow.TryTransition(AppScene.Codex, out var err), $"menu->codex: {err}");
        TestAssert.Equal(AppScene.Codex, flow.Current, "now codex");
        TestAssert.True(flow.TryTransition(AppScene.MainMenu, out err), $"codex->menu: {err}");
        TestAssert.Equal(AppScene.MainMenu, flow.Current, "back at menu");
    }

    private static void FlowResearchCodexIsolated()
    {
        var flow = new SceneFlow();
        TestAssert.True(flow.TryTransition(AppScene.MainMenu, out _), "boot->menu");
        TestAssert.True(flow.TryTransition(AppScene.Research, out _), "menu->research");
        TestAssert.True(!flow.CanTransition(AppScene.Codex), "research->codex illegal");
        TestAssert.True(!flow.TryTransition(AppScene.Codex, out var err) && err.Length > 0, "research->codex refused");
        TestAssert.True(!flow.CanTransition(AppScene.Loadout), "research->loadout illegal");
        TestAssert.Equal(AppScene.Research, flow.Current, "still research after refused hops");

        var flow2 = new SceneFlow();
        TestAssert.True(flow2.TryTransition(AppScene.MainMenu, out _), "boot->menu");
        TestAssert.True(flow2.TryTransition(AppScene.Codex, out _), "menu->codex");
        TestAssert.True(!flow2.CanTransition(AppScene.Research), "codex->research illegal");
        TestAssert.True(!flow2.CanTransition(AppScene.Loadout), "codex->loadout illegal");
        TestAssert.Equal(AppScene.Codex, flow2.Current, "still codex after refused hops");
    }
}
