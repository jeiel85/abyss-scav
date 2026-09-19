namespace AbyssScav.Foundation;

/// <summary>Scene flow from docs/01 §4 (A-01 boots to MainMenu; the rest is defined for future epics).</summary>
public enum AppScene
{
    Boot,
    MainMenu,
    HostOrJoin,
    Lobby,
    Loadout,
    RunLoading,
    InRun,
    Settlement,
    Research,
    Codex,
}

public sealed class SceneFlow
{
    private static readonly IReadOnlyDictionary<AppScene, IReadOnlyList<AppScene>> Allowed = new Dictionary<AppScene, IReadOnlyList<AppScene>>
    {
        [AppScene.Boot] = [AppScene.MainMenu],
        [AppScene.MainMenu] = [AppScene.HostOrJoin, AppScene.Loadout, AppScene.Research, AppScene.Codex],
        [AppScene.Research] = [AppScene.MainMenu],
        [AppScene.Codex] = [AppScene.MainMenu],
        [AppScene.HostOrJoin] = [AppScene.Lobby, AppScene.MainMenu],
        // Lobby -> RunLoading: the host broadcasts the manifest and every peer
        // (host included) stages the shared world. Loadout -> Lobby: the host
        // picks a contract in co-op mode and returns to the lobby to start.
        [AppScene.Lobby] = [AppScene.Loadout, AppScene.RunLoading, AppScene.MainMenu],
        [AppScene.Loadout] = [AppScene.RunLoading, AppScene.Lobby, AppScene.MainMenu],
        [AppScene.RunLoading] = [AppScene.InRun, AppScene.MainMenu],
        [AppScene.InRun] = [AppScene.Settlement, AppScene.MainMenu],
        [AppScene.Settlement] = [AppScene.MainMenu],
    };

    public AppScene Current { get; private set; } = AppScene.Boot;

    public event Action<AppScene, AppScene>? Transitioned;

    public bool CanTransition(AppScene target) =>
        Allowed.TryGetValue(Current, out var next) && next.Contains(target);

    public bool TryTransition(AppScene target, out string error)
    {
        if (CanTransition(target))
        {
            var previous = Current;
            Current = target;
            Transitioned?.Invoke(previous, target);
            error = string.Empty;
            return true;
        }

        error = $"Illegal scene transition {Current} -> {target}.";
        return false;
    }
}
