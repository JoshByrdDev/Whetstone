namespace Whetstone.Services;

/// <summary>
/// A ticket under PR review (<see cref="Models.Ticket.ReviewedOn"/>): the commits its reviewer last
/// saw, checked against the branch before they are recorded, and what is new on the branch since.
/// </summary>
public partial class TicketGitService
{
    /// <summary>
    /// The point to record as what the reviewer saw, in <see cref="Models.QaCommits"/> shape with full
    /// shas, or why it cannot be. <paramref name="given"/> null takes the branch as it stands - its
    /// head in each repo the ticket touched, as a QA pass is recorded. Given, each part names a repo
    /// of this project (<c>dir@sha</c>; a bare sha on a project of one) and a commit on the branch:
    /// the branch head or one of its ancestors, since a round is added on top of it and a commit the
    /// branch no longer holds is one the next round would have to rewrite history to reach.
    /// </summary>
    public async Task<(string? Point, string? Error)> ReviewedPointAsync(string projectKey, string? branch, string? given,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(branch)) return (null, "This ticket has no branch yet, so there is nothing a reviewer could have seen.");
        var cfg = _registry.GetProjectConfig(projectKey);

        if (given is null)
        {
            var heads = await BranchHeadsAsync(projectKey, branch, ct);
            return heads.Count == 0
                ? (null, $"Branch '{branch}' is not on this machine, so its head cannot be taken. Name the commit the reviewer saw instead.")
                : (Models.QaCommits.Format(heads), null);
        }

        var parts = Models.QaCommits.Parse(given);
        if (parts.Count == 0) return (null, "Name the commit the reviewer saw, or leave it out to take the branch as it is now.");
        if (!cfg.IsMultiRepo && parts.Count > 1) return (null, $"{cfg.DisplayName} is one repo: name one commit.");

        var resolved = new List<(string Dir, string Sha)>();
        foreach (var (dir, sha) in parts)
        {
            var target = WriteTargetOf(cfg, dir.Length == 0 ? null : dir, out var notHere);
            if (target is null) return (null, notHere);
            if (resolved.Any(r => string.Equals(r.Dir, target.Dir, StringComparison.OrdinalIgnoreCase)))
                return (null, $"'{target.Dir}' is named twice.");
            if (!IsGitRepo(target.Path)) return (null, $"{cfg.DisplayName} has no git repo at '{target.Path}'.");

            var full = await GitAsync(target.Path, ct, "rev-parse", "--verify", "--quiet", $"{sha}^{{commit}}");
            var at = target.Dir.Length == 0 ? "" : $" in {target.Dir}";
            if (!full.Ok || string.IsNullOrWhiteSpace(full.Stdout))
                return (null, $"'{sha}' is not a commit{at}. Fetch it first if it was pushed from somewhere else.");
            var commit = full.Stdout.Trim();
            if ((await GitAsync(target.Path, ct, "merge-base", "--is-ancestor", commit, $"refs/heads/{branch}")).ExitCode != 0)
                return (null, $"{Models.QaCommits.Short(commit)} is not on '{branch}'{at}. A round goes on top of what the "
                    + "reviewer saw, so it has to be the branch head or a commit under it.");
            // A commit the base already holds is not anything the pull request showed: the reviewer
            // read the branch's own commits, and a round anchored below them would fold those in.
            if (await BaseRefAsync(target.Path, cfg, target.BaseBranch, ct) is { } baseRef
                && (await GitAsync(target.Path, ct, "merge-base", "--is-ancestor", commit, baseRef)).ExitCode == 0)
                return (null, $"{Models.QaCommits.Short(commit)} is already on {baseRef}{at}, so it is not part of the pull request. "
                    + "Name the newest commit of the branch the reviewer has seen.");
            resolved.Add((target.Dir, commit));
        }

        // Config order, as every other record of a ticket's commits is written.
        var order = TargetsOf(cfg).Select(t => t.Dir).ToList();
        return (Models.QaCommits.Format(resolved.OrderBy(r => order.FindIndex(d => string.Equals(d, r.Dir, StringComparison.OrdinalIgnoreCase))).ToList()), null);
    }

    /// <summary>
    /// Push a round of a branch under review: a plain push, never a force, so origin either moves
    /// forward to it or refuses. A refusal means origin holds something this branch does not -
    /// rewriting past it is exactly what a ticket under review must not do - and it is said, not
    /// forced. The pull request already exists, so nothing else is raised.
    /// </summary>
    public async Task<ForcePushResult> PushRoundAsync(string projectKey, string branch, CancellationToken ct = default,
        string? member = null)
    {
        var cfg = _registry.GetProjectConfig(projectKey);
        var target = WriteTargetOf(cfg, member, out var notHere);
        var repo = target?.Path ?? "";
        var output = new List<string>();
        ForcePushResult Fail(string error, bool stale = false) => new(false, error, stale, branch, null, output);

        if (notHere is not null) return Fail(notHere);
        if (string.IsNullOrWhiteSpace(branch)) return Fail("This ticket has no branch yet.");
        if (!IsGitRepo(repo)) return Fail($"{cfg.DisplayName} has no git repo at '{repo}'.");
        if (branch == (target?.BaseBranch ?? ResolveBaseBranch(cfg))) return Fail($"'{branch}' is the base branch.");
        if (!(await GitAsync(repo, ct, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}")).Ok)
            return Fail($"Branch '{branch}' does not exist in {repo}.");
        if (string.IsNullOrWhiteSpace((await GitAsync(repo, ct, "remote", "get-url", "origin")).Stdout))
            return Fail("This repo has no 'origin' remote to push to.");

        var push = await GitAsync(repo, ct, TimeSpan.FromMinutes(5), "push", "--set-upstream", "origin", branch);
        output.Add($"git push --set-upstream origin {branch}");
        AddLines(output, push);
        if (!push.Ok)
        {
            var said = push.Stderr + push.Stdout;
            return said.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase) || said.Contains("rejected", StringComparison.OrdinalIgnoreCase)
                ? Fail($"origin/{branch} has commits this branch does not, so this is not a fast-forward and was not pushed. "
                       + "Under review that is worth a look before anything is forced: it is what the reviewer has.", stale: true)
                : Fail(Explain(push, $"could not push '{branch}' to origin"));
        }
        return new ForcePushResult(true, null, false, branch, (await GitAsync(repo, ct, "rev-parse", branch)).Stdout.Trim(), output);
    }

    /// <summary>The commit the reviewer saw in one repo, from a ticket's <see cref="Models.Ticket.ReviewedOn"/>; null when that repo has none.</summary>
    public static string? ReviewedShaFor(string? reviewedOn, string dir) =>
        Models.QaCommits.Parse(reviewedOn)
            .Where(p => string.Equals(p.Dir, dir, StringComparison.OrdinalIgnoreCase) || (dir.Length == 0 && p.Dir.Length == 0))
            .Select(p => p.Sha).FirstOrDefault();
}
