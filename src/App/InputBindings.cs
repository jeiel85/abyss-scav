using AbyssScav.Foundation;
using Godot;

namespace AbyssScav.App;

/// <summary>
/// Godot glue for keyboard remapping: applies the settings' binding map to the
/// InputMap (keyboard events only — gamepad events are left untouched), and
/// resolves player-facing key labels / localized action names / hint rewriting
/// so every on-screen control hint follows the current binding.
/// Pure binding rules (whitelist, duplicates, swap) live in
/// <see cref="KeyBindingCatalog"/> and are unit tested there.
/// </summary>
public static class InputBindings
{
    private static readonly IReadOnlyDictionary<string, string> DefaultMap = KeyBindingCatalog.Defaults();

    private static IReadOnlyDictionary<string, string>? _hintSource;
    private static IReadOnlyDictionary<string, string> _verbHints = new Dictionary<string, string>();
    private static IReadOnlyDictionary<string, string> _verbAndThrustHints = new Dictionary<string, string>();

    /// <summary>Bindings from the live settings holder; defaults before boot (headless tests).</summary>
    public static IReadOnlyDictionary<string, string> Current
    {
        get
        {
            if (GameServices.IsInitialized
                && GameServices.Registry.TryResolve<AppSettingsHolder>(out var holder)
                && holder?.Current.KeyBindings is { } map)
            {
                return map;
            }

            return DefaultMap;
        }
    }

    /// <summary>
    /// Replaces the keyboard event of every remappable action with its bound
    /// physical key. Input is normalized first, so a malformed map can never
    /// leave an action unbound or two actions on one key.
    /// </summary>
    public static void ApplyToInputMap(IReadOnlyDictionary<string, string>? bindings)
    {
        var map = KeyBindingCatalog.Normalize(bindings);
        foreach (var def in KeyBindingCatalog.Actions)
        {
            if (!InputMap.HasAction(def.Id))
            {
                InputMap.AddAction(def.Id);
            }

            foreach (var ev in InputMap.ActionGetEvents(def.Id))
            {
                if (ev is InputEventKey)
                {
                    InputMap.ActionEraseEvent(def.Id, ev);
                }
            }

            if (TryParseKey(map[def.Id], out var key))
            {
                InputMap.ActionAddEvent(def.Id, new InputEventKey { PhysicalKeycode = key });
            }
            else
            {
                GD.PushError($"[INPUT] Binding '{map[def.Id]}' for {def.Id} is not a Godot key name.");
            }
        }
    }

    /// <summary>Whitelisted key name to Godot key; false for unknown names (never numeric strings).</summary>
    public static bool TryParseKey(string? keyName, out Key key)
    {
        key = Key.None;
        if (string.IsNullOrEmpty(keyName) || char.IsDigit(keyName[0]))
        {
            return false;
        }

        return Enum.TryParse(keyName, ignoreCase: false, out key) && key != Key.None;
    }

    /// <summary>True when every whitelisted Foundation key name maps to a Godot key (smoke-tested).</summary>
    public static bool AllAllowedKeysParse(out string firstBad)
    {
        foreach (var name in KeyBindingCatalog.AllowedKeys)
        {
            if (!TryParseKey(name, out _))
            {
                firstBad = name;
                return false;
            }
        }

        firstBad = string.Empty;
        return true;
    }

    /// <summary>
    /// Key label as printed on the player's keyboard: letter keys follow the
    /// active layout (physical binding, e.g. AZERTY shows 'A' for the Q position);
    /// everything else uses the US label.
    /// </summary>
    public static string KeyLabel(string keyName)
    {
        var fallback = KeyBindingCatalog.DisplayLabel(keyName);
        if (!TryParseKey(keyName, out var key) || key < Key.A || key > Key.Z || !LayoutQuerySupported)
        {
            return fallback;
        }

        var label = DisplayServer.KeyboardGetLabelFromPhysical(key);
        return label >= Key.A && label <= Key.Z
            ? ((char)('A' + (int)(label - Key.A))).ToString()
            : fallback;
    }

    /// <summary>The headless display server cannot report keyboard layouts (and logs an error per query).</summary>
    private static bool LayoutQuerySupported => _layoutQuerySupported ??= DisplayServer.GetName() != "headless";

    private static bool? _layoutQuerySupported;

    /// <summary>Current key label for an action id (defaults for unknown ids).</summary>
    public static string ActionKeyLabel(string actionId)
    {
        var map = Current;
        if (map.TryGetValue(actionId, out var key))
        {
            return KeyLabel(key);
        }

        var def = KeyBindingCatalog.Find(actionId);
        return def is null ? "?" : KeyLabel(def.DefaultKey);
    }

    /// <summary>
    /// Rewrites default verb letters in localized hint text to the current labels.
    /// <paramref name="includeThrust"/> also rewrites W/S (surge) for the tutorial's thrust step.
    /// Call exactly once per displayed string (see <see cref="KeyHintRewriter"/>).
    /// </summary>
    public static string RewriteHints(string? text, bool includeThrust = false)
    {
        RefreshHintMaps();
        return KeyHintRewriter.Rewrite(text, includeThrust ? _verbAndThrustHints : _verbHints);
    }

    private static void RefreshHintMaps()
    {
        var current = Current;
        if (ReferenceEquals(current, _hintSource))
        {
            return;
        }

        var verbs = new Dictionary<string, string>(StringComparer.Ordinal);
        var withThrust = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var def in KeyBindingCatalog.Actions)
        {
            var isThrust = def.Id is "abyss_fwd" or "abyss_back";
            if (!def.HintToken && !isThrust)
            {
                continue;
            }

            var defaultLabel = KeyBindingCatalog.DisplayLabel(def.DefaultKey);
            var currentLabel = current.TryGetValue(def.Id, out var key) ? KeyLabel(key) : defaultLabel;
            if (defaultLabel.Length != 1 || currentLabel == defaultLabel)
            {
                continue;
            }

            if (def.HintToken)
            {
                verbs[defaultLabel] = currentLabel;
            }

            withThrust[defaultLabel] = currentLabel;
        }

        _verbHints = verbs;
        _verbAndThrustHints = withThrust;
        _hintSource = current;
    }

    /// <summary>Localized, player-facing action name for the rebinding UI.</summary>
    public static string ActionLabel(string actionId) => actionId switch
    {
        "abyss_fwd" => Localization.T("Surge forward"),
        "abyss_back" => Localization.T("Surge back"),
        "abyss_left" => Localization.T("Sway left"),
        "abyss_right" => Localization.T("Sway right"),
        "abyss_up" => Localization.T("Heave up"),
        "abyss_down" => Localization.T("Heave down"),
        "abyss_yaw_left" => Localization.T("Yaw left"),
        "abyss_yaw_right" => Localization.T("Yaw right"),
        "abyss_pitch_up" => Localization.T("Pitch up"),
        "abyss_pitch_down" => Localization.T("Pitch down"),
        "abyss_boost" => Localization.T("Boost"),
        "abyss_silent" => Localization.T("Quiet running"),
        "abyss_ping" => Localization.T("Active sonar ping"),
        "abyss_interact" => Localization.T("Salvage"),
        "abyss_survey" => Localization.T("Survey contact"),
        "abyss_service" => Localization.T("Service contract node"),
        "abyss_repair" => Localization.T("Repair hull"),
        "abyss_dock" => Localization.T("Dock / undock"),
        "abyss_drill" => Localization.T("Drill (hold)"),
        "abyss_winch" => Localization.T("Emergency winch"),
        "abyss_buoy" => Localization.T("Fire emergency buoy"),
        "abyss_decoy" => Localization.T("Launch acoustic decoy"),
        "abyss_emp" => Localization.T("Fire EMP coil"),
        "abyss_extract" => Localization.T("Extract"),
        "abyss_consumable_1" => Localization.T("Consumable slot {0}", 1),
        "abyss_consumable_2" => Localization.T("Consumable slot {0}", 2),
        "abyss_consumable_3" => Localization.T("Consumable slot {0}", 3),
        "abyss_consumable_4" => Localization.T("Consumable slot {0}", 4),
        "abyss_consumable_5" => Localization.T("Consumable slot {0}", 5),
        "abyss_consumable_6" => Localization.T("Consumable slot {0}", 6),
        "abyss_consumable_7" => Localization.T("Consumable slot {0}", 7),
        "abyss_consumable_8" => Localization.T("Consumable slot {0}", 8),
        _ => actionId,
    };

    public static string GroupLabel(InputActionGroup group) => group switch
    {
        InputActionGroup.Movement => Localization.T("Movement"),
        InputActionGroup.Verbs => Localization.T("Sonar & tools"),
        _ => Localization.T("Consumables"),
    };

    /// <summary>Bottom-of-screen controls line built from the live bindings.</summary>
    public static string ControlsHint()
    {
        string K(string id) => ActionKeyLabel(id);
        var consumables = new List<string>();
        for (var i = 1; i <= 8; i++)
        {
            consumables.Add(K("abyss_consumable_" + i));
        }

        var slots = string.Join("", consumables) == "12345678" ? "1-8" : string.Join("/", consumables);
        var parts = new[]
        {
            Localization.T("{0}/{1} surge", K("abyss_fwd"), K("abyss_back")),
            Localization.T("{0}/{1} sway", K("abyss_left"), K("abyss_right")),
            Localization.T("{0}/{1} heave", K("abyss_up"), K("abyss_down")),
            Localization.T("{0}/{1} yaw", K("abyss_yaw_left"), K("abyss_yaw_right")),
            Localization.T("{0}/{1} pitch", K("abyss_pitch_up"), K("abyss_pitch_down")),
            Localization.T("{0} boost", (object)K("abyss_boost")),
            Localization.T("{0} quiet", (object)K("abyss_silent")),
            Localization.T("{0} ping", (object)K("abyss_ping")),
            Localization.T("{0} salvage", (object)K("abyss_interact")),
            Localization.T("{0} survey", (object)K("abyss_survey")),
            Localization.T("{0} service", (object)K("abyss_service")),
            Localization.T("{0} repair", (object)K("abyss_repair")),
            Localization.T("{0} dock/undock", (object)K("abyss_dock")),
            Localization.T("{0} drill hold", (object)K("abyss_drill")),
            Localization.T("{0} winch", (object)K("abyss_winch")),
            Localization.T("{0} buoy", (object)K("abyss_buoy")),
            Localization.T("{0} decoy", (object)K("abyss_decoy")),
            Localization.T("{0} EMP", (object)K("abyss_emp")),
            Localization.T("{0} consumables", (object)slots),
            Localization.T("{0} extract", (object)K("abyss_extract")),
            Localization.T("Esc pause"),
        };
        return string.Join(" · ", parts);
    }
}
