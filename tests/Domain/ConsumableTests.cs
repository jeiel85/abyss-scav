using System.Numerics;
using AbyssScav.Domain;

namespace AbyssScav.Domain.Tests;

/// <summary>
/// Per-run consumable coverage (docs/00 §11: 8 types). Verifies the catalog
/// surface, each hardcoded effect, the one-charge spend, and every refusal path.
/// </summary>
internal static class ConsumableTests
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

        Case("catalog defines 8 consumables with valid ids", () =>
        {
            TestAssert.Equal(8, catalog.Consumables.Count, "8 consumables");
            foreach (var c in catalog.Consumables.Values)
                TestAssert.True(c.Id.StartsWith("consumable.", StringComparison.Ordinal), $"{c.Id} prefixed");
        });

        Case("sealant canister restores sealant and spends its one charge", () =>
        {
            var world = DomainSetup.World(catalog, 21UL, "biome.shelf_graveyard", "contract.salvage_quota");
            var sim = DomainSetup.Sim(catalog, world, consumables: new[] { "consumable.sealant_canister" });
            var before = sim.Sealant;
            var use = sim.TryUseConsumable("consumable.sealant_canister");
            TestAssert.True(use.Success, "canister used: " + use.Reason);
            TestAssert.Equal(before + 3, sim.Sealant, "sealant +3");
            TestAssert.Equal(0, use.Remaining, "charge spent");
            TestAssert.Equal(0, sim.ConsumableCounts["consumable.sealant_canister"], "count zeroed");
            var again = sim.TryUseConsumable("consumable.sealant_canister");
            TestAssert.False(again.Success, "second use refused: " + again.Reason);
        });

        Case("battery pack boosts supply for its window", () =>
        {
            var world = DomainSetup.World(catalog, 22UL, "biome.shelf_graveyard", "contract.salvage_quota");
            var sim = DomainSetup.Sim(catalog, world, consumables: new[] { "consumable.battery_pack" });
            TestAssert.True(sim.TryUseConsumable("consumable.battery_pack").Success, "battery used");
            sim.Tick(0.5f, sim.ShipPosition, new ShipControlInput(1f, false, false));
            TestAssert.Equal(130f, sim.PowerSupply, "supply +30 over skiff 100");
            // Window expires: 30 s of ticks later the boost is gone.
            DomainSetup.Wait(sim, 31f);
            sim.Tick(0.5f, sim.ShipPosition, new ShipControlInput(1f, false, false));
            TestAssert.Equal(100f, sim.PowerSupply, "boost expired");
        });

        Case("hull patch repairs 15% of max hull without overflow", () =>
        {
            var world = DomainSetup.World(catalog, 23UL, "biome.black_trench", "contract.salvage_quota");
            var sim = DomainSetup.Sim(catalog, world, frame: "frame.mule", consumables: new[] { "consumable.hull_patch" });
            // Cap: at full hull the patch must not overflow.
            TestAssert.True(sim.TryUseConsumable("consumable.hull_patch").Success, "patch at full hull");
            TestAssert.Equal(sim.MaxHull, sim.HullIntegrity, "no overflow past max hull");
            // Damage via creature strikes (same den pattern as the FSM test), then patch.
            var sim2 = DomainSetup.Sim(catalog, world, frame: "frame.mule", consumables: new[] { "consumable.hull_patch" });
            var den = world.ThreatSpawns.Select(t => t.Position).OrderBy(p => p.Length()).First();
            var guard = 0;
            while (guard++ < 600 && sim2.Phase == RunPhase.Active && sim2.HullIntegrity >= sim2.MaxHull)
                sim2.Tick(0.5f, den, new ShipControlInput(1f, false, false));
            TestAssert.True(sim2.HullIntegrity < sim2.MaxHull, "hull damaged by strikes");
            var before = sim2.HullIntegrity;
            TestAssert.True(sim2.TryUseConsumable("consumable.hull_patch").Success, "patch used on damaged hull");
            var expected = Math.Min(sim2.MaxHull, before + sim2.MaxHull * 0.15f);
            TestAssert.True(Math.Abs(sim2.HullIntegrity - expected) < 0.01f, $"patch math {sim2.HullIntegrity:F1} ~= {expected:F1}");
        });

        Case("decoy retargets nearby creatures away from the ship", () =>
        {
            var world = DomainSetup.World(catalog, 24UL, "biome.shelf_graveyard", "contract.salvage_quota");
            var withDecoy = DomainSetup.Sim(catalog, world, consumables: new[] { "consumable.decoy" });
            var withoutDecoy = DomainSetup.Sim(catalog, world);
            var den = world.ThreatSpawns.Select(t => t.Position).OrderBy(p => p.Length()).First();
            DomainSetup.Teleport(withDecoy, den);
            DomainSetup.Teleport(withoutDecoy, den);
            // Let creatures notice the ship at the den.
            for (var i = 0; i < 4; i++) { withDecoy.Tick(0.5f, den, DomainSetup.Idle); withoutDecoy.Tick(0.5f, den, DomainSetup.Idle); }
            TestAssert.True(withDecoy.TryUseConsumable("consumable.decoy").Success, "decoy deployed");
            // Ship flees 500 m. With the decoy the den creatures keep investigating
            // the decoy; without it they lose interest (beyond hear*2) and go dormant.
            var far = den + new Vector3(500f, 0f, 0f);
            for (var i = 0; i < 20; i++) { withDecoy.Tick(0.5f, far, DomainSetup.Idle); withoutDecoy.Tick(0.5f, far, DomainSetup.Idle); }
            TestAssert.True(withDecoy.CreatureStates.Any(c => c.State is CreatureState.Investigate or CreatureState.Stalk),
                "decoy keeps creatures engaged");
            TestAssert.True(withoutDecoy.CreatureStates.All(c => c.State is CreatureState.Dormant or CreatureState.Disengage),
                "no decoy: creatures break off from the ship");
        });

        Case("flare spikes noise", () =>
        {
            var world = DomainSetup.World(catalog, 25UL, "biome.shelf_graveyard", "contract.salvage_quota");
            var sim = DomainSetup.Sim(catalog, world, consumables: new[] { "consumable.flare" });
            DomainSetup.Wait(sim, 2f); // let noise settle to idle floor
            var before = sim.Noise;
            TestAssert.True(sim.TryUseConsumable("consumable.flare").Success, "flare used");
            sim.Tick(0.5f, sim.ShipPosition, DomainSetup.Idle);
            sim.Tick(0.5f, sim.ShipPosition, DomainSetup.Idle);
            TestAssert.True(sim.Noise > before + 20f, $"noise spike visible ({before:F0} -> {sim.Noise:F0})");
        });

        Case("sonar buoy boosts passive range", () =>
        {
            var world = DomainSetup.World(catalog, 26UL, "biome.shelf_graveyard", "contract.salvage_quota");
            var sim = DomainSetup.Sim(catalog, world, consumables: new[] { "consumable.sonar_buoy" });
            var baseline = sim.PassiveSonarRangeMeters;
            TestAssert.True(sim.TryUseConsumable("consumable.sonar_buoy").Success, "buoy used");
            TestAssert.True(Math.Abs(sim.PassiveSonarRangeMeters - baseline * 1.5f) < 0.01f, "passive range x1.5");
        });

        Case("stim boosts repair efficiency", () =>
        {
            var world = DomainSetup.World(catalog, 27UL, "biome.hadal_ruins", "contract.salvage_quota");
            // Blackwater repair efficiency is 0.8, so the stim's x1.5 is observable:
            // stim -> 1.2 (flood cleared), plain -> 0.2 of the flood remains.
            var a = DomainSetup.Sim(catalog, world, difficulty: "difficulty.blackwater", frame: "frame.warden", consumables: new[] { "consumable.stim" });
            var b = DomainSetup.Sim(catalog, world, difficulty: "difficulty.blackwater", frame: "frame.warden");
            DomainSetup.Teleport(a, world.GetNode(world.ExtractionNodeId).Position);
            DomainSetup.Teleport(b, world.GetNode(world.ExtractionNodeId).Position);
            // Same seed -> same breach stream; tick both identically until flood
            // accumulates (needs a severity >= 2 breach; pumps hold severity 1 at 0).
            var guard = 0;
            while (guard++ < 2400 && a.Phase == RunPhase.Active && a.FloodZones.All(z => z.FloodPercent <= 0f))
                a.Tick(0.5f, a.ShipPosition, DomainSetup.Idle);
            for (var i = 0; i < guard; i++) b.Tick(0.5f, b.ShipPosition, DomainSetup.Idle);
            var zone = a.FloodZones.Select((z, i) => (z, i)).First(x => x.z.FloodPercent > 0f).i;
            TestAssert.True(a.FloodZones[zone].FloodPercent > 0f, "flood accumulated");
            TestAssert.True(a.TryUseConsumable("consumable.stim").Success, "stim used");
            var ra = a.TryRepairHull(zone);
            var rb = b.TryRepairHull(zone);
            TestAssert.True(ra.Success && rb.Success, "both repairs accepted");
            TestAssert.True(a.FloodZones[zone].FloodPercent < b.FloodZones[zone].FloodPercent, "stim clears more flood");
        });

        Case("antifreeze halves pressure event load", () =>
        {
            var world = DomainSetup.World(catalog, 28UL, "biome.hadal_ruins", "contract.salvage_quota");
            var sim = DomainSetup.Sim(catalog, world, frame: "frame.warden", consumables: new[] { "consumable.antifreeze" });
            DomainSetup.Teleport(sim, world.GetNode(world.ExtractionNodeId).Position);
            sim.Tick(0.5f, sim.ShipPosition, DomainSetup.Idle);
            TestAssert.True(sim.PressureMargin < 0f, "deep margin negative before antifreeze");
            TestAssert.True(sim.TryUseConsumable("consumable.antifreeze").Success, "antifreeze used");
            sim.Tick(0.5f, sim.ShipPosition, DomainSetup.Idle);
            TestAssert.True(sim.PressureMargin > 0f, "margin positive after antifreeze halves the load");
        });

        Case("unknown consumable refused", () =>
        {
            var world = DomainSetup.World(catalog, 29UL, "biome.shelf_graveyard", "contract.salvage_quota");
            var sim = DomainSetup.Sim(catalog, world, consumables: new[] { "consumable.sealant_canister" });
            var r = sim.TryUseConsumable("consumable.nope");
            TestAssert.False(r.Success, "unknown refused: " + r.Reason);
        });

        Case("no charges refused", () =>
        {
            var world = DomainSetup.World(catalog, 30UL, "biome.shelf_graveyard", "contract.salvage_quota");
            var sim = DomainSetup.Sim(catalog, world);
            var r = sim.TryUseConsumable("consumable.sealant_canister");
            TestAssert.False(r.Success, "unequipped refused: " + r.Reason);
        });

        Case("use after failure refused", () =>
        {
            var world = DomainSetup.World(catalog, 31UL, "biome.shelf_graveyard", "contract.salvage_quota");
            var sim = DomainSetup.Sim(catalog, world, modifiers: new[] { "modifier.time_window" }, consumables: new[] { "consumable.sealant_canister" });
            var guard = 0;
            while (sim.Phase == RunPhase.Active && guard++ < 5200)
                sim.Tick(0.5f, sim.ShipPosition, DomainSetup.Idle);
            TestAssert.Equal(RunPhase.Failed, sim.Phase, "deadline failed the run");
            var r = sim.TryUseConsumable("consumable.sealant_canister");
            TestAssert.False(r.Success, "post-failure refused: " + r.Reason);
        });

        return fail;
    }
}