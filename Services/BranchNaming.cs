using System.Globalization;
using System.Text;
using System.Text.Json;
using Whetstone.Models;

namespace Whetstone.Services;

/// <summary>
/// The name a ticket's branch gets when nobody chose one. Written once, here: the board works it out
/// and hands it to the worker on the claim (<c>branchName</c>), which takes it as it is
/// (<c>Get-TicketBranchName</c> in data/worker.ps1) - so the name a person is shown at filing and
/// the name the first run creates can never be two answers to one question.
/// <para>
/// The prefix (the project's <c>branchPrefix</c>, else the global one), then the linked issue's
/// branch token, or <c>ticket-&lt;id&gt;</c> for a ticket with no issue. With the project's
/// <c>branchNameStyle</c> at <c>key-slug</c>, a short slug of the ticket's title follows it:
/// <c>jb/4334-open-the-signing-link</c>, which a teammate reviewing the pull request can read.
/// </para>
/// <para>
/// It is only ever a default. A name recorded on the ticket (<see cref="Ticket.Branch"/>) - typed at
/// filing, or the branch a run made - always wins, so a title edited later never renames anything.
/// </para>
/// </summary>
public static class BranchNaming
{
    /// <summary>The issue key alone: <c>jb/4334</c>, <c>jb/ticket-101</c>. The default.</summary>
    public const string KeyStyle = "key";

    /// <summary>The issue key and a slug of the title: <c>jb/4334-open-the-signing-link</c>.</summary>
    public const string KeySlugStyle = "key-slug";

    /// <summary>How long a slug may be, before the key and the prefix.</summary>
    public const int SlugMax = 40;

    /// <summary>The style a project's setting names, with anything unknown read as <see cref="KeyStyle"/>.</summary>
    public static string NormalizeStyle(string? style) =>
        string.Equals(style?.Trim(), KeySlugStyle, StringComparison.OrdinalIgnoreCase) ? KeySlugStyle : KeyStyle;

    /// <summary>
    /// The prefix a project's branches take: its own <c>branchPrefix</c>, else the global one. The
    /// worker's <c>$effectivePrefix</c> is the same rule, and is only reached by a claim from a board
    /// that does not send <c>branchName</c>.
    /// </summary>
    public static string PrefixFor(ProjectConfig cfg, IReadOnlyDictionary<string, JsonElement> global)
    {
        var own = cfg.BranchPrefix?.Trim() ?? "";
        if (own.Length > 0) return own;
        return global.TryGetValue("branchPrefix", out var g) && g.ValueKind == JsonValueKind.String
            ? g.GetString()?.Trim() ?? "" : "";
    }

    /// <summary>
    /// The default name. <paramref name="issueToken"/> is the tracker's branch token for the linked
    /// issue, empty for an unlinked ticket; <paramref name="title"/> is the title as the board shows it
    /// (the linked issue's, resolved), or null when it could not be read - which leaves the slug off
    /// rather than slugging a key that stood in for the title.
    /// </summary>
    public static string Default(string prefix, string? style, int ticketId, string? issueToken, string? title)
    {
        var token = issueToken?.Trim() ?? "";
        var key = token.Length > 0 ? token : $"ticket-{ticketId}";
        var name = $"{prefix}{key}";
        if (NormalizeStyle(style) != KeySlugStyle) return name;
        var slug = Slug(title);
        // A slug that only repeats the key ("4334" for an issue titled "4334") says nothing.
        if (slug.Length == 0 || string.Equals(slug, key, StringComparison.OrdinalIgnoreCase)) return name;
        var withSlug = $"{name}-{slug}";
        return TicketGitService.IsBranchName(withSlug) ? withSlug : name;
    }

    /// <summary>
    /// A title as a branch can carry it: lowercase ASCII letters, digits and single hyphens, accents
    /// folded (<c>é</c> to <c>e</c>), at most <paramref name="max"/> characters, cut at a word where
    /// one is near, and never starting or ending with a hyphen. Empty when nothing is left.
    /// </summary>
    public static string Slug(string? title, int max = SlugMax)
    {
        if (string.IsNullOrWhiteSpace(title) || max <= 0) return "";
        var folded = title.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(folded.Length);
        var dash = false;
        foreach (var ch in folded)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            var c = char.ToLowerInvariant(ch);
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                if (dash && sb.Length > 0) sb.Append('-');
                sb.Append(c);
                dash = false;
            }
            // An apostrophe joins rather than splits: "don't" is "dont", not "don-t".
            else if (c is '\'' or '’') { }
            else dash = true;
        }
        var slug = sb.ToString();
        if (slug.Length <= max) return slug;

        // Cut at the last word boundary inside the limit, unless that throws most of it away.
        var cut = slug[..max];
        var lastDash = cut.LastIndexOf('-');
        if (slug[max] != '-' && lastDash >= max / 2) cut = cut[..lastDash];
        return cut.Trim('-');
    }
}
