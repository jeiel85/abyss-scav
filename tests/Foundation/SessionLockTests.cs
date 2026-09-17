using AbyssScav.Foundation;

namespace AbyssScav.Foundation.Tests;

internal static class SessionLockTests
{
    public static int Run()
    {
        var cases = new (string Name, Action Test)[]
        {
            ("normal_exit_removes_lock", NormalExitRemovesLock),
            ("stale_marker_detects_previous_crash", StaleCrashDetected),
            ("corrupt_marker_detects_crash", CorruptMarkerDetected),
            ("active_peer_rejected_marker_preserved", ActivePeerRejected),
            ("acquire_is_idempotent", AcquireIdempotent),
            ("release_safe_without_acquire", ReleaseWithoutAcquire),
            ("release_is_idempotent", ReleaseIdempotent),
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

    private static void NormalExitRemovesLock()
    {
        var paths = AppPaths.FromRoot(TestTemp.NewRoot());
        paths.EnsureDirectories();
        var session = new SessionLockService(paths);
        session.Acquire();
        TestAssert.True(File.Exists(paths.SessionLockPath), "lock exists after acquire");
        TestAssert.True(!session.PreviousCrashDetected, "first boot is not a crash");
        TestAssert.True(session.OwnsLock, "owns lock");
        TestAssert.True(session.Release(), "release ok");
        TestAssert.True(!File.Exists(paths.SessionLockPath), "lock removed on normal exit");
    }

    private static void StaleCrashDetected()
    {
        var paths = AppPaths.FromRoot(TestTemp.NewRoot());
        paths.EnsureDirectories();
        // Leftover marker with no live owner (previous process was force-closed).
        File.WriteAllText(paths.SessionLockPath, """{"SessionId":"deadbeef","StartedUtc":"2026-01-01T00:00:00Z","ProcessId":1234}""");
        var session = new SessionLockService(paths);
        session.Acquire();
        TestAssert.True(session.PreviousCrashDetected, "stale marker means previous crash");
        TestAssert.Equal("deadbeef", session.PreviousSessionId, "previous session id recorded");
        TestAssert.True(!session.ActivePeerDetected, "no active peer");
        TestAssert.True(session.Release(), "release ok");
        TestAssert.True(!File.Exists(paths.SessionLockPath), "stale marker replaced then removed on clean exit");
    }

    private static void CorruptMarkerDetected()
    {
        var paths = AppPaths.FromRoot(TestTemp.NewRoot());
        paths.EnsureDirectories();
        File.WriteAllText(paths.SessionLockPath, "{not a json marker{{{");
        var session = new SessionLockService(paths);
        session.Acquire();
        TestAssert.True(session.PreviousCrashDetected, "corrupt marker treated as crash");
        TestAssert.Equal("unparseable", session.PreviousSessionId, "corrupt marker id");
        TestAssert.True(session.Release(), "release ok");
    }

    private static void ActivePeerRejected()
    {
        var paths = AppPaths.FromRoot(TestTemp.NewRoot());
        paths.EnsureDirectories();
        var first = new SessionLockService(paths);
        first.Acquire();
        var firstMarker = File.ReadAllText(paths.SessionLockPath);

        // Second live instance on the same profile: rejected, never overwrites.
        var second = new SessionLockService(paths);
        try
        {
            second.Acquire();
            throw new Exception("Second acquire should have thrown ConcurrentSessionException.");
        }
        catch (ConcurrentSessionException)
        {
            // Expected.
        }

        TestAssert.True(second.ActivePeerDetected, "active peer flagged");
        TestAssert.True(!second.PreviousCrashDetected, "concurrent session is not a crash");
        TestAssert.True(!second.OwnsLock, "second owns nothing");
        TestAssert.Equal(firstMarker, File.ReadAllText(paths.SessionLockPath), "owner marker not overwritten");

        // A rejected instance must never delete the owner's marker.
        TestAssert.True(second.Release(), "rejected release is a safe no-op");
        TestAssert.Equal(firstMarker, File.ReadAllText(paths.SessionLockPath), "owner marker not deleted by peer");

        TestAssert.True(first.Release(), "owner release ok");
        TestAssert.True(!File.Exists(paths.SessionLockPath), "owner marker removed on clean exit");
    }

    private static void AcquireIdempotent()
    {
        var paths = AppPaths.FromRoot(TestTemp.NewRoot());
        paths.EnsureDirectories();
        var session = new SessionLockService(paths);
        session.Acquire();
        var marker = File.ReadAllText(paths.SessionLockPath);
        session.Acquire();
        TestAssert.Equal(marker, File.ReadAllText(paths.SessionLockPath), "second acquire changes nothing");
        TestAssert.True(session.Release(), "release ok");
    }

    private static void ReleaseWithoutAcquire()
    {
        var paths = AppPaths.FromRoot(TestTemp.NewRoot());
        paths.EnsureDirectories();
        var session = new SessionLockService(paths);
        TestAssert.True(session.Release(), "release without acquire is safe");
        TestAssert.True(!File.Exists(paths.SessionLockPath), "nothing created");
    }

    private static void ReleaseIdempotent()
    {
        var paths = AppPaths.FromRoot(TestTemp.NewRoot());
        paths.EnsureDirectories();
        var session = new SessionLockService(paths);
        session.Acquire();
        TestAssert.True(session.Release(), "first release");
        TestAssert.True(session.Release(), "second release still true");
    }
}
