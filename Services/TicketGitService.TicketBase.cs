using System.Text.RegularExpressions;

namespace Whetstone.Services;

// A ticket's own base branch (Ticket.BaseBranch): whether a project's repos can carry one.
public partial class TicketGitService
{
    /// <summary>
    /// The refusal for filing a ticket of <paramref name="projectKey"/> on <paramref name="baseBranch"/>,
    /// or null when every writable repo has it where that ticket's git will look for it.
    /// <para>
    /// Where it looks is <see cref="BaseRefAsync"/>'s rule, the one <c>Resolve-BaseRef</c> shares. By
    /// pull request a ticket forks from and opens its pull request into <c>origin/&lt;base&gt;</c>, so
    /// the branch has to be on origin in each writable repo - a local branch alone would be forked
    /// from and then have nowhere to open the pull request into. With <c>mergeInsteadOfPr</c> the
    /// local branch is enough. A read-only repo is only read at its own base and is not asked.
    /// </para>
    /// <para>
    /// <paramref name="fetch"/> fetches that one branch from origin first, so one the owner has just
    /// pushed is found; the claim passes it too. A slice's branch is the steward's and is refused here,
    /// and so is a TFVC project, where the fleet works in a git mirror whose base the human owns.
    /// </para>
    /// </summary>
    public async Task<string?> TicketBaseProblemAsync(string projectKey, string baseBranch, bool fetch = true,
        CancellationToken ct = default)
    {
        var cfg = _registry.GetProjectConfig(projectKey);
        var name = baseBranch?.Trim() ?? "";
        if (!IsBranchName(name)) return $"'{name}' is not a branch name.";
        if (SliceBranches.IsSliceBranch(name))
            return $"'{name}' is a slice's branch, which the steward gives a ticket by its slice. File it against the slice instead.";
        if (cfg.IsTfvc) return $"{cfg.DisplayName} is checked in through TFVC, so its tickets stay on its base.";

        var byPr = !cfg.MergeInsteadOfPr;
        foreach (var target in TargetsOf(cfg).Where(t => !t.ReadOnly))
        {
            var where = target.Dir.Length > 0 ? target.Dir : cfg.DisplayName;
            if (!IsGitRepo(target.Path)) return $"{where} has no git repo at '{target.Path}'.";
            if (name == target.BaseBranch) continue;

            if (await HasRefAsync(target.Path, $"refs/remotes/origin/{name}", ct)) continue;
            if (fetch && await HasOriginAsync(target.Path, ct))
            {
                await GitAsync(target.Path, ct, TimeSpan.FromSeconds(60), "fetch", "origin", name);
                if (await HasRefAsync(target.Path, $"refs/remotes/origin/{name}", ct)) continue;
            }
            if (byPr)
                return $"'{name}' is not on origin in {where}. {cfg.DisplayName} is delivered by pull request, "
                    + "so a ticket based on it forks from origin's copy and opens its pull request into it: push the branch there first.";
            if (!await HasRefAsync(target.Path, $"refs/heads/{name}", ct))
                return $"'{name}' is not a branch in {where}, locally or on origin.";
        }
        return null;
    }

    private async Task<bool> HasRefAsync(string repo, string fullRef, CancellationToken ct) =>
        (await GitAsync(repo, ct, "rev-parse", "--verify", "--quiet", fullRef)).Ok;

    private async Task<bool> HasOriginAsync(string repo, CancellationToken ct) =>
        (await GitAsync(repo, ct, "remote", "get-url", "origin")).Ok;

    /// <summary>
    /// A conservative reading of <c>git check-ref-format --branch</c>: letters, digits and
    /// <c>._/-</c>, no leading dash, no <c>..</c>, no empty or dotted path component, no
    /// <c>.lock</c> ending. Anything it lets through is a branch git accepts.
    /// </summary>
    public static bool IsBranchName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 200
        && BranchNameRx().IsMatch(name)
        && !name.Contains("..") && !name.Contains("//")
        && !name.EndsWith(".lock", StringComparison.Ordinal)
        && name.Split('/').All(part => part.Length > 0 && !part.StartsWith('.'));

    [GeneratedRegex(@"^(?!-)[A-Za-z0-9._/\-]+(?<![/.])$")]
    private static partial Regex BranchNameRx();
}
