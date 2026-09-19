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

        Case("wide array widens pulse reach, raises threat, quickens cooldown", () =>
        {
            var world = DomainSetup.World(catalog, 111UL, "biome.shelf_graveyard", "contract.salvage_quota");
            var stock = DomainSetup.Sim(catalog, world);
            var wide = DomainSetup.Sim(catalog, world, modules: new[] { ModuleLoadout.WideArray });
            TestAssert.Equal(450f * 1.3f, wide.ActivePulseRangeMeters, "pulse range x1.3");
            var stockPulse = stock.Pulse();
            var widePulse = wide.Pulse();
            TestAssert.True(stockPulse.Success && widePulse.Success, "both pulses fire");
            TestAssert.Equal(6f * 1.2f, widePulse.ThreatAdded, "wide threat x1.2");
            TestAssert.Equal(6.5f, widePulse.CooldownSeconds, "wide cooldown 6.5s");
        });

        Case("focus beam sharpens pulse confidence and quickens cooldown", () =>
        {
            var world = DomainSetup.World(catalog, 112UL, "biome.shelf_graveyard", "contract.salvage_quota");
            var stock = DomainSetup.Sim(catalog, world);
            var focused = DomainSetup.Sim(catalog, world, modules: new[] { ModuleLoadout.FocusBeam });
            var stockPulse = stock.Pulse();
            var focusPulse = focused.Pulse();
            TestAssert.True(stockPulse.Success && focusPulse.Success, "both pulses fire");
            TestAssert.Equal(6f, focusPulse.CooldownSeconds, "focus cooldown 6.0s");
            var far = stockPulse.Contacts.First(c => c.Confidence <= 0.31f);
            var same = focusPulse.Contacts.First(c => c.ContactId == far.ContactId);
            TestAssert.True(Math.Abs((same.Confidence - far.Confidence) - 0.1f) < 0.001f,
                $"focus adds +0.1 confidence ({far.Confidence:F2} -> {same.Confidence:F2})");
        });

        Case("resonance classifier sharpens biological reads, slows cooldown", () =>
        {
            var world = DomainSetup.World(catalog, 113UL, "biome.shelf_graveyard", "contract.salvage_quota");
            var stock = DomainSetup.Sim(catalog, world);
            var classified = DomainSetup.Sim(catalog, world, modules: new[] { ModuleLoadout.ResonanceClassifier });
            var creaturePos = stock.CreatureStates[0].Position;
            DomainSetup.Teleport(stock, creaturePos);
            DomainSetup.Teleport(classified, creaturePos);
            var stockPulse = stock.Pulse();
            var classPulse = classified.Pulse();
            TestAssert.True(stockPulse.Success && classPulse.Success, "both pulses fire");
            TestAssert.Equal(9f, classPulse.CooldownSeconds, "classifier cooldown 9.0s");
            var bio = stockPulse.Contacts.First(c => c.Class == SonarClass.Biological);
            var same = classPulse.Contacts.First(c => c.ContactId == bio.ContactId);
            TestAssert.True(Math.Abs((same.Confidence - bio.Confidence) - 0.15f) < 0.001f,
                $"classifier adds +0.15 bio confidence ({bio.Confidence:F2} -> {same.Confidence:F2})");
        });

        Case("ghost filter raises the false-contact threat threshold", () =>
        {
            var fx = ModuleLoadout.Resolve(new[] { ModuleLoadout.GhostFilter });
            TestAssert.Equal(20f, fx.GhostThresholdBonus, "ghost threshold +20");
            TestAssert.Equal(1f, fx.PulseRangeMult, "no pulse range change");
        });

        Case("overdrive thruster boosts thrust and noise together", () =>
        {
            var world = DomainSetup.World(catalog, 114UL, "biome.shelf_graveyard", "contract.salvage_quota");
            var stock = DomainSetup.Sim(catalog, world);
            var over = DomainSetup.Sim(catalog, world, modules: new[] { ModuleLoadout.OverdriveThruster });
            TestAssert.Equal(1.45f, over.EngineThrustMultiplier, "thrust x1.45");
            TestAssert.Equal(1.45f, over.EngineNoiseMultiplier, "noise x1.45");
            for (var i = 0; i < 40; i++)
            {
                stock.Tick(0.5f, stock.ShipPosition, new ShipControlInput(1f, false, false));
                over.Tick(0.5f, over.ShipPosition, new ShipControlInput(1f, false, false));
            }
            TestAssert.True(over.Noise > stock.Noise + 10f, $"overdrive {over.Noise:F1} vs stock {stock.Noise:F1}");
        });

        Case("cavitation dampener quiets without thrust loss", () =>
        {
            var world = DomainSetup.World(catalog, 115UL, "biome.shelf_graveyard", "contract.salvage_quota");
            var stock = DomainSetup.Sim(catalog, world);
            var dampened = DomainSetup.Sim(catalog, world, modules: new[] { ModuleLoadout.CavitationDampener });
            TestAssert.Equal(0.65f, dampened.EngineNoiseMultiplier, "noise x0.65");
            TestAssert.Equal(1f, dampened.EngineThrustMultiplier, "thrust untouched");
            for (var i = 0; i < 40; i++)
            {
                stock.Tick(0.5f, stock.ShipPosition, new ShipControlInput(1f, false, false));
                dampened.Tick(0.5f, dampened.ShipPosition, new ShipControlInput(1f, false, false));
            }
            TestAssert.True(dampened.Noise < stock.Noise - 10f, $"dampened {dampened.Noise:F1} vs stock {stock.Noise:F1}");
        });

        Case("emergency reverse bursts thrust below 30% hull", () =>
        {
            var fx = ModuleLoadout.Resolve(new[] { ModuleLoadout.EmergencyReverse });
            TestAssert.Equal(1.5f, fx.EmergencyThrustMult, "emergency thrust x1.5");
            var world = DomainSetup.World(catalog, 116UL, "biome.shelf_graveyard", "contract.salvage_quota");
            var sim = DomainSetup.Sim(catalog, world, modules: new[] { ModuleLoadout.EmergencyReverse });
            TestAssert.Equal(1f, sim.EngineThrustMultiplier, "full hull: no burst");
        });

        Case("abyss plating adds rating and hull together", () =>
        {
            var world = DomainSetup.World(catalog, 117UL, "biome.black_trench", "contract.salvage_quota");
            var stock = DomainSetup.Sim(catalog, world);
            var plated = DomainSetup.Sim(catalog, world, modules: new[] { ModuleLoadout.AbyssPlating });
            TestAssert.Equal(stock.MaxHull + 100f, plated.MaxHull, "max hull +100");
            TestAssert.Equal(40f, plated.HullRatingEffective - stock.HullRatingEffective, "rating +40");
        });

        Case("flood bulkhead halves breach fill rate", () =>
        {
            var fx = ModuleLoadout.Resolve(new[] { ModuleLoadout.FloodBulkhead });
            TestAssert.Equal(0.5f, fx.FloodFillMult, "fill rate x0.5");
            TestAssert.Equal(0f, fx.PumpRateBonus, "pump untouched");
        });

        Case("self-sealing foam doubles pump rate", () =>
        {
            var fx = ModuleLoadout.Resolve(new[] { ModuleLoadout.SelfSealingFoam });
            TestAssert.Equal(3f, fx.PumpRateBonus, "pump +3/s (3 -> 6)");
            TestAssert.Equal(1f, fx.FloodFillMult, "fill untouched");
        });

        Case("shock buffer softens creature strikes", () =>
        {
            var fx = ModuleLoadout.Resolve(new[] { ModuleLoadout.ShockBuffer });
            TestAssert.Equal(0.7f, fx.CreatureDamageMult, "strike damage x0.7");
        });

        Case("drill arm shortens extraction cuts", () =>
        {
            var world = DomainSetup.World(catalog, 118UL, "biome.shelf_graveyard", "contract.salvage_quota");
            var stock = DomainSetup.Sim(catalog, world);
            var armed = DomainSetup.Sim(catalog, world, modules: new[] { ModuleLoadout.DrillArm });
            TestAssert.Equal(8f, stock.DrillDurationSeconds, "stock drill 8s");
            TestAssert.Equal(5f, armed.DrillDurationSeconds, "drill arm 5s");
        });

        Case("repair drone regens hull while dry", () =>
        {
            var fx = ModuleLoadout.Resolve(new[] { ModuleLoadout.RepairDrone });
            TestAssert.Equal(2f, fx.HullRegenPerSecond, "regen 2/s");
        });

        Case("twenty modules supported, four honest holdouts", () =>
        {
            TestAssert.Equal(20, ModuleLoadout.SupportedIds.Count, "20 implemented modules");
            foreach (var holdout in new[] { "module.utility.emp_coil", "module.utility.decoy_launcher", "module.engine.heat_sink", "module.engine.vector_fin" })
            {
                TestAssert.False(ModuleLoadout.IsSupported(holdout), $"{holdout} still unsupported");
                TestAssert.Equal(-1, ModuleLoadout.PurchasableCostOf(catalog, holdout), $"{holdout} not purchasable");
            }
            foreach (var id in ModuleLoadout.SupportedIds)
            {
                TestAssert.True(ModuleLoadout.PurchasableCostOf(catalog, id) > 0, $"{id} purchasable");
                TestAssert.False(ModuleLoadout.Describe(id).StartsWith("No implemented", StringComparison.Ordinal), $"{id} described");
                TestAssert.False(ModuleLoadout.EffectKey(id) == "fx=none", $"{id} has effect key");
            }
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
                new[] { "module.utility.emp_coil" }, null), "unsupported catalog module rejected");
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
