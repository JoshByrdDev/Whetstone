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
    /// branch to be rebased onto the slice, exactly as a merge into the base does. Also the merge of
    /// a ticket filed on a base of its own (<see cref="Models.Ticket.BaseBranch"/>) on a project that
    /// merges locally: the same move into a branch that is not the project's base.
    /// </summary>
    private async Task<MergeResult> MergeIntoSliceAsync(ProjectConfig cfg, string repo, string slice, string branch,
        string? title, string? description, CancellationToken ct)
    {
        var output = new List<string>();
        var noFiles = Array.Empty<string>();
        MergeResult Fail(string error, bool conflicted = false, IReadOnlyList<string>? files = null) =>
            new(false, error, conflicted, files ?? noFiles, branch, slice, repo, null, false, 0, output);
        var isSlice = SliceBranches.IsSliceBranch(slice);
        var whose = isSlice ? "slice's" : "ticket's base";

        if (string.IsNullOrWhiteSpace(branch)) return Fail("This ticket has no branch yet.");
        if (!IsGitRepo(repo)) return Fail($"{cfg.DisplayName} has no git repo at '{repo}'.");
        if (branch == slice) return Fail($"'{branch}' is {(isSlice ? "the slice's own branch" : "the ticket's base")} - there is nothing to merge.");
        if (!(await GitAsync(repo, ct, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}")).Ok)
            return Fail($"Branch '{branch}' does not exist in {repo}.");
        var sliceTip = await TipAsync(repo, slice, ct);
        if (sliceTip is null) return Fail($"The {whose} branch '{slice}' does not exist in {repo}.");
        if (CheckedOutElsewhere(repo, slice) is { } holder)
            return Fail($"'{slice}' is checked out in {holder}. {(isSlice ? "A slice branch" : "A ticket's base")} is merged into without a checkout; " +
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
    public async Task<SyncFromResult> SyncSliceAsync(string projectKey, string slice, bool dryRun, string? from = null,
        CancellationToken ct = default)
    {
        var (cfg, repo, projectBase) = HubOf(projectKey);
        // A slice built on another keeps up with that one, which keeps up with the base (section 3).
        var baseBranch = string.IsNullOrWhiteSpace(from) ? projectBase : from.Trim();
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

    /// <summary>
    /// Cut <paramref name="slice"/> again from the base's tip, because the slice it was built on,
    /// <paramref name="parent"/>, was abandoned (docs/slice-branches.md, section 3). Only a slice
    /// with nothing of its own yet - every commit on it is the parent's - can move without losing
    /// work; one that has its own is refused, and is the owner's to rebuild or abandon. The branch
    /// moves only from the commit it was read at, and never while a tree has it checked out.
    /// </summary>
    public async Task<(string? Sha, string? Refusal)> RecutSliceAsync(string projectKey, string slice, string parent,
        CancellationToken ct = default)
    {
        var (_, repo, baseBranch) = HubOf(projectKey);
        if (!IsGitRepo(repo)) return (null, $"There is no git repo at '{repo}'.");
        var tip = await TipAsync(repo, slice, ct);
        if (tip is null) return (null, $"The slice's branch '{slice}' does not exist in {repo}.");
        var baseTip = await TipAsync(repo, baseBranch, ct);
        if (baseTip is null) return (null, $"The base branch '{baseBranch}' is not a local branch in {repo}.");
        if (await TipAsync(repo, parent, ct) is null) return (null, $"'{parent}' is gone, so what is its and what is {slice}'s cannot be told apart.");
        var (_, own) = await CountAsync(repo, $"refs/heads/{parent}", $"refs/heads/{slice}", ct);
        if (own > 0)
            return (null, $"{slice} has {own} commit{(own == 1 ? "" : "s")} of its own on top of {parent}, so it cannot be re-cut from {baseBranch} without them. Abandon it too, or rebuild it by hand.");
        if (CheckedOutElsewhere(repo, slice) is { } holder)
            return (null, $"'{slice}' is checked out in {holder}. Switch that tree off it first.");
        var move = await GitAsync(repo, ct, "update-ref", "-m", $"re-cut {slice} from {baseBranch}", $"refs/heads/{slice}", baseTip, tip);
        if (!move.Ok) return (null, Explain(move, $"'{slice}' moved while it was being re-cut; nothing was changed"));
        _log.LogInformation("Re-cut {Slice} from {Base} at {Sha} in {Repo} (was on {Parent})", slice, baseBranch, Short(baseTip), repo, parent);
        return (baseTip, null);
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

    /// <summary>
    /// The first half of releasing a slice (docs/slice-branches.md, "Releasing a slice", step 2):
    /// the hub made ready for a merge into its base, exactly as a ticket's merge makes it, and the
    /// slice's branch brought up to that base. What it answers is what the release's checks run on:
    /// the slice's tip, which now holds everything the base has, so its tree is the release's.
    /// </summary>
    public async Task<SliceReleasePrep> PrepareSliceReleaseAsync(string projectKey, string slice, CancellationToken ct = default)
    {
        var (_, repo, baseBranch) = HubOf(projectKey);
        var none = Array.Empty<string>();
        SliceReleasePrep No(string why, IReadOnlyList<string>? files = null) => new(why, files ?? none, none, null, baseBranch);
        if (!IsGitRepo(repo)) return No($"There is no git repo at '{repo}'.");
        if (await TipAsync(repo, slice, ct) is null) return No($"The slice's branch '{slice}' does not exist in {repo}.");
        if (CheckedOutElsewhere(repo, slice) is { } holder)
            return No($"'{slice}' is checked out in {holder}. Switch that tree off it first: releasing deletes the branch.");

        var output = new List<string>();
        var prep = await PrepareBaseForMergeAsync(repo, baseBranch, output, ct);
        if (prep.Error is not null) return No(prep.Error);

        var sync = await SyncSliceAsync(projectKey, slice, dryRun: false, ct: ct);
        if (sync.Answer == "conflict")
            return No($"{slice} cannot take {baseBranch} without a conflict ({sync.Why}). Resolve it on the slice's branch, then release again.", sync.Files);
        if (sync.Answer is not ("merged" or "up-to-date"))
            return No(sync.Why ?? $"could not bring {slice} up to {baseBranch}");

        var tip = await TipAsync(repo, slice, ct);
        var files = (await GitAsync(repo, ct, "diff", "--name-only", $"refs/heads/{baseBranch}...refs/heads/{slice}")).Stdout
            .Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (files.Count == 0) return No($"{slice} changes nothing that {baseBranch} does not already have.");
        return new(null, none, files, tip, baseBranch);
    }

    /// <summary>
    /// The second half of releasing a slice (step 3): <paramref name="slice"/>, still at
    /// <paramref name="sliceTip"/> - the tip its checks passed on - merged into the hub's local
    /// base with <c>--no-ff</c>, and its branch deleted. Never pushed.
    /// <para>
    /// <paramref name="conventionLines"/>, when there are any, are appended to
    /// <paramref name="conventionsPath"/> in the merge commit itself (section 4, "Promoting at
    /// release"), so the release and what it promoted are one commit.
    /// </para>
    /// <para>
    /// The commit is made without a checkout and the hub then fast-forwarded to it, so a hub that
    /// moved, or a file git will not overwrite, leaves the base where it was. A base that moved since
    /// the slice was brought up to it is refused rather than merged unchecked.
    /// </para>
    /// </summary>
    public async Task<(string? Sha, string? Refusal)> ReleaseSliceAsync(string projectKey, string slice, string sliceTip,
        string message, string? conventionsPath, IReadOnlyList<string> conventionLines, CancellationToken ct = default)
    {
        var (_, repo, baseBranch) = HubOf(projectKey);
        if (!IsGitRepo(repo)) return (null, $"There is no git repo at '{repo}'.");
        if (await TipAsync(repo, slice, ct) != sliceTip)
            return (null, $"{slice} moved while it was being checked. Nothing was merged; release it again.");
        if (CheckedOutElsewhere(repo, slice) is { } holder)
            return (null, $"'{slice}' is checked out in {holder}. Switch that tree off it first: releasing deletes the branch.");
        var output = new List<string>();
        var prep = await PrepareBaseForMergeAsync(repo, baseBranch, output, ct);
        if (prep.Error is not null) return (null, prep.Error);
        var baseTip = await TipAsync(repo, baseBranch, ct);
        if (baseTip is null) return (null, $"The base branch '{baseBranch}' is not a local branch in {repo}.");
        if (!(await GitAsync(repo, ct, "merge-base", "--is-ancestor", baseTip, sliceTip)).Ok)
            return (null, $"{baseBranch} moved while {slice} was being checked. Nothing was merged; release it again, so the checks run on what it merges into.");

        // The slice holds the whole base, so the merge's tree is the slice's own.
        var tree = (await GitAsync(repo, ct, "rev-parse", $"{sliceTip}^{{tree}}")).Stdout.Trim();
        if (tree.Length == 0) return (null, $"could not read {slice}'s tree");
        if (conventionLines.Count > 0)
        {
            if (string.IsNullOrWhiteSpace(conventionsPath)) return (null, "The project names no conventions file to promote decisions into.");
            var (withLines, no) = await AppendToTreeAsync(repo, sliceTip, tree, conventionsPath.Replace('\\', '/').Trim('/'), conventionLines, ct);
            if (withLines is null) return (null, no);
            tree = withLines;
        }

        var commit = await GitAsync(repo, ct, "commit-tree", tree, "-p", baseTip, "-p", sliceTip, "-m", message);
        if (!commit.Ok || commit.Stdout.Trim() is not { Length: > 0 } sha)
            return (null, Explain(commit, "could not write the release's merge commit"));

        if (await HeldRefusalAsync(repo, $"{baseTip}..{sha}", null, "the release", ct) is { } held) return (null, held);
        var forward = await GitAsync(repo, ct, TimeSpan.FromMinutes(5), "merge", "--ff-only", sha);
        if (!forward.Ok) return (null, Explain(forward, $"could not move {baseBranch} to the release; it was left where it was"));
        if (await TipAsync(repo, baseBranch, ct) != sha)
            return (null, $"{baseBranch} did not end at the release's commit {Short(sha)}. Look at {repo} before anything else merges.");

        var drop = await GitAsync(repo, ct, "update-ref", "-d", $"refs/heads/{slice}", sliceTip);
        if (!drop.Ok) _log.LogWarning("Released {Slice} as {Sha} but could not delete its branch: {Why}", slice, Short(sha), drop.Stderr.Trim());
        _log.LogInformation("Released {Slice} into {Base} in {Repo}: {Previous} -> {New}", slice, baseBranch, repo, Short(baseTip), Short(sha));
        return (sha, null);
    }

    /// <summary>
    /// <paramref name="tree"/> with <paramref name="lines"/> appended to the file at
    /// <paramref name="path"/> as it is in <paramref name="at"/> - made, if there is none - written
    /// through a throwaway index so neither the hub's index nor its files are touched.
    /// </summary>
    private async Task<(string? Tree, string? Refusal)> AppendToTreeAsync(string repo, string at, string tree, string path,
        IReadOnlyList<string> lines, CancellationToken ct)
    {
        var shown = await GitAsync(repo, ct, "show", $"{at}:{path}");
        var text = shown.Ok ? shown.Stdout.Replace("\r\n", "\n") : ConventionsHeader;
        if (text.Length > 0 && !text.EndsWith('\n')) text += "\n";
        text += string.Join("", lines.Select(l => l.TrimEnd() + "\n"));

        var scratch = Path.Combine(Path.GetTempPath(), "whetstone-release-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(scratch);
        try
        {
            var file = Path.Combine(scratch, "file");
            await File.WriteAllTextAsync(file, text, new System.Text.UTF8Encoding(false), ct);
            var blob = await GitAsync(repo, ct, "hash-object", "-w", $"--path={path}", file);
            if (!blob.Ok) return (null, Explain(blob, $"could not write {path}"));
            var env = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = Path.Combine(scratch, "index") };
            Task<RunResult> Git(params string[] args) => RunAsync("git", repo, TimeSpan.FromSeconds(30), env, ct, args);
            var read = await Git("read-tree", tree);
            if (!read.Ok) return (null, Explain(read, "could not read the release's tree"));
            var put = await Git("update-index", "--add", "--cacheinfo", $"100644,{blob.Stdout.Trim()},{path}");
            if (!put.Ok) return (null, Explain(put, $"could not add {path} to the release"));
            var written = await Git("write-tree");
            return written.Ok && written.Stdout.Trim() is { Length: > 0 } t ? (t, null) : (null, Explain(written, "could not write the release's tree"));
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>What a conventions file starts as when a release is the first thing to promote into it.</summary>
    public const string ConventionsHeader =
        "# Conventions\n\nRules every slice keeps. Each came from a decision (#n), promoted when its slice was released, or from the owner's own edit.\n\n";

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

    /// <summary>
    /// The git half of switching a project to slice branches (docs/slice-branches.md, "Moving a
    /// project that already exists"): the hub moves off the old base - an integration branch, as a
    /// rule - onto <paramref name="newBase"/>, and the conventions file is committed there. Refused
    /// while the hub is not clean on the old base, and while the old base has a commit the new one
    /// does not: the owner's last merge of the old shape comes first, by hand. Nothing on the old
    /// base is rewritten, and the branch is kept. With <paramref name="dryRun"/> only the checks
    /// run; the answer is then the new base's tip.
    /// </summary>
    public async Task<(string? Sha, string? Refusal)> SwitchBaseAsync(string projectKey, string newBase, string? file, string? text,
        string message, bool dryRun, CancellationToken ct = default)
    {
        var (_, repo, oldBase) = HubOf(projectKey);
        newBase = newBase.Trim();
        if (newBase.Length == 0) return (null, "Name the branch that becomes the base.");
        if (await HubNotWritableAsync(repo, oldBase, ct) is { } no) return (null, no);
        if (await ShaAsync(repo, $"refs/heads/{newBase}", ct) is not { } tip) return (null, $"There is no branch '{newBase}' in {repo}.");
        if (newBase != oldBase)
        {
            var left = await GitAsync(repo, ct, "rev-list", "--count", $"refs/heads/{newBase}..refs/heads/{oldBase}");
            if (!left.Ok) return (null, Explain(left, $"could not compare {oldBase} with {newBase}"));
            if (int.TryParse(left.Stdout.Trim(), out var n) && n > 0)
                return (null, $"{oldBase} has {n} commit{(n == 1 ? "" : "s")} that {newBase} does not. Merge {oldBase} into {newBase} first, by hand: that is the last merge of the old shape.");
            if (CheckedOutElsewhere(repo, newBase) is { } held && !SamePath(held, repo))
                return (null, $"{newBase} is checked out in {held}. The hub has to have it.");
        }
        if (dryRun) return (tip, null);

        if (newBase != oldBase)
        {
            var co = await GitAsync(repo, ct, "checkout", "--quiet", newBase);
            if (!co.Ok) return (null, Explain(co, $"could not check out {newBase}"));
        }
        if (string.IsNullOrWhiteSpace(file) || text is null) return (await ShaAsync(repo, "HEAD", ct), null);

        var rel = file.Replace('\\', '/').TrimStart('/');
        var full = Path.GetFullPath(Path.Combine(repo, rel));
        if (!full.StartsWith(Path.GetFullPath(repo) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return (null, $"'{file}' is outside the repo.");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, text.Replace("\r\n", "\n").TrimEnd() + "\n", ct);
        await GitAsync(repo, ct, "add", "--", rel);
        if (!(await GitAsync(repo, ct, "diff", "--cached", "--quiet", "--", rel)).Ok)
        {
            var commit = await GitAsync(repo, ct, "commit", "-m", message, "--", rel);
            if (!commit.Ok)
            {
                await GitAsync(repo, ct, "reset", "--quiet", "--", rel);
                return (null, Explain(commit, $"switched to {newBase}, but could not commit {rel}"));
            }
        }
        return (await ShaAsync(repo, "HEAD", ct), null);
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
}

/// <summary>The first half of a slice's release: why it cannot go, or the tip and files its checks run on.</summary>
public record SliceReleasePrep(string? Refusal, IReadOnlyList<string> Conflicted, IReadOnlyList<string> Files, string? SliceTip, string BaseBranch);
