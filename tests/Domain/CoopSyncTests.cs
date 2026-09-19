using System.Numerics;
using AbyssScav.Domain;

namespace AbyssScav.Domain.Tests;

/// <summary>
/// Co-op world sync (docs/02 §9): host-authoritative interactions and the
/// client correction path. Host and client sims are built from the same
/// deterministic world, exactly like a LayoutHash-verified join.
/// </summary>
internal static class CoopSyncTests
{
    public static int Run()
    {
        var cases = new (string Name, Action Test)[]
        {
            ("remote_salvage_range_validated", RemoteSalvageRange),
            ("remote_salvage_duplicate_rejected", RemoteSalvageDuplicate),
            ("remote_drill_completes_without_dock", RemoteDrillCompletes),
            ("remote_drill_cancel_stops_cut", RemoteDrillCancel),
            ("remote_survey_merges_contract", RemoteSurveyMerges),
            ("remote_service_validates_range", RemoteServiceRange),
            ("remote_pulse_exposes_creatures", RemotePulseExposes),
            ("world_snapshot_converges_client", SnapshotConverges),
            ("snapshot_phase_failure_applied", SnapshotPhaseFailure),
            ("snapshot_world_mismatch_rejected", SnapshotMismatch),
        };

        var fail = 0;
        foreach (var (name, test) in cases)
        {
            try
            {
                test();
                Console.WriteLine($"  ok: {name}");
            }
            catch (Exception ex)
            {
                fail++;
                Console.WriteLine($"  FAIL: {name}: {ex.Message}");
            }
        }

        return fail;
    }

    private static (RunSimulation Host, RunSimulation Client, GeneratedWorld World, ContentCatalog Catalog) Twins(
        string contractId, string biomeId)
    {
        var catalog = DomainSetup.Catalog();
        var world = DomainSetup.World(catalog, 20260919, biomeId, contractId);
        var host = DomainSetup.Sim(catalog, world);
        var client = DomainSetup.Sim(catalog, world);
        return (host, client, world, catalog);
    }

    private static LootSpawn FirstLoot(GeneratedWorld world) => world.LootSpawns[0];

    private static void RemoteSalvageRange()
    {
        var (host, _, world, _) = Twins("contract.blackbox_recovery", DomainSetup.FirstBiome(DomainSetup.Catalog(), "contract.blackbox_recovery"));
        var loot = FirstLoot(world);
        DomainSetup.Teleport(host, loot.Position);

        var inRange = host.TrySalvageFrom(loot.SpawnId, loot.Position);
        TestAssert.True(inRange.Success, "in-range salvage authorized");
        TestAssert.True(inRange.ValueBanked > 0, "value banked");

        var other = world.LootSpawns[^1];
        var far = other.Position + new Vector3(0f, 0f, 5000f);
        var outOfRange = host.TrySalvageFrom(other.SpawnId, far);
        TestAssert.False(outOfRange.Success, "out-of-range salvage refused");
    }

    private static void RemoteSalvageDuplicate()
    {
        var (host, _, world, _) = Twins("contract.blackbox_recovery", DomainSetup.FirstBiome(DomainSetup.Catalog(), "contract.blackbox_recovery"));
        var loot = FirstLoot(world);
        DomainSetup.Teleport(host, loot.Position);

        TestAssert.True(host.TrySalvageFrom(loot.SpawnId, loot.Position).Success, "first authorized");
        var dup = host.TrySalvageFrom(loot.SpawnId, loot.Position);
        TestAssert.False(dup.Success, "duplicate refused");
    }

    private static void RemoteDrillCompletes()
    {
        var (host, _, world, _) = Twins("contract.blackbox_recovery", DomainSetup.FirstBiome(DomainSetup.Catalog(), "contract.blackbox_recovery"));
        var loot = FirstLoot(world);
        DomainSetup.Teleport(host, loot.Position);

        // No dock required for a remote cut: the requester's sim owns its dock.
        var start = host.TryStartDrillFrom(loot.SpawnId, loot.Position);
        TestAssert.True(start.Success, "remote drill started without host dock");
        DomainSetup.Wait(host, 9f);
        TestAssert.False(host.IsDrilling, "drill finished");
        TestAssert.True(host.CargoItems.Any(c => c.LootSpawnId == loot.SpawnId), "loot banked after cut");
        TestAssert.True(host.SecuredSalvageValue > 0, "value banked");
    }

    private static void RemoteDrillCancel()
    {
        var (host, _, world, _) = Twins("contract.blackbox_recovery", DomainSetup.FirstBiome(DomainSetup.Catalog(), "contract.blackbox_recovery"));
        var loot = FirstLoot(world);
        DomainSetup.Teleport(host, loot.Position);

        TestAssert.True(host.TryStartDrillFrom(loot.SpawnId, loot.Position).Success, "started");
        TestAssert.True(host.TryCancelDrill().Success, "cancelled");
        DomainSetup.Wait(host, 9f);
        TestAssert.False(host.CargoItems.Any(c => c.LootSpawnId == loot.SpawnId), "no award after cancel");
    }

    private static void RemoteSurveyMerges()
    {
        var (host, _, _, _) = Twins("contract.survey_scan", DomainSetup.FirstBiome(DomainSetup.Catalog(), "contract.survey_scan"));
        host.ApplyRemoteSurvey();
        TestAssert.Equal(1, host.SurveysDone, "survey counted");
        var objective = host.Contract.Objectives.First(o => o.Kind == ContractObjectiveKind.Survey);
        TestAssert.Equal(1, objective.Current, "contract survey progress merged");
    }

    private static void RemoteServiceRange()
    {
        var (host, _, world, _) = Twins("contract.beacon_repair", DomainSetup.FirstBiome(DomainSetup.Catalog(), "contract.beacon_repair"));
        var node = world.Nodes.First(n => !string.IsNullOrEmpty(n.ServiceId));

        var inRange = host.ApplyRemoteService(node.Id, node.Position);
        TestAssert.True(inRange.Success, "in-range service authorized");
        TestAssert.Equal(1, host.Contract.Objectives.First(o => o.Kind == ContractObjectiveKind.ServiceNode).Current, "service progress");

        var other = world.Nodes.First(n => n.Id != node.Id && !string.IsNullOrEmpty(n.ServiceId));
        var far = other.Position + new Vector3(0f, 0f, 5000f);
        var outOfRange = host.ApplyRemoteService(other.Id, far);
        TestAssert.False(outOfRange.Success, "out-of-range service refused");
    }

    private static void RemotePulseExposes()
    {
        var (host, _, _, _) = Twins("contract.blackbox_recovery", DomainSetup.FirstBiome(DomainSetup.Catalog(), "contract.blackbox_recovery"));
        var creature = host.CreatureStates[0];
        DomainSetup.Teleport(host, creature.Position);

        host.ApplyRemotePulse(host.ShipPosition);
        var snap = host.BuildWorldStateSnapshot();
        TestAssert.True(snap.Creatures[0].SonarExposedSeconds > 0f, "creature exposed by remote pulse");
    }

    private static void SnapshotConverges()
    {
        var (host, client, world, _) = Twins("contract.blackbox_recovery", DomainSetup.FirstBiome(DomainSetup.Catalog(), "contract.blackbox_recovery"));
        var loot = FirstLoot(world);
        DomainSetup.Teleport(host, loot.Position);
        TestAssert.True(host.TrySalvageFrom(loot.SpawnId, loot.Position).Success, "host salvaged");

        var snap = host.BuildWorldStateSnapshot();
        TestAssert.True(client.ApplyWorldSnapshot(snap), "client applied snapshot");

        TestAssert.Equal(host.SecuredSalvageValue, client.SecuredSalvageValue, "secured value converged");
        TestAssert.Equal(host.CargoItems.Count, client.CargoItems.Count, "cargo converged");
        TestAssert.True(client.CargoItems.Any(c => c.LootSpawnId == loot.SpawnId), "client cargo has the loot");
        TestAssert.Equal(host.Contract.PrimaryComplete, client.Contract.PrimaryComplete, "contract converged");
        TestAssert.Equal(host.CreatureStates.Count, client.CreatureStates.Count, "creature count stable");
    }

    private static void SnapshotPhaseFailure()
    {
        var (host, client, _, _) = Twins("contract.blackbox_recovery", DomainSetup.FirstBiome(DomainSetup.Catalog(), "contract.blackbox_recovery"));
        var snap = host.BuildWorldStateSnapshot() with
        {
            Phase = (byte)RunPhase.Failed,
            FailureReason = "host hull lost",
        };
        TestAssert.True(client.ApplyWorldSnapshot(snap), "applied");
        TestAssert.Equal(RunPhase.Failed, client.Phase, "phase mirrored");
        TestAssert.Equal("host hull lost", client.FailureReason, "reason mirrored");
    }

    private static void SnapshotMismatch()
    {
        var (host, client, _, _) = Twins("contract.blackbox_recovery", DomainSetup.FirstBiome(DomainSetup.Catalog(), "contract.blackbox_recovery"));
        var snap = host.BuildWorldStateSnapshot();
        // Corrupt the creature count: a divergent world must be refused, never applied.
        var corrupt = snap with { Creatures = snap.Creatures.Take(Math.Max(0, snap.Creatures.Count - 1)).ToList() };
        TestAssert.False(client.ApplyWorldSnapshot(corrupt), "divergent snapshot refused");
        TestAssert.Equal(RunPhase.Active, client.Phase, "client state untouched");
    }
}