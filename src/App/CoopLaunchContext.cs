using AbyssScav.Net;

namespace AbyssScav.App;

/// <summary>
/// Cross-scene staging for a co-op session (docs/02 §7-§8). The live
/// <see cref="GodotNetworkSession"/> node is parented to the viewport root so it
/// survives scene changes; the host's staged launch options travel with it.
/// Cleared when the session ends or the player returns to the main menu.
/// </summary>
public static class CoopLaunchContext
{
    /// <summary>Live session node (root-parented; null when no co-op session is active).</summary>
    public static GodotNetworkSession? Session { get; set; }

    /// <summary>Host's staged launch options, set by ContractSelect in co-op mode.</summary>
    public static RunLaunchOptions? StagedOptions { get; set; }

    public static void Clear()
    {
        Session = null;
        StagedOptions = null;
    }
}