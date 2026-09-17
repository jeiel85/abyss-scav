namespace AbyssScav.App;

/// <summary>
/// Session-only profile write opt-out ("Continue unsaved" / "not saved by
/// choice"). Lives in the per-boot registry so it resets on every bootstrap
/// and clears with <c>GameServices.ResetForTests</c>; never persisted to disk.
/// Reads stay allowed while set; every production profile WRITE path must
/// respect it and report "not saved by choice" instead of an error/retry.
/// Only an explicit successful Retry/Restore clears it — never an implicit
/// menu reload. Safe-mode is unrelated (config overrides only).
/// </summary>
public sealed class ProfileSessionState
{
    /// <summary>True after the player explicitly chose to continue unsaved.</summary>
    public bool ContinueWithoutSaving { get; set; }
}
