using AbyssScav.App;

namespace AbyssScav.Gameplay;

/// <summary>
/// Handoff between the contract-select scene and the run scene.
/// Scene changes cannot carry arguments, so the pending options live here.
/// Cleared once the run scene consumes them.
/// </summary>
public static class RunLaunchContext
{
    public static RunLaunchOptions? Pending { get; set; }
}
