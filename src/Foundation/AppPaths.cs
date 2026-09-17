namespace AbyssScav.Foundation;

/// <summary>Local-first directory layout under the platform user-data dir (Godot user://).</summary>
public sealed record AppPaths(
    string RootDir,
    string ConfigDir,
    string ConfigPath,
    string SavesDir,
    string LogsDir,
    string DiagnosticsDir,
    string SessionLockPath)
{
    public static AppPaths FromRoot(string rootDir)
    {
        var configDir = Path.Combine(rootDir, "config");
        return new AppPaths(
            RootDir: rootDir,
            ConfigDir: configDir,
            ConfigPath: Path.Combine(configDir, "settings.cfg"),
            SavesDir: Path.Combine(rootDir, "saves"),
            LogsDir: Path.Combine(rootDir, "logs"),
            DiagnosticsDir: Path.Combine(rootDir, "diagnostics"),
            SessionLockPath: Path.Combine(rootDir, "session.lock"));
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(RootDir);
        Directory.CreateDirectory(ConfigDir);
        Directory.CreateDirectory(SavesDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(DiagnosticsDir);
    }
}
