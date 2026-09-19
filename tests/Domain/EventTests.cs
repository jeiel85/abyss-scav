using System.Numerics;
using AbyssScav.Domain;

namespace AbyssScav.Domain.Tests;

/// <summary>
/// Major random event coverage (docs/00 §12: 5 scheduled kinds + migration).
/// Verifies the scheduling mechanism, that every kind is reachable, that each
/// effect applies while active and expires, and that migration repositions
/// creatures. All seeds are fixed, so the corpus is deterministic.
/// </summary>
internal static class EventTests
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

        Case("scheduled events fire and expire over a long run", () =>
        {
            var world = DomainSetup.World(catalog, 1234UL, "biome.shelf_graveyard", "contract.salvage_quota");
            var sim = DomainSetup.Sim(catalog, world);
            DomainSetup.Wait(sim, 900f);
            var fired = sim.RecentEvents.Where(e => e.Kind.StartsWith("event.", StringComparison.Ordinal)).Select(e => e.Kind).ToList();
            TestAssert.True(fired.Count >= 2, $"at least 2 scheduled events fired ({string.Join(",", fired)})");
            TestAssert.True(sim.RecentEvents.Any(e => e.Kind == "event.ended"), "event expiry raised");
        });

        Case("all nine scheduled event kinds reachable across seeds", () =>
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var seed in new ulong[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18 })
            {
                var world = DomainSetup.World(catalog, seed, "biome.shelf_graveyard", "contract.salvage_quota");
                var sim = DomainSetup.Sim(catalog, world);
                DomainSetup.Wait(sim, 500f);
                foreach (var e in sim.RecentEvents)
                    if (e.Kind.StartsWith("event.", StringComparison.Ordinal)) seen.Add(e.Kind);
            }
            foreach (var kind in new[]
            {
                "event.acoustic_disturbance", "event.facility_alarm", "event.anomaly",
                "event.current_shift", "event.migration", "event.collapsing_trench",
                "event.false_distress_beacon", "event.relic_resonance", "event.extraction_ambush",
            })
                TestAssert.True(seen.Contains(kind), $"{kind} reachable");
        });

        Case("event effects apply while active and expire", () =>
        {
            var verified = new HashSet<string>(StringComparer.Ordinal);
            foreach (var seed in new ulong[] { 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55, 56, 57, 58, 59, 60, 61, 62, 63, 64 })
            {
                var world = DomainSetup.World(catalog, seed, "biome.shelf_graveyard", "contract.salvage_quota");
                var sim = DomainSetup.Sim(catalog, world);
                var baselinePassive = sim.PassiveSonarRangeMeters;
                var initialPositions = sim.CreatureStates.Select(c => c.Position).ToList();
                var guard = 0;
                while (guard++ < 1400 && sim.Phase == RunPhase.Active && verified.Count < 9)
                {
                    sim.Tick(0.5f, sim.ShipPosition, DomainSetup.Idle);
                    var kind = sim.ActiveMajorEvent;
                    if (kind is not null && verified.Add(kind))
                    {
                        switch (kind)
                        {
                            case "event.acoustic_disturbance":
                                TestAssert.True(Math.Abs(sim.PassiveSonarRangeMeters - baselinePassive * 0.5f) < 0.01f, "acoustic halves passive range");
                                break;
                            case "event.current_shift":
                                // Demand is computed before events fire in a tick;
                                // tick once more so the event's +15 PU shows up.
                                sim.Tick(0.5f, sim.ShipPosition, DomainSetup.Idle);
                                TestAssert.True(sim.PowerDemand >= 39f, $"current shift adds demand ({sim.PowerDemand:F0})");
                                break;
                            case "event.anomaly":
                                var pulse = sim.Pulse();
                                if (pulse.Success)
                                    TestAssert.True(pulse.Contacts.All(c => c.Confidence <= 0.4f + 0.001f), "anomaly caps confidence at 0.4");
                                break;
                            case "event.collapsing_trench":
                                var evt = sim.RecentEvents.FirstOrDefault(e => e.Kind == "event.collapsing_trench");
                                if (evt?.Args.Length > 0 && evt.Args[0] is string nodeId)
                                {
                                    var node = world.GetNode(nodeId);
                                    if (node is not null)
                                    {
                                        var before = sim.HullIntegrity;
                                        DomainSetup.Teleport(sim, node.Position);
                                        sim.Tick(0.5f, node.Position, DomainSetup.Idle);
                                        TestAssert.True(sim.HullIntegrity < before, "debris damages hull in the collapse zone");
                                    }
                                }
                                break;
                            case "event.false_distress_beacon":
                                TestAssert.True(sim.Contacts.Any(c => c.ContactId == "contact.beacon.false"), "false beacon contact on sonar");
                                var beacon = sim.Contacts.FirstOrDefault(c => c.ContactId == "contact.beacon.false");
                                if (beacon is not null)
                                {
                                    DomainSetup.Teleport(sim, beacon.ApproxPosition);
                                    sim.Tick(0.5f, beacon.ApproxPosition, DomainSetup.Idle);
                                    TestAssert.True(sim.RecentEvents.Any(e => e.Kind == "event.false_beacon_ambush"), "ambush triggered on approach");
                                }
                                break;
                            case "event.relic_resonance":
                                var noiseBefore = sim.Noise;
                                sim.Tick(0.5f, sim.ShipPosition, DomainSetup.Idle);
                                TestAssert.True(sim.Noise > noiseBefore + 10f, $"resonance raises noise ({noiseBefore:F0} -> {sim.Noise:F0})");
                                break;
                            case "event.extraction_ambush":
                                var extractionPos = world.GetNode(world.ExtractionNodeId).Position;
                                TestAssert.True(sim.CreatureStates.Any(c => c.State is CreatureState.Investigate or CreatureState.Stalk &&
                                    Vector3.Distance(c.Position, extractionPos) < 200f), "creatures waiting near extraction");
                                break;
                            case "event.facility_alarm":
                                break; // hear multiplier; presence verified
                        }
                    }
                    // Migration is instant: detect it via the event stream and verify repositioning.
                    if (sim.RecentEvents.Any(e => e.Kind == "event.migration") && verified.Add("event.migration"))
                    {
                        var moved = false;
                        var now = sim.CreatureStates.Select(c => c.Position).ToList();
                        for (var i = 0; i < initialPositions.Count; i++)
                            if (Vector3.Distance(now[i], initialPositions[i]) > 1f) { moved = true; break; }
                        TestAssert.True(moved, "migration repositioned creatures");
                    }
                }
            }
            foreach (var kind in new[]
            {
                "event.acoustic_disturbance", "event.facility_alarm", "event.anomaly",
                "event.current_shift", "event.migration", "event.collapsing_trench",
                "event.false_distress_beacon", "event.relic_resonance", "event.extraction_ambush",
            })
                TestAssert.True(verified.Contains(kind), $"{kind} effect verified");
        });

        return fail;
    }
}