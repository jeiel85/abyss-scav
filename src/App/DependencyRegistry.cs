using AbyssScav.Foundation;

namespace AbyssScav.App;

/// <summary>
/// Global service holder for the running Godot session (EPIC A-01).
/// The engine-independent <see cref="DependencyRegistry"/> lives in Foundation;
/// this static facade exposes the single instance plus typed shortcuts.
/// File name follows docs/01 §3 (DependencyRegistry.cs); the class is named
/// GameServices to avoid ambiguity with the Foundation type.
/// </summary>
public static class GameServices
{
    private static Foundation.DependencyRegistry? _registry;

    public static Foundation.DependencyRegistry Registry =>
        _registry ?? throw new InvalidOperationException("[BOOT-003] Game services are not initialized. GameBootstrap must run first.");

    public static bool IsInitialized => _registry is not null;

    public static AppPaths Paths => Registry.Resolve<AppPaths>();

    public static AppSettings Settings => Registry.Resolve<AppSettingsHolder>().Current;

    public static LocalLogger Logger => Registry.Resolve<LocalLogger>();

    public static SessionLockService SessionLock => Registry.Resolve<SessionLockService>();

    public static SceneFlow Flow => Registry.Resolve<SceneFlow>();

    public static ProfileSessionState ProfileSession => Registry.Resolve<ProfileSessionState>();

    /// <summary>
    /// True when the player chose "continue unsaved" this session. False when
    /// services (or the session state) are unavailable — reads stay allowed,
    /// writes proceed normally. Every production profile write path checks this.
    /// </summary>
    public static bool WritesSuspendedByChoice
    {
        get
        {
            if (!IsInitialized) return false;
            return Registry.TryResolve<ProfileSessionState>(out var s)
                && s is not null && s.ContinueWithoutSaving;
        }
    }

    public static bool IsSafeMode => Registry.Resolve<AppSettingsHolder>().IsSafeMode;

    public static void Initialize(Foundation.DependencyRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public static void ResetForTests() => _registry = null;
}

/// <summary>Mutable holder so Settings UI can replace settings after an explicit user save.</summary>
public sealed class AppSettingsHolder
{
    public AppSettings Current { get; set; }
    public bool IsSafeMode { get; }
    public bool IsFutureVersionReadOnly { get; }

    public AppSettingsHolder(AppSettings current, bool isSafeMode, bool isFutureVersionReadOnly)
    {
        Current = current;
        IsSafeMode = isSafeMode;
        IsFutureVersionReadOnly = isFutureVersionReadOnly;
    }
}
