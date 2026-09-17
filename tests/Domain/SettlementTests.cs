using AbyssScav.Domain;

namespace AbyssScav.Domain.Tests;

/// <summary>
/// Settlement defect coverage: exact docs/05 §2 success formula (no double
/// difficulty multiply), earned-only failure retention, per-instance settlement
/// IDs, cached repeat drafts, and the priced-but-uncharged insurance table.
/// </summary>
internal static class SettlementTests
{
    public static int Run()
    {
        var fail = 0;
        void Case(string name, Action body)
        {
            try { body(); }
            catch (Exception ex) { Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); fail++; }
        }

        var catalog = DomainSetup.Catalog();

        Case("success credits equal Base+Salvage+Objectives+Risk-Repair exactly", () =>
        {
            var world = DomainSetup.World(catalog, 301UL, "biome.shelf_graveyard", "contract.salvage_quota");
            var sim = DomainSetup.Sim(catalog, world, difficulty: "difficulty.standard", frame: "frame.mule");
            BankUntilComplete(sim, world);
            DomainSetup.Teleport(sim, world.GetNode(world.ExtractionNodeId).Position);
            var result = sim.TryExtract();
            TestAssert.True(result.Success, "extracted: " + result.Reason);
            var d = result.Settlement!;
            // Standard on the shelf: risk bonus is exactly 0 (tier 0, mult 1.0).
            TestAssert.Equal(0L, d.RiskBonus, "standard shelf risk");
            var expected = d.BasePayout + d.SecuredSalvageValue + d.ObjectiveBonus + d.RiskBonus - d.RepairCost;
            TestAssert.Equal(expected, d.Credits, "docs/05 §2 formula, single application");
            TestAssert.Equal(d.Credits, d.RetainedCredits, "success retains in full");
            TestAssert.True(sim.Settlement is not null && ReferenceEquals(sim.Settlement, result.Settlement), "repeat success draft is the same cached instance");
        });

        Case("formula holds across difficulties with single-applied risk uplift", () =>
        {
            var risks = new Dictionary<string, long>();
            foreach (var difficulty in new[] { "difficulty.casual", "difficulty.standard", "difficulty.blackwater", "difficulty.custom" })
            {
                var world = DomainSetup.World(catalog, 302UL, "biome.black_trench", "contract.salvage_quota");
                var sim = DomainSetup.Sim(catalog, world, difficulty: difficulty, frame: "frame.mule");
                BankUntilComplete(sim, world);
                DomainSetup.Teleport(sim, world.GetNode(world.ExtractionNodeId).Position);
                var result = sim.TryExtract();
                TestAssert.True(result.Success, $"{difficulty} extracted: " + result.Reason);
                var d = result.Settlement!;
                var expected = d.BasePayout + d.SecuredSalvageValue + d.ObjectiveBonus + d.RiskBonus - d.RepairCost;
                TestAssert.Equal(expected, d.Credits, $"{difficulty} formula");
                risks[difficulty] = d.RiskBonus;
            }
            // Trench tier 1: risk = Base*0.1 + Base*(mult-1): casual 0, standard 60, blackwater 150, custom 60.
            TestAssert.True(risks["difficulty.blackwater"] > risks["difficulty.standard"], "blackwater risk above standard");
            TestAssert.True(risks["difficulty.casual"] < risks["difficulty.standard"], "casual risk below standard");
            TestAssert.Equal(risks["difficulty.standard"], risks["difficulty.custom"], "custom matches standard");
        });

        Case("failure retains secured cargo at the exact insurance rate", () =>
        {
            var securedByInsurance = new Dictionary<string, long>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (insurance, rate) in new[] { ("insurance.none", 0.2), ("insurance.basic", 0.5), ("insurance.premium", 0.7) })
            {
                var world = DomainSetup.World(catalog, 303UL, "biome.shelf_graveyard", "contract.salvage_quota");
                var sim = SimWithDeadline(catalog, world, insurance: insurance);
                foreach (var loot in world.LootSpawns.Take(2))
                {
                    DomainSetup.Teleport(sim, loot.Position);
                    sim.TrySalvage(loot.SpawnId);
                }
                TestAssert.True(sim.SecuredSalvageValue > 0, $"{insurance} secured something");
                FailByDeadline(sim);
                var first = sim.BuildFailureSettlement();
                var second = sim.BuildFailureSettlement();
                TestAssert.True(first is not null, $"{insurance} failure draft exists");
                TestAssert.True(ReferenceEquals(first, second), $"{insurance} repeat draft is cached");
                TestAssert.Equal(first, second, $"{insurance} repeat draft equal");
                TestAssert.Equal((long)Math.Round(sim.SecuredSalvageValue * rate), first!.RetainedCredits, $"{insurance} exact retention");
                TestAssert.Equal(first.Credits, first.RetainedCredits, $"{insurance} failure credits are retained credits");
                TestAssert.True(first.SettlementId.Contains(sim.RunInstanceId.ToString("N"), StringComparison.Ordinal), $"{insurance} draft id scoped to instance");
                securedByInsurance[insurance] = first.RetainedCredits;
                TestAssert.True(ids.Add(first.SettlementId), $"{insurance} settlement id unique per instance");
            }
            TestAssert.True(securedByInsurance["insurance.premium"] >= securedByInsurance["insurance.basic"], "premium retains at least basic");
            TestAssert.True(securedByInsurance["insurance.basic"] >= securedByInsurance["insurance.none"], "basic retains at least none");
        });

        Case("empty failure settles to zero: no base, no research, no shards", () =>
        {
            // Blackwater hadal would earn shards and uplifts on success; on an empty
            // failure none of that may leak through.
            var world = DomainSetup.World(catalog, 304UL, "biome.hadal_ruins", "contract.salvage_quota");
            var sim = SimWithDeadline(catalog, world, difficulty: "difficulty.blackwater", insurance: "insurance.premium");
            TestAssert.True(sim.BuildFailureSettlement() is null, "no failure draft while active");
            FailByDeadline(sim);
            TestAssert.Equal(RunPhase.Failed, sim.Phase, "deadline failed the run");
            var draft = sim.BuildFailureSettlement();
            TestAssert.True(draft is not null, "failure draft exists");
            TestAssert.Equal(0L, draft!.Credits, "no unconditional base payout");
            TestAssert.Equal(0L, draft.RetainedCredits, "nothing retained from nothing secured");
            TestAssert.Equal(0L, draft.ResearchData, "no participation research");
            TestAssert.Equal(0L, draft.Shards, "no unearned shards");
            TestAssert.Equal(SettlementOutcome.Failed, draft.Outcome, "failed outcome");
        });

        Case("same seed gives identical worlds but distinct settlement ids", () =>
        {
            var a = SimWithDeadline(catalog, DomainSetup.World(catalog, 305UL, "biome.black_trench", "contract.salvage_quota"));
            var b = SimWithDeadline(catalog, DomainSetup.World(catalog, 305UL, "biome.black_trench", "contract.salvage_quota"));
            TestAssert.Equal(a.World.LayoutHash, b.World.LayoutHash, "world hash untouched by instance ids");
            TestAssert.False(a.RunInstanceId == b.RunInstanceId, "distinct run instances");
            FailByDeadline(a);
            FailByDeadline(b);
            var da = a.BuildFailureSettlement()!;
            var db = b.BuildFailureSettlement()!;
            TestAssert.False(da.SettlementId == db.SettlementId, "distinct settlement ids for distinct instances");
            TestAssert.True(da.SettlementId.StartsWith("settle.", StringComparison.Ordinal), "id prefix kept");
            TestAssert.True(da.SettlementId.Length <= 128, "id within persistence bounds");
        });

        Case("insurance quote prices policy without charging", () =>
        {
            TestAssert.True(RunSimulation.TryGetInsuranceQuote("insurance.none", out var none, out _), "none quoted");
            TestAssert.Equal(0.2, none!.Retention, "none retention");
            TestAssert.Equal(0.0, none.Rate, "none rate");
            TestAssert.True(RunSimulation.TryGetInsuranceQuote("insurance.basic", out var basic, out _), "basic quoted");
            TestAssert.Equal(0.5, basic!.Retention, "basic retention");
            TestAssert.Equal(0.08, basic.Rate, "basic rate");
            TestAssert.True(RunSimulation.TryGetInsuranceQuote("insurance.premium", out var premium, out _), "premium quoted");
            TestAssert.Equal(0.7, premium!.Retention, "premium retention");
            TestAssert.Equal(0.15, premium.Rate, "premium rate");
            TestAssert.False(RunSimulation.TryGetInsuranceQuote("insurance.nope", out _, out var reason), "unknown rejected: " + reason);
        });

        return fail;
    }

    private static void BankUntilComplete(RunSimulation sim, GeneratedWorld world)
    {
        foreach (var loot in world.LootSpawns)
        {
            if (sim.Contract.PrimaryComplete) break;
            if (sim.Phase != RunPhase.Active) break;
            DomainSetup.Teleport(sim, loot.Position);
            sim.TrySalvage(loot.SpawnId);
        }
    }

    private static void FailByDeadline(RunSimulation sim)
    {
        // Every caller builds its sim with modifier.time_window, so the deadline
        // fails the run with zero further earnings.
        var guard = 0;
        while (sim.Phase == RunPhase.Active && guard++ < 5200)
            sim.Tick(0.5f, sim.ShipPosition, DomainSetup.Idle);
    }

    private static RunSimulation SimWithDeadline(
        ContentCatalog catalog, GeneratedWorld world,
        string difficulty = "difficulty.standard",
        string frame = "frame.skiff",
        string insurance = "insurance.basic")
    {
        if (!RunSimulation.TryCreate(world, catalog, difficulty, frame,
                new[] { "modifier.time_window" }, insurance, out var sim, out var reason) || sim is null)
            throw new Exception("Simulation creation failed: " + reason);
        return sim;
    }
}
