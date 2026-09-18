using System.IO.Compression;
using System.Text;

namespace AbyssScav.Foundation;

/// <summary>
/// Builds the J-08 support bundle: manifest plus logs and settings, no saves.
/// Pure .NET (no Godot types) so the console test project can exercise it.
/// </summary>
public static class SupportBundleWriter
{
    /// <summary>Bundle file name inside <see cref="AppPaths.DiagnosticsDir"/>.</summary>
    public const string BundleFileName = "support-bundle.zip";

    /// <summary>
    /// Creates support-bundle.zip in DiagnosticsDir (overwrites any existing bundle).
    /// Contents: manifest.txt at root, logs/*.log, settings.cfg at root when present.
    /// Excludes: saves/, session.lock, session.lock.owner, user_data/.
    /// Returns the bundle path.
    /// </summary>
    public static string Create(AppPaths paths, string gameVersion, string sessionId, string osInfo)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(osInfo);

        Directory.CreateDirectory(paths.DiagnosticsDir);
        var bundlePath = Path.Combine(paths.DiagnosticsDir, BundleFileName);
        if (File.Exists(bundlePath))
        {
            File.Delete(bundlePath);
        }

        var manifest = BuildManifest(gameVersion, sessionId, osInfo);

        using var stream = new FileStream(bundlePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        var manifestEntry = archive.CreateEntry("manifest.txt");
        using (var writer = new StreamWriter(manifestEntry.Open(), new UTF8Encoding(false)))
        {
            writer.Write(manifest);
        }

        if (Directory.Exists(paths.LogsDir))
        {
            foreach (var logPath in Directory.EnumerateFiles(paths.LogsDir, "*.log"))
            {
                var entryName = "logs/" + Path.GetFileName(logPath);
                var entry = archive.CreateEntry(entryName);
                using var source = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var destination = entry.Open();
                source.CopyTo(destination);
            }
        }

        if (File.Exists(paths.ConfigPath))
        {
            var entry = archive.CreateEntry("settings.cfg");
            using var source = new FileStream(paths.ConfigPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var destination = entry.Open();
            source.CopyTo(destination);
        }

        return bundlePath;
    }

    private static string BuildManifest(string gameVersion, string sessionId, string osInfo)
    {
        var lines = new[]
        {
            "game version: " + gameVersion,
            "protocol version: " + GameVersion.ProtocolVersion,
            "save schema version: " + GameVersion.SaveSchemaVersion,
            "os: " + osInfo,
            "session id: " + sessionId,
            "created utc: " + DateTime.UtcNow.ToString("O"),
        };
        return string.Join("\n", lines) + "\n";
    }
}
