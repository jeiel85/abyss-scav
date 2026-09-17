using AbyssScav.App;
using AbyssScav.Domain;
using AbyssScav.Foundation;
using AbyssScav.Infra.Logging;
using AbyssScav.Persistence;
using Godot;

namespace AbyssScav.Presentation;

/// <summary>
/// Solo blueprint research (docs/05 §6): the 4 branches (Navigation /
/// Engineering / Salvage / Survival) as columns, every catalog module under
/// its branch with its ResearchCost. Only the 6 modules with implemented
/// in-run effects (<see cref="ModuleLoadout"/>) are purchasable; the other 18
/// show as explicitly unavailable for NEW purchase (owned copies retained, no
/// paid no-effect sale). A purchase is an atomic idempotent
/// FileSaveStore.UpdateAsync with visible refuse reasons. Blueprints are
/// permanent unlocks and are never consumed by equipping.
/// </summary>
public partial class ResearchScreen : Control
{
    private static readonly string[] Branches =
    {
        "Navigation", "Engineering", "Salvage", "Survival",
    };

    private ContentCatalog? _catalog;
    private FileSaveStore? _store;
    private ProfileSave _profile = ProfileSave.Default();
    private bool _saveUsable = true;
    private Label? _balanceLabel;
    private Label? _statusLabel;
    private VBoxContainer? _listRoot;
    private Button? _backButton;
    private int _savingCount;
    private CancellationTokenSource? _cts;
    private readonly Dictionary<string, (Button Buy, Label State)> _rows = new(StringComparer.Ordinal);
    private readonly List<Control> _focusOrder = new();

    /// <summary>Debug/smoke readout: branch columns built from the real catalog.</summary>
    public int BranchColumnCount { get; private set; }

    /// <summary>Debug/smoke readout: module rows built from the real catalog.</summary>
    public int ModuleRowCount { get; private set; }

    /// <summary>Debug/smoke readout: balance line currently shown.</summary>
    public string BalanceText => _balanceLabel?.Text ?? string.Empty;

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
                _saveUsable = false;
                BuildUi("Save folder is unavailable: " + ex.Message);
                return;
            }
        }
        else
        {
            _saveUsable = false;
        }
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        BuildUi(_saveUsable ? "" : "Profile storage unavailable: showing catalog read-only; purchases disabled.");
        LoadProfileAsync();
    }

    public override void _ExitTree()
    {
        try { _cts?.Cancel(); }
        catch { }
        finally { _cts?.Dispose(); _cts = null; }
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
        panel.CustomMinimumSize = new Vector2(1060, 620);
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

        var title = new Label { Text = "RESEARCH — BLUEPRINT ARCHIVE", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 24);
        title.AddThemeColorOverride("font_color", Parchment());
        box.AddChild(title);

        _balanceLabel = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _balanceLabel.AddThemeFontSizeOverride("font_size", 15);
        _balanceLabel.AddThemeColorOverride("font_color", Cyan());
        box.AddChild(_balanceLabel);

        _statusLabel = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, HorizontalAlignment = HorizontalAlignment.Center };
        _statusLabel.AddThemeFontSizeOverride("font_size", 13);
        _statusLabel.AddThemeColorOverride("font_color", Amber());
        _statusLabel.Text = notice;
        box.AddChild(_statusLabel);

        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.ShowAlways, FollowFocus = true };
        scroll.CustomMinimumSize = new Vector2(1020, 400);
        box.AddChild(scroll);

        _listRoot = new VBoxContainer();
        _listRoot.AddThemeConstantOverride("separation", 6);
        _listRoot.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(_listRoot);

        var columns = new HBoxContainer();
        columns.AddThemeConstantOverride("separation", 12);
        columns.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _listRoot.AddChild(columns);

        _focusOrder.Clear();
        _rows.Clear();
        BranchColumnCount = 0;
        ModuleRowCount = 0;
        foreach (var branch in Branches)
        {
            var col = new VBoxContainer();
            col.AddThemeConstantOverride("separation", 4);
            col.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            columns.AddChild(col);
            var header = new Label { Text = branch.ToUpperInvariant(), HorizontalAlignment = HorizontalAlignment.Center };
            header.AddThemeFontSizeOverride("font_size", 16);
            header.AddThemeColorOverride("font_color", Cyan());
            col.AddChild(header);
            BranchColumnCount++;
            foreach (var m in catalog.Modules.Values.Where(m => m.ResearchBranch == branch).OrderBy(m => m.Id))
            {
                AddModuleRow(col, m);
                ModuleRowCount++;
            }
        }

        var footer = new Label
        {
            Text = "Blueprints are permanent unlocks, never consumed by equipping. 6 of 24 modules have real in-run effects (equip them at Contract Select); the rest are unavailable for new purchase until their effects exist — owned copies are retained.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        footer.AddThemeFontSizeOverride("font_size", 13);
        footer.AddThemeColorOverride("font_color", Dim());
        box.AddChild(footer);

        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        row.AddThemeConstantOverride("separation", 10);
        box.AddChild(row);
        var back = new Button { Text = "Back", CustomMinimumSize = new Vector2(140, 38) };
        back.Pressed += () => Navigate(AppScene.MainMenu);
        row.AddChild(back);
        _backButton = back;
        _focusOrder.Add(back);

        ChainFocus();
        RefreshTexts();
        _focusOrder.FirstOrDefault(c => c.FocusMode != FocusModeEnum.None)?.GrabFocus();
    }

    private void AddModuleRow(VBoxContainer col, ModuleDef m)
    {
        var frame = new PanelContainer();        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.12f, 0.15f, 1f),
            BorderColor = new Color("#29414b"),
            BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1,
            ContentMarginLeft = 8, ContentMarginRight = 8, ContentMarginTop = 6, ContentMarginBottom = 6,
        };
        frame.AddThemeStyleboxOverride("panel", style);
        col.AddChild(frame);
        var inner = new VBoxContainer();
        inner.AddThemeConstantOverride("separation", 2);
        frame.AddChild(inner);
        var name = new Label { Text = $"{m.Id}\nTier {m.Tier} · {m.Category} · {m.ResearchCost} RD\n{ModuleLoadout.Describe(m.Id)}", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        name.AddThemeFontSizeOverride("font_size", 12);
        name.AddThemeColorOverride("font_color", Parchment());
        inner.AddChild(name);
        var state = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        state.AddThemeFontSizeOverride("font_size", 12);
        state.AddThemeColorOverride("font_color", Dim());
        inner.AddChild(state);
        var buy = new Button { Text = $"Unlock — {m.ResearchCost} RD", CustomMinimumSize = new Vector2(0, 30) };
        var id = m.Id;
        var cost = m.ResearchCost;
        buy.Pressed += () => BuyModule(id, cost);
        inner.AddChild(buy);
        _rows[id] = (buy, state);
        _focusOrder.Add(buy);
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
        var byChoice = GameServices.WritesSuspendedByChoice;
        if (_balanceLabel is not null && IsInstanceValid(_balanceLabel))
        {
            var suffix = byChoice ? " (not saved by choice)" : _saveUsable ? "" : " (unsaved session)";
            _balanceLabel.Text = $"Research data: {_profile.Currencies.ResearchData} RD{suffix}";
        }
        if (_catalog is null) return;
        var owned = new HashSet<string>(_profile.Unlocks.Blueprints, StringComparer.Ordinal);
        foreach (var kv in _catalog.Modules)
        {
            if (!_rows.TryGetValue(kv.Key, out var row)) continue;
            if (!IsInstanceValid(row.Buy) || !IsInstanceValid(row.State)) continue;
            var m = kv.Value;
            var supported = ModuleLoadout.IsSupported(kv.Key);
            if (owned.Contains(kv.Key))
            {
                row.State.Text = supported
                    ? "Unlocked — permanent blueprint; equip one per category at Contract Select."
                    : "Owned — retained on your profile; no in-run effect yet, not equippable.";
                row.Buy.Disabled = true;
                row.Buy.FocusMode = FocusModeEnum.None;
                row.Buy.Text = "Unlocked";
            }
            else if (!supported)
            {
                row.State.Text = "Unavailable — in-run effect not implemented; new purchase disabled (nothing charged).";
                row.Buy.Disabled = true;
                row.Buy.FocusMode = FocusModeEnum.None;
                row.Buy.Text = "Unavailable";
            }
            else if (byChoice)
            {
                row.State.Text = "Not saved by choice: purchases disabled for this unsaved session (nothing charged).";
                row.Buy.Disabled = true;
                row.Buy.FocusMode = FocusModeEnum.None;
            }
            else if (!_saveUsable)
            {
                row.State.Text = "Purchases disabled: profile storage unavailable.";
                row.Buy.Disabled = true;
                row.Buy.FocusMode = FocusModeEnum.None;
            }
            else if (_profile.Currencies.ResearchData < m.ResearchCost)
            {
                row.State.Text = $"Needs {m.ResearchCost - _profile.Currencies.ResearchData} more RD.";
                row.Buy.Disabled = _savingCount > 0;
                row.Buy.FocusMode = _savingCount > 0 ? FocusModeEnum.None : FocusModeEnum.All;
            }
            else
            {
                row.State.Text = "Available — deducts research data once; blueprint is permanent.";
                row.Buy.Disabled = _savingCount > 0;
                row.Buy.FocusMode = _savingCount > 0 ? FocusModeEnum.None : FocusModeEnum.All;
            }
        }
        UpdateBusy();
    }

    /// <summary>While a purchase write is in flight, Back is held so the
    /// committed result is surfaced here instead of abandoned mid-write.</summary>
    private void UpdateBusy()
    {
        if (_backButton is not null && IsInstanceValid(_backButton))
        {
            _backButton.Disabled = _savingCount > 0;
            _backButton.TooltipText = _savingCount > 0
                ? "Purchase write in flight — exit held until it completes."
                : "Return to menu.";
        }
    }

    private void Say(string text)
    {
        if (_statusLabel is not null && IsInstanceValid(_statusLabel)) _statusLabel.Text = text;
    }

    private async void LoadProfileAsync()
    {
        if (_store is null) return;
        var ct = _cts?.Token ?? CancellationToken.None;
        try
        {
            var loaded = await _store.LoadAsync(ct);
            if (ct.IsCancellationRequested || !IsInstanceValid(this)) return;
            _profile = loaded;
            _saveUsable = true;
            if (GameServices.WritesSuspendedByChoice)
                Say("Not saved by choice: catalog is read-only for this unsaved session; purchases disabled.");
            RefreshTexts();
        }
        catch (OperationCanceledException)
        {
            // Scene torn down; leave nodes alone.
        }
        catch (SaveFutureVersionException ex)
        {
            if (ct.IsCancellationRequested || !IsInstanceValid(this)) return;
            _saveUsable = false;
            Say(GameServices.WritesSuspendedByChoice
                ? "Not saved by choice: purchases disabled for this unsaved session (nothing charged)."
                : $"Profile is from a newer version (schema v{ex.FoundVersion}): catalog is read-only, purchases disabled.");
            RefreshTexts();
        }
        catch (SaveException ex)
        {
            if (ct.IsCancellationRequested || !IsInstanceValid(this)) return;
            _saveUsable = false;
            Say(GameServices.WritesSuspendedByChoice
                ? "Not saved by choice: purchases disabled for this unsaved session (nothing charged)."
                : $"Profile load failed [{ex.Code}]: catalog is read-only, purchases disabled. {ex.Message}");
            RefreshTexts();
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested || !IsInstanceValid(this)) return;
            _saveUsable = false;
            Say(GameServices.WritesSuspendedByChoice
                ? "Not saved by choice: purchases disabled for this unsaved session (nothing charged)."
                : $"Profile load failed ({ex.GetType().Name}): catalog is read-only, purchases disabled.");
            RefreshTexts();
        }
    }

    private async void BuyModule(string moduleId, int cost)
    {
        if (!IsInstanceValid(this)) return;
        if (GameServices.WritesSuspendedByChoice)
        {
            Say("Not saved by choice: purchases disabled for this unsaved session (nothing charged).");
            RefreshTexts();
            return;
        }
        if (_store is null || !_saveUsable)
        {
            Say("Purchases disabled: profile storage is unavailable.");
            return;
        }
        if (!ModuleLoadout.IsSupported(moduleId))
        {
            Say($"Unavailable: {moduleId} has no implemented in-run effect; new purchase disabled, nothing charged.");
            RefreshTexts();
            return;
        }
        if (_profile.Unlocks.Blueprints.Contains(moduleId, StringComparer.Ordinal))
        {
            Say("Already unlocked: duplicate purchase refused.");
            RefreshTexts();
            return;
        }
        if (_profile.Currencies.ResearchData < cost)
        {
            Say($"Insufficient research data: need {cost} RD, have {_profile.Currencies.ResearchData} RD.");
            return;
        }
        var ct = _cts?.Token ?? CancellationToken.None;
        Say($"Saving {moduleId}…");
        SetRowBusy(moduleId, true);
        _savingCount++;
        UpdateBusy();
        RefreshTexts();
        try
        {
            var alreadyOwned = false;
            var insufficient = false;
            var updated = await _store.UpdateAsync(p =>
            {
                if (p.Unlocks.Blueprints.Contains(moduleId, StringComparer.Ordinal))
                {
                    alreadyOwned = true;
                    return p;
                }
                if (p.Currencies.ResearchData < cost)
                {
                    insufficient = true;
                    return p;
                }
                var blueprints = new List<string>(p.Unlocks.Blueprints) { moduleId };
                return p with
                {
                    Currencies = new SaveCurrencies(p.Currencies.Credits, p.Currencies.ResearchData - cost, p.Currencies.AbyssShards),
                    Unlocks = new SaveUnlocks(blueprints, p.Unlocks.Frames),
                };
            }, ct);
            if (ct.IsCancellationRequested || !IsInstanceValid(this)) return;
            if (alreadyOwned)
            {
                _profile = updated;
                Say("Already unlocked: another writer recorded it first; nothing charged.");
            }
            else if (insufficient)
            {
                _profile = updated;
                Say($"Insufficient research data on the saved profile: need {cost} RD, have {updated.Currencies.ResearchData} RD.");
            }
            else
            {
                // Surface the committed write result; the next menu reloads the
                // same live profile, so the blueprint is visible everywhere.
                _profile = updated;
                Say($"Unlocked {moduleId}: permanent blueprint recorded (never consumed by equipping). {ModuleLoadout.Describe(moduleId)}");
                if (GameServices.IsInitialized)
                {
                    GodotLogBridge.Info(GameServices.Logger, $"Research purchased {moduleId} for {cost} RD.");
                }
            }
            RefreshTexts();
        }
        catch (OperationCanceledException)
        {
            // Scene torn down; leave nodes alone.
        }
        catch (SaveFutureVersionException ex)
        {
            if (ct.IsCancellationRequested || !IsInstanceValid(this)) return;
            _saveUsable = false;
            Say($"Profile is from a newer version (schema v{ex.FoundVersion}): purchases disabled, nothing charged.");
            RefreshTexts();
        }
        catch (SaveException ex)
        {
            if (ct.IsCancellationRequested || !IsInstanceValid(this)) return;
            Say($"Purchase failed [{ex.Code}]: nothing charged. {ex.Message}");
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested || !IsInstanceValid(this)) return;
            Say($"Purchase failed ({ex.GetType().Name}): nothing charged.");
        }
        finally
        {
            _savingCount = Math.Max(0, _savingCount - 1);
            if (IsInstanceValid(this))
            {
                SetRowBusy(moduleId, false);
                UpdateBusy();
                RefreshTexts();
            }
        }
    }

    private void SetRowBusy(string moduleId, bool busy)
    {
        if (!IsInstanceValid(this)) return;
        if (!_rows.TryGetValue(moduleId, out var row)) return;
        if (!IsInstanceValid(row.Buy)) return;
        if (busy)
        {
            row.Buy.Disabled = true;
        }
        else
        {
            RefreshTexts();
        }
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
            Say($"Cannot navigate to {target} from here.");
            GodotLogBridge.Warn(GameServices.Logger, $"ResearchScreen navigate to {target} refused.");
        }
    }
}
