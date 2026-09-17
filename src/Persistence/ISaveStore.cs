namespace AbyssScav.Persistence;

/// <summary>Save contract (docs/16 §8).</summary>
public interface ISaveStore
{
    Task<ProfileSave> LoadAsync(CancellationToken ct);
    Task SaveAsync(ProfileSave save, CancellationToken ct);
    Task<RestoreResult> RestoreBackupAsync(int generation, CancellationToken ct);
}

/// <summary>Explicit backup-restore outcome. Never triggered implicitly.</summary>
public sealed record RestoreResult(
    ProfileSave Save,
    int Generation,
    string PrimaryPath,
    string? QuarantinedPath);
