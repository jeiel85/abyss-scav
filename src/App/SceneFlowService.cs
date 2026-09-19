using AbyssScav.Foundation;
using Godot;

namespace AbyssScav.App;

/// <summary>
/// Bridges the engine-independent <see cref="SceneFlow"/> to actual Godot scene changes.
/// Ships Boot, MainMenu, host/join + lobby, contract select (Loadout), and the run scene.
/// Plain class (not a Node): the registry owns the single instance, so there is
/// no orphan node to leak at menu exit. Scene access goes via the main loop.
/// </summary>
public sealed class SceneFlowService
{
    private static readonly Dictionary<AppScene, string> ScenePaths = new()
    {
        [AppScene.Boot] = "res://scenes/boot.tscn",
        [AppScene.MainMenu] = "res://scenes/main_menu.tscn",
        [AppScene.HostOrJoin] = "res://scenes/host_or_join.tscn",
        [AppScene.Lobby] = "res://scenes/lobby.tscn",
        [AppScene.Loadout] = "res://scenes/contract_select.tscn",
        [AppScene.Research] = "res://scenes/research.tscn",
        [AppScene.Codex] = "res://scenes/codex.tscn",
        [AppScene.RunLoading] = "res://scenes/run.tscn",
        [AppScene.InRun] = "res://scenes/run.tscn",
        // Settlement resolves inside the run scene overlay; the flow target
        // falls back to the menu so late transitions never strand the player.
        [AppScene.Settlement] = "res://scenes/main_menu.tscn",
    };

    public bool Navigate(AppScene target)
    {
        var flow = GameServices.Flow;
        var logger = GameServices.Logger;

        if (!flow.TryTransition(target, out var error))
        {
            logger.Error(error, ErrorCodes.ContentSceneMissing);
            GD.PushError(error);
            return false;
        }

        if (!ScenePaths.TryGetValue(target, out var path))
        {
            logger.Error($"Scene for {target} is not implemented. Staying on current scene.", ErrorCodes.ContentSceneMissing);
            GD.PushError($"[CONTENT-001] Scene for {target} is not implemented.");
            return false;
        }

        // InRun after RunLoading is the same scene: bookkeeping only, no reload.
        if (target == AppScene.InRun && IsCurrentScene(path))
        {
            return true;
        }

        var tree = Godot.Engine.GetMainLoop() as SceneTree;
        if (tree is null)
        {
            logger.Error("SceneTree unavailable during Navigate.", ErrorCodes.ContentSceneMissing);
            return false;
        }

        tree.CallDeferred(SceneTree.MethodName.ChangeSceneToFile, path);
        return true;
    }

    private static bool IsCurrentScene(string path)
    {
        // Bookkeeping-only hop (RunLoading -> InRun share run.tscn): compare the
        // live scene path when available; unknown means "change scene".
        var tree = Godot.Engine.GetMainLoop() as SceneTree;
        var name = tree?.CurrentScene?.SceneFilePath ?? string.Empty;
        if (string.IsNullOrEmpty(name)) return false;
        return string.Equals(name, path, StringComparison.OrdinalIgnoreCase);
    }
}
