namespace Whetstone.Services;

/// <summary>Outcome of taking a worker host's branch into the board host's repo. <c>Why</c> is set on a refusal.</summary>
/// <param name="SetAside">Uncommitted files found in the board host's worktree and stashed before the move, or null.</param>
/// <param name="Stopped">Rebases a run here left stopped on the branch, kept and cleared before the move.</param>
public record BranchAcceptResult(bool Ok, string? Why, string? Moved = null, SetAsideStash? SetAside = null,
    IReadOnlyList<StoppedRebaseKept>? Stopped = null);

/// <summary>Leftovers moved into a named stash so a branch could move: the stash is the only copy of them.</summary>
public record SetAsideStash(string Sha, string Message, IReadOnlyList<string> Files, string Path);

/// <summary>
/// The board host's half of the worker host round trip (docs/worker-hosts-plan.md, piece 1).
/// <para>
/// A worker on a worker host cannot push its branch here: the board host normally has that branch
/// checked out in a worktree of its own, and git refuses to update a checked-out branch. So it
/// pushes to <c>refs/whetstone/incoming/&lt;runId&gt;</c>, which nothing checks out, and this moves
/// the branch - in the worktree that holds it when one does, so the worktree is never left
/// pointing at a commit its files do not match.
/// </para>
/// </summary>
public partial class TicketGitService
{
    public const string IncomingRefPrefix = "refs/whetstone/incoming/";
    private const string NoCommit = "0000000000000000000000000000000000000000";

    /// <summary>
    /// Move <paramref name="branch"/> to <paramref name="sha"/>, which a worker host pushed to
    /// <paramref name="incomingRef"/>. Refused when the branch is no longer at
    /// <paramref name="expectedOld"/> (empty: it must not exist yet) - someone moved it here while
    /// the worker host was working, and the worker host's run must not overwrite that - or when the
    /// worktree holding it is outside the project's worktree root and has uncommitted changes, or
    /// when it is checked out in the main checkout, which is a person's. Checking the run's lease is
    /// the caller's; this is the git alone.
    /// <para>
    /// A dirty worktree inside the root is the fleet's own, and what is uncommitted there is what a
    /// run on this machine left when it was stopped before committing - the branch tip is the one the
    /// worker host started from, or the check above has already refused. The worker sets such
    /// leftovers aside when a run starts on the machine that has them (Resolve-Worktree), but a
    /// ticket that moved to a worker host never starts here again, so refusing them blocked its
    /// hand-back on every retry. They go into a named stash instead, as the worker does it, and the
    /// caller says so on the ticket.
    /// </para>
    /// </summary>
    public async Task<BranchAcceptResult> AcceptWorkerBranchAsync(
        string projectKey, string branch, string incomingRef, string sha, string? expectedOld, CancellationToken ct = default)
    {
        var (cfg, repo, _) = HubOf(projectKey);
        if (!IsGitRepo(repo)) return new(false, $"There is no git repo at '{repo}'.");
        if (!incomingRef.StartsWith(IncomingRefPrefix, StringComparison.Ordinal) || incomingRef.Length == IncomingRefPrefix.Length)
            return new(false, $"'{incomingRef}' is not an incoming ref.");
        if (!(await GitAsync(repo, ct, "check-ref-format", "--branch", branch)).Ok)
            return new(false, $"'{branch}' is not a valid branch name.");

        var pushed = await ShaOfAsync(repo, incomingRef + "^{commit}", ct);
        if (pushed is null) return new(false, $"Nothing was pushed to {incomingRef}.");
        if (!pushed.Equals(sha.Trim(), StringComparison.OrdinalIgnoreCase))
            return new(false, $"{incomingRef} holds {Short(pushed)}, not {Short(sha)}.");

        var current = await ShaOfAsync(repo, $"refs/heads/{branch}", ct);
        var expected = string.IsNullOrWhiteSpace(expectedOld) ? null : expectedOld.Trim();
        if (!string.Equals(current, expected, StringComparison.OrdinalIgnoreCase))
            return new(false, current is null
                ? $"'{branch}' no longer exists on the board host."
                : expected is null
                    ? $"'{branch}' was created on the board host while the worker host was working."
                    : $"'{branch}' moved on the board host while the worker host was working ({Short(expected)} -> {Short(current)}).");

        // A run here killed mid-rebase left this machine's worktree detached on a rebase of the
        // branch. The run that holds the ticket now is the worker host's, so nothing is in it: the
        // rebase is cleared without moving the branch, which puts the worktree back on it below.
        var (stopped, stuck) = await ClearStoppedRebasesAsync(projectKey, branch, ct);
        if (stuck is not null) return new(false, stuck, Stopped: stopped);

        var holder = _worktrees.List(repo).FirstOrDefault(w => w.Branch == branch);
        string moved;
        if (holder is { IsMain: true })
            return new(false, $"'{branch}' is checked out in {repo}, which is a person's checkout. Switch it off the branch and the next run hands it back.");
        SetAsideStash? setAside = null;
        if (holder is not null)
        {
            var dirty = DirtyFiles((await GitAsync(holder.Path, ct, "status", "--porcelain")).Stdout);
            if (dirty.Count > 0)
            {
                if (!IsUnder(holder.Path, cfg.ResolvedWorktreeRoot))
                    return new(false, $"The worktree at {holder.Path} has uncommitted changes ({Listed(dirty)}), and it is outside this project's worktree root, so they may be a person's.");
                var message = $"whetstone: left uncommitted on {branch} on the board host, set aside before a worker host's hand-back";
                var stash = await GitAsync(holder.Path, ct, "stash", "push", "--quiet", "--include-untracked", "-m", message);
                if (!stash.Ok)
                    return new(false, Explain(stash, $"The worktree at {holder.Path} has uncommitted changes ({Listed(dirty)}), and setting them aside in a stash failed."));
                if (DirtyFiles((await GitAsync(holder.Path, ct, "status", "--porcelain")).Stdout).Count > 0)
                    return new(false, $"The worktree at {holder.Path} still has uncommitted changes after setting them aside in a stash ({Listed(dirty)}).");
                var stashSha = await ShaOfAsync(holder.Path, "refs/stash", ct) ?? "";
                setAside = new(stashSha, message, dirty, holder.Path);
            }
            var reset = await GitAsync(holder.Path, ct, TimeSpan.FromMinutes(2), "reset", "--quiet", "--hard", pushed);
            if (!reset.Ok) return new(false, Explain(reset, $"Could not move the worktree at {holder.Path}."));
            moved = holder.Path;
        }
        else
        {
            var update = await GitAsync(repo, ct, "update-ref", $"refs/heads/{branch}", pushed, current ?? NoCommit);
            if (!update.Ok) return new(false, Explain(update, $"Could not move '{branch}'."));
            moved = $"refs/heads/{branch}";
        }

        await GitAsync(repo, ct, "update-ref", "-d", incomingRef);
        return new(true, null, moved, setAside, stopped);
    }

    /// <summary>The paths in <c>git status --porcelain</c> output.</summary>
    private static List<string> DirtyFiles(string porcelain) =>
        porcelain.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 3).Select(l => l[3..]).ToList();

    private static string Listed(IReadOnlyList<string> files) =>
        string.Join(", ", files.Take(8)) + (files.Count > 8 ? $" and {files.Count - 8} more" : "");

    private static bool IsUnder(string path, string root) => PathIdentity.IsUnder(path, root);

    private async Task<string?> ShaOfAsync(string repo, string rev, CancellationToken ct)
    {
        var r = await GitAsync(repo, ct, "rev-parse", "--verify", "--quiet", rev);
        var sha = r.Stdout.Trim();
        return r.Ok && sha.Length > 0 ? sha : null;
    }
}
