using System.Numerics;
using AbyssScav.Domain;

namespace AbyssScav.Domain.Tests;

/// <summary>
/// Module loadout: precise numeric baseline-vs-equipped checks per supported
/// module, plus rejection of duplicates/unsupported/unknown/unowned IDs and
/// deterministic empty-default behavior.
/// </summary>
internal static class ModuleLoadoutTests
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

        Case("empty loadout is the stock default", () =>
        {
            var world = DomainSetup.World(catalog, 101UL, "biome.shelf_graveyard", "contract.salvage_quota");
            var sim = DomainSetup.Sim(catalog, world);
            TestAssert.Equal(0, sim.ModuleIds.Count, "no modules by default");
            TestAssert.Equal(15f, sim.SalvageRangeMeters, "stock salvage reach");
            TestAssert.Equal(1f, sim.EngineNoiseMultiplier, "stock noise mult");
            TestAssert.Equal(1f, sim.EngineThrustMultiplier, "stock thrust mult");
            TestAssert.Equal(700f, sim.MaxHull, "skiff stock hull");
            TestAssert.Equal(450f, sim.ActivePulseRangeMeters, "shelf pulse range stock");
            TestAssert.Equal(220f, sim.PassiveSonarRangeMeters, "shelf passive range stock");
        });

        Case("whisper pulse shortens reach, halves threat, quickens cooldown", () =>
        {
            var world = DomainSetup.World(catalog, 102UL, "biome.shelf_graveyard", "contract.salvage_quota");
            var stock = DomainSetup.Sim(catalog, world);
            var quiet = DomainSetup.Sim(catalog, world, modules: new[] { ModuleLoadout.WhisperPulse });
            TestAssert.Equal(1, quiet.ModuleIds.Count, "loadout snapshot holds one id");
            TestAssert.Equal(450f * 0.7f, quiet.ActivePulseRangeMeters, "pulse range x0.7");
            TestAssert.Equal(stock.ActivePulseRangeMeters * 0.7f, quiet.ActivePulseRangeMeters, "range ratio vs baseline");
            var stockPulse = stock.Pulse();
            var whisperPulse = quiet.Pulse();
            TestAssert.True(stockPulse.Success && whisperPulse.Success, "both pulses fire");
            TestAssert.Equal(6f, stockPulse.ThreatAdded, "baseline threat +6");
            TestAssert.Equal(6f * 0.4f, whisperPulse.ThreatAdded, "whisper threat x0.4");
            TestAssert.Equal(8f, stockPulse.CooldownSeconds, "baseline cooldown 8s");
            TestAssert.Equal(7.5f, whisperPulse.CooldownSeconds, "whisper cooldown 7.5s");
        });

        Case("passive booster widens passive range only", () =>
        {
            var world = DomainSetup.World(catalog, 103UL, "biome.shelf_graveyard", "contract.salvage_quota");
            var stock = DomainSetup.Sim(catalog, world);
            var boosted = DomainSetup.Sim(catalog, world, modules: new[] { ModuleLoadout.PassiveBooster });
            TestAssert.Equal(220f * 1.5f, boosted.PassiveSonarRangeMeters, "passive range x1.5");
            TestAssert.True(boosted.PassiveSonarRangeMeters > stock.PassiveSonarRangeMeters, "wider than baseline");
            TestAssert.Equal(stock.ActivePulseRangeMeters, boosted.ActivePulseRangeMeters, "pulse range untouched");
        });

        Case("quiet prop quiets noise and trims thrust", () =>
        {
            var world = DomainSetup.World(catalog, 104UL, "biome.shelf_graveyard", "contract.salvage_quota");
            var stock = DomainSetup.Sim(catalog, world);
            var muffled = DomainSetup.Sim(catalog, world, modules: new[] { ModuleLoadout.QuietProp });
            TestAssert.Equal(0.6f, muffled.EngineNoiseMultiplier, "noise x0.6");
            TestAssert.Equal(0.85f, muffled.EngineThrustMultiplier, "thrust x0.85");
            for (var i = 0; i < 40; i++)
            {
                stock.Tick(0.5f, stock.ShipPosition, new ShipControlInput(1f, false, false));
                muffled.Tick(0.5f, muffled.ShipPosition, new ShipControlInput(1f, false, false));
            }
            TestAssert.True(muffled.Noise < stock.Noise - 10f, $"muffled {muffled.Noise:F1} vs stock {stock.Noise:F1}");
        });

        Case("reinforced rib adds max hull only", () =>
        {
            var world = DomainSetup.World(catalog, 105UL, "biome.shelf_graveyard", "contract.salvage_quota");
            var stock = DomainSetup.Sim(catalog, world);
            var ribbed = DomainSetup.Sim(catalog, world, modules: new[] { ModuleLoadout.ReinforcedRib });
            TestAssert.Equal(stock.MaxHull + 150f, ribbed.MaxHull, "max hull +150");
            TestAssert.Equal(850f, ribbed.MaxHull, "skiff ribbed hull");
            TestAssert.Equal(stock.HullRatingEffective, ribbed.HullRatingEffective, "pressure rating untouched");
        });

        Case("pressure skin lifts pressure margin by exactly its bonus", () =>
        {
            var world = DomainSetup.World(catalog, 106UL, "biome.black_trench", "contract.salvage_quota");
            var stock = DomainSetup.Sim(catalog, world);
            var skinned = DomainSetup.Sim(catalog, world, modules: new[] { ModuleLoadout.PressureSkin });
            DomainSetup.Teleport(stock, world.GetNode(world.ExtractionNodeId).Position);
            DomainSetup.Teleport(skinned, world.GetNode(world.ExtractionNodeId).Position);
            TestAssert.Equal(20f, skinned.HullRatingEffective - stock.HullRatingEffective, "rating +20");
            TestAssert.True(Math.Abs((skinned.PressureMargin - stock.PressureMargin) - 20f) < 0.01f,
                $"margin delta {(skinned.PressureMargin - stock.PressureMargin):F2} == +20");
            TestAssert.Equal(stock.MaxHull, skinned.MaxHull, "max hull untouched");
        });

        Case("salvage magnet extends real salvage reach", () =>
        {
            var world = DomainSetup.World(catalog, 107UL, "biome.shelf_graveyard", "contract.salvage_quota");
            var loot = world.LootSpawns.First(l => l.MassKg <= 1500f);
            var anchor = loot.Position + new Vector3(20f, 0f, 0f);
            var stock = DomainSetup.Sim(catalog, world, frame: "frame.mule");
            var magnet = DomainSetup.Sim(catalog, world, frame: "frame.mule", modules: new[] { ModuleLoadout.SalvageMagnet });
            TestAssert.Equal(24f, magnet.SalvageRangeMeters, "magnet reach 24m");
            DomainSetup.Teleport(stock, anchor);
            DomainSetup.Teleport(magnet, anchor);
            var denied = stock.TrySalvage(loot.SpawnId);
            TestAssert.False(denied.Success, "stock cannot reach 20m loot: " + denied.Reason);
            var banked = magnet.TrySalvage(loot.SpawnId);
            TestAssert.True(banked.Success, "magnet banks 20m loot: " + banked.Reason);
        });

        Case("duplicates, category collisions, unsupported, unknown rejected", () =>
        {
            var world = DomainSetup.World(catalog, 108UL, "biome.shelf_graveyard", "contract.salvage_quota");
            var owned = ModuleLoadout.SupportedIds.ToList();
            TestAssert.False(RunSimulation.TryCreate(world, catalog, "difficulty.standard", "frame.skiff",
                null, RunSimulation.InsuranceNone, out _, out var dupReason,
                new[] { ModuleLoadout.WhisperPulse, ModuleLoadout.WhisperPulse }, null), "duplicate rejected");
            TestAssert.True(dupReason.Contains("duplicate", StringComparison.Ordinal), "duplicate reason: " + dupReason);
            TestAssert.False(RunSimulation.TryCreate(world, catalog, "difficulty.standard", "frame.skiff",
                null, RunSimulation.InsuranceNone, out _, out var catReason,
                new[] { ModuleLoadout.WhisperPulse, ModuleLoadout.PassiveBooster }, null), "same-category pair rejected");
            TestAssert.True(catReason.Contains("one module", StringComparison.Ordinal), "category reason: " + catReason);
            TestAssert.False(RunSimulation.TryCreate(world, catalog, "difficulty.standard", "frame.skiff",
                null, RunSimulation.InsuranceNone, out _, out var unsupReason,
                new[] { "module.sonar.wide_array" }, null), "unsupported catalog module rejected");
            TestAssert.True(unsupReason.Contains("no implemented in-run effect", StringComparison.Ordinal), "unsupported reason: " + unsupReason);
            TestAssert.False(RunSimulation.TryCreate(world, catalog, "difficulty.standard", "frame.skiff",
                null, RunSimulation.InsuranceNone, out _, out var unknownReason,
                new[] { "module.nope.ghost" }, null), "unknown id rejected");
            TestAssert.True(unknownReason.Contains("unknown module", StringComparison.Ordinal), "unknown reason: " + unknownReason);
            TestAssert.False(RunSimulation.TryCreate(world, catalog, "difficulty.standard", "frame.skiff",
                null, RunSimulation.InsuranceNone, out _, out var ownedReason,
                new[] { ModuleLoadout.QuietProp }, Array.Empty<string>()), "unowned rejected when profile given");
            TestAssert.True(ownedReason.Contains("not unlocked", StringComparison.Ordinal), "unowned reason: " + ownedReason);
            TestAssert.True(RunSimulation.TryCreate(world, catalog, "difficulty.standard", "frame.skiff",
                null, RunSimulation.InsuranceNone, out _, out _,
                new[] { ModuleLoadout.QuietProp }, owned), "owned passes");
        });

        Case("blueprints are not consumed by equipping", () =>
        {
            var world = DomainSetup.World(catalog, 109UL, "biome.shelf_graveyard", "contract.salvage_quota");
            var owned = new List<string> { ModuleLoadout.SalvageMagnet };
            TestAssert.True(RunSimulation.TryCreate(world, catalog, "difficulty.standard", "frame.skiff",
                null, RunSimulation.InsuranceNone, out var sim, out _, new[] { ModuleLoadout.SalvageMagnet }, owned), "equips");
            TestAssert.Equal(1, owned.Count, "owned list untouched by run creation");
            TestAssert.True(owned.Contains(ModuleLoadout.SalvageMagnet, StringComparer.Ordinal), "blueprint retained");
            TestAssert.True(sim!.ModuleIds.Contains(ModuleLoadout.SalvageMagnet, StringComparer.Ordinal), "snapshot holds the equip");
        });

        Case("loadout snapshot is immutable and empty validates clean", () =>
        {
            var world = DomainSetup.World(catalog, 110UL, "biome.shelf_graveyard", "contract.salvage_quota");
            var ids = new List<string> { ModuleLoadout.ReinforcedRib };
            var sim = DomainSetup.Sim(catalog, world, modules: ids);
            ids.Clear();
            TestAssert.Equal(1, sim.ModuleIds.Count, "snapshot immune to caller mutation");
            TestAssert.True(ModuleLoadout.TryValidate(null, catalog, null, out var emptyErrors), "null loadout valid: " + string.Join("; ", emptyErrors));
            var neutral = ModuleLoadout.Resolve(null);
            TestAssert.Equal(1f, neutral.PulseRangeMult, "neutral pulse range");
            TestAssert.Equal(DomainConstants.InteractRangeMeters, neutral.SalvageRangeMeters, "neutral salvage reach");
        });

        Case("catalog hash is deterministic and versioned", () =>
        {
            var again = DomainSetup.Catalog();
            TestAssert.Equal(catalog.CatalogHash, again.CatalogHash, "hash stable across builds");
            TestAssert.True(catalog.CatalogHash.StartsWith("sha256:", StringComparison.Ordinal), "hash prefix");
        });

        return fail;
    }
}
