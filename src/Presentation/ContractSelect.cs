using AbyssScav.App;
using AbyssScav.Domain;
using AbyssScav.Foundation;
using AbyssScav.Gameplay;
using AbyssScav.Infra.Logging;
using AbyssScav.Persistence;
using Godot;

namespace AbyssScav.Presentation;

/// <summary>
/// Solo contract/loadout selection: biome, contract (filtered to the biome),
/// frame, difficulty, seed, modifiers, insurance, and the field-module loadout
/// (one slot per category, owned blueprints only). Generates and validates via
/// the domain before launching; failures show reasons, never a fake launch.
/// Paid insurance (docs/05 §4: 8%/15% of the departure cost) charges the
/// profile idempotently at launch; the dive is refused when the charge cannot
/// land, so paid coverage is never granted free.
/// </summary>
public partial class ContractSelect : Control
{
    private static readonly string[] LoadoutCategories = { "Sonar", "Engine", "Hull", "Utility" };

    private ContentCatalog? _catalog;
    private OptionButton? _biome;
    private OptionButton? _contract;
    private OptionButton? _frame;
    private OptionButton? _difficulty;
    private OptionButton? _insurance;
    private LineEdit? _seed;
    private Label? _desc;
    private Label? _status;
    private readonly List<CheckBox> _modBoxes = new();
    private readonly List<CheckBox> _consumableBoxes = new();
    private Button? _launch;
    private FileSaveStore? _saveStore;
    private ProfileSave _profile = ProfileSave.Default();
    private List<string> _ownedBlueprints = new();
    private readonly Dictionary<string, OptionButton> _moduleSlots = new(StringComparer.Ordinal);
    private Label? _loadoutDesc;
    private CancellationTokenSource? _cts;
    private bool _profileReady;
    private bool _profileUsable;
    private bool _tutorialConsumed;
    private RunLaunchOptions? _deferredTutorial;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        if (!ContentCatalog.TryBuild(out var catalog, out var errors) || catalog is null)
        {
            BuildError(Localization.T("Content catalog failed: {0}", (object)string.Join("; ", errors)));
            return;
        }
        _catalog = catalog;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        BuildUi();
        RefreshContracts();
        UpdateDescription();
        PopulateModuleSlots();
        LoadProfileAsync();
        ConsumePendingTutorial();
    }

    public override void _ExitTree()
    {
        try { _cts?.Cancel(); }
        catch { }
        finally { _cts?.Dispose(); _cts = null; }
    }

    /// <summary>
    /// Single-consumption tutorial handoff: MainMenu stages the fixed T0 options
    /// and navigates here once. At readiness the pending tutorial is consumed and
    /// the single deferred Launch to RunLoading runs exactly once.
    /// </summary>
    private void ConsumePendingTutorial()
    {
        if (_tutorialConsumed) return;
        if (RunLaunchContext.Pending is not { IsTutorial: true } pending) return;
        _tutorialConsumed = true;
        _deferredTutorial = pending;
        RunLaunchContext.Pending = null;
        CallDeferred(nameof(DeferredTutorialLaunch));
    }

    private void DeferredTutorialLaunch()
    {
        if (!IsInstanceValid(this)) return;
        var options = _deferredTutorial;
        _deferredTutorial = null;
        if (options is null || _catalog is null) return;
        Launch(options);
    }

    private static Color Ink() => new("#071622");
    private static Color Parchment() => new("#dae4df");
    private static Color Cyan() => new("#71d9d1");

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

    private void BuildUi()
    {
        var catalog = _catalog!;
        var bg = new ColorRect { Color = Ink() };
        bg.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(bg);

        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(center);

        var panel = new PanelContainer();
        panel.CustomMinimumSize = new Vector2(640, 0);
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.03f, 0.09f, 0.12f, 1f),
            BorderColor = new Color("#29414b"),
            BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1,
            ContentMarginLeft = 20, ContentMarginRight = 20, ContentMarginTop = 16, ContentMarginBottom = 16,
        };
        panel.AddThemeStyleboxOverride("panel", style);
        center.AddChild(panel);

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);
        panel.AddChild(box);

        var title = new Label { Text = Localization.T("SOLO DIVE — CONTRACT & LOADOUT"), HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 24);
        title.AddThemeColorOverride("font_color", Parchment());
        box.AddChild(title);

        var sub = new Label { Text = Localization.T("Generate · validate · then dive. Solo dive only."), HorizontalAlignment = HorizontalAlignment.Center };
        sub.AddThemeFontSizeOverride("font_size", 13);
        sub.AddThemeColorOverride("font_color", Cyan());
        box.AddChild(sub);

        // Bounded scroll: at 720p the full selector stack exceeds the viewport,
        // so everything between the header and the footer scrolls while the
        // Back / Generate / Tutorial row stays pinned and reachable.
        var scroll = new ScrollContainer { FollowFocus = true };
        scroll.CustomMinimumSize = new Vector2(600, 340);
        box.AddChild(scroll);

        var content = new VBoxContainer();
        content.AddThemeConstantOverride("separation", 6);
        content.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(content);

        _biome = LabeledOption(content, Localization.T("Waters (biome)"), catalog.Biomes.Values.OrderBy(b => b.Id).Select(b => $"{b.Id}|{Localization.T(b.DisplayName)}").ToList());
        _contract = LabeledOption(content, Localization.T("Contract"), new List<string>());
        _frame = LabeledOption(content, Localization.T("Frame"), catalog.Frames.Values.OrderBy(f => f.Id).Select(f => $"{f.Id}|{Localization.T(f.DisplayName)}").ToList());
        _difficulty = LabeledOption(content, Localization.T("Difficulty"), catalog.Difficulties.Values.OrderBy(d => d.Id).Select(d => $"{d.Id}|{Localization.T(d.DisplayName)}").ToList());
        _insurance = LabeledOption(content, Localization.T("Insurance"), new List<string>
        {
            $"{RunSimulation.InsuranceNone}|{Localization.T("No cover (20% retention)")}",
            $"{RunSimulation.InsuranceBasic}|{Localization.T("Basic (50% retention) — {0} cr", RunSimulation.InsurancePremiumCredits(RunSimulation.InsuranceBasic))}",
            $"{RunSimulation.InsurancePremium}|{Localization.T("Premium (70% retention) — {0} cr", RunSimulation.InsurancePremiumCredits(RunSimulation.InsurancePremium))}",
        });
        // Paid policies charge the profile at launch (idempotent, docs/05 §4);
        // the dive is refused when the charge cannot land.
        _biome.ItemSelected += _ => { RefreshContracts(); UpdateDescription(); };
        _contract.ItemSelected += _ => UpdateDescription();
        _frame.ItemSelected += _ => UpdateDescription();
        _difficulty.ItemSelected += _ => UpdateDescription();
        SelectId(_difficulty, "difficulty.standard");
        SelectId(_frame, "frame.skiff");
        SelectId(_insurance, RunSimulation.InsuranceNone);

        var seedLabel = new Label { Text = Localization.T("Run seed (number)") };
        seedLabel.AddThemeColorOverride("font_color", Parchment());
        content.AddChild(seedLabel);
        var seedRow = new HBoxContainer();
        seedRow.AddThemeConstantOverride("separation", 8);
        content.AddChild(seedRow);
        _seed = new LineEdit { Text = "1234", CustomMinimumSize = new Vector2(200, 32) };
        seedRow.AddChild(_seed);
        var reroll = new Button { Text = Localization.T("Randomize") };
        reroll.Pressed += () => { if (_seed is not null) _seed.Text = new Random().Next(1, 999999).ToString(); };
        seedRow.AddChild(reroll);

        var modLabel = new Label { Text = Localization.T("Contract modifiers (optional)") };
        modLabel.AddThemeColorOverride("font_color", Parchment());
        content.AddChild(modLabel);
        var mods = new VBoxContainer();
        content.AddChild(mods);
        foreach (var m in catalog.Modifiers.Values.OrderBy(m => m.Id))
        {
            var cb = new CheckBox { Text = $"{Localization.T(m.DisplayName)} ({m.Id})" };
            cb.AddThemeColorOverride("font_color", Parchment());
            cb.TooltipText = Localization.T(m.DomainEffect);
            mods.AddChild(cb);
            _modBoxes.Add(cb);
            cb.SetMeta("mod_id", m.Id);
        }

        var loadoutLabel = new Label { Text = Localization.T("Module loadout — one slot per category, owned blueprints only") };
        loadoutLabel.AddThemeColorOverride("font_color", Parchment());
        content.AddChild(loadoutLabel);
        foreach (var category in LoadoutCategories)
        {
            var slot = LabeledOption(content, Localization.T(category), new List<string> { $"|{Localization.T("Empty — stock loadout")}" });
            slot.TooltipText = Localization.T("Only owned blueprints with implemented in-run effects are listed. Blueprints are permanent: equipping never consumes them.");
            _moduleSlots[category] = slot;
            var captured = slot;
            captured.ItemSelected += _ => UpdateLoadoutDescription();
        }
        _loadoutDesc = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _loadoutDesc.AddThemeFontSizeOverride("font_size", 13);
        _loadoutDesc.AddThemeColorOverride("font_color", Cyan());
        _loadoutDesc.Text = Localization.T("Stock loadout — no modules equipped.");
        content.AddChild(_loadoutDesc);

        var consumableLabel = new Label { Text = Localization.T("Consumables (optional) — one charge each, spent on use (1-8 keys)") };
        consumableLabel.AddThemeColorOverride("font_color", Parchment());
        content.AddChild(consumableLabel);
        var consumables = new VBoxContainer();
        content.AddChild(consumables);
        foreach (var c in catalog.Consumables.Values.OrderBy(c => c.Id))
        {
            var cb = new CheckBox { Text = $"{Localization.T(c.DisplayName)} ({c.Id})" };
            cb.AddThemeColorOverride("font_color", Parchment());
            cb.TooltipText = Localization.T(c.Description);
            consumables.AddChild(cb);
            _consumableBoxes.Add(cb);
            cb.SetMeta("cons_id", c.Id);
        }

        _desc = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _desc.AddThemeFontSizeOverride("font_size", 13);
        _desc.AddThemeColorOverride("font_color", Parchment());
        content.AddChild(_desc);

        _status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _status.AddThemeFontSizeOverride("font_size", 13);
        _status.AddThemeColorOverride("font_color", new Color("#dfa44d"));
        content.AddChild(_status);

        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        row.AddThemeConstantOverride("separation", 10);
        box.AddChild(row);
        var back = new Button { Text = Localization.T("Back"), CustomMinimumSize = new Vector2(140, 38) };
        back.Pressed += () => Navigate(AppScene.MainMenu);
        _launch = new Button { Text = Localization.T("Generate & Dive"), CustomMinimumSize = new Vector2(200, 38) };
        _launch.Pressed += OnLaunch;
        var tutorial = new Button { Text = Localization.T("Tutorial: The First Ping"), CustomMinimumSize = new Vector2(220, 38) };
        tutorial.TooltipText = Localization.T("Guided first dive: fixed waters, contract, seed 4242, and skiff. Steps are skippable; progress resumes.");
        tutorial.Pressed += OnTutorialLaunch;
        row.AddChild(back);
        row.AddChild(_launch);
        row.AddChild(tutorial);
        _launch.GrabFocus();
    }

    private OptionButton LabeledOption(VBoxContainer box, string label, List<string> idPipeName)
    {
        var l = new Label { Text = label };
        l.AddThemeColorOverride("font_color", Parchment());
        box.AddChild(l);
        var opt = new OptionButton();
        foreach (var entry in idPipeName)
        {
            var parts = entry.Split('|', 2);
            opt.AddItem(parts.Length > 1 ? parts[1] : parts[0]);
            opt.SetItemMetadata(opt.ItemCount - 1, parts[0]);
        }
        box.AddChild(opt);
        return opt;
    }

    private void RefreshContracts()
    {
        if (_catalog is null || _biome is null || _contract is null) return;
        var biomeId = SelectedId(_biome);
        _contract.Clear();
        foreach (var c in _catalog.Contracts.Values.Where(c => c.AllowedBiomeIds.Contains(biomeId)).OrderBy(c => c.Id))
        {
            _contract.AddItem($"{Localization.T(c.Archetype)} — {Localization.T(c.Description)}");
            _contract.SetItemMetadata(_contract.ItemCount - 1, c.Id);
        }
        if (_contract.ItemCount == 0)
        {
            foreach (var c in _catalog.Contracts.Values.OrderBy(c => c.Id))
            {
                _contract.AddItem(Localization.T("{0} (elsewhere) — {1}", (object)Localization.T(c.Archetype), c.Id));
                _contract.SetItemMetadata(_contract.ItemCount - 1, c.Id);
            }
        }
        _contract.Selected = 0;
    }

    private void UpdateDescription()
    {
        if (_catalog is null || _desc is null) return;
        var parts = new List<string>();
        if (_biome is not null && _catalog.Biomes.TryGetValue(SelectedId(_biome), out var b))
        {
            parts.Add(Localization.T("{0}: {1} Depth {2}–{3} m.", Localization.T(b.DisplayName), Localization.T(b.Description), $"{b.MinDepthMeters:F0}", $"{b.MaxDepthMeters:F0}"));
        }
        if (_contract is not null)
        {
            var cid = SelectedId(_contract);
            if (_catalog.Contracts.TryGetValue(cid, out var c))
            {
                parts.Add(Localization.T("{0} (tier {1}, {2} cr): {3} {4}", Localization.T(c.Archetype), c.Tier, c.BasePayout, Localization.T(c.Description), Localization.T(c.DomainInteraction)));
            }
        }
        if (_frame is not null && _catalog.Frames.TryGetValue(SelectedId(_frame), out var f))
        {
            parts.Add(Localization.T("{0}: hull {1}, power {2} PU, cargo {3} slots / {4} kg.", Localization.T(f.DisplayName), $"{f.MaxHull:F0}", $"{f.PowerSupply:F0}", f.CargoSlots, $"{f.CargoMaxMassKg:F0}"));
        }
        _desc.Text = string.Join("\n", parts);
    }

    private async void OnLaunch()
    {
        if (_catalog is null || _status is null || !IsInstanceValid(this)) return;
        if (!IsInstanceValid(_status)) return;
        if (!ulong.TryParse(_seed?.Text.Trim() ?? "", out var seed))
        {
            _status.Text = Localization.T("Seed must be a number (e.g. 1234).");
            return;
        }
        var mods = new List<string>();
        foreach (var cb in _modBoxes)
        {
            if (IsInstanceValid(cb) && cb.ButtonPressed) mods.Add((string)cb.GetMeta("mod_id"));
        }
        var moduleIds = SelectedModuleIds();
        var consumableIds = SelectedConsumableIds();
        if (moduleIds.Count > 0)
        {
            // Fresh ownership needs the live profile: a module-equipped launch
            // is refused until the profile loads, and a stock-only notice is
            // shown when it is unreadable. Never launch equipped on a guess.
            if (!_profileReady)
            {
                _status.Text = Localization.T("Profile still loading: wait a moment, or dive stock (no modules) meanwhile.");
                return;
            }
            if (!_profileUsable)
            {
                _status.Text = Localization.T("Profile unreadable: stock loadout only (no modules). Nothing was equipped; fix the profile on the main menu.");
                return;
            }
        }
        if (!ModuleLoadout.TryValidate(moduleIds, _catalog, _ownedBlueprints, out var modErrors))
        {
            _status.Text = Localization.T("Loadout refused: {0}", (object)string.Join("; ", modErrors));
            return;
        }
        var options = new RunLaunchOptions(
            SelectedId(_biome!), SelectedId(_contract!), SelectedId(_difficulty!),
            SelectedId(_frame!), seed, mods, SelectedId(_insurance!),
            ModuleIds: moduleIds, OwnedBlueprints: new List<string>(_ownedBlueprints),
            ConsumableIds: consumableIds);
        // Pre-flight first: generation + validation + dry-run must pass before
        // any money moves, so a refused run never costs the player a premium.
        if (!TryPreflight(options, out var world, out var preflightError))
        {
            _status.Text = preflightError;
            return;
        }
        if (!await ChargeInsuranceAsync(options.InsuranceId)) return;
        RunLaunchContext.Pending = options;
        _status.Text = Localization.T("World ready: {0} nodes, {1} legs, route {2} waypoints. Diving…", world!.Nodes.Count, world.Segments.Count, world.RouteFromExtractionToObjective.Count);
        Navigate(AppScene.RunLoading);
    }

    /// <summary>
    /// Charges the selected paid policy to the profile before the run starts.
    /// Returns false (with a visible reason) when the charge cannot land —
    /// paid coverage is never granted free. Free policy returns true untouched.
    /// </summary>
    private async Task<bool> ChargeInsuranceAsync(string insuranceId)
    {
        var premium = RunSimulation.InsurancePremiumCredits(insuranceId);
        if (premium <= 0) return true;
        if (_status is null || !IsInstanceValid(_status)) return false;
        if (GameServices.WritesSuspendedByChoice)
        {
            _status.Text = Localization.T("Paid insurance needs a saved profile: this unsaved session cannot charge it. Use No cover.");
            return false;
        }
        if (!_profileReady)
        {
            _status.Text = Localization.T("Profile still loading: wait a moment, or use No cover.");
            return false;
        }
        if (!_profileUsable || _saveStore is null)
        {
            _status.Text = Localization.T("Profile unreadable: paid insurance cannot be charged. Use No cover.");
            return false;
        }
        if (_profile.Currencies.Credits < premium)
        {
            _status.Text = Localization.T("Insufficient credits for paid insurance: need {0} cr, have {1} cr.", premium, _profile.Currencies.Credits);
            return false;
        }
        var ct = _cts?.Token ?? CancellationToken.None;
        var chargeId = "charge.insurance." + Guid.NewGuid().ToString("N");
        try
        {
            var result = await _saveStore.ApplyInsuranceChargeAsync(new InsuranceChargePayload(chargeId, premium), ct);
            if (ct.IsCancellationRequested || !IsInstanceValid(this)) return false;
            if (result.Outcome == InsuranceChargeOutcome.InsufficientFunds)
            {
                _status.Text = Localization.T("Insufficient credits for paid insurance: need {0} cr, have {1} cr.", premium, result.Save.Currencies.Credits);
                return false;
            }
            _profile = result.Save; // refresh the ledger view (Applied or AlreadyApplied)
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested || !IsInstanceValid(this)) return false;
            _status.Text = Localization.T("Insurance charge failed ({0}): nothing charged, no dive. Retry when storage is available.", (object)ex.GetType().Name);
            return false;
        }
    }

    private void OnTutorialLaunch()
    {
        Launch(RunLaunchOptions.Tutorial());
    }

    /// <summary>
    /// Generation + validation + dry-run pre-flight. Returns false with a
    /// player-facing reason; never a fake launch.
    /// </summary>
    private bool TryPreflight(RunLaunchOptions options, out GeneratedWorld? world, out string error)
    {
        world = null;
        if (_catalog is null || _status is null || !IsInstanceValid(this) || !IsInstanceValid(_status))
        {
            error = Localization.T("Selection screen is not ready.");
            return false;
        }
        if (!options.TryValidate(_catalog, out var problems))
        {
            error = Localization.T("Invalid selection: {0}", (object)string.Join("; ", problems));
            return false;
        }
        var request = new RunGenerationRequest(options.RunSeed, options.BiomeId, options.ContractId, _catalog);
        if (!TrenchGenerator.TryGenerate(request, out var generated, out var verdict, out var reason) || generated is null)
        {
            error = Localization.T("Generation refused: {0}", (object)reason);
            return false;
        }
        if (!verdict.IsValid)
        {
            error = Localization.T("World invalid: {0}", (object)string.Join("; ", verdict.Errors));
            return false;
        }
        if (!RunSimulation.TryCreate(generated, _catalog, options.DifficultyId, options.FrameId,
                options.ModifierIds, options.InsuranceId, out _, out var simReason,
                options.EffectiveModuleIds, options.EffectiveOwnedBlueprints, options.IsTutorial,
                options.EffectiveConsumableIds))
        {
            error = Localization.T("Run rejected: {0}", (object)simReason);
            return false;
        }
        world = generated;
        error = string.Empty;
        return true;
    }

    private void Launch(RunLaunchOptions options)
    {
        if (!TryPreflight(options, out var world, out var error))
        {
            if (_status is not null && IsInstanceValid(_status)) _status.Text = error;
            return;
        }
        RunLaunchContext.Pending = options;
        if (_status is not null && IsInstanceValid(_status))
            _status.Text = Localization.T("World ready: {0} nodes, {1} legs, route {2} waypoints. Diving…", world!.Nodes.Count, world.Segments.Count, world.RouteFromExtractionToObjective.Count);
        Navigate(AppScene.RunLoading);
    }

    private static string SelectedId(OptionButton opt)
    {
        if (opt.ItemCount == 0 || opt.Selected < 0) return "";
        return (string)opt.GetItemMetadata(opt.Selected);
    }

    private static void SelectId(OptionButton? opt, string id)
    {
        if (opt is null) return;
        for (var i = 0; i < opt.ItemCount; i++)
        {
            if (string.Equals((string)opt.GetItemMetadata(i), id, StringComparison.Ordinal))
            {
                opt.Selected = i;
                return;
            }
        }
    }

    /// <summary>Equipped module IDs from the four category slots (empties skipped, in slot order).</summary>
    private List<string> SelectedModuleIds()
    {
        var ids = new List<string>();
        foreach (var category in LoadoutCategories)
        {
            if (!_moduleSlots.TryGetValue(category, out var slot) || slot.ItemCount == 0 || slot.Selected < 0)
                continue;
            var id = (string)slot.GetItemMetadata(slot.Selected);
            if (!string.IsNullOrEmpty(id)) ids.Add(id);
        }
        return ids;
    }

    /// <summary>Checked consumable IDs in catalog order (slot order = key order 1-8).</summary>
    private List<string> SelectedConsumableIds()
    {
        var ids = new List<string>();
        foreach (var cb in _consumableBoxes)
        {
            if (IsInstanceValid(cb) && cb.ButtonPressed) ids.Add((string)cb.GetMeta("cons_id"));
        }
        return ids;
    }

    private void UpdateLoadoutDescription()
    {
        if (_loadoutDesc is null) return;
        var ids = SelectedModuleIds();
        _loadoutDesc.Text = ids.Count == 0
            ? Localization.T("Stock loadout — no modules equipped.")
            : string.Join("\n", ids.Select(id => $"{id}: {Localization.T(ModuleLoadout.Describe(id))}"));
    }

    /// <summary>Rebuilds each category slot from owned blueprints (supported effects only).</summary>
    private void PopulateModuleSlots()
    {
        if (_catalog is null) return;
        var owned = new HashSet<string>(_ownedBlueprints, StringComparer.Ordinal);
        foreach (var category in LoadoutCategories)
        {
            if (!_moduleSlots.TryGetValue(category, out var slot)) continue;
            var prior = slot.ItemCount > 0 && slot.Selected >= 0 ? (string)slot.GetItemMetadata(slot.Selected) : "";
            slot.Clear();
            slot.AddItem(Localization.T("Empty — stock loadout"));
            slot.SetItemMetadata(0, "");
            foreach (var m in _catalog.Modules.Values
                         .Where(m => m.Category == category && ModuleLoadout.IsSupported(m.Id) && owned.Contains(m.Id))
                         .OrderBy(m => m.Id))
            {
                slot.AddItem($"{Localization.T(ShortModuleName(m.Id))} — {Localization.T(ModuleLoadout.Describe(m.Id))}");
                slot.SetItemMetadata(slot.ItemCount - 1, m.Id);
                slot.SetItemTooltip(slot.ItemCount - 1, Localization.T(ModuleLoadout.Describe(m.Id)));
            }
            slot.Selected = 0;
            for (var i = 0; i < slot.ItemCount; i++)
            {
                if (string.Equals((string)slot.GetItemMetadata(i), prior, StringComparison.Ordinal))
                {
                    slot.Selected = i;
                    break;
                }
            }
        }
        UpdateLoadoutDescription();
    }

    private static string ShortModuleName(string id)
    {
        var tail = id.Contains('.') ? id[(id.LastIndexOf('.') + 1)..] : id;
        return tail.Replace('_', ' ');
    }

    private async void LoadProfileAsync()
    {
        if (!GameServices.IsInitialized) return;
        var ct = _cts?.Token ?? CancellationToken.None;
        try
        {
            _saveStore = new FileSaveStore(GameServices.Paths.SavesDir);
        }
        catch
        {
            if (ct.IsCancellationRequested || !IsInstanceValid(this)) return;
            _profileReady = true;
            _profileUsable = false;
            if (_status is not null && IsInstanceValid(_status))
                _status.Text = Localization.T("Profile storage unavailable: stock loadout only (no modules).");
            return; // stock loadout; slots already show owned-nothing honestly.
        }
        try
        {
            var profile = await _saveStore.LoadAsync(ct);
            if (ct.IsCancellationRequested || !IsInstanceValid(this)) return;
            _profile = profile;
            _ownedBlueprints = new List<string>(profile.Unlocks.Blueprints);
            _profileReady = true;
            _profileUsable = true;
            PopulateModuleSlots();
        }
        catch (OperationCanceledException)
        {
            // Scene torn down; leave nodes alone.
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested || !IsInstanceValid(this)) return;
            // Profile unreadable: stay on the stock loadout with an explicit
            // notice — never a fake equip, never a silent "live" claim.
            _ownedBlueprints = new List<string>();
            _profileReady = true;
            _profileUsable = false;
            PopulateModuleSlots();
            if (_status is not null && IsInstanceValid(_status))
                _status.Text = Localization.T("Profile unreadable ({0}): stock loadout only (no modules).", (object)ex.GetType().Name);
        }
    }

    private void Navigate(AppScene target)
    {
        if (!GameServices.IsInitialized) return;
        var svc = GameServices.Registry.TryResolve<SceneFlowService>(out var s) ? s : null;
        if (svc is null)
        {
            var tree = GetTree();
            var path = target == AppScene.MainMenu ? "res://scenes/main_menu.tscn" : "res://scenes/run.tscn";
            tree?.CallDeferred(SceneTree.MethodName.ChangeSceneToFile, path);
            return;
        }
        if (!svc.Navigate(target))
        {
            if (_status is not null) _status.Text = Localization.T("Cannot navigate to {0} from here.", target);
            GodotLogBridge.Warn(GameServices.Logger, $"ContractSelect navigate to {target} refused.");
        }
    }
}
