using AbyssScav.App;
using AbyssScav.Domain;
using AbyssScav.Foundation;
using AbyssScav.Infra.Logging;
using AbyssScav.Persistence;
using Godot;

namespace AbyssScav.Presentation;

/// <summary>
/// Solo codex (docs/06 roster + docs/09 §2 discoveries): catalog creatures
/// (11) + biomes (3) + relic traits (8) with discovered state from
/// profile.Codex.DiscoveredIds. Undiscovered entries show as "???" with a
/// silhouette placeholder line — never invented data. Discovery itself is fed
/// by real play via the run settlement payload (RunController).
/// </summary>
public partial class CodexScreen : Control
{
    private ContentCatalog? _catalog;
    private FileSaveStore? _store;
    private ProfileSave _profile = ProfileSave.Default();
    private Label? _countLabel;
    private Label? _statusLabel;
    private VBoxContainer? _listRoot;
    private readonly List<Control> _focusOrder = new();

    /// <summary>Debug/smoke readout: total codex entries built from the real catalog.</summary>
    public int EntryCount { get; private set; }

    /// <summary>Debug/smoke readout: entries currently shown as discovered.</summary>
    public int DiscoveredShown { get; private set; }

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        if (!ContentCatalog.TryBuild(out var catalog, out var errors) || catalog is null)
        {
            BuildError("Content catalog failed: " + string.Join("; ", errors));
            return;
        }
        _catalog = catalog;
        if (GameServices.IsInitialized)
        {
            try
            {
                _store = new FileSaveStore(GameServices.Paths.SavesDir);
            }
            catch (Exception ex)
            {
                BuildUi("Save folder is unavailable: " + ex.Message);
                return;
            }
        }
        BuildUi(GameServices.IsInitialized ? "" : "Profile storage unavailable: showing catalog with nothing discovered.");
        LoadProfileAsync();
    }

    private static Color Ink() => new("#071622");
    private static Color Parchment() => new("#dae4df");
    private static Color Cyan() => new("#71d9d1");
    private static Color Amber() => new("#dfa44d");
    private static Color Dim() => new(0.62f, 0.72f, 0.70f);

    private void BuildError(string text)
    {
        var bg = new ColorRect { Color = Ink() };
        bg.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(bg);
        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(center);
        var label = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(480, 0) };
        label.AddThemeColorOverride("font_color", Parchment());
        center.AddChild(label);
    }

    private void BuildUi(string notice)
    {
        var catalog = _catalog!;
        var bg = new ColorRect { Color = Ink() };
        bg.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(bg);

        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(center);

        var panel = new PanelContainer();
        panel.CustomMinimumSize = new Vector2(720, 620);
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.03f, 0.09f, 0.12f, 1f),
            BorderColor = new Color("#29414b"),
            BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1,
            ContentMarginLeft = 20, ContentMarginRight = 20, ContentMarginTop = 14, ContentMarginBottom = 14,
        };
        panel.AddThemeStyleboxOverride("panel", style);
        center.AddChild(panel);

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);
        panel.AddChild(box);

        var title = new Label { Text = "CODEX — SURVEY RECORD", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 24);
        title.AddThemeColorOverride("font_color", Parchment());
        box.AddChild(title);

        _countLabel = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _countLabel.AddThemeFontSizeOverride("font_size", 14);
        _countLabel.AddThemeColorOverride("font_color", Cyan());
        box.AddChild(_countLabel);

        _statusLabel = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, HorizontalAlignment = HorizontalAlignment.Center };
        _statusLabel.AddThemeFontSizeOverride("font_size", 13);
        _statusLabel.AddThemeColorOverride("font_color", Amber());
        _statusLabel.Text = notice;
        box.AddChild(_statusLabel);

        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, FollowFocus = true };
        scroll.CustomMinimumSize = new Vector2(680, 420);
        box.AddChild(scroll);

        _listRoot = new VBoxContainer();
        _listRoot.AddThemeConstantOverride("separation", 4);
        _listRoot.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(_listRoot);

        _focusOrder.Clear();
        EntryCount = 0;
        var known = new HashSet<string>(_profile.Codex.DiscoveredIds, StringComparer.Ordinal);

        AddSection("CREATURES — survey biological contacts at sea");
        foreach (var c in catalog.Creatures.Values.OrderBy(c => c.Id))
        {
            var found = known.Contains(c.Id);
            AddEntry(c.Id, found ? c.DisplayName : "???", found ? c.Description : "[ silhouette — undiscovered: survey it on a dive ]", found);
        }
        AddSection("WATERS — complete a dive in each biome");
        foreach (var b in catalog.Biomes.Values.OrderBy(b => b.Id))
        {
            var found = known.Contains(b.Id);
            AddEntry(b.Id, found ? b.DisplayName : "???", found ? b.Description : "[ silhouette — undiscovered: dive these waters ]", found);
        }
        AddSection("RELIC TRAITS — secure salvage carrying the trait");
        foreach (var t in catalog.RelicTraits.Values.OrderBy(t => t.Id))
        {
            var found = known.Contains(t.Id);
            var detail = found ? $"Threat on recovery: +{t.ThreatOnRecover:F0}" : "[ silhouette — undiscovered: secure marked salvage ]";
            AddEntry(t.Id, found ? t.DisplayName : "???", detail, found);
        }

        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        row.AddThemeConstantOverride("separation", 10);
        box.AddChild(row);
        var back = new Button { Text = "Back", CustomMinimumSize = new Vector2(140, 38) };
        back.Pressed += () => Navigate(AppScene.MainMenu);
        row.AddChild(back);
        _focusOrder.Add(back);

        ChainFocus();
        RefreshTexts();
        _focusOrder.FirstOrDefault(c => c.FocusMode != FocusModeEnum.None)?.GrabFocus();
    }

    private void AddSection(string text)
    {
        var header = new Label { Text = text, HorizontalAlignment = HorizontalAlignment.Center };
        header.AddThemeFontSizeOverride("font_size", 15);
        header.AddThemeColorOverride("font_color", Cyan());
        _listRoot!.AddChild(header);
    }

    private void AddEntry(string id, string name, string detail, bool found)
    {
        var line = new HBoxContainer();
        line.AddThemeConstantOverride("separation", 8);
        _listRoot!.AddChild(line);
        var nameLabel = new Label { Text = name, CustomMinimumSize = new Vector2(220, 0) };
        nameLabel.AddThemeFontSizeOverride("font_size", 14);
        nameLabel.AddThemeColorOverride("font_color", found ? Parchment() : Dim());
        nameLabel.TooltipText = id;
        line.AddChild(nameLabel);
        var detailLabel = new Label { Text = detail, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        detailLabel.AddThemeFontSizeOverride("font_size", 13);
        detailLabel.AddThemeColorOverride("font_color", found ? Parchment() : Dim());
        line.AddChild(detailLabel);
        EntryCount++;
        if (found) DiscoveredShown++;
    }

    private void ChainFocus()
    {
        for (var i = 0; i < _focusOrder.Count; i++)
        {
            var prev = _focusOrder[(i - 1 + _focusOrder.Count) % _focusOrder.Count];
            var next = _focusOrder[(i + 1) % _focusOrder.Count];
            _focusOrder[i].FocusNeighborTop = _focusOrder[i].GetPathTo(prev);
            _focusOrder[i].FocusNeighborBottom = _focusOrder[i].GetPathTo(next);
        }
    }

    private void RefreshTexts()
    {
        if (_countLabel is not null && _catalog is not null)
        {
            var known = new HashSet<string>(_profile.Codex.DiscoveredIds, StringComparer.Ordinal);
            var total = _catalog.Creatures.Count + _catalog.Biomes.Count + _catalog.RelicTraits.Count;
            var found = _catalog.Creatures.Keys.Count(known.Contains)
                + _catalog.Biomes.Keys.Count(known.Contains)
                + _catalog.RelicTraits.Keys.Count(known.Contains);
            _countLabel.Text = $"Discovered {found} / {total} entries";
        }
    }

    private async void LoadProfileAsync()
    {
        if (_store is null) return;
        try
        {
            _profile = await _store.LoadAsync(CancellationToken.None);
            if (!IsInstanceValid(this) || IsQueuedForDeletion()) return;
            RefreshTexts();
            RebuildEntries();
        }
        catch (SaveFutureVersionException ex)
        {
            if (IsInstanceValid(this) && !IsQueuedForDeletion() && _statusLabel is not null && IsInstanceValid(_statusLabel))
                _statusLabel.Text = $"Profile is from a newer version (schema v{ex.FoundVersion}): showing catalog with nothing discovered.";
        }
        catch (SaveException ex)
        {
            if (IsInstanceValid(this) && !IsQueuedForDeletion() && _statusLabel is not null && IsInstanceValid(_statusLabel))
                _statusLabel.Text = $"Profile load failed [{ex.Code}]: showing catalog with nothing discovered. {ex.Message}";
        }
        catch (Exception ex)
        {
            if (IsInstanceValid(this) && !IsQueuedForDeletion() && _statusLabel is not null && IsInstanceValid(_statusLabel))
                _statusLabel.Text = $"Profile load failed ({ex.GetType().Name}): showing catalog with nothing discovered.";
        }
    }

    private void RebuildEntries()
    {
        if (_listRoot is null || _catalog is null) return;
        foreach (var child in _listRoot.GetChildren()) child.QueueFree();
        EntryCount = 0;
        DiscoveredShown = 0;
        var catalog = _catalog;
        var known = new HashSet<string>(_profile.Codex.DiscoveredIds, StringComparer.Ordinal);
        AddSection("CREATURES — survey biological contacts at sea");
        foreach (var c in catalog.Creatures.Values.OrderBy(c => c.Id))
        {
            var found = known.Contains(c.Id);
            AddEntry(c.Id, found ? c.DisplayName : "???", found ? c.Description : "[ silhouette — undiscovered: survey it on a dive ]", found);
        }
        AddSection("WATERS — complete a dive in each biome");
        foreach (var b in catalog.Biomes.Values.OrderBy(b => b.Id))
        {
            var found = known.Contains(b.Id);
            AddEntry(b.Id, found ? b.DisplayName : "???", found ? b.Description : "[ silhouette — undiscovered: dive these waters ]", found);
        }
        AddSection("RELIC TRAITS — secure salvage carrying the trait");
        foreach (var t in catalog.RelicTraits.Values.OrderBy(t => t.Id))
        {
            var found = known.Contains(t.Id);
            var detail = found ? $"Threat on recovery: +{t.ThreatOnRecover:F0}" : "[ silhouette — undiscovered: secure marked salvage ]";
            AddEntry(t.Id, found ? t.DisplayName : "???", detail, found);
        }
        RefreshTexts();
    }

    private void Navigate(AppScene target)
    {
        if (!GameServices.IsInitialized) return;
        var svc = GameServices.Registry.TryResolve<SceneFlowService>(out var s) ? s : null;
        if (svc is null)
        {
            GetTree()?.CallDeferred(SceneTree.MethodName.ChangeSceneToFile, "res://scenes/main_menu.tscn");
            return;
        }
        if (!svc.Navigate(target))
        {
            if (_statusLabel is not null) _statusLabel.Text = $"Cannot navigate to {target} from here.";
            GodotLogBridge.Warn(GameServices.Logger, $"CodexScreen navigate to {target} refused.");
        }
    }
}
