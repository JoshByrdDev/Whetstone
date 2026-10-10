using System.Text;
using Whetstone.Models;

namespace Whetstone.Services;

/// <summary>
/// The staged review, rendered as one block of markdown to paste into a pull request.
/// <para>
/// This is how v1 sends feedback, and it is deliberately the whole feature rather than a stopgap
/// dressed as one. Everything expensive about reviewing someone else's branch — reading the diff,
/// standing the app up, driving it, working out which of forty files is the one that matters — has
/// already happened by the time this is called. What is left is a paste, and a paste needs no
/// credentials, writes to nobody's repository, and cannot post the wrong thing to the wrong place.
/// </para>
/// <para>
/// It stays useful after an API client lands: a reviewer who wants to reword something in the host's
/// own editor should not have to fight the tool for the text.
/// </para>
/// </summary>
public static class ReviewMarkdown
{
    /// <summary>Most serious first — the order a reader should meet the findings in.</summary>
    private static readonly (ReviewSeverity Severity, string Heading)[] Sections =
    {
        (ReviewSeverity.Blocking, "Blocking"),
        (ReviewSeverity.Question, "Questions"),
        (ReviewSeverity.Nit,      "Nits"),
        (ReviewSeverity.Praise,   "Worth calling out"),
    };

    private static string VerdictLine(ReviewVerdict v) => v switch
    {
        ReviewVerdict.Approve => "Approve",
        ReviewVerdict.ApproveWithComments => "Approve with comments",
        ReviewVerdict.RequestChanges => "Request changes",
        ReviewVerdict.CannotTell => "Cannot tell — see below",
        _ => v.ToString(),
    };

    public static string Compose(Ticket t, string? effectiveTitle = null)
    {
        var sb = new StringBuilder();
        var target = t.ReviewTarget;

        var title = effectiveTitle?.Trim();
        if (string.IsNullOrWhiteSpace(title)) title = t.Title?.Trim();

        // An unlinked review ticket names itself "Review: <branch>" when it is created, which under
        // this heading would read "Review - Review: ...". Strip our own prefix rather than change
        // the ticket's name: the name is right on the board, and it is only wrong here.
        if (title is not null && title.StartsWith("Review:", StringComparison.OrdinalIgnoreCase))
            title = title["Review:".Length..].Trim();

        sb.AppendLine(string.IsNullOrWhiteSpace(title) ? "## Review" : $"## Review — {title}");
        sb.AppendLine();

        if (target?.Verdict is { } verdict)
        {
            sb.AppendLine($"**{VerdictLine(verdict)}**");
            sb.AppendLine();
        }

        // Dismissed comments never leave this machine: the owner has already decided against them,
        // and re-surfacing one here would undo that decision at the last possible moment.
        var live = t.ReviewComments
            .Where(c => c.Status != ReviewCommentStatus.Dismissed)
            .OrderBy(c => c.Order).ThenBy(c => c.Id)
            .ToList();

        foreach (var (severity, heading) in Sections)
        {
            var group = live.Where(c => c.Severity == severity).ToList();
            if (group.Count == 0) continue;

            sb.AppendLine($"### {heading}");
            sb.AppendLine();
            foreach (var c in group)
            {
                var where = Where(c);
                // The location leads and the body follows on the same bullet, so a long review still
                // scans as a list of places rather than a list of paragraphs.
                sb.AppendLine(where is null ? $"- {Inline(c.Body)}" : $"- **{where}** — {Inline(c.Body)}");
            }
            sb.AppendLine();
        }

        if (live.Count == 0)
            sb.AppendLine("No line-level comments.").AppendLine();

        AppendTesting(sb, t);

        // What was actually read. Without it the author has to guess whether this describes the
        // commit they are looking at, and a review of a superseded commit is worse than none.
        if (target?.ReviewedAtSha is { Length: > 0 } sha)
        {
            sb.AppendLine("---");
            sb.AppendLine();
            var shortSha = sha.Length > 12 ? sha[..12] : sha;
            sb.AppendLine($"*Reviewed at `{shortSha}`"
                        + (string.IsNullOrWhiteSpace(target.SourceBranch) ? "" : $" on `{target.SourceBranch}`")
                        + ".*");
        }

        return sb.ToString().TrimEnd() + "\n";
    }

    /// <summary>
    /// What the review actually exercised. A list of assertions that were driven and what each one
    /// did is the part of a review an author can act on without argument — and a failing one with a
    /// reproduction beats any amount of prose about the diff.
    /// </summary>
    private static void AppendTesting(StringBuilder sb, Ticket t)
    {
        var items = t.QaItems.OrderBy(q => q.Order).ToList();
        if (items.Count == 0) return;

        var passed = items.Count(q => q.Status == QaStatus.Passed);
        var failed = items.Where(q => q.Status == QaStatus.Failed).ToList();
        var blocked = items.Where(q => q.Status == QaStatus.Blocked).ToList();

        sb.AppendLine("### What I tested");
        sb.AppendLine();
        sb.AppendLine($"{passed} of {items.Count} checks passed.");
        sb.AppendLine();

        foreach (var q in failed)
            sb.AppendLine($"- ❌ {Inline(q.Text)}"
                        + (string.IsNullOrWhiteSpace(q.Note) ? "" : $" — {Inline(q.Note)}"));
        foreach (var q in blocked)
            sb.AppendLine($"- ⚠️ {Inline(q.Text)} — could not test"
                        + (string.IsNullOrWhiteSpace(q.Note) ? "" : $": {Inline(q.Note)}"));

        if (failed.Count > 0 || blocked.Count > 0) sb.AppendLine();
    }

    /// <summary>`path/to/File.cs:42`, `path/to/File.cs`, or null for a comment about the whole change.</summary>
    private static string? Where(ReviewComment c)
    {
        if (string.IsNullOrWhiteSpace(c.FilePath)) return null;
        return c.Line is { } line ? $"`{c.FilePath}:{line}`" : $"`{c.FilePath}`";
    }

    /// <summary>
    /// Flatten to a single line. A staged comment may hold newlines — an agent writing a paragraph
    /// with a code block in it is normal — and a raw newline inside a markdown list item silently
    /// ends the item, dropping everything after it out of the bullet it belongs to.
    /// </summary>
    private static string Inline(string s) =>
        string.Join(" ", (s ?? "").Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0));
}
