namespace Whetstone.Services;

/// <summary>What <see cref="TicketGitService.RenameTicketBranchAsync"/> did: the repos whose branch it renamed, or why none.</summary>
public sealed record BranchRenameResult(bool Ok, string? Error, IReadOnlyList<string> Renamed);

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
    /// worktree holding it, its reflog and its config along with it. All or nothing: a repo that fails
    /// puts back the ones already renamed. Never pushes, and never touches origin.
    /// <para>
    /// Refused, with nothing renamed, when the branch is on origin in any writable repo (a pull
    /// request is opened from a branch name and cannot follow a rename, so that is the owner's to do),
    /// when a worktree is stopped mid-rebase on it (the rebase's own state names the branch), when it
    /// is checked out in a person's own checkout, and when <paramref name="to"/> is not free.
    /// A branch that is in none of the repos renames nothing and is <c>Ok</c>, with nothing renamed:
    /// it is gone, or its owner renamed it by hand, and the record only has to follow.
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

        // Nothing holds the old name: the branch was renamed by hand, or is gone. Then the record
        // only follows git, and the new name may already be the branch it follows.
        if (holding.Count == 0) return new(true, null, Array.Empty<string>());
        if (await NewBranchNameProblemAsync(projectKey, to, ct) is { } taken) return No(taken);

        var done = new List<RepoTarget>();
        foreach (var target in holding)
        {
            var mv = await GitAsync(target.Path, ct, "branch", "-m", from, to);
            if (mv.Ok) { done.Add(target); continue; }
            var where = target.Dir.Length > 0 ? target.Dir : cfg.DisplayName;
            var failed = Explain(mv, $"Could not rename '{from}' to '{to}' in {where}");
            // Put back what was renamed, so the ticket is never half on one name and half on the other.
            foreach (var back in done)
            {
                var undo = await GitAsync(back.Path, CancellationToken.None, "branch", "-m", to, from);
                if (!undo.Ok)
                    failed += $" And putting '{from}' back in {(back.Dir.Length > 0 ? back.Dir : cfg.DisplayName)} failed too: "
                        + $"it is '{to}' there now - rename it back by hand ('git branch -m {to} {from}').";
            }
            return No(failed);
        }
        return new(true, null, done.Select(t => t.Dir.Length > 0 ? t.Dir : cfg.DisplayName).ToList());
    }
}
