using System.IO.Compression;
using AbyssScav.Foundation;

namespace AbyssScav.Foundation.Tests;

internal static class SupportBundleTests
{
    public static int Run()
    {
        var cases = new (string Name, Action Test)[]
        {
            ("bundle_contains_manifest_logs_settings_excludes_saves_lock", BundleContents),
            ("bundle_overwrite_produces_valid_zip", BundleOverwrite),
            ("bundle_empty_logs_manifest_only", EmptyLogsManifestOnly),
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

    private static void BundleContents()
    {
        var root = TestTemp.NewRoot();
        try
        {
            var paths = AppPaths.FromRoot(root);
            paths.EnsureDirectories();
            File.WriteAllText(Path.Combine(paths.LogsDir, "game_test.log"), "log line\n");
            File.WriteAllText(paths.ConfigPath, "[settings]\n");
            File.WriteAllText(Path.Combine(paths.SavesDir, "profile.save"), "save data");
            File.WriteAllText(paths.SessionLockPath, "lock marker");

            var sessionId = Guid.NewGuid().ToString("N");
            var bundlePath = SupportBundleWriter.Create(paths, GameVersion.Current, sessionId, "TestOS 1.0");

            TestAssert.True(File.Exists(bundlePath), "bundle exists");
            using var stream = new FileStream(bundlePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var names = archive.Entries.Select(e => e.FullName).ToArray();
            TestAssert.True(names.Contains("manifest.txt"), "manifest present");
            TestAssert.True(names.Contains("logs/game_test.log"), "log present");
            TestAssert.True(names.Contains("settings.cfg"), "settings present");
            TestAssert.True(!names.Any(n => n.StartsWith("saves/", StringComparison.Ordinal)), "no saves entries");
            TestAssert.True(!names.Any(n => n.Contains("session.lock", StringComparison.Ordinal)), "no lock entries");

            var manifestEntry = archive.GetEntry("manifest.txt");
            TestAssert.True(manifestEntry is not null, "manifest entry readable");
            using var reader = new StreamReader(manifestEntry!.Open());
            var manifest = reader.ReadToEnd();
            TestAssert.True(manifest.Contains(GameVersion.Current), "manifest has game version");
            TestAssert.True(manifest.Contains(sessionId), "manifest has session id");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void BundleOverwrite()
    {
        var root = TestTemp.NewRoot();
        try
        {
            var paths = AppPaths.FromRoot(root);
            paths.EnsureDirectories();
            File.WriteAllText(Path.Combine(paths.LogsDir, "game_test.log"), "first\n");
            var sessionId = Guid.NewGuid().ToString("N");
            var first = SupportBundleWriter.Create(paths, GameVersion.Current, sessionId, "TestOS 1.0");
            TestAssert.True(File.Exists(first), "first bundle exists");

            File.WriteAllText(Path.Combine(paths.LogsDir, "game_test.log"), "second\n");
            var second = SupportBundleWriter.Create(paths, GameVersion.Current, sessionId, "TestOS 1.0");
            TestAssert.Equal(first, second, "same bundle path on overwrite");
            using var stream = new FileStream(second, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            TestAssert.True(archive.GetEntry("manifest.txt") is not null, "overwritten zip valid");
            var logEntry = archive.GetEntry("logs/game_test.log");
            TestAssert.True(logEntry is not null, "log still present after overwrite");
            using var reader = new StreamReader(logEntry!.Open());
            TestAssert.True(reader.ReadToEnd().Contains("second"), "overwrite picks up new content");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void EmptyLogsManifestOnly()
    {
        var root = TestTemp.NewRoot();
        try
        {
            var paths = AppPaths.FromRoot(root);
            paths.EnsureDirectories();
            var sessionId = Guid.NewGuid().ToString("N");
            var bundlePath = SupportBundleWriter.Create(paths, GameVersion.Current, sessionId, "TestOS 1.0");
            TestAssert.True(File.Exists(bundlePath), "bundle exists with empty logs");
            using var stream = new FileStream(bundlePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            TestAssert.True(archive.GetEntry("manifest.txt") is not null, "manifest present");
            TestAssert.True(!archive.Entries.Any(e => e.FullName.StartsWith("logs/", StringComparison.Ordinal)), "no log entries");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
