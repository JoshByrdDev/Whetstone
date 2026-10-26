using System.Text;
using Whetstone.Models;

namespace Whetstone.Services;

/// <summary>
/// The first draft of a PR description.
///
/// <para>
/// It is the branch's commit message, and that is the whole idea. This pipeline already has a
/// house style for describing a change to other people — one ticket, one commit, and refinement
/// writes that message deliberately: a lead paragraph saying what the change delivers, then
/// bullets for the specifics, in the reviewer's language rather than the ticket's. A second
/// description assembled from the ticket's own record cannot compete with it. Composing the work
/// items into a list looked structured but read as a transcript: every item verbatim, in the
/// order a worker happened to tick them, at whatever length they were typed. The commit message
/// is the version somebody edited.
/// </para>
/// <para>
/// So the only judgement here is which source to reach for, and that is the reason this is a
/// class rather than a string concatenation at the call site.
/// </para>
/// <list type="number">
/// <item>The single commit's body — present once refinement has squashed the branch, which is the
/// state a branch is in by the time anyone raises a PR from it.</item>
/// <item>The implementation summary — prose, for a branch raised before refinement has run.</item>
/// <item>The ticket's description — the original ask, for a branch raised before anything was
/// implemented at all.</item>
/// </list>
/// <para>
/// Nothing is stapled on beneath it. Not the QA record, which is this pipeline's account of its
/// own internal testing and no part of what a reviewer is being asked to weigh; not the commit
/// list or the file list, which the host's own PR page already shows; and not this dashboard's
/// ticket id, which is a row number in a local database and not ours to publish on someone else's
/// repo. The issue reference is the one identifier that travels, so it is the one thing in the footer.
/// </para>
/// <para>
/// One exception, for a project of several repos: when the same change is also in other repos, the
/// footer names them and the branch they share. A reviewer of one PR has no other way to learn the
/// others exist, and the repo names and the branch are already on the host.
/// </para>
/// </summary>
public static class PrDescription
{
    /// <summary>
    /// Compose the draft description.
    /// </summary>
    /// <param name="commitBody">
    /// <see cref="PrPreflight.CommitBody"/> — the branch's one commit message, minus its subject
    /// line, which the PR title already carries.
    /// </param>
    /// <param name="description">
    /// The ticket's EFFECTIVE description — resolved from the tracker for a linked ticket, which
    /// stores none of its own. Reading <see cref="Ticket.Description"/> here instead would leave a
    /// linked ticket with a blank draft.
    /// </param>
    /// <param name="issueRef">
    /// How this project's tracker wants its issue cited — <c>PROJ-123</c>, or <c>AB#1234</c> on
    /// Azure DevOps, where that exact form is what makes the pull request link back to the work
    /// item. Null falls back to the raw key.
    /// </param>
    /// <param name="alsoIn">
    /// The other repos this ticket's change is in, on a project of several. Null or empty for a
    /// change in one repo, which reads exactly as it always has.
    /// </param>
    public static string Compose(Ticket t, string? commitBody = null, string? description = null,
        string? issueRef = null, IReadOnlyList<string>? alsoIn = null)
    {
        var lead = Normalize(commitBody)
                   ?? Normalize(t.ImplementationSummary)
                   ?? Normalize(description);

        var sb = new StringBuilder();
        if (lead is not null) sb.Append(lead).Append("\n\n");

        var parts = new[] { Siblings(t, alsoIn), Footer(t, issueRef) }.Where(p => p is not null).ToList();
        if (parts.Count > 0) sb.Append("---\n\n").Append(string.Join("\n\n", parts)).Append('\n');
        return sb.ToString();
    }

    /// <summary>The other repos the same change is in, and the branch they share; null when there are none.</summary>
    private static string? Siblings(Ticket t, IReadOnlyList<string>? alsoIn)
    {
        if (alsoIn is not { Count: > 0 }) return null;
        var branch = t.Branch?.Trim();
        var on = string.IsNullOrEmpty(branch) ? "" : $", on branch `{branch}` in each";
        return $"Part of one change across several repos{on}. Also in: {string.Join(", ", alsoIn)}.";
    }

    /// <summary>
    /// Where this came from — the linked issue, and nothing else. Null when the ticket has no link,
    /// in which case there is no separator either: a footer saying "Ticket #28" would be this
    /// dashboard's own row id, which identifies nothing outside this machine.
    /// </summary>
    private static string? Footer(Ticket t, string? issueRef)
    {
        var key = t.IssueKey?.Trim();
        if (string.IsNullOrEmpty(key)) return null;
        var label = string.IsNullOrWhiteSpace(issueRef) ? key : issueRef.Trim();
        var url = t.IssueUrl?.Trim();
        return string.IsNullOrEmpty(url) ? label : $"[{label}]({url})";
    }

    /// <summary>Trim, drop CR, and cap runs of blank lines at one. Null for nothing worth printing.</summary>
    private static string? Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var text = s.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        while (text.Contains("\n\n\n")) text = text.Replace("\n\n\n", "\n\n");
        return text.Length == 0 ? null : text;
    }
}
