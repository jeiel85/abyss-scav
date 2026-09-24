using System.Text.RegularExpressions;

namespace AbyssScav.Foundation;

/// <summary>
/// Rewrites default key letters inside already-localized hint text ("press J",
/// "drill (H)", "도킹(J)") to the player's current key labels, so on-screen hints
/// never lie after a rebind. Engine-independent and single-pass: a swap
/// (F&lt;-&gt;E) rewrites both tokens at once instead of chaining.
///
/// A token is a lone uppercase ASCII letter: not touching another ASCII
/// letter/digit, '_' or '-' (so "F0" format specs, "D-pad", ids like "zone_A"
/// are never hit), and not preceded by "pad " / "패드 " (gamepad button names
/// such as "pad B" are not keyboard keys). Only letters present in the map are
/// replaced; callers pass verbs whose default key is a letter, which keeps
/// English articles ("A") and pad buttons out of scope.
/// Apply exactly once per displayed string: rewriting output again would undo a swap.
/// </summary>
public static class KeyHintRewriter
{
    private static readonly Regex Token = new(
        @"(?<![A-Za-z0-9_\-])(?<![Pp]ad )(?<!패드 )([A-Z])(?![A-Za-z0-9_\-])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <param name="text">Localized text; null/empty is returned unchanged.</param>
    /// <param name="labelMap">Default key label (single uppercase letter) to current label; entries mapping to themselves are ignored.</param>
    public static string Rewrite(string? text, IReadOnlyDictionary<string, string>? labelMap)
    {
        if (string.IsNullOrEmpty(text) || labelMap is null || labelMap.Count == 0)
        {
            return text ?? string.Empty;
        }

        return Token.Replace(text, m =>
            labelMap.TryGetValue(m.Groups[1].Value, out var label) && !string.IsNullOrEmpty(label)
                ? label
                : m.Value);
    }
}
