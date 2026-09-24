namespace AbyssScav.Foundation;

/// <summary>Rebinding-UI grouping for a keyboard action.</summary>
public enum InputActionGroup
{
    Movement,
    Verbs,
    Consumables,
}

/// <summary>
/// One remappable keyboard action. <see cref="DefaultKey"/> is a Godot <c>Key</c>
/// enum name (physical US-layout position). <see cref="HintToken"/> marks verbs
/// whose default key letter appears in on-screen hint text and may be rewritten
/// by <see cref="KeyHintRewriter"/> when the binding differs.
/// </summary>
public sealed record InputActionDef(string Id, string DefaultKey, InputActionGroup Group, bool HintToken);

/// <summary>Result kind of <see cref="KeyBindingCatalog.Rebind"/>.</summary>
public enum RebindOutcome
{
    /// <summary>The action already used that key; nothing changed.</summary>
    Unchanged,
    /// <summary>The key was free and is now bound to the action.</summary>
    Bound,
    /// <summary>The key belonged to another action, which received this action's previous key.</summary>
    Swapped,
    UnknownAction,
    /// <summary>The key is not in the bindable whitelist.</summary>
    KeyNotAllowed,
    /// <summary>The key is reserved (Escape: pause and cancel-capture).</summary>
    KeyReserved,
}

public sealed record RebindResult(
    RebindOutcome Outcome,
    IReadOnlyDictionary<string, string> Bindings,
    string? SwappedActionId,
    string? SwappedToKey)
{
    public bool Success => Outcome is RebindOutcome.Unchanged or RebindOutcome.Bound or RebindOutcome.Swapped;
}

/// <summary>
/// Engine-independent keyboard binding model (docs/07 §6 "full key rebinding").
/// Bindings are a map action id -&gt; Godot <c>Key</c> enum name, one key per
/// action, never duplicated. Pause stays on Escape (reserved: it also cancels a
/// rebind capture) and gamepad bindings are not part of this map.
/// Every map that leaves this class is complete, whitelisted, and duplicate-free,
/// so a hand-edited settings file can never produce an unusable control scheme.
/// </summary>
public static class KeyBindingCatalog
{
    /// <summary>Reserved for pause and for cancelling a rebind capture.</summary>
    public const string ReservedKey = "Escape";

    public static readonly IReadOnlyList<InputActionDef> Actions = new InputActionDef[]
    {
        new("abyss_fwd", "W", InputActionGroup.Movement, false),
        new("abyss_back", "S", InputActionGroup.Movement, false),
        new("abyss_left", "A", InputActionGroup.Movement, false),
        new("abyss_right", "D", InputActionGroup.Movement, false),
        new("abyss_up", "Space", InputActionGroup.Movement, false),
        new("abyss_down", "Ctrl", InputActionGroup.Movement, false),
        new("abyss_yaw_left", "Left", InputActionGroup.Movement, false),
        new("abyss_yaw_right", "Right", InputActionGroup.Movement, false),
        new("abyss_pitch_up", "Up", InputActionGroup.Movement, false),
        new("abyss_pitch_down", "Down", InputActionGroup.Movement, false),
        new("abyss_boost", "Shift", InputActionGroup.Movement, false),
        new("abyss_silent", "Z", InputActionGroup.Movement, true),
        new("abyss_ping", "F", InputActionGroup.Verbs, true),
        new("abyss_interact", "E", InputActionGroup.Verbs, true),
        new("abyss_survey", "V", InputActionGroup.Verbs, true),
        new("abyss_service", "G", InputActionGroup.Verbs, true),
        new("abyss_repair", "R", InputActionGroup.Verbs, true),
        new("abyss_dock", "J", InputActionGroup.Verbs, true),
        new("abyss_drill", "H", InputActionGroup.Verbs, true),
        new("abyss_winch", "X", InputActionGroup.Verbs, true),
        new("abyss_buoy", "B", InputActionGroup.Verbs, true),
        new("abyss_decoy", "N", InputActionGroup.Verbs, true),
        new("abyss_emp", "M", InputActionGroup.Verbs, true),
        new("abyss_extract", "T", InputActionGroup.Verbs, true),
        new("abyss_consumable_1", "Key1", InputActionGroup.Consumables, false),
        new("abyss_consumable_2", "Key2", InputActionGroup.Consumables, false),
        new("abyss_consumable_3", "Key3", InputActionGroup.Consumables, false),
        new("abyss_consumable_4", "Key4", InputActionGroup.Consumables, false),
        new("abyss_consumable_5", "Key5", InputActionGroup.Consumables, false),
        new("abyss_consumable_6", "Key6", InputActionGroup.Consumables, false),
        new("abyss_consumable_7", "Key7", InputActionGroup.Consumables, false),
        new("abyss_consumable_8", "Key8", InputActionGroup.Consumables, false),
    };

    /// <summary>
    /// Bindable keys (Godot <c>Key</c> enum names). Excludes Escape (reserved),
    /// OS/system keys (Meta, Menu, Print, lock toggles) and media keys.
    /// Order matters: it is the deterministic fallback order used by <see cref="Normalize"/>.
    /// </summary>
    public static readonly IReadOnlyList<string> AllowedKeys = BuildAllowedKeys();

    private static readonly Dictionary<string, string> CanonicalByName =
        AllowedKeys.ToDictionary(k => k, k => k, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, InputActionDef> ById =
        Actions.ToDictionary(a => a.Id, a => a, StringComparer.Ordinal);

    public static bool IsKnownAction(string? actionId) => actionId is not null && ById.ContainsKey(actionId);

    public static InputActionDef? Find(string actionId) => ById.TryGetValue(actionId, out var def) ? def : null;

    /// <summary>Canonical casing for an allowed key name; false for unknown, reserved, or blank names.</summary>
    public static bool TryCanonicalKey(string? raw, out string canonical)
    {
        canonical = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        if (CanonicalByName.TryGetValue(raw.Trim(), out var hit))
        {
            canonical = hit;
            return true;
        }

        return false;
    }

    public static bool IsReserved(string? raw) =>
        raw is not null && string.Equals(raw.Trim(), ReservedKey, StringComparison.OrdinalIgnoreCase);

    /// <summary>Fresh default map in canonical action order.</summary>
    public static IReadOnlyDictionary<string, string> Defaults()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var a in Actions)
        {
            map[a.Id] = a.DefaultKey;
        }

        return map;
    }

    /// <summary>
    /// Complete, whitelisted, duplicate-free map. Unknown actions are dropped;
    /// missing or invalid keys fall back to the action default. On a duplicate
    /// the earlier action in canonical order keeps the key and the later action
    /// takes its default, or (if that is taken too) the first free allowed key.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Normalize(IReadOnlyDictionary<string, string>? raw)
    {
        var chosen = new Dictionary<string, string>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);

        // Pass 1: explicit valid choices, first come first served in canonical order.
        var pending = new List<InputActionDef>();
        foreach (var a in Actions)
        {
            if (raw is not null && raw.TryGetValue(a.Id, out var rawKey) && TryCanonicalKey(rawKey, out var key) && used.Add(key))
            {
                chosen[a.Id] = key;
            }
            else
            {
                pending.Add(a);
            }
        }

        // Pass 2: missing / invalid / duplicate entries take their default when it is still free.
        var unresolved = new List<InputActionDef>();
        foreach (var a in pending)
        {
            if (used.Add(a.DefaultKey))
            {
                chosen[a.Id] = a.DefaultKey;
            }
            else
            {
                unresolved.Add(a);
            }
        }

        // Pass 3: guaranteed-unique fallback (AllowedKeys is far larger than Actions).
        foreach (var a in unresolved)
        {
            foreach (var key in AllowedKeys)
            {
                if (used.Add(key))
                {
                    chosen[a.Id] = key;
                    break;
                }
            }
        }

        var ordered = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var a in Actions)
        {
            ordered[a.Id] = chosen[a.Id];
        }

        return ordered;
    }

    /// <summary>
    /// Press-to-rebind with swap-on-conflict: if another action holds the key it
    /// receives this action's previous key, so the map stays duplicate-free and
    /// no action is ever left unbound.
    /// </summary>
    public static RebindResult Rebind(IReadOnlyDictionary<string, string>? current, string actionId, string? key)
    {
        var map = new Dictionary<string, string>(Normalize(current), StringComparer.Ordinal);
        if (!IsKnownAction(actionId))
        {
            return new RebindResult(RebindOutcome.UnknownAction, map, null, null);
        }

        if (IsReserved(key))
        {
            return new RebindResult(RebindOutcome.KeyReserved, map, null, null);
        }

        if (!TryCanonicalKey(key, out var canonical))
        {
            return new RebindResult(RebindOutcome.KeyNotAllowed, map, null, null);
        }

        var previous = map[actionId];
        if (previous == canonical)
        {
            return new RebindResult(RebindOutcome.Unchanged, map, null, null);
        }

        string? holder = null;
        foreach (var kv in map)
        {
            if (kv.Key != actionId && kv.Value == canonical)
            {
                holder = kv.Key;
                break;
            }
        }

        map[actionId] = canonical;
        if (holder is null)
        {
            return new RebindResult(RebindOutcome.Bound, map, null, null);
        }

        map[holder] = previous;
        return new RebindResult(RebindOutcome.Swapped, map, holder, previous);
    }

    /// <summary>US-layout display label for a key name ("Key1" -&gt; "1", "Bracketleft" -&gt; "[").</summary>
    public static string DisplayLabel(string keyName)
    {
        if (string.IsNullOrEmpty(keyName))
        {
            return "?";
        }

        if (keyName.Length == 1)
        {
            return keyName.ToUpperInvariant();
        }

        if (keyName.Length == 4 && keyName.StartsWith("Key", StringComparison.Ordinal) && char.IsDigit(keyName[3]))
        {
            return keyName[3].ToString();
        }

        if (keyName.Length == 3 && keyName.StartsWith("Kp", StringComparison.Ordinal) && char.IsDigit(keyName[2]))
        {
            return "Num " + keyName[2];
        }

        return keyName switch
        {
            "Escape" => "Esc",
            "Pageup" => "PgUp",
            "Pagedown" => "PgDn",
            "Insert" => "Ins",
            "Delete" => "Del",
            "Backspace" => "Bksp",
            "KpEnter" => "Num Enter",
            "KpAdd" => "Num +",
            "KpSubtract" => "Num -",
            "KpMultiply" => "Num *",
            "KpDivide" => "Num /",
            "KpPeriod" => "Num .",
            "Comma" => ",",
            "Period" => ".",
            "Slash" => "/",
            "Semicolon" => ";",
            "Apostrophe" => "'",
            "Bracketleft" => "[",
            "Bracketright" => "]",
            "Backslash" => "\\",
            "Minus" => "-",
            "Equal" => "=",
            "Quoteleft" => "`",
            _ => keyName,
        };
    }

    private static IReadOnlyList<string> BuildAllowedKeys()
    {
        var keys = new List<string>();
        for (var c = 'A'; c <= 'Z'; c++)
        {
            keys.Add(c.ToString());
        }

        for (var d = 0; d <= 9; d++)
        {
            keys.Add("Key" + d);
        }

        keys.AddRange(new[]
        {
            "Space", "Shift", "Ctrl", "Alt", "Tab", "Enter", "Backspace",
            "Left", "Right", "Up", "Down",
            "Insert", "Delete", "Home", "End", "Pageup", "Pagedown",
            "Comma", "Period", "Slash", "Semicolon", "Apostrophe",
            "Bracketleft", "Bracketright", "Backslash", "Minus", "Equal", "Quoteleft",
        });

        for (var f = 1; f <= 12; f++)
        {
            keys.Add("F" + f);
        }

        for (var d = 0; d <= 9; d++)
        {
            keys.Add("Kp" + d);
        }

        keys.AddRange(new[] { "KpEnter", "KpAdd", "KpSubtract", "KpMultiply", "KpDivide", "KpPeriod" });
        return keys;
    }
}
