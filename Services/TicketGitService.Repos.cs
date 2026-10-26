namespace Whetstone.Services;

/// <summary>
/// One repo a git action is about: its folder in the project (empty for a project that is one
/// repo), its hub - the human's own checkout of it - and its base branch, already fallen back.
/// </summary>
public sealed record RepoTarget(string Dir, string Path, string BaseBranch, bool ReadOnly);

/// <summary>
/// Whether one of a ticket's repos is in the ticket, by the rule <c>Get-TouchedMembers</c> in
/// <c>data/worker-repos.ps1</c> applies on the worker's side. The two must agree: the worker
/// squashes, rebases and delivers the repos it calls touched, and this is what the board shows a
/// person as the ticket's change.
/// </summary>
/// <param name="Ahead">Commits on the ticket's branch that the base does not have.</param>
/// <param name="Dirty">Uncommitted files in the worktree holding the branch, untracked included.</param>
/// <param name="BaseRef">The ref it was measured against, or null when the base exists nowhere.</param>
public sealed record RepoTouch(string Dir, bool ReadOnly, bool Touched, int Ahead, int Dirty, string? BaseRef, string? Worktree);

/// <summary>
/// The repos of a project, for every git action on it. See docs/multi-repo-plan.md, piece 4.
/// <para>
/// A project that is one repo has one target, named by nothing, at exactly the path and base every
/// action here always used - so a caller that names no repo gets today's answer. A project of
/// several has one per repo in <see cref="ProjectConfig.Repos"/>, and an action on it must say which:
/// there is no default repo, for the same reason there is no default project.
/// </para>
/// <para>
/// A path that stands on its own - one a model wrote, or one listed across the whole ticket - is
/// named with its repo's folder first on a project of several (<c>alpha-api/src/Foo.cs</c>), and
/// relative to the repo on a project of one. A path inside one repo's own answer (that repo's
/// preflight, say) is relative to that repo either way.
/// </para>
/// </summary>
public partial class TicketGitService
{
    /// <summary>
    /// The repo <paramref name="repo"/> names in this project, or null with <paramref name="error"/>
    /// saying why. Empty or null names the one repo of a project that is one, and nothing on a
    /// project of several.
    /// </summary>
    public RepoTarget? TargetOf(ProjectConfig cfg, string? repo, out string? error)
    {
        error = null;
        var root = cfg.ProjectPath?.Trim().TrimEnd('\\', '/') ?? "";
        var named = repo?.Trim() ?? "";

        if (!cfg.IsMultiRepo)
        {
            if (named.Length > 0)
            {
                error = $"{cfg.DisplayName} is one repo, with no repo named '{named}' in it.";
                return null;
            }
            return new RepoTarget("", root, ResolveBaseBranch(cfg), false);
        }

        var dirs = string.Join(", ", cfg.Repos.Select(m => m.Dir));
        if (named.Length == 0)
        {
            error = $"{cfg.DisplayName} is several repos ({dirs}): say which one this is about.";
            return null;
        }
        var member = cfg.Repos.FirstOrDefault(m => string.Equals(m.Dir, named, StringComparison.OrdinalIgnoreCase));
        if (member is null)
        {
            error = $"{cfg.DisplayName} has no repo named '{named}'. Its repos are {dirs}.";
            return null;
        }
        return TargetFor(cfg, member);
    }

    /// <summary>Every repo of this project, in config order: the one, or each of several.</summary>
    public IReadOnlyList<RepoTarget> TargetsOf(ProjectConfig cfg) => cfg.IsMultiRepo
        ? cfg.Repos.Select(m => TargetFor(cfg, m)).ToList()
        : new[] { TargetOf(cfg, null, out _)! };

    private RepoTarget TargetFor(ProjectConfig cfg, RepoMember member)
    {
        var root = cfg.ProjectPath?.Trim().TrimEnd('\\', '/') ?? "";
        var path = root.Length == 0 ? "" : System.IO.Path.Combine(root, member.Dir);
        var baseBranch = member.BaseBranch.Length > 0 ? member.BaseBranch : ResolveBaseBranch(cfg);
        return new RepoTarget(member.Dir, path, baseBranch, member.ReadOnly);
    }

    /// <summary>
    /// The target a write - a push, a rebase, a checkout - may go to, or null with the reason. A
    /// read-only repo is in every workspace for reading and is never delivered from, so no write
    /// reaches it from here either.
    /// </summary>
    public RepoTarget? WriteTargetOf(ProjectConfig cfg, string? repo, out string? error)
    {
        var target = TargetOf(cfg, repo, out error);
        if (target is { ReadOnly: true })
        {
            error = $"'{target.Dir}' is read-only in {cfg.DisplayName}: nothing is ever delivered from it.";
            return null;
        }
        return target;
    }

    /// <summary>
    /// Which of the ticket's repos it has touched, in config order: a writable repo whose branch has
    /// commits ahead of its base ref, or whose worktree has uncommitted changes. A read-only repo
    /// never is. A repo whose base exists nowhere counts as touched, so what follows fails loudly on
    /// it rather than skipping work nobody else has - the worker's rule exactly.
    /// </summary>
    public async Task<IReadOnlyList<RepoTouch>> TouchedAsync(string projectKey, string branch, CancellationToken ct = default)
    {
        var cfg = _registry.GetProjectConfig(projectKey);
        var result = new List<RepoTouch>();
        foreach (var target in TargetsOf(cfg))
            result.Add(await TouchOfAsync(cfg, target, branch, ct));
        return result;
    }

    private async Task<RepoTouch> TouchOfAsync(ProjectConfig cfg, RepoTarget target, string branch, CancellationToken ct)
    {
        RepoTouch Untouched(string? baseRef = null, string? worktree = null) =>
            new(target.Dir, target.ReadOnly, false, 0, 0, baseRef, worktree);

        if (target.ReadOnly || string.IsNullOrWhiteSpace(branch) || !IsGitRepo(target.Path)) return Untouched();
        if (!(await GitAsync(target.Path, ct, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}")).Ok) return Untouched();

        var worktree = SharedWorktree(target.Path, branch);
        var dirty = worktree is not null && Directory.Exists(worktree) ? (await PendingChangesAsync(worktree, ct)).Count : 0;
        var baseRef = await BaseRefAsync(target.Path, cfg, target.BaseBranch, ct);
        if (baseRef is null) return new RepoTouch(target.Dir, false, true, 0, dirty, null, worktree);

        var (_, ahead) = await CountAsync(target.Path, baseRef, branch, ct);
        return new RepoTouch(target.Dir, false, dirty > 0 || ahead > 0, ahead, dirty, baseRef, worktree);
    }

    /// <summary>
    /// A path that names its repo, split into the repo and the path inside it. On a project of one
    /// the whole path is inside the one repo. Null when it names no repo of a project of several.
    /// </summary>
    private (RepoTarget Target, string Inner)? SplitRepoPath(ProjectConfig cfg, string path, out string? error)
    {
        error = null;
        if (!cfg.IsMultiRepo) return (TargetOf(cfg, null, out _)!, path);

        var clean = path.Replace('\\', '/').TrimStart('/');
        var slash = clean.IndexOf('/');
        var first = slash < 0 ? clean : clean[..slash];
        var target = TargetOf(cfg, first, out _);
        if (target is null || slash < 0)
        {
            error = $"'{path}' does not start with one of {cfg.DisplayName}'s repos ({string.Join(", ", cfg.Repos.Select(m => m.Dir))}).";
            return null;
        }
        return (target, clean[(slash + 1)..]);
    }

    /// <summary>A path inside <paramref name="target"/>, named as it stands across the whole ticket.</summary>
    private static string Prefixed(RepoTarget target, string inner) =>
        target.Dir.Length == 0 ? inner : $"{target.Dir}/{inner}";
}
