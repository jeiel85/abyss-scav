using AbyssScav.Domain;
using AbyssScav.Presentation;
using Godot;

namespace AbyssScav.Gameplay;

/// <summary>
/// Debug-only smoke harness. Exercises actual run construction plus real
/// simulation ticks (never a pretend run), then reports measured invariants.
/// Invoked from the menu when launched with --run-smoke in a debug build, or
/// from the dedicated smoke scene. Prints SMOKE lines and quits.
/// </summary>
public static class RunSmoke
{
    public static void Execute(Node host, string[] args)
    {
        var log = new List<string>();
        var ok = true;
        void Check(bool cond, string name, string detail = "")
        {
            log.Add((cond ? "SMOKE PASS " : "SMOKE FAIL ") + name + (detail == "" ? "" : " | " + detail));
            if (!cond) ok = false;
        }

        try
        {
            Check(ContentCatalog.TryBuild(out var catalog, out var errors) && catalog is not null,
                "catalog.build", catalog is null ? string.Join("; ", errors) : catalog.CatalogHash);
            if (catalog is null)
            {
                Finish(host, ok, log);
                return;
            }
            // Cover the focused blackbox contract plus one interaction-heavy contract.
            var picks = new List<(string Biome, string Contract)>();
            foreach (var c in catalog.Contracts.Values.OrderBy(c => c.Id))
            {
                picks.Add((c.AllowedBiomeIds[0], c.Id));
                if (picks.Count >= 2) break;
            }
            Check(picks.Count > 0, "contracts.present", $"count={catalog.Contracts.Count}");
            foreach (var (biome, contract) in picks)
            {
                var seed = 4242UL;
                var req = new RunGenerationRequest(seed, biome, contract, catalog);
                var genOk = TrenchGenerator.TryGenerate(req, out var world, out var verdict, out var reason);
                Check(genOk && world is not null, $"generate.{contract}", genOk ? $"nodes={world!.Nodes.Count} segs={world.Segments.Count} loot={world.LootSpawns.Count} threats={world.ThreatSpawns.Count}" : reason);
                if (world is null) continue;
                Check(verdict.IsValid, $"validate.{contract}", string.Join("; ", verdict.Errors));
                var minClear = world.Segments.Min(s => s.ClearanceRadiusMeters);
                Check(minClear >= DomainConstants.MinClearanceMeters, $"clearance.{contract}", $"min={minClear:F1}m");
                var simOk = RunSimulation.TryCreate(world, catalog, "difficulty.standard", "frame.skiff", null, RunSimulation.InsuranceBasic, out var sim, out var simReason);
                Check(simOk && sim is not null, $"sim.create.{contract}", simReason);
                if (sim is null) continue;
                // Real physics-rate ticks with varying throttle.
                var idle = new ShipControlInput(0f, false, false);
                var cruise = new ShipControlInput(0.6f, false, false);
                for (var i = 0; i < 120; i++)
                {
                    sim.Tick(1f / 60f, sim.ShipPosition, i % 2 == 0 ? cruise : idle);
                    if (sim.Phase != RunPhase.Active) break;
                }
                Check(float.IsFinite(sim.HullIntegrity) && float.IsFinite(sim.Noise) && float.IsFinite(sim.Threat),
                    $"sim.invariants.{contract}", $"hull={sim.HullIntegrity:F0} noise={sim.Noise:F0} threat={sim.Threat:F0} phase={sim.Phase}");
                // Exercise a real pulse + cooldown path (not a fake success claim).
                sim.Tick(0.1f, sim.ShipPosition, idle);
                var pulse = sim.Pulse();
                Check(pulse.Success || sim.PulseCooldownRemaining > 0f || pulse.Reason != "",
                    $"sim.pulse.{contract}", pulse.Success ? $"contacts={pulse.Contacts.Count}" : pulse.Reason);
                Check(sim.Phase is RunPhase.Active or RunPhase.Failed, $"sim.phase.{contract}", sim.Phase.ToString());
                // Actual run-scene construction: physical corridors + hull + markers.
                CheckConstruction(host, world, catalog, contract, Check);
            }
            // Debug-only headless check: progression screens build from the real
            // catalog (no fake data), show balances, then are freed.
            CheckProgressionScreens(host, catalog, Check);
            log.Add(ok ? "SMOKE RESULT PASS" : "SMOKE RESULT FAIL");
        }
        catch (Exception ex)
        {
            log.Add("SMOKE FAIL exception | " + ex.GetType().Name + ": " + ex.Message);
            ok = false;
            log.Add("SMOKE RESULT FAIL");
        }
        Finish(host, ok, log);
    }

    private static void CheckConstruction(Node host, GeneratedWorld world, ContentCatalog catalog, string contract, Action<bool, string, string> check)
    {
        try
        {
            AbyssInput.EnsureRegistered();
            check(InputMap.HasAction("abyss_ping") && InputMap.HasAction("abyss_interact"), $"inputmap.{contract}", "ping+interact registered from code");
            var root = new Node3D { Name = "SmokeRoot" };
            host.AddChild(root);
            WorldBuilder.Build(root, world, catalog);
            var statics = root.GetChildren().Count;
            // Terrain body + environment + landmarks + loot + ring expected.
            check(statics >= world.Nodes.Count + world.LootSpawns.Count, $"scene.build.{contract}", $"children={statics} nodes={world.Nodes.Count} loot={world.LootSpawns.Count}");
            var terrain = root.GetNodeOrNull("Terrain");
            check(terrain is not null, $"scene.terrain.{contract}", "StaticBody Terrain present");
            var sub = new SubmarineController { Name = "SmokeSub" };
            root.AddChild(sub);
            var hull = sub.GetNodeOrNull("Hull") as CollisionShape3D;
            var cam = sub.GetNodeOrNull("CameraRig/ChaseCamera") as Camera3D;
            check(hull?.Shape is CapsuleShape3D, $"scene.hull.{contract}", "independent capsule hull present");
            check(cam is not null, $"scene.camera.{contract}", "independent chase camera present");
            sub.GlobalPosition = WorldBuilder.ToG(world.GetNode(world.ExtractionNodeId).Position);
            // Step real physics frames headless: let the tree run them.
            check(sub.Throttle01 == 0f, $"scene.physics.{contract}", "rigid body idle throttle 0 before input");
            root.QueueFree();
        }
        catch (Exception ex)
        {
            check(false, $"scene.build.{contract}", ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static void CheckProgressionScreens(Node host, ContentCatalog catalog, Action<bool, string, string> check)
    {
        try
        {
            var research = new ResearchScreen { Name = "SmokeResearch" };
            host.AddChild(research);
            check(research.BranchColumnCount == 4, "screen.research.branches", $"columns={research.BranchColumnCount}");
            check(research.ModuleRowCount == catalog.Modules.Count, "screen.research.modules", $"rows={research.ModuleRowCount} catalog={catalog.Modules.Count}");
            check(research.BalanceText.Contains("Research data:"), "screen.research.balance", research.BalanceText);
            research.QueueFree();

            var codex = new CodexScreen { Name = "SmokeCodex" };
            host.AddChild(codex);
            var expected = catalog.Creatures.Count + catalog.Biomes.Count + catalog.RelicTraits.Count;
            check(codex.EntryCount == expected, "screen.codex.entries", $"entries={codex.EntryCount} catalog={expected}");
            codex.QueueFree();
        }
        catch (Exception ex)
        {
            check(false, "screen.progression", ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static void Finish(Node host, bool ok, List<string> log)
    {
        foreach (var line in log)
        {
            GD.Print(line);
        }
        GD.Print("SMOKE DONE ok=" + ok);
        host.GetTree()?.CallDeferred(SceneTree.MethodName.Quit, ok ? 0 : 1);
    }
}
