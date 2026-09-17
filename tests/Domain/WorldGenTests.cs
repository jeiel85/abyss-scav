using System.Numerics;
using AbyssScav.Domain;

namespace AbyssScav.Domain.Tests;

internal static class WorldGenTests
{
    private static readonly string[] ContractIds =
    {
        "contract.salvage_quota", "contract.blackbox_recovery", "contract.facility_core",
        "contract.bio_sample", "contract.beacon_repair", "contract.survey_scan",
        "contract.rescue_pod", "contract.apex_observe",
    };

    public static int Run()
    {
        var fail = 0;
        void Case(string name, Action body)
        {
            try { body(); }
            catch (Exception ex) { Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); fail++; }
        }

        var catalog = DomainSetup.Catalog();

        Case("deterministic layout for identical seeds", () =>
        {
            var a = DomainSetup.World(catalog, 123456789UL, "biome.black_trench", "contract.blackbox_recovery");
            var b = DomainSetup.World(catalog, 123456789UL, "biome.black_trench", "contract.blackbox_recovery");
            TestAssert.Equal(a.LayoutHash, b.LayoutHash, "layout hash");
            TestAssert.Equal(a.Nodes.Count, b.Nodes.Count, "node count");
            for (var i = 0; i < a.Nodes.Count; i++)
                TestAssert.Equal(a.Nodes[i].Position, b.Nodes[i].Position, "node " + a.Nodes[i].Id);
            TestAssert.Equal(a.LootSpawns.Count, b.LootSpawns.Count, "loot count");
            TestAssert.Equal(a.ThreatSpawns.Count, b.ThreatSpawns.Count, "threat count");
        });

        Case("distinct seeds diverge", () =>
        {
            var hashes = new HashSet<string>(StringComparer.Ordinal);
            for (ulong s = 1; s <= 8; s++)
                hashes.Add(DomainSetup.World(catalog, s, "biome.black_trench", "contract.salvage_quota").LayoutHash);
            TestAssert.Equal(8, hashes.Count, "distinct layouts");
        });

        Case("structural invariants hold across contracts and biomes", () =>
        {
            foreach (var contractId in ContractIds)
            {
                var biome = DomainSetup.FirstBiome(catalog, contractId);
                for (ulong seed = 1; seed <= 10; seed++)
                {
                    var world = DomainSetup.World(catalog, seed * 7919UL + (ulong)contractId.Length, biome, contractId);
                    CheckInvariants(world, catalog, $"seed {seed} {contractId}");
                }
            }
        });

        Case("invalid requests are rejected with reasons", () =>
        {
            var badBiome = new RunGenerationRequest(1, "biome.nope", "contract.salvage_quota", catalog);
            TestAssert.False(TrenchGenerator.TryGenerate(badBiome, out _, out _, out var r1) && r1.Length == 0, "bad biome rejected, reason kept: " + r1);
            var badContract = new RunGenerationRequest(1, "biome.black_trench", "contract.nope", catalog);
            TestAssert.False(TrenchGenerator.TryGenerate(badContract, out _, out _, out var r2), "bad contract rejected: " + r2);
            // Blackbox is not valid in hadal ruins per catalog cross-refs.
            var mismatched = new RunGenerationRequest(1, "biome.hadal_ruins", "contract.blackbox_recovery", catalog);
            TestAssert.False(TrenchGenerator.TryGenerate(mismatched, out _, out _, out var r3), "biome/contract mismatch rejected: " + r3);
        });

        Case("10000 seeds all validate", () =>
        {
            var invalid = 0;
            var fallbacks = 0;
            var firstError = string.Empty;
            for (var i = 0; i < 10000; i++)
            {
                var contractId = ContractIds[i % ContractIds.Length];
                var biome = DomainSetup.FirstBiome(catalog, contractId);
                var request = new RunGenerationRequest((ulong)i * 6364136223846793005UL + 1442695040888963407UL, biome, contractId, catalog);
                if (!TrenchGenerator.TryGenerate(request, out var world, out var validation, out var reason) || world is null)
                {
                    invalid++;
                    if (firstError.Length == 0) firstError = $"seed-index {i}: {reason}";
                    continue;
                }
                if (world.UsedFallback) fallbacks++;
                if (!validation.IsValid)
                {
                    invalid++;
                    if (firstError.Length == 0) firstError = $"seed-index {i}: {string.Join("; ", validation.Errors)}";
                    continue;
                }
                // Cheap per-seed spot checks beyond Validate (which already covers geometry,
                // connectivity, quest minima, and hash integrity).
                if (world.EdgeDistance(world.ExtractionNodeId, world.ObjectiveNodeId) < DomainConstants.MinObjectiveEdgeDistance)
                {
                    invalid++;
                    if (firstError.Length == 0) firstError = $"seed-index {i}: objective too close";
                }
                if (world.GetNode(world.ExtractionNodeId).Position.Length() > 1.0f)
                {
                    invalid++;
                    if (firstError.Length == 0) firstError = $"seed-index {i}: extraction drifted";
                }
            }
            Console.WriteLine($"  [INFO] 10000 seeds: {invalid} invalid, {fallbacks} fallbacks.");
            TestAssert.Equal(0, invalid, "all 10000 seeds valid. " + firstError);
        });

        return fail;
    }

    internal static void CheckInvariants(GeneratedWorld world, ContentCatalog catalog, string context)
    {
        var verdict = TrenchGenerator.Validate(world, catalog);
        if (!verdict.IsValid)
            throw new Exception(context + ": " + string.Join("; ", verdict.Errors));

        var extraction = world.GetNode(world.ExtractionNodeId).Position;
        TestAssert.True(extraction.Length() <= 1.0f, context + " extraction near origin");
        var edges = world.EdgeDistance(world.ExtractionNodeId, world.ObjectiveNodeId);
        TestAssert.True(edges >= DomainConstants.MinObjectiveEdgeDistance, context + $" objective edges {edges}");

        TestAssert.Equal(world.ExtractionNodeId, world.RouteFromExtractionToObjective[0], context + " route start");
        TestAssert.Equal(world.ObjectiveNodeId, world.RouteFromExtractionToObjective[^1], context + " route end");

        foreach (var v in world.Nodes.Select(n => n.Position))
            TestAssert.True(float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z), context + " finite positions");

        // Positions are meters: the trench spans hundreds of meters, legs stay bounded.
        var span = world.Nodes.Max(n => Math.Abs(n.Position.Z));
        TestAssert.True(span > 500f && span < 20000f, context + $" trench span {span:F0} m");

        if (world.ContractId == "contract.apex_observe")
            TestAssert.True(world.ThreatSpawns.Any(t => t.IsApex), context + " apex present");
    }
}
