using AbyssScav.App;
using AbyssScav.Foundation;
using AbyssScav.Infra.Logging;
using Godot;

namespace AbyssScav.Presentation.Menus;

/// <summary>
/// Settings panel: resolution, window mode, quality, master volume, language,
/// accessibility (colour-vision sonar palette, high-contrast HUD) and keyboard
/// rebinding. Saves are explicit user actions; boot in safe mode never writes on
/// its own. While in safe mode, quality stays Low (locked) so an explicit save
/// can never persist a higher quality from a safe-mode session. A future-schema
/// settings file is read-only: Apply and every editor are visibly disabled.
///
/// Rebinding is press-to-rebind: click an action's key, press the new key.
/// Escape cancels (it is reserved for pause). A key already used by another
/// action is swapped onto that action, so bindings are never duplicated or
/// empty. Edits are pending until Apply; Back discards them.
/// </summary>
public partial class SettingsPanel : PanelContainer
{
    private OptionButton? _resolutionOption;
    private OptionButton? _modeOption;
    private OptionButton? _qualityOption;
    private OptionButton? _languageOption;
    private OptionButton? _paletteOption;
    private CheckButton? _highContrastCheck;
    private HSlider? _volumeSlider;
    private Label? _volumeLabel;
    private Label? _noticeLabel;
    private Label? _titleLabel;
    private Label? _resolutionLabel;
    private Label? _modeLabel;
    private Label? _qualityLabel;
    private Label? _languageLabel;
    private Label? _accessibilityLabel;
    private Label? _paletteLabel;
    private Label? _paletteNote;
    private Label? _bindingsTitle;
    private Label? _bindingsHelp;
    private Label? _bindingsStatus;
    private Button? _resetBindingsButton;
    private Button? _applyButton;
    private Button? _backButton;

    private readonly List<(InputActionGroup Group, Label Label)> _groupLabels = new();
    private readonly List<(string ActionId, Label Label, Button Button)> _bindingRows = new();
    private IReadOnlyDictionary<string, string> _pendingBindings = KeyBindingCatalog.Defaults();
    private string? _captureAction;

    public event Action? Applied;

    private static readonly (int W, int H)[] Resolutions = [(1280, 720), (1600, 900), (1920, 1080)];
    private static readonly string[] Modes = ["Windowed", "Maximized", "Fullscreen"];
    private static readonly string[] Qualities = ["Low", "Medium", "High"];
    private static readonly string[] Languages = ["en", "ko"];

    public SettingsPanel()
    {
        SetAnchorsPreset(LayoutPreset.Center);
        // Grow both ways from the anchor so the (two-column) panel stays centred.
        GrowHorizontal = GrowDirection.Both;
        GrowVertical = GrowDirection.Both;
    }

    public override void _Ready()
    {
        BuildUi();
        Reload();
        VisibilityChanged += () =>
        {
            if (!Visible)
            {
                CancelCapture(null);
            }
        };
    }

    private void BuildUi()
    {
        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 24);
        margin.AddThemeConstantOverride("margin_right", 24);
        margin.AddThemeConstantOverride("margin_top", 16);
        margin.AddThemeConstantOverride("margin_bottom", 16);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 8);
        margin.AddChild(root);

        _titleLabel = new Label();
        root.AddChild(_titleLabel);

        _noticeLabel = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(600, 0) };
        root.AddChild(_noticeLabel);

        var columns = new HBoxContainer();
        columns.AddThemeConstantOverride("separation", 24);
        root.AddChild(columns);

        var box = new VBoxContainer { CustomMinimumSize = new Vector2(300, 0) };
        box.AddThemeConstantOverride("separation", 6);
        columns.AddChild(box);
        BuildGeneralColumn(box);

        var controls = new VBoxContainer { CustomMinimumSize = new Vector2(420, 0) };
        controls.AddThemeConstantOverride("separation", 6);
        columns.AddChild(controls);
        BuildBindingsColumn(controls);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        root.AddChild(row);

        var apply = new Button();
        apply.Pressed += OnApply;
        _applyButton = apply;
        var back = new Button();
        back.Pressed += () => Visible = false;
        _backButton = back;
        row.AddChild(apply);
        row.AddChild(back);

        ApplyTexts();
    }

    private void BuildGeneralColumn(VBoxContainer box)
    {
        var resolution = new OptionButton();
        foreach (var (w, h) in Resolutions)
        {
            resolution.AddItem($"{w} x {h}");
        }

        var mode = new OptionButton();
        foreach (var m in Modes)
        {
            mode.AddItem(Localization.T(m));
        }

        var quality = new OptionButton();
        foreach (var q in Qualities)
        {
            quality.AddItem(Localization.T(q));
        }

        _resolutionOption = resolution;
        _modeOption = mode;
        _qualityOption = quality;

        _resolutionLabel = new Label();
        box.AddChild(_resolutionLabel);
        box.AddChild(resolution);
        _modeLabel = new Label();
        box.AddChild(_modeLabel);
        box.AddChild(mode);
        _qualityLabel = new Label();
        box.AddChild(_qualityLabel);
        box.AddChild(quality);

        var volumeLabel = new Label();
        _volumeLabel = volumeLabel;
        box.AddChild(volumeLabel);
        var volume = new HSlider { MinValue = 0, MaxValue = 100, Step = 1, Value = 80, CustomMinimumSize = new Vector2(280, 16) };
        volume.ValueChanged += v =>
        {
            var label = _volumeLabel;
            if (label is not null)
            {
                label.Text = Localization.T("Master volume: {0}%", (int)v);
            }
        };
        _volumeSlider = volume;
        box.AddChild(volume);

        _languageLabel = new Label();
        box.AddChild(_languageLabel);
        var language = new OptionButton();
        foreach (var code in Languages)
        {
            language.AddItem(Localization.LanguageName(code));
        }

        _languageOption = language;
        box.AddChild(language);

        _accessibilityLabel = new Label();
        _accessibilityLabel.AddThemeColorOverride("font_color", new Color("#71d9d1"));
        box.AddChild(_accessibilityLabel);

        _paletteLabel = new Label();
        box.AddChild(_paletteLabel);
        var palette = new OptionButton();
        foreach (var _ in SonarPalette.Ids)
        {
            palette.AddItem(string.Empty);
        }

        _paletteOption = palette;
        box.AddChild(palette);
        _paletteNote = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(300, 0) };
        _paletteNote.AddThemeFontSizeOverride("font_size", 12);
        _paletteNote.AddThemeColorOverride("font_color", new Color(0.62f, 0.72f, 0.70f));
        box.AddChild(_paletteNote);

        _highContrastCheck = new CheckButton();
        box.AddChild(_highContrastCheck);
    }

    private void BuildBindingsColumn(VBoxContainer controls)
    {
        _bindingsTitle = new Label();
        _bindingsTitle.AddThemeColorOverride("font_color", new Color("#71d9d1"));
        controls.AddChild(_bindingsTitle);

        _bindingsHelp = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(420, 0) };
        _bindingsHelp.AddThemeFontSizeOverride("font_size", 12);
        _bindingsHelp.AddThemeColorOverride("font_color", new Color(0.62f, 0.72f, 0.70f));
        controls.AddChild(_bindingsHelp);

        var scroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(420, 330),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        controls.AddChild(scroll);
        var list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 2);
        scroll.AddChild(list);

        InputActionGroup? currentGroup = null;
        foreach (var def in KeyBindingCatalog.Actions)
        {
            if (currentGroup != def.Group)
            {
                currentGroup = def.Group;
                var header = new Label();
                header.AddThemeColorOverride("font_color", new Color("#dfa44d"));
                list.AddChild(header);
                _groupLabels.Add((def.Group, header));
            }

            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 8);
            var name = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(230, 0) };
            var keyButton = new Button { CustomMinimumSize = new Vector2(140, 0) };
            var actionId = def.Id;
            keyButton.Pressed += () => BeginCapture(actionId);
            row.AddChild(name);
            row.AddChild(keyButton);
            list.AddChild(row);
            _bindingRows.Add((def.Id, name, keyButton));
        }

        _bindingsStatus = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(420, 0) };
        _bindingsStatus.AddThemeColorOverride("font_color", new Color("#dae4df"));
        controls.AddChild(_bindingsStatus);

        _resetBindingsButton = new Button();
        _resetBindingsButton.Pressed += () =>
        {
            CancelCapture(null);
            _pendingBindings = KeyBindingCatalog.Defaults();
            RefreshBindingButtons();
            SetBindingStatus(Localization.T("Key bindings reset to defaults. Press Apply to save."));
        };
        controls.AddChild(_resetBindingsButton);
    }

    /// <summary>Re-resolves every visible string so a language change applies on next open.</summary>
    private void ApplyTexts()
    {
        if (_titleLabel is not null) _titleLabel.Text = Localization.T("Settings");
        if (_resolutionLabel is not null) _resolutionLabel.Text = Localization.T("Resolution");
        if (_modeLabel is not null) _modeLabel.Text = Localization.T("Window mode");
        if (_qualityLabel is not null) _qualityLabel.Text = Localization.T("Graphics quality");
        if (_languageLabel is not null) _languageLabel.Text = Localization.T("Language");
        if (_modeOption is not null)
        {
            for (var i = 0; i < Modes.Length && i < _modeOption.ItemCount; i++)
            {
                _modeOption.SetItemText(i, Localization.T(Modes[i]));
            }
        }

        if (_qualityOption is not null)
        {
            for (var i = 0; i < Qualities.Length && i < _qualityOption.ItemCount; i++)
            {
                _qualityOption.SetItemText(i, Localization.T(Qualities[i]));
            }
        }

        if (_languageOption is not null)
        {
            for (var i = 0; i < Languages.Length && i < _languageOption.ItemCount; i++)
            {
                _languageOption.SetItemText(i, Localization.LanguageName(Languages[i]));
            }
        }

        if (_volumeLabel is not null && _volumeSlider is not null)
        {
            _volumeLabel.Text = Localization.T("Master volume: {0}%", (int)_volumeSlider.Value);
        }

        if (_accessibilityLabel is not null) _accessibilityLabel.Text = Localization.T("Accessibility");
        if (_paletteLabel is not null) _paletteLabel.Text = Localization.T("Sonar colour palette");
        if (_paletteOption is not null)
        {
            for (var i = 0; i < SonarPalette.Ids.Count && i < _paletteOption.ItemCount; i++)
            {
                _paletteOption.SetItemText(i, PaletteName(SonarPalette.Ids[i]));
            }
        }

        if (_paletteNote is not null)
        {
            _paletteNote.Text = Localization.T("Contacts also differ by shape (diamond salvage, triangle biological, square structure, ? unknown, dash terrain); a legend sits under the sonar scope.");
        }

        if (_highContrastCheck is not null)
        {
            _highContrastCheck.Text = Localization.T("High-contrast HUD");
            _highContrastCheck.TooltipText = Localization.T("Opaque HUD panels, bright outlined text, heavier borders and larger sonar glyphs.");
        }

        if (_bindingsTitle is not null) _bindingsTitle.Text = Localization.T("Key bindings (keyboard)");
        if (_bindingsHelp is not null)
        {
            _bindingsHelp.Text = Localization.T("Click a key, then press the new key. Esc cancels and stays on pause. A key already in use is swapped with the other action. Gamepad buttons are fixed.");
        }

        foreach (var (group, label) in _groupLabels)
        {
            label.Text = InputBindings.GroupLabel(group);
        }

        foreach (var (actionId, label, _) in _bindingRows)
        {
            label.Text = InputBindings.ActionLabel(actionId);
        }

        if (_resetBindingsButton is not null) _resetBindingsButton.Text = Localization.T("Reset key bindings to defaults");
        if (_applyButton is not null) _applyButton.Text = Localization.T("Apply");
        if (_backButton is not null) _backButton.Text = Localization.T("Back");
        RefreshBindingButtons();
    }

    private static string PaletteName(string id) => id switch
    {
        SonarPalette.RedGreenSafeId => Localization.T("Red–green safe (deuteranopia / protanopia)"),
        SonarPalette.BlueYellowSafeId => Localization.T("Blue–yellow safe (tritanopia)"),
        _ => Localization.T("Standard"),
    };

    public void Reload()
    {
        var resolution = _resolutionOption;
        var mode = _modeOption;
        var quality = _qualityOption;
        var language = _languageOption;
        var palette = _paletteOption;
        var highContrast = _highContrastCheck;
        var volume = _volumeSlider;
        var notice = _noticeLabel;
        var apply = _applyButton;
        if (resolution is null || mode is null || quality is null || language is null || palette is null ||
            highContrast is null || volume is null || notice is null || apply is null || !GameServices.IsInitialized)
        {
            return;
        }

        CancelCapture(null);
        ApplyTexts();

        var holder = GameServices.Registry.Resolve<AppSettingsHolder>();
        var s = holder.Current;
        resolution.Selected = IndexOfResolution(s.Graphics.Width, s.Graphics.Height);
        mode.Selected = Math.Max(0, Array.IndexOf(Modes, s.Graphics.WindowMode));
        volume.Value = s.MasterVolumePercent;
        language.Selected = Math.Max(0, Array.IndexOf(Languages, s.Language));
        palette.Selected = Math.Max(0, IndexOf(SonarPalette.Ids, SonarPalette.CanonicalId(s.SonarPaletteId)));
        highContrast.ButtonPressed = s.HighContrastHud;
        _pendingBindings = KeyBindingCatalog.Normalize(s.KeyBindings);
        SetBindingStatus(string.Empty);
        if (_volumeLabel is not null)
        {
            _volumeLabel.Text = Localization.T("Master volume: {0}%", s.MasterVolumePercent);
        }

        var readOnly = holder.IsFutureVersionReadOnly;
        palette.Disabled = readOnly;
        highContrast.Disabled = readOnly;
        if (_resetBindingsButton is not null) _resetBindingsButton.Disabled = readOnly;
        foreach (var (_, _, button) in _bindingRows)
        {
            button.Disabled = readOnly;
        }

        if (readOnly)
        {
            quality.Selected = Math.Max(0, Array.IndexOf(Qualities, s.Graphics.Quality));
            quality.Disabled = true;
            apply.Disabled = true;
            apply.TooltipText = Localization.T("Saving is disabled: the settings file is from a newer version.");
            notice.Text = Localization.T("Settings file is from a newer version. Saving is disabled to avoid data loss; the game runs on defaults.");
        }
        else if (holder.IsSafeMode)
        {
            quality.Selected = 0;
            quality.Disabled = true;
            quality.TooltipText = Localization.T("Safe mode keeps quality at Low. Restart normally to change it.");
            apply.Disabled = false;
            notice.Text = Localization.T("Safe mode is active: quality stays Low. Apply saves your explicit choice (volume, window) and quality Low.");
        }
        else
        {
            quality.Selected = Math.Max(0, Array.IndexOf(Qualities, s.Graphics.Quality));
            quality.Disabled = false;
            apply.Disabled = false;
            notice.Text = string.Empty;
        }

        RefreshBindingButtons();
    }

    // ------------------------------------------------------------------ rebinding

    private void RefreshBindingButtons()
    {
        foreach (var (actionId, _, button) in _bindingRows)
        {
            if (actionId == _captureAction)
            {
                button.Text = Localization.T("Press a key…");
                continue;
            }

            button.Text = _pendingBindings.TryGetValue(actionId, out var key) ? InputBindings.KeyLabel(key) : "?";
        }
    }

    private void SetBindingStatus(string text)
    {
        if (_bindingsStatus is not null) _bindingsStatus.Text = text;
    }

    private void BeginCapture(string actionId)
    {
        if (!KeyBindingCatalog.IsKnownAction(actionId)) return;
        _captureAction = actionId;
        RefreshBindingButtons();
        SetBindingStatus(Localization.T("Press the new key for {0} (Esc cancels).", (object)InputBindings.ActionLabel(actionId)));
    }

    private void CancelCapture(string? status)
    {
        if (_captureAction is null) return;
        _captureAction = null;
        RefreshBindingButtons();
        if (status is not null) SetBindingStatus(status);
    }

    /// <summary>
    /// While capturing, the next key press is consumed here (before any button or
    /// menu sees it) so Enter/Space/Esc cannot also trigger UI actions.
    /// </summary>
    public override void _Input(InputEvent @event)
    {
        var action = _captureAction;
        if (action is null || !Visible) return;

        if (@event is InputEventMouseButton { Pressed: true })
        {
            GetViewport()?.SetInputAsHandled();
            CancelCapture(Localization.T("Rebind cancelled."));
            return;
        }

        if (@event is not InputEventKey { Pressed: true, Echo: false } keyEvent) return;
        GetViewport()?.SetInputAsHandled();

        var pressed = keyEvent.PhysicalKeycode != Key.None ? keyEvent.PhysicalKeycode : keyEvent.Keycode;
        if (pressed == Key.Escape)
        {
            CancelCapture(Localization.T("Rebind cancelled."));
            return;
        }

        var keyName = KeyNameOf(pressed);
        if (keyName is null)
        {
            // Stay in capture so the player can simply press another key.
            SetBindingStatus(Localization.T("{0} cannot be bound. Press another key (Esc cancels).", (object)OS.GetKeycodeString(pressed)));
            return;
        }

        var result = KeyBindingCatalog.Rebind(_pendingBindings, action, keyName);
        _captureAction = null;
        _pendingBindings = result.Bindings;
        RefreshBindingButtons();
        var actionName = InputBindings.ActionLabel(action);
        var keyLabel = InputBindings.KeyLabel(keyName);
        SetBindingStatus(result.Outcome switch
        {
            RebindOutcome.Bound => Localization.T("{0} → {1}. Press Apply to save.", actionName, keyLabel),
            RebindOutcome.Swapped => Localization.T("{0} → {1}; {2} moved to {3} (swapped). Press Apply to save.",
                actionName, keyLabel, InputBindings.ActionLabel(result.SwappedActionId ?? string.Empty), InputBindings.KeyLabel(result.SwappedToKey ?? string.Empty)),
            RebindOutcome.Unchanged => Localization.T("{0} already uses {1}.", actionName, keyLabel),
            _ => Localization.T("{0} cannot be bound. Press another key (Esc cancels).", (object)keyLabel),
        });
    }

    /// <summary>Whitelisted binding name for a pressed Godot key; null when not bindable.</summary>
    private static string? KeyNameOf(Key key)
    {
        foreach (var name in KeyBindingCatalog.AllowedKeys)
        {
            if (InputBindings.TryParseKey(name, out var parsed) && parsed == key)
            {
                return name;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ apply

    private void OnApply()
    {
        var resolution = _resolutionOption;
        var mode = _modeOption;
        var quality = _qualityOption;
        var language = _languageOption;
        var palette = _paletteOption;
        var highContrast = _highContrastCheck;
        var volume = _volumeSlider;
        if (resolution is null || mode is null || quality is null || language is null || palette is null ||
            highContrast is null || volume is null || !GameServices.IsInitialized)
        {
            return;
        }

        CancelCapture(null);
        var holder = GameServices.Registry.Resolve<AppSettingsHolder>();
        var logger = GameServices.Logger;

        if (holder.IsFutureVersionReadOnly)
        {
            GodotLogBridge.Warn(logger, "Settings Apply refused: future-schema file is read-only.", ErrorCodes.BootConfigFutureVersion);
            return;
        }

        var (w, h) = Resolutions[Math.Clamp(resolution.Selected, 0, Resolutions.Length - 1)];
        // Safe mode forces Low even on explicit save, so a safe-mode session can
        // never persist higher quality behind the user's back.
        var qualityName = holder.IsSafeMode ? "Low" : Qualities[Math.Clamp(quality.Selected, 0, Qualities.Length - 1)];
        var languageCode = Languages[Math.Clamp(language.Selected, 0, Languages.Length - 1)];
        var paletteId = SonarPalette.Ids[Math.Clamp(palette.Selected, 0, SonarPalette.Ids.Count - 1)];
        var next = (holder.Current with
        {
            SchemaVersion = AppSettings.CurrentSchemaVersion,
            Graphics = new GraphicsSettings(w, h, Modes[Math.Clamp(mode.Selected, 0, Modes.Length - 1)], qualityName),
            MasterVolumePercent = (int)volume.Value,
            Language = languageCode,
            SonarPaletteId = paletteId,
            HighContrastHud = highContrast.ButtonPressed,
            KeyBindings = _pendingBindings,
        }).Normalized();

        // Explicit user action: allowed to persist even when launched in safe mode.
        if (!ConfigLoader.TrySave(GameServices.Paths, next, holder.IsSafeMode, userInitiated: true, out var error))
        {
            GodotLogBridge.Error(logger, error, ErrorCodes.SaveWrite);
            SetBindingStatus(Localization.T("Settings could not be saved. Check the log for details."));
            return;
        }

        Localization.SetLanguage(languageCode);
        holder.Current = next;
        try
        {
            SettingsAppliance.Apply(next, GetViewport());
            InputBindings.ApplyToInputMap(next.KeyBindings);
        }
        catch (Exception ex)
        {
            GodotLogBridge.Warn(logger, $"Settings saved but could not be fully applied: {ex.GetType().Name}.", ErrorCodes.SaveWrite);
        }

        var remapped = KeyBindingCatalog.Actions.Count(a => next.KeyBindings is not null && next.KeyBindings[a.Id] != a.DefaultKey);
        GodotLogBridge.Info(logger, $"Settings saved by user: {w}x{h} {next.Graphics.WindowMode}/{next.Graphics.Quality} vol={next.MasterVolumePercent} palette={next.SonarPaletteId} highContrast={next.HighContrastHud} remappedKeys={remapped}.");
        Visible = false;
        Applied?.Invoke();
    }

    private static int IndexOfResolution(int w, int h)
    {
        for (var i = 0; i < Resolutions.Length; i++)
        {
            if (Resolutions[i].W == w && Resolutions[i].H == h)
            {
                return i;
            }
        }

        return 0;
    }

    private static int IndexOf(IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] == value)
            {
                return i;
            }
        }

        return -1;
    }
}
