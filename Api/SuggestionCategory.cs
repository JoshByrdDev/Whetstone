using System.Text.RegularExpressions;

namespace Whetstone.Api;

/// <summary>
/// The tags an improvement suggestion can carry, and the one regex that reads them.
///
/// There are five because the vocabulary changed once and the old items did not. The reflection
/// writes <c>environment</c>/<c>codebase</c>/<c>process</c> (worker.ps1's
/// <c>$ReflectOutputContract</c>); <c>lesson</c>/<c>system</c> are the older pair, still attached to
/// anything filed before that. They route to two destinations, not five - see <see cref="IsFact"/>.
///
/// This lives in one place because it did not, and drifted: the worker started writing the new tags
/// while the parse regex, the category endpoint and the apply prompt all still said
/// <c>lesson|system</c>. A filed <c>[environment]</c> item then rendered with the tag stuck inside
/// its text and silently reported itself as a lesson, which is the wrong destination.
/// </summary>
public static class SuggestionCategory
{
    /// <summary>Every valid tag, in the order the UI offers them: facts first, then pipeline changes.</summary>
    public static readonly string[] All =
        ["environment", "codebase", "process", "lesson", "system"];

    /// <summary>What an item with no tag at all means. Untagged items predate tagging entirely.</summary>
    public const string Default = "lesson";

    public static readonly string Alternation = string.Join('|', All);

    public static bool IsValid(string? category) =>
        category is not null && Array.IndexOf(All, category) >= 0;

    /// <summary>
    /// True when the tag denotes a FACT about the repo or machine (destination: the project's own
    /// notes store), false when it denotes a change to how the pipeline behaves (destination: a
    /// dashboard-owned skill or prompt). The test behind the split is whether a human developer, or
    /// an interactive session in the repo, would also be wrong without it.
    /// </summary>
    public static bool IsFact(string? category) =>
        (category ?? Default) is "environment" or "codebase" or "lesson";

    /// <summary>
    /// Matches one checklist line. Group 1 = the checkbox state, group 2 = the tag (may be empty),
    /// group 3 = the text. Deliberately anchored so a tag the list does not know stays part of the
    /// text rather than being silently accepted as a category.
    /// </summary>
    public static readonly Regex CheckboxPattern =
        new($@"^-\s*\[([ x])\]\s*(?:\[({Alternation})\]\s*)?(.+)$", RegexOptions.Compiled);

    /// <summary>Rewrites (or inserts) the tag on a line, preserving the checkbox state.</summary>
    public static string WithCategory(string line, string category) =>
        Regex.Replace(line, $@"(\-\s*\[[ x]\])\s*(?:\[(?:{Alternation})\]\s*)?", $"$1 [{category}] ");

    /// <summary>
    /// The trailing <c>(key: some-key, 2026-08-26 15:16)</c> the reflection stamps onto every item.
    /// The <c>###</c> date headings this file used to carry are gone, so this is the only record of
    /// when an item was filed.
    /// </summary>
    private static readonly Regex FiledStampPattern =
        new(@"\(key:\s*[^,)]+,\s*(\d{4}-\d{2}-\d{2}(?:[ T]\d{2}:\d{2})?)\s*\)\s*$", RegexOptions.Compiled);

    /// <summary>
    /// When an item was filed, or null if it predates stamping. Exposed so the queue can show its
    /// own age: the list has sat at ~50 unactioned items before, and an item nobody has actioned in
    /// two months is telling you something an undated list cannot.
    /// </summary>
    public static DateTime? FiledAt(string text)
    {
        var m = FiledStampPattern.Match(text);
        if (!m.Success) return null;
        return DateTime.TryParse(m.Groups[1].Value, out var when) ? when : null;
    }
}
