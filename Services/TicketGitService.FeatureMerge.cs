namespace Whetstone.Services;

/// <summary>
/// What one repo did in a feature-branch merge (<see cref="TicketGitService.MergeIntoFeatureBaseAsync"/>).
/// </summary>
/// <param name="Outcome">
/// <c>merged</c> (pushed to origin), <c>already</c> (origin's feature branch had all of it),
/// <c>conflict</c>, <c>ready</c> (would have merged, but another repo stopped the whole merge),
/// <c>not-pushed</c> (its push was refused or never tried because an earlier repo's was).
/// </param>
/// <param name="LocalNote">Why the local copy of the feature branch was left where it was, when it was.</param>
public record FeatureMergeRepo(
    string Dir,
    string Outcome,
    string? MergeSha,
    IReadOnlyList<string> ConflictedFiles,
    string? LocalNote = null);

/// <summary>
/// A ticket merged straight into its own feature branch on a project that otherwise delivers by pull
/// request (<see cref="ProjectConfig.FeatureMergeOn"/>). <paramref name="Repos"/> has one entry per
/// repo the branch is in. Not ok with every repo <c>ready</c> or <c>conflict</c> means nothing moved
/// anywhere; not ok with some <c>merged</c> means those landed and the rest did not, and pressing
/// again finishes the job, because a landed repo then answers <c>already</c>.
/// </summary>
public record FeatureMergeResult(
    bool Ok,
    string? Error,
    string Branch,
    string BaseBranch,
    IReadOnlyList<FeatureMergeRepo> Repos,
    IReadOnlyList<string> Output)
{
    public bool MovedToDone { get; init; }
    public string? PhaseNote { get; init; }

    /// <summary>The merge commit to record on the ticket: the first repo that has one.</summary>
    public string? MergeSha => Repos.FirstOrDefault(r => r.MergeSha is not null)?.MergeSha;
}

/// <summary>
/// The second way a ticket on a feature branch is delivered, beside its pull request: merged into
/// that feature branch here and pushed, with no pull request at all. Only for a ticket whose own
/// base (<see cref="Models.Ticket.BaseBranch"/>) is not the project's - a ticket on the project's
/// base still goes by pull request - and only where the project's owner switched it on.
/// <para>
/// The merge is made against <c>origin/&lt;feature&gt;</c>, freshly fetched, because on a pull-request
/// project that is the ref every ticket of the feature forks from and is measured against
/// (<c>Resolve-BaseRef</c>); a merge left in the local branch would be invisible to the next one. So
/// the merge commit is pushed, as a plain push origin takes only as a fast-forward. No checkout is
/// touched: the merge is worked out by <c>merge-tree</c> and written by <c>commit-tree</c>, as a
/// slice's merge is. The local feature branch is moved after it only when that is a fast-forward and
/// no tree has it checked out.
/// </para>
/// <para>
/// A project of several repos merges every repo the branch is in. Every repo is fetched and its merge
/// worked out before anything is pushed, so a conflict in one leaves all of them as they were. The
/// pushes themselves cannot be one transaction: one origin can move between two of them. Then the
/// repos already pushed stay pushed, and the next press merges only the rest.
/// </para>
/// </summary>
public partial class TicketGitService
{
    /// <summary>
    /// The feature branch the ticket on <paramref name="branch"/> can be merged into without a pull
    /// request, or null: the project has not switched it on, or the ticket's base is the project's.
    /// </summary>
    public async Task<string?> FeatureBaseOfAsync(string projectKey, string? branch, CancellationToken ct = default)
    {
        var cfg = _registry.GetProjectConfig(projectKey);
        if (!cfg.FeatureMergeOn || string.IsNullOrWhiteSpace(branch)) return null;
        var target = TargetsOf(cfg).FirstOrDefault(t => !t.ReadOnly);
        if (target is null) return null;
        var own = await ForTicketAsync(cfg, target, branch, ct);
        return own is null || own.BaseBranch == target.BaseBranch ? null : own.BaseBranch;
    }

    public async Task<FeatureMergeResult> MergeIntoFeatureBaseAsync(
        string projectKey, string branch, string? title, string? description, CancellationToken ct = default)
    {
        var cfg = _registry.GetProjectConfig(projectKey);
        var output = new List<string>();
        var feature = await FeatureBaseOfAsync(projectKey, branch, ct) ?? "";
        FeatureMergeResult Fail(string error, IReadOnlyList<FeatureMergeRepo>? repos = null) =>
            new(false, error, branch, feature, repos ?? Array.Empty<FeatureMergeRepo>(), output);

        if (!cfg.FeatureMergeOn)
            return Fail($"{cfg.DisplayName} does not merge feature-branch tickets without a pull request. " +
                        "Turn it on in the project's settings first.");
        if (string.IsNullOrWhiteSpace(branch)) return Fail("This ticket has no branch yet.");
        if (feature.Length == 0)
            return Fail("This ticket is on the project's own base, so it is delivered by pull request. " +
                        "Only a ticket filed on a feature branch can be merged without one.");

        // ---- every repo read and worked out before anything moves ------------------------------
        var plans = new List<Plan>();
        var conflicted = false;
        foreach (var target in TargetsOf(cfg).Where(t => !t.ReadOnly))
        {
            var label = target.Dir.Length > 0 ? target.Dir : cfg.DisplayName;
            if (!IsGitRepo(target.Path)) return Fail($"There is no git repo at '{target.Path}'.");
            var mine = await ForTicketAsync(cfg, target, branch, ct) ?? target;
            if (mine.BaseBranch == target.BaseBranch)
                return Fail($"In {label} this ticket is on {target.BaseBranch}, the repo's own base, so it goes by pull request there.");

            var tip = await TipAsync(target.Path, branch, ct);
            if (tip is null) continue; // the branch is not in this repo: nothing of the ticket's is here

            var worktree = SharedWorktree(target.Path, branch);
            if (worktree is not null && Directory.Exists(worktree)
                && (await PendingChangesAsync(worktree, ct)).Count is > 0 and var dirty)
                return Fail($"{label}: the ticket's worktree has {dirty} uncommitted file{(dirty == 1 ? "" : "s")}, which a merge " +
                            $"would leave behind. Commit or discard {(dirty == 1 ? "it" : "them")} first ({worktree}).");

            var fetch = await GitAsync(target.Path, ct, TimeSpan.FromSeconds(90), "fetch", "origin",
                $"+refs/heads/{mine.BaseBranch}:refs/remotes/origin/{mine.BaseBranch}");
            AddLines(output, fetch);
            if (!fetch.Ok) return Fail($"{label}: {Explain(fetch, $"could not fetch {mine.BaseBranch} from origin")}. Nothing was changed.");
            var onOrigin = await TipOfRevAsync(target.Path, $"refs/remotes/origin/{mine.BaseBranch}", ct);
            if (onOrigin is null) return Fail($"{label}: origin has no branch '{mine.BaseBranch}'. Nothing was changed.");

            var (_, ahead) = await CountAsync(target.Path, onOrigin, tip, ct);
            if (ahead == 0)
            {
                plans.Add(new Plan(target, label, mine.BaseBranch, onOrigin, tip, null, Array.Empty<string>()));
                continue;
            }

            var tree = await GitAsync(target.Path, ct, TimeSpan.FromMinutes(2), "merge-tree", "--write-tree", "--name-only", onOrigin, tip);
            var lines = tree.Stdout.Replace("\r", "").Split('\n');
            if (tree.ExitCode == 1)
            {
                conflicted = true;
                plans.Add(new Plan(target, label, mine.BaseBranch, onOrigin, tip, null,
                    lines.Skip(1).TakeWhile(l => l.Length > 0).Distinct().ToList()));
                continue;
            }
            if (tree.ExitCode != 0 || lines[0].Trim() is not { Length: > 0 } treeId)
                return Fail($"{label}: {Explain(tree, "could not work out the merge")}. Nothing was changed.");
            plans.Add(new Plan(target, label, mine.BaseBranch, onOrigin, tip, treeId, Array.Empty<string>()));
        }

        if (plans.Count == 0)
            return Fail($"'{branch}' is in none of {cfg.DisplayName}'s repos on this machine.");

        FeatureMergeRepo Unmoved(Plan p) => new(p.Target.Dir,
            p.Conflicts.Count > 0 ? "conflict" : p.TreeId is null ? "already" : "ready", null, p.Conflicts);

        if (conflicted)
        {
            var clashing = plans.Where(p => p.Conflicts.Count > 0).ToList();
            var files = clashing.Sum(p => p.Conflicts.Count);
            return Fail($"Merging into {feature} conflicts in {files} file{(files == 1 ? "" : "s")} " +
                        $"({string.Join(", ", clashing.Select(p => p.Label))}). Nothing was changed in any repo - " +
                        $"rebase the branch onto {feature} and resolve them, then merge again.",
                plans.Select(Unmoved).ToList());
        }

        if (plans.All(p => p.TreeId is null))
            return Fail($"origin's {feature} already has everything on '{branch}'" +
                        (plans.Count > 1 ? " in every repo" : "") + ". There is nothing to merge.",
                plans.Select(Unmoved).ToList());

        // ---- write and push, repo by repo -------------------------------------------------------
        var subject = string.IsNullOrWhiteSpace(title) ? $"Merge branch '{branch}' into {feature}" : title.Trim();
        var message = string.IsNullOrWhiteSpace(description) ? subject : $"{subject}\n\n{description.Trim()}";
        var done = new List<FeatureMergeRepo>();
        for (var i = 0; i < plans.Count; i++)
        {
            var p = plans[i];
            if (p.TreeId is null) { done.Add(Unmoved(p)); continue; }
            var repo = p.Target.Path;

            string Stop(string why)
            {
                done.Add(new FeatureMergeRepo(p.Target.Dir, "not-pushed", null, Array.Empty<string>()));
                done.AddRange(plans.Skip(i + 1).Select(q => q.TreeId is null ? Unmoved(q)
                    : new FeatureMergeRepo(q.Target.Dir, "not-pushed", null, Array.Empty<string>())));
                var landed = done.Where(d => d.Outcome == "merged").Select(d => d.Dir.Length > 0 ? d.Dir : cfg.DisplayName).ToList();
                return landed.Count == 0
                    ? $"{p.Label}: {why}. Nothing was pushed anywhere."
                    : $"{p.Label}: {why}. {string.Join(", ", landed)} {(landed.Count == 1 ? "was" : "were")} already merged and pushed; " +
                      "merge again to finish the rest.";
            }

            var commit = await GitAsync(repo, ct, "commit-tree", p.TreeId, "-p", p.OriginTip, "-p", p.BranchTip, "-m", message);
            if (!commit.Ok || commit.Stdout.Trim() is not { Length: > 0 } sha)
                return Fail(Stop(Explain(commit, "could not write the merge commit")), done);

            // A plain push: origin takes it only as a fast-forward of what was fetched above.
            output.Add($"git push origin {Short(sha)}:refs/heads/{p.BaseBranch}" + (p.Target.Dir.Length > 0 ? $" (in {p.Target.Dir})" : ""));
            var push = await GitAsync(repo, ct, TimeSpan.FromMinutes(5), "push", "origin", $"{sha}:refs/heads/{p.BaseBranch}");
            AddLines(output, push);
            if (!push.Ok)
                return Fail(Stop(Explain(push, $"origin refused the push to {p.BaseBranch} (it may have moved since it was fetched)")), done);

            await GitAsync(repo, ct, "update-ref", $"refs/remotes/origin/{p.BaseBranch}", sha, p.OriginTip);
            var note = await KeepLocalFeatureUpAsync(repo, p.BaseBranch, sha, ct);
            _log.LogInformation("Merged {Branch} into {Feature} and pushed it in {Repo}: {Previous} -> {New}",
                branch, p.BaseBranch, repo, Short(p.OriginTip), Short(sha));
            done.Add(new FeatureMergeRepo(p.Target.Dir, "merged", sha, Array.Empty<string>(), note));
        }

        return new FeatureMergeResult(true, null, branch, feature, done, output);
    }

    /// <summary>
    /// Move the local feature branch to <paramref name="sha"/>, just pushed, when that is a
    /// fast-forward and no tree has it checked out. Otherwise it is left alone and the reason said:
    /// moving a checked-out branch would leave that tree holding files its HEAD no longer has, and a
    /// local branch with commits origin lacks is somebody's work.
    /// </summary>
    private async Task<string?> KeepLocalFeatureUpAsync(string repo, string feature, string sha, CancellationToken ct)
    {
        var local = await TipAsync(repo, feature, ct);
        if (local is null || local == sha) return null;
        if (CheckedOutElsewhere(repo, feature) is { } holder)
            return $"Your local {feature} is checked out in {holder}, so it was left where it was; pull it there.";
        if (!(await GitAsync(repo, ct, "merge-base", "--is-ancestor", local, sha)).Ok)
            return $"Your local {feature} has commits origin does not, so it was left where it was.";
        var move = await GitAsync(repo, ct, "update-ref", "-m", $"merged into {feature} and pushed", $"refs/heads/{feature}", sha, local);
        return move.Ok ? null : $"Your local {feature} moved while this ran, so it was left where it was.";
    }

    private sealed record Plan(RepoTarget Target, string Label, string BaseBranch, string OriginTip, string BranchTip,
        string? TreeId, IReadOnlyList<string> Conflicts);
}
