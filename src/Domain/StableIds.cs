using System.Text.RegularExpressions;

namespace AbyssScav.Domain;

/// <summary>
/// Stable-ID rules shared by every content kind in the domain catalog
/// (docs/16 §10: lower snake/dot namespace, stable after public lock).
/// <para>
/// INTEGRATION: presentation and persistence layers must store and compare these
/// IDs as opaque strings. Never rename an ID after release; add a migration
/// alias handled outside this project instead.
/// </para>
/// </summary>
public static class StableIds
{
    private static readonly Regex Pattern = new(
        "^[a-z0-9]+([._-][a-z0-9]+)*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Maximum stored length for any stable ID (matches persistence bounds).</summary>
    public const int MaxLength = 128;

    /// <summary>Returns true when <paramref name="id"/> is a well-formed stable ID.</summary>
    public static bool IsValid(string? id) =>
        !string.IsNullOrWhiteSpace(id) &&
        id!.Length <= MaxLength &&
        Pattern.IsMatch(id);
}
