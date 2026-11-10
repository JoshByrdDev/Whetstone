namespace Whetstone.Services;

/// <summary>
/// A rebase a run left stopped in one of the fleet's worktrees, kept and cleared so the branch could
/// be moved. <c>Ref</c> holds everything the worktree had - its stopped HEAD, resolved and unresolved
/// files - and is the only copy of it.
/// </summary>
public record StoppedRebaseKept(string Path, string Ref, string Sha);

/// <summary>
/// The board host's counterpart to <c>Undo-StoppedGitOperation</c> in <c>data/worker-board.ps1</c>.
/// <para>
/// A run killed mid-rebase leaves its worktree detached with the rebase's state still naming the
/// branch. Git then refuses to check the branch out anywhere else, and <c>git worktree list</c>
/// shows that worktree as detached, so nothing that looks for "the worktree holding the branch"
/// finds it. When the ticket's later runs happen on a worker host, no run here ever starts in that
/// worktree again to clean it, and every rebase of the branch from the board fails on it.
/// </para>
/// <para>
/// It is cleared with <c>rebase --quit</c>, never <c>--abort</c>: an abort moves the branch back to
/// where the rebase found it, and by now the branch has usually moved on (a worker host's
/// hand-back), so an abort is a rewind past every commit since. Quit touches no ref.
/// </para>
/// </summary>
public partial class TicketGitService
{
    /// <summary>The linked worktrees of <paramref name="repo"/> stopped mid-rebase on <paramref name="branch"/>.</summary>
    private async Task<List<string>> RebasingWorktreesAsync(string repo, string branch, CancellationToken ct)
    {
        var found = new List<string>();
        foreach (var w in _worktrees.List(repo).Where(w => !w.IsMain && w.Branch is null && Directory.Exists(w.Path)))
        {
            foreach (var state in new[] { "rebase-merge", "rebase-apply" })
            {
                var p = (await GitAsync(w.Path, ct, "rev-parse", "--git-path", state)).Stdout.Trim();
                if (p.Length == 0) continue;
                var headName = Path.Combine(Path.IsPathRooted(p) ? p : Path.Combine(w.Path, p), "head-name");
                if (!File.Exists(headName)) continue;
                if (File.ReadAllText(headName).Trim() == $"refs/heads/{branch}") { found.Add(w.Path); break; }
            }
        }
        return found;
    }

    /// <summary>
    /// Keep what a stopped rebase in <paramref name="dir"/> had got to under
    /// <c>refs/whetstone/stopped/&lt;branch&gt;/&lt;short sha&gt;</c>, drop the rebase without moving
    /// the branch, and put the worktree on the branch as it stands. Only in the project's own worktree
    /// root - anywhere else it may be a person's rebase - and only where the caller knows no run is in
    /// it. Returns what was kept, or why nothing was touched.
    /// </summary>
    private async Task<(StoppedRebaseKept? Kept, string? Why)> SetAsideStoppedRebaseAsync(
        string repo, ProjectConfig cfg, string branch, string dir, CancellationToken ct)
    {
        if (!IsUnder(dir, cfg.ResolvedWorktreeRoot))
            return (null, $"'{branch}' is mid-rebase in {dir}, which is outside this project's worktree root, so it may be a person's. Finish or quit that rebase there (git -C \"{dir}\" rebase --quit), then try again.");

        var head = await ShaOfAsync(dir, "HEAD", ct);
        // The rebase is being dropped, so its own index is not needed afterwards: staging everything
        // into it - unresolved files with their markers, untracked ones - is how the commit is made.
        var add = await GitAsync(dir, ct, TimeSpan.FromMinutes(2), "add", "-A");
        var tree = add.Ok ? (await GitAsync(dir, ct, "write-tree")).Stdout.Trim() : "";
        if (tree.Length == 0)
            return (null, Explain(add, $"'{branch}' is mid-rebase in {dir}, and keeping what it had got to before clearing it failed."));
        var commitArgs = new List<string> { "commit-tree", tree };
        if (head is not null) { commitArgs.Add("-p"); commitArgs.Add(head); }
        commitArgs.AddRange(new[] { "-m", $"whetstone: {branch} as a stopped rebase left it on the board host, kept before clearing it" });
        var sha = (await GitAsync(dir, ct, commitArgs.ToArray())).Stdout.Trim();
        if (sha.Length < 8)
            return (null, $"'{branch}' is mid-rebase in {dir}, and keeping what it had got to before clearing it failed.");
        var keptAs = $"refs/whetstone/stopped/{branch}/{sha[..8]}";
        if (!(await GitAsync(repo, ct, "update-ref", keptAs, sha)).Ok)
            return (null, $"'{branch}' is mid-rebase in {dir}, and keeping what it had got to as {keptAs} failed.");

        var quit = await GitAsync(dir, ct, "rebase", "--quit");
        if (!quit.Ok || await RebaseInProgressAsync(dir, ct))
            return (null, Explain(quit, $"'{branch}' is mid-rebase in {dir} and the rebase would not clear (what it had got to is kept as {keptAs})."));
        var back = await GitAsync(dir, ct, TimeSpan.FromMinutes(2), "checkout", "--quiet", "--force", branch);
        if (!back.Ok)
            return (null, Explain(back, $"The stopped rebase in {dir} was cleared, but the worktree could not be put back on '{branch}' (what it had got to is kept as {keptAs})."));
        await GitAsync(dir, ct, TimeSpan.FromMinutes(2), "clean", "-fdq");
        return (new StoppedRebaseKept(dir, keptAs, sha), null);
    }

    /// <summary>
    /// Every stopped rebase of <paramref name="branch"/> in the project's worktrees, cleared as
    /// <see cref="SetAsideStoppedRebaseAsync"/> does it. For a caller that knows no run is in the
    /// ticket: the steward at NeedsReview, the hand-back of a worker host's live run.
    /// </summary>
    public async Task<(IReadOnlyList<StoppedRebaseKept> Kept, string? Why)> ClearStoppedRebasesAsync(
        string projectKey, string branch, CancellationToken ct = default)
    {
        var (cfg, repo, _) = HubOf(projectKey);
        var kept = new List<StoppedRebaseKept>();
        if (string.IsNullOrWhiteSpace(branch) || !IsGitRepo(repo)) return (kept, null);
        foreach (var dir in await RebasingWorktreesAsync(repo, branch, ct))
        {
            var (k, why) = await SetAsideStoppedRebaseAsync(repo, cfg, branch, dir, ct);
            if (k is null) return (kept, why);
            kept.Add(k);
        }
        return (kept, null);
    }

    /// <summary>A ticket comment on what <see cref="SetAsideStoppedRebaseAsync"/> did.</summary>
    public static string SayStoppedRebaseKept(StoppedRebaseKept k, string branch) =>
        $"The board host's worktree for this ticket, `{k.Path}`, was stopped in the middle of a rebase, left by a run here that ended before finishing it. " +
        $"That rebase was cleared without moving `{branch}` (an abort would have wound the branch back to where the rebase began, past everything since), and the worktree is on the branch as it stands. " +
        $"What the rebase had got to - its HEAD, and the files resolved or not - is kept as `{k.Ref}` (`{k.Sha[..8]}`). To look: `git show --stat {k.Ref}`. Nothing needs doing unless something in it was wanted.";
}
