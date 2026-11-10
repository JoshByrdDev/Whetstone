namespace Whetstone.Services;

/// <summary>
/// The git of a slice's own branch (docs/slice-branches.md, piece 2): a finished ticket of the
/// slice merged into it, and the slice kept up with the base.
/// <para>
/// A slice branch is never checked out by anything the board runs - tickets fork from it, and the
/// walk-through reads it detached - and the hub stays on the base, where a person may be working.
/// So both merges are made without a working tree: the merge is worked out by
/// <c>merge-tree --write-tree</c>, committed by <c>commit-tree</c>, and the branch moved by
/// <c>update-ref</c> from the commit it was read at, so a branch that moved in between is never
/// overwritten. A branch a person has checked out somewhere is refused, because moving it would
/// leave that tree holding files its HEAD no longer has.
/// </para>
/// </summary>
public partial class TicketGitService
{
    /// <summary>
    /// Merge the ticket's <paramref name="branch"/> into its slice's branch <paramref name="slice"/>,
    /// <c>--no-ff</c>, without touching any checkout. A conflict changes nothing and asks for the
    /// branch to be rebased onto the slice, exactly as a merge into the base does.
    /// </summary>
    private async Task<MergeResult> MergeIntoSliceAsync(ProjectConfig cfg, string repo, string slice, string branch,
        string? title, string? description, CancellationToken ct)
    {
        var output = new List<string>();
        var noFiles = Array.Empty<string>();
        MergeResult Fail(string error, bool conflicted = false, IReadOnlyList<string>? files = null) =>
            new(false, error, conflicted, files ?? noFiles, branch, slice, repo, null, false, 0, output);

        if (string.IsNullOrWhiteSpace(branch)) return Fail("This ticket has no branch yet.");
        if (!IsGitRepo(repo)) return Fail($"{cfg.DisplayName} has no git repo at '{repo}'.");
        if (branch == slice) return Fail($"'{branch}' is the slice's own branch - there is nothing to merge.");
        if (!(await GitAsync(repo, ct, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}")).Ok)
            return Fail($"Branch '{branch}' does not exist in {repo}.");
        var sliceTip = await TipAsync(repo, slice, ct);
        if (sliceTip is null) return Fail($"The slice's branch '{slice}' does not exist in {repo}.");
        if (CheckedOutElsewhere(repo, slice) is { } holder)
            return Fail($"'{slice}' is checked out in {holder}. A slice branch is merged into without a checkout; " +
                        "switch that tree off it first, so it is not left holding files its branch no longer has.");

        var (_, ahead) = await CountAsync(repo, $"refs/heads/{slice}", $"refs/heads/{branch}", ct);
        if (ahead == 0) return Fail($"'{branch}' has no commits that {slice} does not already have.");

        var subject = string.IsNullOrWhiteSpace(title) ? $"Merge branch '{branch}' into {slice}" : title.Trim();
        var message = string.IsNullOrWhiteSpace(description) ? subject : $"{subject}\n\n{description.Trim()}";
        output.Add($"git merge --no-ff {branch} (into {slice}, without a checkout)");
        var made = await CommitMergeAsync(repo, slice, sliceTip, $"refs/heads/{branch}", message, ct);
        AddLines(output, made.Said);
        if (made.Conflicted.Count > 0)
        {
            var word = made.Conflicted.Count == 1 ? "file" : "files";
            return Fail($"Merging into {slice} conflicts in {made.Conflicted.Count} {word}. Nothing was changed - " +
                        "rebase the branch onto the slice and resolve them, then merge again.", true, made.Conflicted);
        }
        if (made.Sha is null) return Fail(made.Error ?? $"could not merge '{branch}' into {slice}");

        _log.LogInformation("Merged {Branch} into {Slice} in {Repo}: {Previous} -> {New}",
            branch, slice, repo, Short(sliceTip), Short(made.Sha));
        return new MergeResult(true, null, false, noFiles, branch, slice, repo, made.Sha, false, 0, output);
    }

    /// <summary>
    /// Bring the slice's branch <paramref name="slice"/> up to the base with a <c>merge --no-ff</c>
    /// of it - never a rebase, because tickets have been merged into a slice branch and rewriting it
    /// would orphan every branch forked from it. With <paramref name="dryRun"/> it only says how far
    /// behind the slice is.
    /// <para>
    /// A merge that conflicts is handed to the project's <c>rebase-resolve.ps1</c> first, as a
    /// rebase's stops are, and in the same orientation: the base is checked out (in a throwaway
    /// detached tree) and the slice merged into it, so the resolver's "ours" is the base and
    /// "theirs" is the work being brought onto it, as at every stop of a rebase. The commit it
    /// makes has the slice as its first parent all the same. A conflict it does not settle changes
    /// nothing and is answered "conflict", with the files, for the owner.
    /// </para>
    /// </summary>
    public async Task<SyncFromResult> SyncSliceAsync(string projectKey, string slice, bool dryRun, CancellationToken ct = default)
    {
        var (cfg, repo, baseBranch) = HubOf(projectKey);
        var none = Array.Empty<string>();
        if (!IsGitRepo(repo)) return new("refused", 0, none, $"There is no git repo at '{repo}'.");
        var sliceTip = await TipAsync(repo, slice, ct);
        if (sliceTip is null) return new("refused", 0, none, $"The slice's branch '{slice}' does not exist in {repo}.");
        var baseTip = await TipAsync(repo, baseBranch, ct);
        if (baseTip is null) return new("refused", 0, none, $"The base branch '{baseBranch}' is not a local branch in {repo}.");

        var (_, waiting) = await CountAsync(repo, $"refs/heads/{slice}", $"refs/heads/{baseBranch}", ct);
        if (waiting == 0) return new("up-to-date", 0, none, null);
        if (dryRun) return new("proposed", waiting, none, $"merge {waiting} commit(s) of {baseBranch} into {slice}");
        if (CheckedOutElsewhere(repo, slice) is { } holder)
            return new("refused", waiting, none, $"'{slice}' is checked out in {holder}, so it cannot be moved without a checkout.");

        var message = $"Merge {baseBranch} into {slice}";
        var made = await CommitMergeAsync(repo, slice, sliceTip, $"refs/heads/{baseBranch}", message, ct);
        var resolved = Array.Empty<string>() as IReadOnlyList<string>;
        if (made.Conflicted.Count > 0)
        {
            if (RebaseResolverOf(projectKey) is not { } script)
                return new("conflict", waiting, made.Conflicted, "nothing was changed: the merge conflicts, and the project has no resolver");
            var settled = await ResolveSliceSyncAsync(cfg, repo, script, slice, sliceTip, baseBranch, baseTip, message, made.Conflicted, ct);
            if (settled.Sha is null)
                return new("conflict", waiting, made.Conflicted, $"nothing was changed: {settled.Error}");
            made = settled;
            resolved = made.Conflicted;
        }
        if (made.Sha is null) return new("refused", waiting, none, made.Error ?? $"could not merge {baseBranch} into {slice}");

        var files = (await GitAsync(repo, ct, "diff", "--name-only", sliceTip, made.Sha)).Stdout
            .Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        _log.LogInformation("Brought {Slice} up to {Base} in {Repo}: {Previous} -> {New}{Settled}",
            slice, baseBranch, repo, Short(sliceTip), Short(made.Sha),
            resolved.Count > 0 ? $" (resolver settled {string.Join(", ", resolved)})" : "");
        return new("merged", waiting, files, resolved.Count > 0 ? $"the project's resolver settled {string.Join(", ", resolved)}" : null);
    }

    private record SliceMerge(string? Sha, IReadOnlyList<string> Conflicted, string? Error, RunResult? Said = null);

    /// <summary>
    /// Merge <paramref name="from"/> into <paramref name="into"/> (read at <paramref name="intoTip"/>)
    /// as a merge commit, with no working tree, and move <paramref name="into"/> to it only if it is
    /// still at <paramref name="intoTip"/>. The conflicted files, when it would conflict.
    /// </summary>
    private async Task<SliceMerge> CommitMergeAsync(string repo, string into, string intoTip, string from, string message,
        CancellationToken ct)
    {
        var tree = await GitAsync(repo, ct, TimeSpan.FromMinutes(2), "merge-tree", "--write-tree", "--name-only", intoTip, from);
        var lines = tree.Stdout.Replace("\r", "").Split('\n');
        if (tree.ExitCode == 1)
            return new(null, lines.Skip(1).TakeWhile(l => l.Length > 0).Distinct().ToList(), null, tree);
        if (tree.ExitCode != 0 || lines[0].Trim() is not { Length: > 0 } treeId)
            return new(null, Array.Empty<string>(), Explain(tree, "could not work out the merge"), tree);
        var fromSha = await TipOfRevAsync(repo, from, ct);
        if (fromSha is null) return new(null, Array.Empty<string>(), $"could not read {from}");
        return await CommitAndMoveAsync(repo, into, intoTip, treeId, fromSha, message, ct);
    }

    private async Task<SliceMerge> CommitAndMoveAsync(string repo, string into, string intoTip, string treeId, string otherParent,
        string message, CancellationToken ct)
    {
        var commit = await GitAsync(repo, ct, "commit-tree", treeId, "-p", intoTip, "-p", otherParent, "-m", message);
        if (!commit.Ok || commit.Stdout.Trim() is not { Length: > 0 } sha)
            return new(null, Array.Empty<string>(), Explain(commit, "could not write the merge commit"), commit);
        var move = await GitAsync(repo, ct, "update-ref", "-m", message, $"refs/heads/{into}", sha, intoTip);
        if (!move.Ok)
            return new(null, Array.Empty<string>(),
                Explain(move, $"'{into}' moved while it was being merged into; nothing was changed. Try again"), move);
        return new(sha, Array.Empty<string>(), null, commit);
    }

    /// <summary>
    /// The conflicted base-into-slice merge, replayed in a throwaway detached tree on the base with
    /// the slice merged into it, handed to the project's resolver, and committed - with the slice
    /// first - only when it leaves nothing unmerged. The tree is removed however it ends.
    /// </summary>
    private async Task<SliceMerge> ResolveSliceSyncAsync(ProjectConfig cfg, string repo, string script, string slice, string sliceTip,
        string baseBranch, string baseTip, string message, IReadOnlyList<string> conflicted, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), "whetstone-slice-sync-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var add = await GitAsync(repo, ct, TimeSpan.FromMinutes(2), "worktree", "add", "--detach", dir, baseTip);
            if (!add.Ok) return new(null, conflicted, Explain(add, "could not make a tree to resolve the merge in"));
            await GitAsync(dir, ct, TimeSpan.FromMinutes(2), "-c", "core.editor=true", "merge", "--no-ff", "--no-commit", sliceTip);
            var stopped = await ConflictedPathsAsync(dir, ct);
            if (stopped.Count == 0) stopped = conflicted.ToList();
            var run = await RunRebaseResolverAsync(script, dir, baseBranch, slice, null, stopped, ct);
            if (!run.Ok) return new(null, conflicted, $"the project's resolver declined: {run.Why}");
            if ((await ConflictedPathsAsync(dir, ct)).Count > 0)
                return new(null, conflicted, "the project's resolver exited as resolved but left files unmerged");
            var tree = await GitAsync(dir, ct, "write-tree");
            if (!tree.Ok || tree.Stdout.Trim() is not { Length: > 0 } treeId)
                return new(null, conflicted, Explain(tree, "could not write the resolved tree"));
            var made = await CommitAndMoveAsync(repo, slice, sliceTip, treeId, baseTip, message, ct);
            return made with { Conflicted = stopped };
        }
        finally
        {
            await GitAsync(repo, CancellationToken.None, "worktree", "remove", "--force", dir);
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private async Task<string?> TipAsync(string repo, string branch, CancellationToken ct) =>
        await TipOfRevAsync(repo, $"refs/heads/{branch}", ct);

    private async Task<string?> TipOfRevAsync(string repo, string rev, CancellationToken ct)
    {
        var r = await GitAsync(repo, ct, "rev-parse", "--verify", "--quiet", $"{rev}^{{commit}}");
        return r.Ok && r.Stdout.Trim() is { Length: > 0 } sha ? sha : null;
    }

    /// <summary>Where <paramref name="branch"/> is checked out, in the hub or any worktree of it, or null.</summary>
    private string? CheckedOutElsewhere(string repo, string branch) =>
        _worktrees.List(repo).FirstOrDefault(w => w.Branch == branch)?.Path;
}
