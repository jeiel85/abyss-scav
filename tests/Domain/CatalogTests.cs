using AbyssScav.Domain;

namespace AbyssScav.Domain.Tests;

internal static class CatalogTests
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

        Case("exact content counts", () =>
        {
            TestAssert.Equal(3, catalog.Biomes.Count, "biome count");
            TestAssert.Equal(3, catalog.Frames.Count, "frame count");
            TestAssert.Equal(8, catalog.Contracts.Count, "contract count");
            TestAssert.Equal(24, catalog.Modules.Count, "module count");
            TestAssert.Equal(4, catalog.Difficulties.Count, "difficulty count");
            TestAssert.Equal(11, catalog.Creatures.Count, "creature count");
            TestAssert.Equal(8, catalog.RelicTraits.Count, "trait count");
            TestAssert.Equal(8, catalog.Modifiers.Count, "modifier count");
        });

        Case("module roster is 6/6/6/6 with distinct stats", () =>
        {
            foreach (var category in new[] { "Sonar", "Engine", "Hull", "Utility" })
            {
                var inCategory = catalog.Modules.Values.Where(m => m.Category == category).ToList();
                TestAssert.Equal(6, inCategory.Count, category + " module count");
                TestAssert.True(inCategory.Select(m => m.NoiseMultiplier).Distinct().Count() > 1, category + " modules vary");
            }
            TestAssert.True(catalog.Modules.ContainsKey("module.sonar.whisper_pulse"), "whisper pulse present");
        });

        Case("creature roster is 8 plus 3 apex", () =>
        {
            TestAssert.Equal(8, catalog.Creatures.Values.Count(c => !c.IsApex), "normal creatures");
            TestAssert.Equal(3, catalog.Creatures.Values.Count(c => c.IsApex), "apex creatures");
        });

        Case("all ids are well-formed and globally unique", () =>
        {
            var all = catalog.Biomes.Keys.Concat(catalog.Frames.Keys).Concat(catalog.Contracts.Keys)
                .Concat(catalog.Modules.Keys).Concat(catalog.Difficulties.Keys).Concat(catalog.Creatures.Keys)
                .Concat(catalog.RelicTraits.Keys).Concat(catalog.Modifiers.Keys).ToList();
            foreach (var id in all) TestAssert.True(StableIds.IsValid(id), "stable id " + id);
            TestAssert.Equal(all.Count, all.Distinct(StringComparer.Ordinal).Count(), "global uniqueness");
            TestAssert.False(StableIds.IsValid("UPPER case"), "uppercase rejected");
            TestAssert.False(StableIds.IsValid(""), "empty rejected");
        });

        Case("contract cross-references resolve", () =>
        {
            foreach (var c in catalog.Contracts.Values)
            {
                TestAssert.True(c.AllowedBiomeIds.Count > 0, c.Id + " has biomes");
                foreach (var b in c.AllowedBiomeIds)
                    TestAssert.True(catalog.Biomes.ContainsKey(b), $"{c.Id} biome {b}");
                foreach (var o in c.PrimaryObjectives)
                {
                    if (o.Kind is ContractObjectiveKind.QuestItem or ContractObjectiveKind.ServiceNode)
                        TestAssert.True(QuestItems.IsKnown(o.TargetQuestItemId), $"{c.Id} quest {o.TargetQuestItemId}");
                }
            }
        });

        Case("catalog hash is deterministic and versioned", () =>
        {
            var again = DomainSetup.Catalog();
            TestAssert.Equal(catalog.CatalogHash, again.CatalogHash, "hash stable across builds");
            TestAssert.True(catalog.CatalogHash.StartsWith("sha256:", StringComparison.Ordinal), "hash prefix");
            TestAssert.Equal(7 + 64, catalog.CatalogHash.Length, "hash length");
        });

        return fail;
    }
}
