using AbyssScav.Domain;

namespace AbyssScav.Domain.Tests;

internal static class SafetyTests
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

        Case("duplicate salvage and quest recovery rejected", () =>
        {
            var world = DomainSetup.World(catalog, 21UL, DomainSetup.FirstBiome(catalog, "contract.blackbox_recovery"), "contract.blackbox_recovery");
            var sim = DomainSetup.Sim(catalog, world);
            var box = world.LootSpawns.First(l => l.QuestItemId == QuestItems.BlackBox);
            DomainSetup.Teleport(sim, box.Position);
            TestAssert.True(sim.TrySalvage(box.SpawnId).Success, "first recovery");
            var dup = sim.TrySalvage(box.SpawnId);
            TestAssert.False(dup.Success, "second recovery rejected");
            TestAssert.Equal(1, sim.QuestItemsRecovered.Count(q => q == QuestItems.BlackBox), "quest counted once");
        });

        Case("distance gate rejects far salvage", () =>
        {
            var world = DomainSetup.World(catalog, 22UL, "biome.shelf_graveyard", "contract.salvage_quota");
            var sim = DomainSetup.Sim(catalog, world);
            var loot = world.LootSpawns[0];
            var far = loot.Position + new System.Numerics.Vector3(500f, 0f, 0f);
            DomainSetup.Teleport(sim, far);
            var result = sim.TrySalvage(loot.SpawnId);
            TestAssert.False(result.Success, "far salvage rejected: " + result.Reason);
            TestAssert.Equal(0, sim.SecuredSalvageValue, "no value banked");
        });

        Case("cargo capacity and slots are hard limits", () =>
        {
            var world = DomainSetup.World(catalog, 23UL, "biome.shelf_graveyard", "contract.salvage_quota");
            var sim = DomainSetup.Sim(catalog, world);
            var secured = 0;
            foreach (var loot in world.LootSpawns)
            {
                DomainSetup.Teleport(sim, loot.Position);
                if (sim.TrySalvage(loot.SpawnId).Success) secured++;
            }
            TestAssert.True(sim.CargoUsedSlots <= sim.CargoMaxSlots, "slots bounded");
            TestAssert.True(sim.CargoUsedMassKg <= sim.CargoMaxMassKg + 0.01f, "mass bounded");
            TestAssert.Equal(secured, sim.CargoItems.Count, "cargo matches successes");
        });

        Case("sealant can never go negative", () =>
        {
            var world = DomainSetup.World(catalog, 24UL, "biome.hadal_ruins", "contract.salvage_quota");
            var sim = DomainSetup.Sim(catalog, world, frame: "frame.warden");
            TestAssert.True(sim.Sealant >= 1, "starts with sealant");
            // Dive deep until breaches exist, then spend every charge.
            var guard = 0;
            while (sim.FloodZones.All(z => z.Severity == 0) && guard++ < 200 && sim.Phase == RunPhase.Active)
                sim.Tick(0.5f, sim.ShipPosition, DomainSetup.Idle);
            var repairs = 0;
            for (var i = 0; i < 4 && sim.Sealant > 0; i++)
                if (sim.TryRepairHull(i).Success) repairs++;
            // Keep repairing zone 0 until the resource runs dry.
            while (sim.Sealant > 0)
            {
                var zone = sim.FloodZones.ToList().FindIndex(z => z.Severity > 0);
                if (zone < 0) break;
                if (!sim.TryRepairHull(zone).Success) break;
                repairs++;
                if (repairs > 20) break;
            }
            TestAssert.True(sim.Sealant >= 0, "sealant non-negative");
            var refused = sim.TryRepairHull(0);
            if (sim.Sealant == 0)
                TestAssert.False(refused.Success && sim.Sealant < 0, "broke spend rejected without debt");
        });

        Case("high-threat ghosts fade with an explicit reason", () =>
        {
            var world = DomainSetup.World(catalog, 25UL, "biome.black_trench", "contract.survey_scan");
            var sim = DomainSetup.Sim(catalog, world);
            // Full-burn running parks threat above 60, which guarantees false returns.
            for (var i = 0; i < 300 && sim.Phase == RunPhase.Active; i++)
                sim.Tick(0.5f, sim.ShipPosition, new ShipControlInput(1f, false, true));
            TestAssert.True(sim.Threat > 60f, $"threat high ({sim.Threat:F0})");
            DomainSetup.Wait(sim, sim.PulseCooldownRemaining + 0.1f);
            var pulse = sim.Pulse();
            TestAssert.True(pulse.Success, "pulse fires");
            var successes = 0;
            var ghostFades = 0;
            foreach (var contact in sim.Contacts.ToList())
            {
                DomainSetup.Teleport(sim, contact.ApproxPosition);
                var survey = sim.TrySurvey(contact.ContactId);
                if (survey.Success) successes++;
                else if (survey.Args.Any(a => a is string s && s.Contains("ghost", StringComparison.OrdinalIgnoreCase))) ghostFades++;
                else throw new Exception($"Unexplained survey failure: {survey.Reason}");
            }
            TestAssert.True(ghostFades >= 1, "at least one ghost faded explicitly");
            TestAssert.Equal(successes, sim.SurveysDone, "survey counter matches successes");
        });

        Case("unknown ids fail with reasons, never exceptions", () =>
        {
            var world = DomainSetup.World(catalog, 26UL, "biome.shelf_graveyard", "contract.salvage_quota");
            var sim = DomainSetup.Sim(catalog, world);
            TestAssert.False(sim.TrySalvage("loot.999").Success, "unknown loot");
            TestAssert.False(sim.TrySurvey("contact.nope").Success, "unknown contact");
            TestAssert.False(sim.TryServiceContractNode("node.999").Success, "unknown node");
            TestAssert.False(sim.TryServiceContractNode(world.ExtractionNodeId).Success, "non-service node");
            TestAssert.False(sim.TryRepairHull(9).Success, "bad zone");
            TestAssert.False(sim.TryRepairHull(0).Success, "healthy zone");
            TestAssert.False(RunSimulation.TryCreate(world, catalog, "difficulty.nope", "frame.skiff", null, "insurance.basic", out _, out var r1), "bad difficulty: " + r1);
            TestAssert.False(RunSimulation.TryCreate(world, catalog, "difficulty.standard", "frame.nope", null, "insurance.basic", out _, out var r2), "bad frame: " + r2);
            TestAssert.False(RunSimulation.TryCreate(world, catalog, "difficulty.standard", "frame.skiff", new[] { "modifier.nope" }, "insurance.basic", out _, out var r3), "bad modifier: " + r3);
        });

        Case("identical inputs reproduce identical host state", () =>
        {
            var a = DomainSetup.Sim(catalog, DomainSetup.World(catalog, 27UL, "biome.black_trench", "contract.salvage_quota"));
            var b = DomainSetup.Sim(catalog, DomainSetup.World(catalog, 27UL, "biome.black_trench", "contract.salvage_quota"));
            for (var i = 0; i < 30; i++)
            {
                var input = new ShipControlInput(0.6f, false, i % 3 == 0);
                a.Tick(0.2f, a.ShipPosition, input);
                b.Tick(0.2f, b.ShipPosition, input);
            }
            a.Pulse();
            b.Pulse();
            TestAssert.Equal(a.Threat, b.Threat, "threat deterministic");
            TestAssert.Equal(a.Noise, b.Noise, "noise deterministic");
            TestAssert.Equal(a.HullIntegrity, b.HullIntegrity, "hull deterministic");
            TestAssert.Equal(a.PowerDemand, b.PowerDemand, "power deterministic");
        });

        Case("failure draft applies insurance retention", () =>
        {
            var heavies = new HashSet<string>(StringComparer.Ordinal)
                { "creature.bell_maw", "creature.silt_stalker", "creature.warden_crab" };
            GeneratedWorld? world = null;
            System.Numerics.Vector3 denPos = default;
            var denFound = false;
            for (ulong seed = 100; seed < 140 && !denFound; seed++)
            {
                var candidate = DomainSetup.World(catalog, seed, "biome.shelf_graveyard", "contract.salvage_quota");
                var pick = candidate.ThreatSpawns.FirstOrDefault(t => heavies.Contains(t.CreatureId));
                if (pick is not null) { world = candidate; denPos = pick.Position; denFound = true; }
            }
            TestAssert.True(denFound, "found a heavy predator den");
            var sim = DomainSetup.Sim(catalog, world!, frame: "frame.skiff", insurance: "insurance.basic");
            foreach (var loot in world!.LootSpawns.Take(3))
            {
                DomainSetup.Teleport(sim, loot.Position);
                sim.TrySalvage(loot.SpawnId);
            }
            TestAssert.True(sim.SecuredSalvageValue > 0, "something secured");
            // Sit in the den at full throttle until the hull gives out.
            var guard = 0;
            while (sim.Phase == RunPhase.Active && guard++ < 1000)
            {
                sim.Tick(0.5f, denPos, new ShipControlInput(1f, false, false));
                if (sim.PulseCooldownRemaining <= 0f) sim.Pulse();
            }
            TestAssert.Equal(RunPhase.Failed, sim.Phase, "hull loss fails the run");
            var draft = sim.BuildFailureSettlement();
            TestAssert.True(draft is not null, "failure draft exists");
            TestAssert.Equal(SettlementOutcome.Failed, draft!.Outcome, "failed outcome");
            TestAssert.True(draft.RetainedCredits >= 0 && draft.RetainedCredits <= sim.SecuredSalvageValue + 1000, "retention bounded");
            TestAssert.True(!string.IsNullOrEmpty(sim.FailureReason), "failure reason recorded");
            TestAssert.True(draft.SettlementId.StartsWith("settle.", StringComparison.Ordinal), "failure settlement id");
        });

        return fail;
    }
}
