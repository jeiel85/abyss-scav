namespace AbyssScav.Persistence;

/// <summary>
/// Host-confirmed settlement payload (docs/09 §2, docs/16 §9).
/// Signed envelopes are NOT used; the only trust root is host-confirmed input,
/// so every field is re-validated and bounded before touching the profile.
/// </summary>
public sealed record SettlementPayload(
    string Id,
    long CreditsDelta,
    long ResearchDataDelta,
    long ShardsDelta,
    IReadOnlyList<string> UnlockBlueprintIds,
    IReadOnlyList<string> UnlockFrameIds,
    IReadOnlyList<string> DiscoverCodexIds,
    int RunsCompletedDelta,
    int RunsFailedDelta)
{
    public const long MaxDelta = 1_000_000L;

    public static SettlementPayload ForCredits(string id, long creditsDelta) => new(
        id, creditsDelta, 0, 0,
        Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), 0, 0);

    /// <summary>Validate host-confirmed input shape; returns false with reason on rejection.</summary>
    public bool TryValidate(out string reason)
    {
        var id = (Id ?? string.Empty).Trim();
        if (id.Length == 0 || id.Length > ProfileSave.MaxIdLength || !SaveIds.IsValid(id))
        {
            reason = "Settlement id is missing or not a stable id.";
            return false;
        }

        if (Math.Abs(CreditsDelta) > MaxDelta ||
            Math.Abs(ResearchDataDelta) > MaxDelta ||
            Math.Abs(ShardsDelta) > MaxDelta)
        {
            reason = "Settlement delta exceeds safe bounds.";
            return false;
        }

        if (RunsCompletedDelta is < 0 or > 1 || RunsFailedDelta is < 0 or > 1 ||
            (RunsCompletedDelta + RunsFailedDelta) > 1)
        {
            reason = "Settlement run counter delta must be at most one run.";
            return false;
        }

        foreach (var list in new[] { UnlockBlueprintIds, UnlockFrameIds, DiscoverCodexIds })
        {
            if (list is null)
            {
                reason = "Settlement id list is null.";
                return false;
            }

            if (list.Count > ProfileSave.MaxIdsPerList)
            {
                reason = "Settlement id list exceeds safe bounds.";
                return false;
            }

            foreach (var raw in list)
            {
                var item = (raw ?? string.Empty).Trim();
                if (item.Length == 0 || item.Length > ProfileSave.MaxIdLength || !SaveIds.IsValid(item))
                {
                    reason = "Settlement contains an invalid stable id.";
                    return false;
                }
            }
        }

        reason = string.Empty;
        return true;
    }
}

public enum SettlementApplyOutcome
{
    Applied,
    AlreadyApplied,
}

public sealed record SettlementApplyResult(SettlementApplyOutcome Outcome, ProfileSave Save);

/// <summary>
/// Upfront insurance-premium charge (docs/05 §4): a negative-credits ledger
/// entry keyed by a unique charge id. Applied at launch before the run starts;
/// the run is refused when the charge cannot land (insufficient funds or a
/// save fault), so paid coverage is never granted free.
/// </summary>
public sealed record InsuranceChargePayload(string Id, long PremiumCredits)
{
    public const long MaxPremium = 1_000_000L;

    /// <summary>Validate host-confirmed input shape; returns false with reason on rejection.</summary>
    public bool TryValidate(out string reason)
    {
        var id = (Id ?? string.Empty).Trim();
        if (id.Length == 0 || id.Length > ProfileSave.MaxIdLength || !SaveIds.IsValid(id))
        {
            reason = "Insurance charge id is missing or not a stable id.";
            return false;
        }

        if (PremiumCredits <= 0 || PremiumCredits > MaxPremium)
        {
            reason = "Insurance premium must be positive and within safe bounds.";
            return false;
        }

        reason = string.Empty;
        return true;
    }
}

public enum InsuranceChargeOutcome
{
    Applied,
    AlreadyApplied,
    InsufficientFunds,
}

public sealed record InsuranceChargeResult(InsuranceChargeOutcome Outcome, ProfileSave Save);
