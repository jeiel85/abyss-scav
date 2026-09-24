using System.Numerics;
using AbyssScav.Domain;

namespace AbyssScav.Domain.Tests;

/// <summary>
/// Co-op ship replication, per-player extraction and host-loss settlement
/// (docs/02 §9.5, §9.6, §12): interpolation buffer, correction blend, host pose
/// gate, team/individual extraction drafts, and the host-confirmed ledger.
/// </summary>
internal static class CoopShipTests
{
    public static int Run()
    {
        var cases = new (string Name, Action Test)[]
        {
            ("interp_renders_between_fixes", InterpBetweenFixes),
            ("interp_extrapolates_then_holds", InterpExtrapolates),
            ("interp_ignores_out_of_order_and_nonfinite", InterpRejectsGarbage),
            ("interp_teleport_resets_and_snaps", InterpTeleport),
            ("correction_blends_large_errors_snaps_huge", CorrectionBlend),
            ("pose_gate_accepts_plausible_motion", GateAccepts),
            ("pose_gate_rejects_invalid_values", GateRejectsInvalid),
            ("pose_gate_rate_and_jump_limits", GateRateAndJump),
            ("pose_gate_winch_teleport_once_survives_rekey", GateTeleportOnce),
            ("remote_extraction_requires_zone_and_objectives", RemoteExtractionRules),
            ("host_authority_extraction_is_idempotent", HostAuthorityExtraction),
            ("host_extraction_snapshot_is_team_extraction", TeamExtraction),
            ("snapshot_unknown_phase_refused", UnknownPhaseRefused),
            ("host_loss_pays_only_confirmed_cargo", HostLossConfirmedOnly),
            ("host_loss_without_confirmation_pays_zero", HostLossNoConfirmation),
            ("host_loss_draft_idempotent_and_final", HostLossIdempotent),
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

    private static ShipPoseSample Fix(double t, float x, float vx = 0f) =>
        new(t, new Vector3(x, 0f, 0f), Quaternion.Identity, new Vector3(vx, 0f, 0f));

    private static void InterpBetweenFixes()
    {
        var buffer = new PoseInterpolationBuffer();
        TestAssert.True(buffer.Push(Fix(1.0, 0f)), "first fix");
        TestAssert.True(buffer.Push(Fix(1.1, 10f)), "second fix");
        // Render time = now - 0.12; now 1.17 → 1.05 → halfway.
        TestAssert.True(buffer.TrySample(1.17, out var pose), "sampled");
        TestAssert.True(Math.Abs(pose.Position.X - 5f) < 0.01f, $"midpoint, got {pose.Position.X}");
        TestAssert.True(PoseInterpolationBuffer.DelaySeconds is >= 0.1 and <= 0.15, "delay inside the documented band");
    }

    private static void InterpExtrapolates()
    {
        var buffer = new PoseInterpolationBuffer();
        buffer.Push(Fix(1.0, 0f, 10f));
        buffer.Push(Fix(1.1, 1f, 10f));
        // Render 0.1 s past the newest fix → +1 m along velocity.
        TestAssert.True(buffer.TrySample(1.1 + PoseInterpolationBuffer.DelaySeconds + 0.1, out var ahead), "extrapolated");
        TestAssert.True(Math.Abs(ahead.Position.X - 2f) < 0.01f, $"extrapolated 1 m, got {ahead.Position.X}");
        // Far past: capped at MaxExtrapolationSeconds.
        TestAssert.True(buffer.TrySample(1.1 + PoseInterpolationBuffer.DelaySeconds + 5.0, out var held), "held");
        var cap = 1f + 10f * (float)PoseInterpolationBuffer.MaxExtrapolationSeconds;
        TestAssert.True(Math.Abs(held.Position.X - cap) < 0.01f, $"extrapolation capped at {cap}, got {held.Position.X}");
    }

    private static void InterpRejectsGarbage()
    {
        var buffer = new PoseInterpolationBuffer();
        TestAssert.True(buffer.Push(Fix(2.0, 0f)), "baseline");
        TestAssert.False(buffer.Push(Fix(1.9, 5f)), "out-of-order refused");
        TestAssert.False(buffer.Push(Fix(2.1, float.NaN)), "NaN refused");
        TestAssert.False(buffer.Push(new ShipPoseSample(2.2, Vector3.Zero, new Quaternion(0, 0, 0, 0), Vector3.Zero)), "zero quaternion refused");
        TestAssert.False(buffer.Push(Fix(double.PositiveInfinity, 0f)), "infinite time refused");
        TestAssert.Equal(1, buffer.Count, "only the baseline kept");
        TestAssert.False(new PoseInterpolationBuffer().TrySample(1.0, out _), "empty buffer yields nothing");
    }

    private static void InterpTeleport()
    {
        var buffer = new PoseInterpolationBuffer();
        buffer.Push(Fix(1.0, 0f));
        buffer.Push(Fix(1.1, 1f));
        TestAssert.True(buffer.Push(Fix(1.2, 500f)), "teleport accepted");
        TestAssert.Equal(1, buffer.Count, "history cleared");
        TestAssert.True(buffer.ConsumeSnap(), "snap flagged");
        TestAssert.False(buffer.ConsumeSnap(), "snap consumed once");
        TestAssert.True(buffer.TrySample(1.25, out var pose) && Math.Abs(pose.Position.X - 500f) < 0.01f, "renders the new fix, no slide");
    }

    private static void CorrectionBlend()
    {
        var target = new Vector3(0f, 0f, 0f);
        var small = PoseCorrection.Step(new Vector3(1f, 0f, 0f), target, 1f / 60f);
        TestAssert.True(small == target, "small error follows exactly");
        var large = PoseCorrection.Step(new Vector3(10f, 0f, 0f), target, 1f / 60f);
        TestAssert.True(large.X > 0.5f && large.X < 10f, $"large error eases in, got {large.X}");
        var huge = PoseCorrection.Step(new Vector3(100f, 0f, 0f), target, 1f / 60f);
        TestAssert.True(huge == target, "teleport-sized error snaps");
        var nan = PoseCorrection.Step(new Vector3(float.NaN, 0f, 0f), target, 1f / 60f);
        TestAssert.True(nan == target, "non-finite display recovers");
    }

    private static (ShipPoseGate Gate, Vector3 Origin) Gate()
    {
        var catalog = DomainSetup.Catalog();
        var contract = "contract.blackbox_recovery";
        var world = DomainSetup.World(catalog, 20260924, DomainSetup.FirstBiome(catalog, contract), contract);
        return (new ShipPoseGate(world), world.GetNode(world.ExtractionNodeId).Position);
    }

    /// <summary>A point <paramref name="meters"/> from a node toward the bounds centre (always in bounds: pad ≥ 80 m).</summary>
    private static Vector3 Toward(ShipPoseGate gate, Vector3 origin, float meters)
    {
        var center = (gate.BoundsMin + gate.BoundsMax) * 0.5f;
        var dir = center - origin;
        dir = dir.LengthSquared() < 1e-4f ? Vector3.UnitX : Vector3.Normalize(dir);
        return origin + dir * meters;
    }

    private static void GateAccepts()
    {
        var (gate, origin) = Gate();
        TestAssert.Equal(PoseVerdict.Accepted, gate.Evaluate(2, 10.0, origin, Quaternion.Identity, new Vector3(5f, 0f, 0f), false), "first pose");
        TestAssert.Equal(PoseVerdict.Accepted, gate.Evaluate(2, 10.0667, origin + new Vector3(1.5f, 0f, 0f), Quaternion.Identity, new Vector3(20f, 0f, 0f), false), "next pose at 15 Hz");
        TestAssert.Equal(PoseVerdict.Accepted, gate.Evaluate(3, 10.0, origin, new Quaternion(0f, 0f, 0f, 1.05f), Vector3.Zero, false), "other peer independent, near-unit quaternion ok");
    }

    private static void GateRejectsInvalid()
    {
        var (gate, origin) = Gate();
        TestAssert.Equal(PoseVerdict.NonFinite, gate.Evaluate(2, 1.0, new Vector3(float.NaN, 0f, 0f), Quaternion.Identity, Vector3.Zero, false), "NaN position");
        TestAssert.Equal(PoseVerdict.NonFinite, gate.Evaluate(2, 1.0, origin, Quaternion.Identity, new Vector3(float.PositiveInfinity, 0f, 0f), false), "infinite velocity");
        TestAssert.Equal(PoseVerdict.BadRotation, gate.Evaluate(2, 1.0, origin, new Quaternion(0f, 0f, 0f, 3f), Vector3.Zero, false), "non-unit quaternion");
        TestAssert.Equal(PoseVerdict.TooFast, gate.Evaluate(2, 1.0, origin, Quaternion.Identity, new Vector3(ShipPoseGate.MaxSpeedMps + 1f, 0f, 0f), false), "speed cap");
        TestAssert.Equal(PoseVerdict.OutOfBounds, gate.Evaluate(2, 1.0, gate.BoundsMax + new Vector3(1f, 0f, 0f), Quaternion.Identity, Vector3.Zero, false), "outside the world");
        TestAssert.Equal(PoseVerdict.Accepted, gate.Evaluate(2, 1.0, origin, Quaternion.Identity, Vector3.Zero, false), "refusals never poisoned the reference");
    }

    private static void GateRateAndJump()
    {
        var (gate, origin) = Gate();
        gate.Evaluate(2, 5.0, origin, Quaternion.Identity, Vector3.Zero, false);
        TestAssert.Equal(PoseVerdict.TooFrequent, gate.Evaluate(2, 5.01, origin, Quaternion.Identity, Vector3.Zero, false), "faster than 30 Hz refused");
        var far = Toward(gate, origin, 60f);
        TestAssert.Equal(PoseVerdict.ImpossibleJump, gate.Evaluate(2, 5.1, far, Quaternion.Identity, Vector3.Zero, false), "60 m in 0.1 s refused");
        // After enough time the same spot is reachable again (no permanent lock).
        TestAssert.Equal(PoseVerdict.Accepted, gate.Evaluate(2, 9.0, far, Quaternion.Identity, Vector3.Zero, false), "reachable after 4 s");
    }

    private static void GateTeleportOnce()
    {
        var (gate, origin) = Gate();
        gate.Evaluate(2, 1.0, origin, Quaternion.Identity, Vector3.Zero, false);
        var far = Toward(gate, origin, 60f);
        TestAssert.Equal(PoseVerdict.Accepted, gate.Evaluate(2, 1.1, far, Quaternion.Identity, Vector3.Zero, true), "winch relocation allowed once");
        gate.Rekey(2, 7);
        TestAssert.Equal(PoseVerdict.ImpossibleJump, gate.Evaluate(7, 1.2, origin, Quaternion.Identity, Vector3.Zero, true), "budget survives a reconnect rekey");
    }

    private static (RunSimulation Host, RunSimulation Client, GeneratedWorld World) SurveyTwins()
    {
        var catalog = DomainSetup.Catalog();
        var contract = "contract.survey_scan";
        var world = DomainSetup.World(catalog, 20260924, DomainSetup.FirstBiome(catalog, contract), contract);
        return (DomainSetup.Sim(catalog, world), DomainSetup.Sim(catalog, world), world);
    }

    private static void CompleteSurveys(RunSimulation host)
    {
        for (var i = 0; i < 10 && !host.Contract.PrimaryComplete; i++)
        {
            host.ApplyRemoteSurvey();
        }

        TestAssert.True(host.Contract.PrimaryComplete, "survey contract completed by merged surveys");
    }

    private static void RemoteExtractionRules()
    {
        var (host, _, world) = SurveyTwins();
        var zone = world.GetNode(world.ExtractionNodeId).Position;
        var early = host.CanAuthorizeRemoteExtraction(zone);
        TestAssert.False(early.Success, "objectives incomplete refused");
        TestAssert.True(early.Reason.StartsWith("Primary objectives incomplete", StringComparison.Ordinal), "reason names objectives");

        CompleteSurveys(host);
        TestAssert.False(host.CanAuthorizeRemoteExtraction(zone + new Vector3(0f, 0f, 500f)).Success, "outside the zone refused");
        TestAssert.False(host.CanAuthorizeRemoteExtraction(new Vector3(float.NaN, 0f, 0f)).Success, "invalid position refused");
        TestAssert.True(host.CanAuthorizeRemoteExtraction(zone).Success, "in zone with objectives complete approved");
        TestAssert.Equal(RunPhase.Active, host.Phase, "authorization never ends the host run");
    }

    private static void HostAuthorityExtraction()
    {
        var (host, client, _) = SurveyTwins();
        CompleteSurveys(host);
        TestAssert.True(client.ApplyWorldSnapshot(host.BuildWorldStateSnapshot()), "client converged");
        var first = client.TryExtractByHostAuthority();
        TestAssert.True(first.Success && first.Settlement is not null, "approved extraction settles");
        TestAssert.Equal(SettlementOutcome.Success, first.Settlement!.Outcome, "success outcome");
        TestAssert.True(first.Settlement.Credits > 0, "contract pays");
        var second = client.TryExtractByHostAuthority();
        TestAssert.False(second.Success, "second extraction refused");
        TestAssert.Equal(first.Settlement.SettlementId, client.Settlement!.SettlementId, "draft id stable");
    }

    private static void TeamExtraction()
    {
        var (host, client, _) = SurveyTwins();
        CompleteSurveys(host);
        var snap = host.BuildWorldStateSnapshot() with { Phase = (byte)RunPhase.Extracted };
        TestAssert.True(client.ApplyWorldSnapshot(snap), "applied");
        TestAssert.Equal(RunPhase.Extracted, client.Phase, "team extracted");
        TestAssert.True(client.Settlement is not null, "client has its own success draft");
        TestAssert.Equal(SettlementOutcome.Success, client.Settlement!.Outcome, "success outcome");
        TestAssert.Equal(host.SurveysDone, client.SurveysDone, "shared state converged before settling");
        var id = client.Settlement.SettlementId;
        TestAssert.True(client.ApplyWorldSnapshot(snap), "repeat delivery tolerated");
        TestAssert.Equal(id, client.Settlement!.SettlementId, "repeat delivery never mints a second draft");
    }

    private static void UnknownPhaseRefused()
    {
        var (host, client, _) = SurveyTwins();
        var snap = host.BuildWorldStateSnapshot() with { Phase = (byte)RunPhase.HostLost };
        TestAssert.False(client.ApplyWorldSnapshot(snap), "host-lost is never a wire phase");
        TestAssert.Equal(RunPhase.Active, client.Phase, "state untouched");
    }

    private static (RunSimulation Host, RunSimulation Client, GeneratedWorld World) SalvageTwins()
    {
        var catalog = DomainSetup.Catalog();
        var contract = "contract.blackbox_recovery";
        var world = DomainSetup.World(catalog, 20260919, DomainSetup.FirstBiome(catalog, contract), contract);
        return (DomainSetup.Sim(catalog, world), DomainSetup.Sim(catalog, world, insurance: RunSimulation.InsuranceNone), world);
    }

    private static void HostLossConfirmedOnly()
    {
        var (host, client, world) = SalvageTwins();
        var confirmedLoot = world.LootSpawns[0];
        TestAssert.True(host.TrySalvageFrom(confirmedLoot.SpawnId, confirmedLoot.Position).Success, "host confirmed salvage");
        TestAssert.True(client.ApplyWorldSnapshot(host.BuildWorldStateSnapshot()), "client converged");
        var confirmed = client.SecuredSalvageValue;
        TestAssert.True(confirmed > 0, "confirmed value present");
        TestAssert.Equal(confirmed, client.HostConfirmedSecuredValue, "ledger tracks the snapshot");

        // Local prediction the host never confirmed.
        var predicted = world.LootSpawns[^1];
        DomainSetup.Teleport(client, predicted.Position);
        TestAssert.True(client.TrySalvage(predicted.SpawnId).Success, "local prediction banked locally");
        TestAssert.True(client.SecuredSalvageValue > confirmed, "prediction raised the local value");

        var draft = client.BuildHostLossSettlement();
        TestAssert.True(draft is not null, "host-loss draft");
        TestAssert.Equal(SettlementOutcome.HostLost, draft!.Outcome, "host-lost outcome");
        TestAssert.Equal(RunPhase.HostLost, client.Phase, "phase moved");
        TestAssert.Equal((long)confirmed, draft.SecuredSalvageValue, "only confirmed cargo counted");
        TestAssert.Equal((long)Math.Round(confirmed * 0.2), draft.RetainedCredits, "no-cover retention (20%)");
        TestAssert.Equal(0L, draft.Shards, "no shards");
    }

    private static void HostLossNoConfirmation()
    {
        var (_, client, world) = SalvageTwins();
        var loot = world.LootSpawns[0];
        DomainSetup.Teleport(client, loot.Position);
        client.TrySalvage(loot.SpawnId);
        TestAssert.False(client.HasHostConfirmation, "no snapshot applied");
        var draft = client.BuildHostLossSettlement();
        TestAssert.True(draft is not null, "draft exists");
        TestAssert.Equal(0L, draft!.RetainedCredits, "nothing confirmed, nothing paid");
        TestAssert.Equal(0L, draft.ResearchData, "no confirmed research");
    }

    private static void HostLossIdempotent()
    {
        var (host, client, _) = SalvageTwins();
        TestAssert.True(client.ApplyWorldSnapshot(host.BuildWorldStateSnapshot()), "converged");
        var first = client.BuildHostLossSettlement();
        var second = client.BuildHostLossSettlement();
        TestAssert.True(first is not null, "first");
        TestAssert.True(ReferenceEquals(first, second), "cached draft returned");
        // A late snapshot after settlement never revives the run.
        client.ApplyWorldSnapshot(host.BuildWorldStateSnapshot() with { Phase = (byte)RunPhase.Extracted });
        TestAssert.Equal(RunPhase.HostLost, client.Phase, "phase is final");
        TestAssert.True(client.Settlement is null, "no success draft after host loss");
        TestAssert.False(client.TryExtractByHostAuthority().Success, "no extraction after host loss");

        var (_, failed, _) = SalvageTwins();
        failed.ApplyWorldSnapshot(host.BuildWorldStateSnapshot() with { Phase = (byte)RunPhase.Failed, FailureReason = "x" });
        TestAssert.True(failed.BuildHostLossSettlement() is null, "a run that already ended never gets a host-loss draft");
    }
}
