using AbyssScav.Domain;

namespace AbyssScav.Domain.Tests;

internal static class SystemsTests
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

        Case("pressure margin math and deep stress breaches", () =>
        {
            var world = DomainSetup.World(catalog, 11UL, "biome.hadal_ruins", "contract.salvage_quota");
            var sim = DomainSetup.Sim(catalog, world, frame: "frame.warden");
            // Hold at the trench mean: depth 7500 m vs warden rating 80.
            DomainSetup.Teleport(sim, world.GetNode(world.ExtractionNodeId).Position);
            var expected = 80f - (7500f / 100f) * 1.7f;
            TestAssert.True(Math.Abs(sim.PressureMargin - expected) < 0.5f, $"margin {sim.PressureMargin:F1} ~= {expected:F1}");
            TestAssert.True(sim.PressureMargin < 0f, "deep margin negative");
            var guard = 0;
            while (sim.FloodZones.All(z => z.Severity == 0) && guard++ < 120 && sim.Phase == RunPhase.Active)
                sim.Tick(0.5f, sim.ShipPosition, DomainSetup.Idle);
            TestAssert.True(sim.FloodZones.Any(z => z.Severity > 0), "stress breach occurred");
            TestAssert.True(sim.RecentEvents.Any(e => e.Kind == "hull.breach" && e.Args.Any(a => a is string s && s.Contains("pressure stress", StringComparison.Ordinal))), "pressure-stress breach event raised");
        });

        Case("shallow water never stress-breaches", () =>
        {
            var world = DomainSetup.World(catalog, 12UL, "biome.shelf_graveyard", "contract.salvage_quota");
            var sim = DomainSetup.Sim(catalog, world, frame: "frame.warden");
            // Shelf mean 2250 m: 22.5 pressure vs warden rating 80.
            DomainSetup.Teleport(sim, world.GetNode(world.ExtractionNodeId).Position);
            TestAssert.True(sim.PressureMargin > 0f, "shallow margin positive");
            for (var i = 0; i < 120; i++) sim.Tick(0.5f, sim.ShipPosition, DomainSetup.Idle);
            // Creature strikes may legitimately breach the hull; pressure must not.
            TestAssert.False(sim.RecentEvents.Any(e => e.Kind == "hull.breach" && e.Args.Any(a => a is string s && s.Contains("pressure stress", StringComparison.Ordinal))), "no pressure-stress breaches in shallow water");
        });

        Case("power deficit sheds load and browns out", () =>
        {
            var world = DomainSetup.World(catalog, 13UL, DomainSetup.FirstBiome(catalog, "contract.beacon_repair"), "contract.beacon_repair");
            var sim = DomainSetup.Sim(catalog, world, modifiers: new[] { "modifier.severe_current" });
            var beacon = world.Nodes.First(n => !string.IsNullOrEmpty(n.ServiceId));
            DomainSetup.Teleport(sim, beacon.Position);
            TestAssert.True(sim.TryServiceContractNode(beacon.Id).Success, "service starts weld load");
            TestAssert.True(sim.Pulse().Success, "pulse starts burst load");
            // Full burn on a skiff: 15 + 62.5 + 25 + 20 + 10 current > 100 supply.
            sim.Tick(0.1f, sim.ShipPosition, new ShipControlInput(1f, false, true));
            TestAssert.True(sim.PowerShedLevel >= 1, $"shed level {sim.PowerShedLevel}");
            TestAssert.True(sim.BrownoutActive, "brownout active");
            TestAssert.True(sim.RecentEvents.Any(e => e.Kind == "power.brownout"), "brownout event");
        });

        Case("silent running caps noise and kills the pulse", () =>
        {
            var loudWorld = DomainSetup.World(catalog, 14UL, "biome.shelf_graveyard", "contract.salvage_quota");
            var loud = DomainSetup.Sim(catalog, loudWorld);
            var quiet = DomainSetup.Sim(catalog, loudWorld);
            for (var i = 0; i < 40; i++)
            {
                loud.Tick(0.5f, loud.ShipPosition, new ShipControlInput(1f, false, false));
                quiet.Tick(0.5f, quiet.ShipPosition, new ShipControlInput(1f, true, false));
            }
            TestAssert.True(quiet.Noise < loud.Noise - 10f, $"silent {quiet.Noise:F1} vs loud {loud.Noise:F1}");
            var pulse = quiet.Pulse();
            TestAssert.False(pulse.Success, "silent pulse rejected: " + pulse.Reason);
        });

        Case("threat clock ramps and pulses add discrete threat", () =>
        {
            var world = DomainSetup.World(catalog, 15UL, "biome.shelf_graveyard", "contract.salvage_quota");
            var sim = DomainSetup.Sim(catalog, world);
            var before = sim.Threat;
            for (var i = 0; i < 20; i++) sim.Tick(0.5f, sim.ShipPosition, DomainSetup.Idle);
            TestAssert.True(sim.Threat > before, "threat ramps with time");
            var pulse = sim.Pulse();
            TestAssert.True(pulse.Success, "pulse fires");
            TestAssert.True(pulse.ThreatAdded >= 4f && pulse.ThreatAdded <= 10f, $"pulse threat {pulse.ThreatAdded:F1} in 4..10");
        });

        Case("creature FSM investigates, hunts, strikes, disengages", () =>
        {
            var world = DomainSetup.World(catalog, 16UL, "biome.black_trench", "contract.salvage_quota");
            var sim = DomainSetup.Sim(catalog, world, frame: "frame.mule");
            var den = world.ThreatSpawns.Select(t => t.Position).OrderBy(p => p.Length()).First();
            var states = new HashSet<CreatureState>();
            var guard = 0;
            while (guard++ < 600 && sim.Phase == RunPhase.Active)
            {
                // Full-throttle noise plus regular pulses to push the threat gate.
                sim.Tick(0.5f, den, new ShipControlInput(1f, false, false));
                if (sim.PulseCooldownRemaining <= 0f) sim.Pulse();
                foreach (var c in sim.CreatureStates) states.Add(c.State);
                if (states.Contains(CreatureState.Attack)) break;
            }
            TestAssert.True(states.Contains(CreatureState.Investigate), "investigated");
            TestAssert.True(states.Contains(CreatureState.Stalk) || states.Contains(CreatureState.Hunt), "stalked or hunted");
            TestAssert.True(states.Contains(CreatureState.Attack), "attacked (states: " + string.Join(",", states) + ")");
            // The strike lands on the ticks after entering the Attack state.
            for (var i = 0; i < 5 && sim.Phase == RunPhase.Active; i++)
                sim.Tick(0.5f, den, new ShipControlInput(1f, false, false));
            TestAssert.True(sim.HullIntegrity < sim.MaxHull, "strikes damaged the hull");

            // Outrun everything: distance breaks contact into disengage then dormant.
            var far = den + new System.Numerics.Vector3(900f, 0f, 900f);
            var calmed = false;
            for (var i = 0; i < 120 && sim.Phase == RunPhase.Active; i++)
            {
                sim.Tick(0.5f, far, DomainSetup.Idle);
                if (sim.CreatureStates.All(c => c.State is CreatureState.Dormant or CreatureState.Disengage)) { calmed = true; break; }
            }
            TestAssert.True(calmed, "AI disengages at range");
        });

        Case("winch records safe ground and fires exactly once", () =>
        {
            var world = DomainSetup.World(catalog, 17UL, "biome.shelf_graveyard", "contract.salvage_quota");
            var sim = DomainSetup.Sim(catalog, world);
            for (var i = 0; i < 12; i++) sim.Tick(0.5f, sim.ShipPosition, DomainSetup.Idle);
            TestAssert.True(sim.HasSafePosition, "safe position tracked");
            var first = sim.TryUseWinch(out var pos);
            TestAssert.True(first.Success, "first winch fires: " + first.Reason);
            TestAssert.True(sim.Winched(), "winch flagged spent");
            var second = sim.TryUseWinch(out _);
            TestAssert.False(second.Success, "second winch rejected: " + second.Reason);
            TestAssert.True(pos.Length() < 5000f, "return position sane");
        });

        return fail;
    }

    private static bool Winched(this RunSimulation sim) => sim.WinchUsed;
}
