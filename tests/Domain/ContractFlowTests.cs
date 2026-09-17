using System.Numerics;
using AbyssScav.Domain;

namespace AbyssScav.Domain.Tests;

internal static class ContractFlowTests
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

        Case("salvage quota full flow", () =>
        {
            var world = DomainSetup.World(catalog, 101UL, DomainSetup.FirstBiome(catalog, "contract.salvage_quota"), "contract.salvage_quota");
            var sim = DomainSetup.Sim(catalog, world, frame: "frame.mule");
            BankUntilComplete(sim, world);
            TestAssert.True(sim.Contract.PrimaryComplete, "quota complete");
            var early = sim.TryExtract();
            TestAssert.False(early.Success, "extract far from zone fails: " + early.Reason);
            DomainSetup.Teleport(sim, world.GetNode(world.ExtractionNodeId).Position);
            var result = sim.TryExtract();
            TestAssert.True(result.Success, "extract succeeds: " + result.Reason);
            TestAssert.True(result.Settlement is not null && result.Settlement.Credits > 0, "settlement pays");
            TestAssert.True(result.Settlement!.SettlementId.StartsWith("settle.", StringComparison.Ordinal), "settlement id");
            TestAssert.Equal(SettlementOutcome.Success, result.Settlement.Outcome, "outcome");
            TestAssert.Equal(RunPhase.Extracted, sim.Phase, "phase");
        });

        Case("blackbox recovery full flow", () =>
        {
            var world = DomainSetup.World(catalog, 202UL, DomainSetup.FirstBiome(catalog, "contract.blackbox_recovery"), "contract.blackbox_recovery");
            var sim = DomainSetup.Sim(catalog, world);
            var box = world.LootSpawns.First(l => l.QuestItemId == QuestItems.BlackBox);
            DomainSetup.Teleport(sim, box.Position);
            var salvage = sim.TrySalvage(box.SpawnId);
            TestAssert.True(salvage.Success, "blackbox salvaged: " + salvage.Reason);
            TestAssert.True(sim.Contract.PrimaryComplete, "contract complete");
            DomainSetup.Teleport(sim, world.GetNode(world.ExtractionNodeId).Position);
            TestAssert.True(sim.TryExtract().Success, "extracted");
        });

        Case("facility core stabilize then recover", () =>
        {
            var world = DomainSetup.World(catalog, 303UL, DomainSetup.FirstBiome(catalog, "contract.facility_core"), "contract.facility_core");
            var sim = DomainSetup.Sim(catalog, world);
            var coreNode = world.Nodes.First(n => n.ServiceId == QuestItems.CoreService);
            DomainSetup.Teleport(sim, coreNode.Position);
            var dock = sim.TryDock(coreNode.Id, 0f);
            TestAssert.True(dock.Success, "docked at facility: " + dock.Reason);
            var service = sim.TryServiceContractNode(coreNode.Id);
            TestAssert.True(service.Success, "core stabilized: " + service.Reason);
            TestAssert.Equal(1, sim.Sealant, "core service cost 2 of 3");
            var core = world.LootSpawns.First(l => l.QuestItemId == QuestItems.Core);
            var drill = sim.TryStartDrill(core.SpawnId);
            TestAssert.True(drill.Success, "drill started: " + drill.Reason);
            DomainSetup.Wait(sim, DomainConstants.DrillDurationSeconds + 0.5f);
            TestAssert.True(sim.Contract.PrimaryComplete, "contract complete");
            var undock = sim.TryUndock();
            TestAssert.True(undock.Success, "undocked from facility: " + undock.Reason);
            DomainSetup.Teleport(sim, world.GetNode(world.ExtractionNodeId).Position);
            TestAssert.True(sim.TryExtract().Success, "extracted");
        });

        Case("bio sample triple recovery", () =>
        {
            var world = DomainSetup.World(catalog, 404UL, DomainSetup.FirstBiome(catalog, "contract.bio_sample"), "contract.bio_sample");
            var sim = DomainSetup.Sim(catalog, world);
            var samples = world.LootSpawns.Where(l => l.QuestItemId.StartsWith("quest.bio.", StringComparison.Ordinal)).ToList();
            TestAssert.Equal(3, samples.Count, "three sample spawns");
            foreach (var s in samples)
            {
                DomainSetup.Teleport(sim, s.Position);
                TestAssert.True(sim.TrySalvage(s.SpawnId).Success, "sample " + s.SpawnId);
            }
            TestAssert.True(sim.Contract.PrimaryComplete, "contract complete");
            DomainSetup.Teleport(sim, world.GetNode(world.ExtractionNodeId).Position);
            TestAssert.True(sim.TryExtract().Success, "extracted");
        });

        Case("beacon repair both relays", () =>
        {
            var world = DomainSetup.World(catalog, 505UL, DomainSetup.FirstBiome(catalog, "contract.beacon_repair"), "contract.beacon_repair");
            var sim = DomainSetup.Sim(catalog, world);
            var beacons = world.Nodes.Where(n => n.ServiceId.StartsWith("service.beacon.", StringComparison.Ordinal)).ToList();
            TestAssert.Equal(2, beacons.Count, "two beacon nodes");
            foreach (var b in beacons)
            {
                DomainSetup.Teleport(sim, b.Position);
                TestAssert.True(sim.TryServiceContractNode(b.Id).Success, "beacon " + b.Id);
            }
            TestAssert.True(sim.Contract.PrimaryComplete, "contract complete");
            DomainSetup.Teleport(sim, world.GetNode(world.ExtractionNodeId).Position);
            TestAssert.True(sim.TryExtract().Success, "extracted");
        });

        Case("survey scan five contacts", () =>
        {
            var world = DomainSetup.World(catalog, 606UL, DomainSetup.FirstBiome(catalog, "contract.survey_scan"), "contract.survey_scan");
            var sim = DomainSetup.Sim(catalog, world, frame: "frame.mule");
            var surveyed = 0;
            foreach (var node in world.Nodes)
            {
                if (surveyed >= 5) break;
                DomainSetup.Teleport(sim, node.Position);
                DomainSetup.Wait(sim, sim.PulseCooldownRemaining + 0.1f);
                var pulse = sim.Pulse();
                TestAssert.True(pulse.Success, "pulse fires: " + pulse.Reason);
                foreach (var contact in sim.Contacts.Where(c => !c.Surveyed).Take(5 - surveyed).ToList())
                {
                    DomainSetup.Teleport(sim, contact.ApproxPosition);
                    if (sim.TrySurvey(contact.ContactId).Success) surveyed++;
                    if (surveyed >= 5) break;
                }
            }
            TestAssert.True(surveyed >= 5, $"surveyed {surveyed}/5");
            TestAssert.True(sim.Contract.PrimaryComplete, "contract complete");
            TestAssert.True(sim.SurveysDone >= 5, "survey counter");
            DomainSetup.Teleport(sim, world.GetNode(world.ExtractionNodeId).Position);
            TestAssert.True(sim.TryExtract().Success, "extracted");
        });

        Case("rescue pod heavy lift plus manifest", () =>
        {
            var world = DomainSetup.World(catalog, 707UL, DomainSetup.FirstBiome(catalog, "contract.rescue_pod"), "contract.rescue_pod");
            var sim = DomainSetup.Sim(catalog, world, frame: "frame.mule");
            var pod = world.LootSpawns.First(l => l.QuestItemId == QuestItems.Pod);
            DomainSetup.Teleport(sim, pod.Position);
            TestAssert.True(sim.TrySalvage(pod.SpawnId).Success, "pod recovered");
            BankUntilComplete(sim, world);
            TestAssert.True(sim.Contract.PrimaryComplete, "contract complete");
            DomainSetup.Teleport(sim, world.GetNode(world.ExtractionNodeId).Position);
            TestAssert.True(sim.TryExtract().Success, "extracted");
        });

        Case("apex observe then escape", () =>
        {
            var world = DomainSetup.World(catalog, 808UL, DomainSetup.FirstBiome(catalog, "contract.apex_observe"), "contract.apex_observe");
            var sim = DomainSetup.Sim(catalog, world, frame: "frame.mule");
            var apex = world.ThreatSpawns.First(t => t.IsApex);
            DomainSetup.Teleport(sim, apex.Position);
            TestAssert.True(sim.Pulse().Success, "reveal pulse");
            var guard = 0;
            while (!sim.Contract.PrimaryComplete && guard++ < 160 && sim.Phase == RunPhase.Active)
            {
                // Hold station inside observe range; the ship does not drift on its own.
                sim.Tick(0.5f, apex.Position, DomainSetup.Idle);
            }
            TestAssert.True(sim.Contract.PrimaryComplete, "observation complete");
            TestAssert.True(sim.Phase == RunPhase.Active, "survived the observation");
            DomainSetup.Teleport(sim, world.GetNode(world.ExtractionNodeId).Position);
            var result = sim.TryExtract();
            TestAssert.True(result.Success, "escaped: " + result.Reason);
            TestAssert.True(result.Settlement!.Shards >= 1, "apex shards paid");
        });

        Case("extraction gate rejects incomplete runs", () =>
        {
            var world = DomainSetup.World(catalog, 909UL, DomainSetup.FirstBiome(catalog, "contract.blackbox_recovery"), "contract.blackbox_recovery");
            var sim = DomainSetup.Sim(catalog, world);
            DomainSetup.Teleport(sim, world.GetNode(world.ExtractionNodeId).Position);
            var result = sim.TryExtract();
            TestAssert.False(result.Success, "incomplete extraction rejected");
            TestAssert.True(result.Settlement is null, "no settlement on rejection");
            TestAssert.True(result.Reason.Contains("recover_blackbox", StringComparison.Ordinal), "reason names missing objective: " + result.Reason);
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
}
