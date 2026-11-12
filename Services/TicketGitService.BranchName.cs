namespace Whetstone.Services;

/// <summary>
/// What <see cref="TicketGitService.RenameTicketBranchAsync"/> did: the repos whose branch it renamed,
/// or why none; and where the ticket's worktree or workspace moved from and to, when it moved.
/// </summary>
public sealed record BranchRenameResult(bool Ok, string? Error, IReadOnlyList<string> Renamed,
    string? WorkspaceFrom = null, string? WorkspaceTo = null);

// A ticket's branch name: whether a name is free, and renaming one a run has already made.
public partial class TicketGitService
{
    /// <summary>
    /// Why <paramref name="name"/> cannot be a new ticket branch of <paramref name="projectKey"/>, or
    /// null when it can: not a branch name, a slice's, or already a branch - locally or on origin - in
    /// any writable repo of the project. Whether another ticket has it recorded is the caller's
    /// question; this one is only git's.
    /// </summary>
    public async Task<string?> NewBranchNameProblemAsync(string projectKey, string name, CancellationToken ct = default)
    {
        var cfg = _registry.GetProjectConfig(projectKey);
        if (!IsBranchName(name)) return $"'{name}' is not a branch name git would take.";
        if (SliceBranches.IsSliceBranch(name)) return $"'{name}' is the shape of a slice's branch, which the steward names.";
        foreach (var target in TargetsOf(cfg).Where(t => !t.ReadOnly))
        {
            if (!IsGitRepo(target.Path)) continue;
            var where = target.Dir.Length > 0 ? target.Dir : cfg.DisplayName;
            if (await HasRefAsync(target.Path, $"refs/heads/{name}", ct))
                return $"'{name}' is already a branch in {where}.";
            if (await HasRefAsync(target.Path, $"refs/remotes/origin/{name}", ct))
                return $"'{name}' is already a branch on origin in {where}.";
        }
        return null;
    }

    /// <summary>
    /// Renames a ticket's branch from <paramref name="from"/> to <paramref name="to"/> in every writable
    /// repo of the project that has it locally, with <c>git branch -m</c> - which moves the HEAD of a
    /// worktree holding it, its reflog and its config along with it - and moves the ticket's worktree,
    /// or on a project of several repos its workspace, to the folder the new name gives
    /// (<see cref="BranchNaming.WorktreeDirName"/>). The worker finds a workspace by its branch's
    /// name, so one left under the old name blocks the next run as outside the ticket's workspace.
    /// All or nothing: a repo or a move that fails puts back everything already done. Never pushes,
    /// and never touches origin.
    /// <para>
    /// Refused, with nothing renamed, when the branch is on origin in any writable repo (a pull
    /// request is opened from a branch name and cannot follow a rename, so that is the owner's to do),
    /// when a worktree is stopped mid-rebase on it (the rebase's own state names the branch), when it
    /// is checked out in a person's own checkout, when <paramref name="to"/> is not free, and when the
    /// folder the worktree would move to is already there and not empty.
    /// A branch that is in none of the repos renames nothing, with nothing in <c>Renamed</c>: it is
    /// gone, or its owner renamed it by hand, and the record only has to follow - though a workspace
    /// still under the old name is moved, which is the half a rename by hand leaves behind.
    /// </para>
    /// <para>
    /// Only this machine's worktrees move. A worker host's clone keeps the branch, and its worktree,
    /// under the name its run made, and nothing here reaches it - which is why a ticket that has run
    /// on one is refused before this is called (<c>TicketEndpoints.RenameRefusalAsync</c>).
    /// </para>
    /// </summary>
    public async Task<BranchRenameResult> RenameTicketBranchAsync(string projectKey, string from, string to,
        CancellationToken ct = default)
    {
        static BranchRenameResult No(string why) => new(false, why, Array.Empty<string>());
        var cfg = _registry.GetProjectConfig(projectKey);
        from = from.Trim();
        to = to.Trim();
        if (!IsBranchName(to)) return No($"'{to}' is not a branch name git would take.");

        var holding = new List<RepoTarget>();
        foreach (var target in TargetsOf(cfg).Where(t => !t.ReadOnly))
        {
            if (!IsGitRepo(target.Path)) continue;
            var where = target.Dir.Length > 0 ? target.Dir : cfg.DisplayName;
            // Origin as this repo last fetched it. A pull request is opened from the branch's name, so
            // this is the owner's: Whetstone pushes nothing, and says what to do instead.
            if (await HasRefAsync(target.Path, $"refs/remotes/origin/{from}", ct))
                return No($"'{from}' is on origin in {where}, and a pull request cannot follow a renamed branch. "
                    + $"To rename it yourself: in each repo, 'git branch -m {from} {to}', 'git push -u origin {to}' and "
                    + $"'git push origin --delete {from}', then reopen the pull request from '{to}'. Set the name here after that: "
                    + $"finding '{from}' gone and '{to}' in its place, the board only records it.");
            if (!await HasRefAsync(target.Path, $"refs/heads/{from}", ct)) continue;
            if ((await RebasingWorktreesAsync(target.Path, from, ct)).FirstOrDefault() is { } stopped)
                return No($"A rebase of '{from}' is stopped in the worktree at {stopped} ({where}). Finish or clear it first: its own state names the branch.");
            if (_worktrees.List(target.Path).FirstOrDefault(w => w.IsMain && w.Branch == from) is not null)
                return No($"'{from}' is checked out in {target.Path}, which is a person's checkout. Switch it off the branch first.");
            holding.Add(target);
        }

        // With nothing holding the old name, the new one may already be the branch the record follows.
        if (holding.Count > 0 && await NewBranchNameProblemAsync(projectKey, to, ct) is { } taken) return No(taken);
        var plan = PlanWorkspaceMove(cfg, from, to, out var refused);
        if (refused is not null) return No(refused);

        var done = new List<RepoTarget>();
        foreach (var target in holding)
        {
            var mv = await GitAsync(target.Path, ct, "branch", "-m", from, to);
            if (mv.Ok) { done.Add(target); continue; }
            var where = target.Dir.Length > 0 ? target.Dir : cfg.DisplayName;
            return No(Explain(mv, $"Could not rename '{from}' to '{to}' in {where}")
                + await PutBackRenamesAsync(cfg, done, from, to));
        }

        if (plan is not null && await MoveWorkspaceAsync(plan) is { } failed)
            return No(failed + await PutBackRenamesAsync(cfg, done, from, to));

        return new(true, null, done.Select(t => t.Dir.Length > 0 ? t.Dir : cfg.DisplayName).ToList(),
            plan?.From, plan?.To);
    }

    // Puts back the branches already renamed, so the ticket is never half on one name and half on
    // the other. What it returns is added to the error: empty when every one went back.
    private async Task<string> PutBackRenamesAsync(ProjectConfig cfg, IEnumerable<RepoTarget> done, string from, string to)
    {
        var said = "";
        foreach (var back in done)
        {
            var undo = await GitAsync(back.Path, CancellationToken.None, "branch", "-m", to, from);
            if (!undo.Ok)
                said += $" And putting '{from}' back in {(back.Dir.Length > 0 ? back.Dir : cfg.DisplayName)} failed too: "
                    + $"it is '{to}' there now - rename it back by hand ('git branch -m {to} {from}').";
        }
        return said;
    }

    /// <summary>
    /// A ticket's worktree or workspace moving with its branch: the folder, and each repo's worktree
    /// in it - which only git may move, because it records where each one is, both ways.
    /// <c>Workspace</c> is a project of several repos, whose folder holds the worktrees and the
    /// files the project keeps at a workspace's root; on one repo the folder is the worktree.
    /// </summary>
    private sealed record WorkspaceMove(string From, string To, bool Workspace,
        IReadOnlyList<(string Repo, string From, string To)> Worktrees);

    // What moves when the branch is renamed, or null when nothing does: no worktree root, no folder
    // under the old name (a ticket not run yet, or a worktree its owner keeps somewhere else - the
    // worker uses that where it is), or the two names flatten to the same folder. Refused when the
    // new folder is already there with something in it: nothing is moved in on top of anything.
    private WorkspaceMove? PlanWorkspaceMove(ProjectConfig cfg, string from, string to, out string? refused)
    {
        refused = null;
        var root = cfg.ResolvedWorktreeRoot;
        if (string.IsNullOrWhiteSpace(root)) return null;
        var oldDir = Path.GetFullPath(Path.Combine(root, BranchNaming.WorktreeDirName(from)));
        var newDir = Path.GetFullPath(Path.Combine(root, BranchNaming.WorktreeDirName(to)));
        if (SamePath(oldDir, newDir) || !Directory.Exists(oldDir)) return null;

        var targets = TargetsOf(cfg).Where(t => IsGitRepo(t.Path)).ToList();
        WorkspaceMove? plan;
        if (targets.Any(t => t.Dir.Length > 0))
        {
            // A workspace: one worktree per repo in a folder of its own, read-only repos' included.
            var moves = new List<(string, string, string)>();
            foreach (var t in targets)
            {
                var at = Path.Combine(oldDir, t.Dir);
                if (_worktrees.List(t.Path).Any(w => !w.IsMain && SamePath(w.Path, at)))
                    moves.Add((t.Path, at, Path.Combine(newDir, t.Dir)));
            }
            plan = new WorkspaceMove(oldDir, newDir, true, moves);
        }
        else
        {
            // One repo: a folder there that git does not know as a worktree is not the ticket's, and
            // is left where it is.
            var repo = targets.FirstOrDefault(t => _worktrees.List(t.Path).Any(w => !w.IsMain && SamePath(w.Path, oldDir)));
            plan = repo is null ? null : new WorkspaceMove(oldDir, newDir, false, new[] { (repo.Path, oldDir, newDir) });
        }
        if (plan is null) return null;

        if (File.Exists(newDir))
            refused = $"'{newDir}' is a file, where the ticket's worktree would move to with its branch. Move or remove it first.";
        else if (Directory.Exists(newDir) && Directory.EnumerateFileSystemEntries(newDir).Any())
            refused = $"'{newDir}' is already there and not empty, and the ticket's worktree would move to it with its branch. Move or remove it first.";
        return refused is null ? plan : null;
    }

    // Moves a planned workspace, or puts back what it moved and says why not. Null when it moved.
    private async Task<string?> MoveWorkspaceAsync(WorkspaceMove plan)
    {
        var moved = new List<(string Repo, string From, string To)>();
        var loose = new List<(string From, string To)>();
        var made = false;
        string? failed = null;
        try
        {
            if (Directory.Exists(plan.To))
            {
                // Empty, or the plan would have refused. Git moves a worktree to a path that is not
                // there yet, and on one repo the folder is that path.
                if (!plan.Workspace) Directory.Delete(plan.To);
            }
            else if (plan.Workspace) { Directory.CreateDirectory(plan.To); made = true; }

            foreach (var w in plan.Worktrees)
            {
                var mv = await GitAsync(w.Repo, CancellationToken.None, "worktree", "move", w.From, w.To);
                if (!mv.Ok) { failed = Explain(mv, $"Could not move the worktree at {w.From} to {w.To}"); break; }
                moved.Add(w);
            }
            if (failed is null && plan.Workspace)
            {
                // What else the workspace holds - the conventions file the project keeps at its root.
                foreach (var entry in Directory.EnumerateFileSystemEntries(plan.From).ToList())
                {
                    var dest = Path.Combine(plan.To, Path.GetFileName(entry));
                    if (Directory.Exists(entry)) Directory.Move(entry, dest); else File.Move(entry, dest);
                    loose.Add((entry, dest));
                }
                Directory.Delete(plan.From);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failed = $"Could not move the ticket's workspace from {plan.From} to {plan.To}: {ex.Message}";
        }
        if (failed is null) return null;

        // Back the way it came, last first.
        if (plan.Workspace && !Directory.Exists(plan.From))
            try { Directory.CreateDirectory(plan.From); } catch (IOException) { }
        foreach (var (src, dest) in Enumerable.Reverse(loose))
        {
            try { if (Directory.Exists(dest)) Directory.Move(dest, src); else File.Move(dest, src); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { failed += $" And {dest} could not be put back at {src}: move it by hand."; }
        }
        foreach (var w in Enumerable.Reverse(moved))
        {
            var back = await GitAsync(w.Repo, CancellationToken.None, "worktree", "move", w.To, w.From);
            if (!back.Ok) failed += $" And the worktree at {w.To} could not be moved back: run 'git worktree move {w.To} {w.From}' in {w.Repo}.";
        }
        if (made)
            try { if (!Directory.EnumerateFileSystemEntries(plan.To).Any()) Directory.Delete(plan.To); } catch (IOException) { }
        return failed;
    }
}
