using AbyssScav.App;
using AbyssScav.Foundation;
using AbyssScav.Infra.Logging;
using Godot;

namespace AbyssScav.Presentation.Menus;

/// <summary>
/// Operational settings panel for A-01: resolution, window mode, quality, master volume, language.
/// Saves are explicit user actions; boot in safe mode never writes on its own.
/// While in safe mode, quality stays Low (locked) so an explicit save can never
/// persist a higher quality from a safe-mode session. A future-schema settings
/// file is read-only: Apply is visibly disabled.
/// </summary>
public partial class SettingsPanel : PanelContainer
{
    private OptionButton? _resolutionOption;
    private OptionButton? _modeOption;
    private OptionButton? _qualityOption;
    private OptionButton? _languageOption;
    private HSlider? _volumeSlider;
    private Label? _volumeLabel;
    private Label? _noticeLabel;
    private Label? _titleLabel;
    private Label? _resolutionLabel;
    private Label? _modeLabel;
    private Label? _qualityLabel;
    private Label? _languageLabel;
    private Button? _applyButton;
    private Button? _backButton;

    public event Action? Applied;

    private static readonly (int W, int H)[] Resolutions = [(1280, 720), (1600, 900), (1920, 1080)];
    private static readonly string[] Modes = ["Windowed", "Maximized", "Fullscreen"];
    private static readonly string[] Qualities = ["Low", "Medium", "High"];
    private static readonly string[] Languages = ["en", "ko"];

    public SettingsPanel()
    {
        SetAnchorsPreset(LayoutPreset.Center);
    }

    public override void _Ready()
    {
        BuildUi();
        Reload();
    }

    private void BuildUi()
    {
        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 24);
        margin.AddThemeConstantOverride("margin_right", 24);
        margin.AddThemeConstantOverride("margin_top", 16);
        margin.AddThemeConstantOverride("margin_bottom", 16);
        AddChild(margin);

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        margin.AddChild(box);

        _titleLabel = new Label();
        box.AddChild(_titleLabel);

        _noticeLabel = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        box.AddChild(_noticeLabel);

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

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        box.AddChild(row);

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

        if (_applyButton is not null) _applyButton.Text = Localization.T("Apply");
        if (_backButton is not null) _backButton.Text = Localization.T("Back");
    }

    public void Reload()
    {
        var resolution = _resolutionOption;
        var mode = _modeOption;
        var quality = _qualityOption;
        var language = _languageOption;
        var volume = _volumeSlider;
        var notice = _noticeLabel;
        var apply = _applyButton;
        if (resolution is null || mode is null || quality is null || language is null ||
            volume is null || notice is null || apply is null || !GameServices.IsInitialized)
        {
            return;
        }

        ApplyTexts();

        var holder = GameServices.Registry.Resolve<AppSettingsHolder>();
        var s = holder.Current;
        resolution.Selected = IndexOfResolution(s.Graphics.Width, s.Graphics.Height);
        mode.Selected = Math.Max(0, Array.IndexOf(Modes, s.Graphics.WindowMode));
        volume.Value = s.MasterVolumePercent;
        language.Selected = Math.Max(0, Array.IndexOf(Languages, s.Language));
        if (_volumeLabel is not null)
        {
            _volumeLabel.Text = Localization.T("Master volume: {0}%", s.MasterVolumePercent);
        }

        if (holder.IsFutureVersionReadOnly)
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
    }

    private void OnApply()
    {
        var resolution = _resolutionOption;
        var mode = _modeOption;
        var quality = _qualityOption;
        var language = _languageOption;
        var volume = _volumeSlider;
        if (resolution is null || mode is null || quality is null || language is null ||
            volume is null || !GameServices.IsInitialized)
        {
            return;
        }

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
        Localization.SetLanguage(languageCode);
        var next = new AppSettings(
            AppSettings.CurrentSchemaVersion,
            new GraphicsSettings(w, h, Modes[Math.Clamp(mode.Selected, 0, Modes.Length - 1)], qualityName),
            (int)volume.Value,
            holder.Current.AutoReconnectLastSession,
            languageCode).Normalized();

        // Explicit user action: allowed to persist even when launched in safe mode.
        if (!ConfigLoader.TrySave(GameServices.Paths, next, holder.IsSafeMode, userInitiated: true, out var error))
        {
            GodotLogBridge.Error(logger, error, ErrorCodes.SaveWrite);
            return;
        }

        holder.Current = next;
        try
        {
            SettingsAppliance.Apply(next, GetViewport());
        }
        catch (Exception ex)
        {
            GodotLogBridge.Warn(logger, $"Settings saved but could not be fully applied: {ex.GetType().Name}.", ErrorCodes.SaveWrite);
        }

        GodotLogBridge.Info(logger, $"Settings saved by user: {w}x{h} {next.Graphics.WindowMode}/{next.Graphics.Quality} vol={next.MasterVolumePercent}.");
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
}
