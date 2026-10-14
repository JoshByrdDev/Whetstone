using Whetstone.Services.UnitReports;

namespace Whetstone.Services;

/// <summary>One ticket of a unit, as far as the unit's range needs to know it.</summary>
/// <param name="AnalysedHead">The head its chosen analysis read, for a fork point when it never merged here.</param>
public record UnitTicketRef(int Id, string? Branch, string? MergeSha, string? AnalysedHead);

/// <summary>
/// The stretch of the base a unit report describes.
/// </summary>
/// <param name="BaseRef">The ref resolved as the base, by the same rule as every diff on the board.</param>
/// <param name="FromSha">The base before the unit.</param>
/// <param name="ToSha">The base now.</param>
/// <param name="RangeCommits">Commits on the base's first-parent line between the two.</param>
/// <param name="OwnCommits">How many of those are this unit's own merges, as stamped on its tickets.</param>
/// <param name="Tips">Each ticket branch's head and tree now, where the branch still exists.</param>
public record UnitRange(string BaseRef, string FromSha, string ToSha, int RangeCommits, int OwnCommits,
    IReadOnlyDictionary<int, UnitMaterial.BranchTip> Tips);

public partial class TicketGitService
{
    /// <summary>
    /// Where a unit starts and ends on the base, and where each of its branches is now.
    /// <para>
    /// The start is the previous unit's release when there was one: that is literally the base
    /// before this unit. Without one it is the oldest fork point among the unit's tickets - for a
    /// ticket merged here, the merge's first parent; otherwise where its analysed head left the
    /// base. Neither needs the ticket's own commits to be in the base, so a squash merge and a
    /// pull request merged elsewhere both work.
    /// </para>
    /// <para>
    /// The range may hold commits that are not the unit's - another ticket merged between two of
    /// its own. It is not trimmed to hide that; <see cref="UnitRange.OwnCommits"/> says how much
    /// of it is the unit's, and the report says the rest.
    /// </para>
    /// </summary>
    public async Task<(UnitRange? Range, string? Error)> UnitRangeAsync(string projectKey,
        IReadOnlyList<UnitTicketRef> tickets, string? previousReleasedSha, CancellationToken ct = default)
    {
        var (cfg, repo, baseBranch) = HubOf(projectKey);
        if (!IsGitRepo(repo)) return (null, $"{cfg.DisplayName} has no git repo at '{repo}'.");
        if (await BaseRefAsync(repo, cfg, baseBranch, ct) is not { } baseRef)
            return (null, $"There is no '{baseBranch}' in {repo} to read the unit from.");
        if (await ShaAsync(repo, baseRef, ct) is not { } to)
            return (null, $"Could not read {baseRef} in {repo}.");

        var tips = new Dictionary<int, UnitMaterial.BranchTip>();
        foreach (var t in tickets.Where(t => !string.IsNullOrWhiteSpace(t.Branch)))
            if (await ShaAsync(repo, $"refs/heads/{t.Branch}", ct) is { } head)
                tips[t.Id] = new UnitMaterial.BranchTip(head, await ShaAsync(repo, $"{head}^{{tree}}", ct));

        var from = previousReleasedSha is { Length: > 0 } released ? await ShaAsync(repo, released, ct) : null;
        if (from is null)
        {
            var forks = new List<string>();
            foreach (var t in tickets)
            {
                var fork = t.MergeSha is { Length: > 0 } merge ? await ShaAsync(repo, $"{merge}^1", ct) : null;
                var head = t.AnalysedHead ?? tips.GetValueOrDefault(t.Id)?.HeadSha;
                if (fork is null && head is not null)
                {
                    var mb = await GitAsync(repo, ct, "merge-base", to, head);
                    if (mb.Ok) fork = mb.Stdout.Trim();
                }
                if (fork is { Length: > 0 }) forks.Add(fork);
            }

            // The fork points all lie on the base, so their common ancestor is the oldest of them.
            from = forks.Count switch
            {
                0 => to,
                1 => forks[0],
                _ => (await GitAsync(repo, ct, new[] { "merge-base", "--octopus" }.Concat(forks.Distinct()).ToArray())) is { Ok: true } r
                    ? r.Stdout.Trim() : forks[0],
            };
        }

        var line = (await GitAsync(repo, ct, "rev-list", "--first-parent", $"{from}..{to}")).Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
        var own = tickets.Count(t => t.MergeSha is { Length: > 0 } m && line.Contains(m));

        return (new UnitRange(baseRef, from, to, line.Count, own, tips), null);
    }

    /// <summary>
    /// A directory holding the code at <paramref name="sha"/> to read a unit from, and how to give
    /// it back. The hub itself when it is already there and clean - the usual case on a project
    /// that merges locally - else a detached worktree under <paramref name="scratch"/>. The hub's
    /// own checkout is never moved.
    /// </summary>
    public async Task<(string? Dir, Func<Task> Release, string? Error)> CheckoutForReadingAsync(
        string projectKey, string sha, string scratch, CancellationToken ct = default)
    {
        static Task None() => Task.CompletedTask;
        var (cfg, repo, _) = HubOf(projectKey);
        if (!IsGitRepo(repo)) return (null, None, $"{cfg.DisplayName} has no git repo at '{repo}'.");

        var head = await ShaAsync(repo, "HEAD", ct);
        var dirty = (await GitAsync(repo, ct, "status", "--porcelain", "--untracked-files=no")).Stdout.Trim().Length > 0;
        if (head == sha && !dirty) return (repo, None, null);

        var dir = Path.Combine(scratch, "unit-" + sha[..Math.Min(12, sha.Length)]);
        if (Directory.Exists(dir)) await GitAsync(repo, ct, "worktree", "remove", "--force", dir);
        var add = await GitAsync(repo, ct, TimeSpan.FromMinutes(5), "worktree", "add", "--detach", dir, sha);
        if (!add.Ok) return (null, None, Explain(add, $"Could not check out {sha[..Math.Min(7, sha.Length)]} to read"));

        return (dir, async () =>
        {
            await GitAsync(repo, CancellationToken.None, "worktree", "remove", "--force", dir);
        }, null);
    }

    private async Task<string?> ShaAsync(string repo, string rev, CancellationToken ct)
    {
        var r = await GitAsync(repo, ct, "rev-parse", "--verify", "--quiet", rev);
        return r.Ok && r.Stdout.Trim().Length > 0 ? r.Stdout.Trim() : null;
    }
}
