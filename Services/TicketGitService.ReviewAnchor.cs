namespace Whetstone.Services;

/// <summary>
/// Whether a staged review comment's file and line exist in the commit it is anchored to.
/// <para>
/// A comment is a claim about exact line numbers at one commit (<see cref="Models.ReviewComment.AnchorSha"/>),
/// and a run supplies the line itself. Nothing used to hold it to the file, so a run could stage a
/// Blocking finding on line 706 of a 62-line file, and the owner was left to work out where it
/// meant before sending it to the author. Checked against the commit, not the workspace, because
/// that commit is the one the pull request shows.
/// </para>
/// </summary>
public partial class TicketGitService
{
    /// <summary>
    /// Why <paramref name="line"/> of <paramref name="filePath"/> cannot be anchored at
    /// <paramref name="sha"/> in the review's repo, or null when it can - or when this machine
    /// cannot tell (no anchor, no repo, a commit not fetched here). Not knowing is not a refusal:
    /// the anchor and the staleness flag still date the comment, as they always have.
    /// <para>
    /// Only the head side is checked. A <see cref="Models.DiffSide.Base"/> comment is about the
    /// base's copy of the file, which the anchor commit does not hold.
    /// </para>
    /// </summary>
    public async Task<string?> ReviewAnchorProblemAsync(string projectKey, string? repo, string? sha,
        string? filePath, int? line, Models.DiffSide side, CancellationToken ct = default)
    {
        if (line is null) return null;
        if (line < 1) return $"Line {line} is not a line: lines are numbered from 1.";
        if (string.IsNullOrWhiteSpace(filePath))
            return "A line needs a file. Name the file it is in, or leave the line out for a comment on the whole change.";
        if (side != Models.DiffSide.Head || string.IsNullOrWhiteSpace(sha)) return null;

        var cfg = _registry.GetProjectConfig(projectKey);
        if (TargetOf(cfg, repo, out _) is not { } target || !IsGitRepo(target.Path)) return null;
        var commit = sha.Trim();
        if ((await GitAsync(target.Path, ct, "cat-file", "-e", $"{commit}^{{commit}}")).ExitCode != 0) return null;

        var path = filePath.Trim().Replace('\\', '/');
        var at = Models.QaCommits.Short(commit);
        var blob = await GitAsync(target.Path, ct, "cat-file", "-p", $"{commit}:{path}");
        if (!blob.Ok)
            return $"'{path}' is not in the branch at {at}. Give the path relative to the repo under review, "
                 + "as the pull request shows it - or use side 'Base' for a file the change removed.";

        var lines = LineCount(blob.Stdout);
        return line > lines
            ? $"'{path}' has {lines} line{(lines == 1 ? "" : "s")} at {at}, so line {line} is past its end. "
              + "Read the file at that commit and anchor the comment to the line the finding is about."
            : null;
    }

    /// <summary>Lines in a file's text as an editor numbers them: a final line without a newline still counts.</summary>
    public static int LineCount(string text) =>
        text.Length == 0 ? 0 : text.Count(c => c == '\n') + (text[^1] == '\n' ? 0 : 1);
}
