using System.Text.Json;

namespace AbyssScav.Foundation;

/// <summary>Thrown when another live instance already owns this profile's session lock.</summary>
public sealed class ConcurrentSessionException : InvalidOperationException
{
    public ConcurrentSessionException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Crash marker via session.lock (docs/09 §6): written at boot, deleted on clean exit.
/// A leftover lock at next boot means the previous session may have crashed.
///
/// Ownership is exclusive via a held OS file lock on a sidecar guard file:
/// a second live instance on the same profile is rejected as a concurrent
/// session (not a crash) and must never overwrite or delete the owner's marker.
/// A leftover marker with a free guard is a stale crash marker and is preserved
/// until the next successful acquire overwrites it.
/// Acquire is idempotent; Release is safe to call without ownership.
/// </summary>
public sealed class SessionLockService : IDisposable
{
    private const int SharingViolationHResult = unchecked((int)0x80070020);

    private readonly string _lockPath;
    private readonly string _guardPath;
    private FileStream? _guard;
    private bool _acquired;
    private bool _ownsLock;
    private bool _disposed;

    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    public DateTime StartedUtc { get; } = DateTime.UtcNow;
    public bool PreviousCrashDetected { get; private set; }
    public string? PreviousSessionId { get; private set; }
    public bool ActivePeerDetected { get; private set; }
    public bool OwnsLock => _ownsLock;

    public SessionLockService(AppPaths paths)
    {
        _lockPath = paths.SessionLockPath;
        _guardPath = paths.SessionLockPath + ".owner";
    }

    public void Acquire()
    {
        if (_acquired)
        {
            return;
        }

        FileStream guard;
        try
        {
            var dir = Path.GetDirectoryName(_lockPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            guard = new FileStream(_guardPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex) when (ex.HResult == SharingViolationHResult)
        {
            // Another live instance holds the guard: concurrent session, not a crash.
            ActivePeerDetected = true;
            PreviousCrashDetected = false;
            PreviousSessionId = TryReadSessionId(_lockPath);
            throw new ConcurrentSessionException($"[{ErrorCodes.BootSessionLock}] Another instance is already running with this profile.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"[{ErrorCodes.BootSessionLock}] Cannot claim the session lock.", ex);
        }

        _guard = guard;
        try
        {
            if (File.Exists(_lockPath))
            {
                PreviousCrashDetected = true;
                PreviousSessionId = TryReadSessionId(_lockPath);
            }

            var payload = JsonSerializer.Serialize(new SessionLockPayload(SessionId, StartedUtc, Environment.ProcessId));
            AtomicFileStore.WriteAllTextAtomic(_lockPath, payload);
            _ownsLock = true;
            _acquired = true;
        }
        catch
        {
            _guard.Dispose();
            _guard = null;
            throw;
        }
    }

    /// <summary>
    /// Releases ownership without ever deleting another instance's marker.
    /// Safe to call without Acquire and idempotent. Returns false only if our own
    /// marker could not be deleted.
    /// </summary>
    public bool Release()
    {
        if (!_acquired)
        {
            return true;
        }

        var ok = true;
        if (_ownsLock)
        {
            var current = File.Exists(_lockPath) ? TryReadSessionId(_lockPath) : null;
            if (current is null || string.Equals(current, SessionId, StringComparison.Ordinal))
            {
                try
                {
                    if (File.Exists(_lockPath))
                    {
                        File.Delete(_lockPath);
                    }
                }
                catch
                {
                    ok = false;
                }
            }

            _ownsLock = false;
        }

        _guard?.Dispose();
        _guard = null;
        _acquired = false;
        return ok;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Release();
    }

    private static string? TryReadSessionId(string path)
    {
        try
        {
            var raw = File.ReadAllText(path);
            var payload = JsonSerializer.Deserialize<SessionLockPayload>(raw);
            return string.IsNullOrWhiteSpace(payload?.SessionId) ? "unparseable" : payload!.SessionId;
        }
        catch
        {
            return "unparseable";
        }
    }

    private sealed record SessionLockPayload(string SessionId, DateTime StartedUtc, int ProcessId);
}
