using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Whetstone.Services;

/// <summary>One repo's part of a fix: the commit it forked from and the commit it ends at.</summary>
public sealed record FixRange(string Repo, string Path, string Parent, string Tip);

/// <summary>A commit a ticket delivered, as read from git.</summary>
public sealed record DeliveredCommit(string Sha, string? PatchId, string Subject);

/// <summary>
/// Blamed lines that came from one commit. <paramref name="Weight"/> counts a changed line as one and
/// a neighbour of an insertion as half; <paramref name="Neighbours"/> says every line was a neighbour.
/// </summary>
public sealed record BlamedCommit(string Repo, string Sha, string Subject, double Weight, bool Neighbours,
    IReadOnlyList<string> Files);

/// <summary>
/// The git half of bug tracing (docs/bugs-and-experiments-plan.md, 1.3 and 1.4): where a fix sits,
/// which commits wrote the lines it changed, and which commits a ticket delivered. Read-only: it
/// runs <c>diff</c>, <c>blame</c> and <c>rev-list</c> in the hub and never moves a ref.
/// </summary>
public partial class TicketGitService
{
    /// <summary>Most files one fix is blamed in, and most lines - a fix bigger than this is a rewrite, not a fix.</summary>
    private const int MaxBlameFiles = 200;
    private const int MaxBlameLines = 5000;

    private static readonly Regex HunkHeader = new(@"^@@ -(\d+)(?:,(\d+))? \+\d+(?:,\d+)? @@", RegexOptions.Compiled);
    private static readonly Regex PorcelainHeader = new(@"^([0-9a-f]{40}) \d+ (\d+)(?: \d+)?$", RegexOptions.Compiled);

    /// <summary>Generated or vendored: where a bug is never written, only rebuilt.</summary>
    public static bool IsBlameNoise(string path)
    {
        var p = path.Replace('\\', '/');
        var name = p[(p.LastIndexOf('/') + 1)..];
        if (name is "package-lock.json" or "yarn.lock" or "pnpm-lock.yaml" or "packages.lock.json" or "Cargo.lock"
            or "poetry.lock" or "uv.lock" or "composer.lock" or "Gemfile.lock") return true;
        return p.StartsWith("wwwroot/") || p.Contains("/wwwroot/")
            || p.StartsWith("dist/") || p.Contains("/dist/")
            || p.StartsWith("node_modules/") || p.Contains("/node_modules/");
    }

    /// <summary>
    /// Where a ticket's fix sits in each repo of its project. A repo whose range changes nothing is
    /// left out, which is the same answer <see cref="TouchedAsync"/> gives without needing a worktree.
    /// </summary>
    /// <param name="mergeSha">The board's merge of the ticket, when it made one.</param>
    /// <param name="branch">The ticket's branch, when it still exists.</param>
    /// <param name="recorded">Commits recorded as the ticket's, by repo: the fallback when neither of the above is left.</param>
    public async Task<IReadOnlyList<FixRange>> FixRangesAsync(string projectKey, string? mergeSha, string? branch,
        IReadOnlyDictionary<string, IReadOnlyList<string>> recorded, CancellationToken ct = default)
    {
        var cfg = _registry.GetProjectConfig(projectKey);
        var ranges = new List<FixRange>();
        foreach (var target in TargetsOf(cfg))
        {
            if (target.ReadOnly || !IsGitRepo(target.Path)) continue;
            var range = await FixRangeInAsync(cfg, target, mergeSha, branch,
                recorded.TryGetValue(target.Dir, out var shas) ? shas : Array.Empty<string>(), ct);
            if (range is null) continue;
            var changed = await GitAsync(target.Path, ct, "diff", "--quiet", range.Parent, range.Tip);
            if (changed.ExitCode == 1) ranges.Add(range);
        }
        return ranges;
    }

    private async Task<FixRange?> FixRangeInAsync(ProjectConfig cfg, RepoTarget target, string? mergeSha, string? branch,
        IReadOnlyList<string> recorded, CancellationToken ct)
    {
        // A board merge is --no-ff: the fix is its second parent, forked where the two parents meet.
        if (mergeSha is { Length: > 0 } && await ShaOfAsync(target.Path, $"{mergeSha}^2", ct) is { } tip2
            && await ShaOfAsync(target.Path, $"{mergeSha}^1", ct) is { } first)
        {
            var fork = (await GitAsync(target.Path, ct, "merge-base", first, tip2)).Stdout.Trim();
            if (fork.Length > 0) return new FixRange(target.Dir, target.Path, fork, tip2);
        }

        if (branch is { Length: > 0 } && await ShaOfAsync(target.Path, $"refs/heads/{branch}", ct) is { } tip
            && await BaseRefAsync(target.Path, cfg, target.BaseBranch, ct) is { } baseRef)
        {
            var fork = (await GitAsync(target.Path, ct, "merge-base", baseRef, tip)).Stdout.Trim();
            if (fork.Length > 0 && fork != tip) return new FixRange(target.Dir, target.Path, fork, tip);
        }

        // Nothing left but what was recorded: the newest of the ticket's commits that still exists,
        // and the oldest one's parent.
        var present = new List<string>();
        foreach (var sha in recorded)
            if (await ShaOfAsync(target.Path, $"{sha}^{{commit}}", ct) is { } s) present.Add(s);
        if (present.Count == 0) return null;
        var ordered = (await GitAsync(target.Path, ct, new[] { "rev-list", "--topo-order", "--no-walk" }.Concat(present).ToArray()))
            .Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (ordered.Count == 0) return null;
        var newest = ordered[0];
        var oldestParent = await ShaOfAsync(target.Path, $"{ordered[^1]}^1", ct);
        return oldestParent is null ? null : new FixRange(target.Dir, target.Path, oldestParent, newest);
    }

    /// <summary>
    /// Which commits wrote the lines a fix changed or removed, blamed at the fix's parent with
    /// whitespace ignored and moves and same-commit copies followed (<c>-w -M -C</c>). A fix that
    /// only adds lines has its insertion points' neighbours blamed instead, at half weight.
    /// </summary>
    public async Task<IReadOnlyList<BlamedCommit>> BlameFixAsync(FixRange range, CancellationToken ct = default)
    {
        var diff = await GitAsync(range.Path, ct, TimeSpan.FromMinutes(2),
            "diff", "-U0", "-w", "-M", "--no-color", "--no-ext-diff", range.Parent, range.Tip);
        if (!diff.Ok) return Array.Empty<BlamedCommit>();

        // Old-side line -> weight, per old path. A changed line is 1; a neighbour of an insertion 0.5.
        var perFile = new Dictionary<string, Dictionary<int, double>>();
        var insertionsIn = new HashSet<string>();
        string? oldPath = null;
        foreach (var raw in diff.Stdout.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("diff --git ")) { oldPath = null; continue; }
            if (line.StartsWith("--- "))
            {
                var p = line[4..];
                oldPath = p == "/dev/null" ? null : p.StartsWith("a/") ? p[2..] : p;
                continue;
            }
            if (oldPath is null || IsBlameNoise(oldPath)) continue;
            var m = HunkHeader.Match(line);
            if (!m.Success) continue;
            var start = int.Parse(m.Groups[1].Value);
            var count = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 1;
            var lines = perFile.TryGetValue(oldPath, out var l) ? l : perFile[oldPath] = new();
            if (count > 0)
                for (var i = start; i < start + count; i++) lines[i] = 1;
            else
            {
                // An insertion after old line `start`: the line before it, and the line after it.
                if (start >= 1) lines.TryAdd(start, 0.5);
                lines.TryAdd(start + 1, 0.5);
                insertionsIn.Add(oldPath);
            }
        }

        var ignoreRevs = File.Exists(Path.Combine(range.Path, ".git-blame-ignore-revs"));
        var bySha = new Dictionary<string, (double Weight, bool AllNeighbours, string Subject, HashSet<string> Files)>();
        var total = 0;
        foreach (var (file, wanted) in perFile.Take(MaxBlameFiles))
        {
            if (total >= MaxBlameLines) break;
            // A neighbour past the end of the file is not a line; the blame would refuse the range.
            if (insertionsIn.Contains(file))
            {
                var length = await LineCountAsync(range.Path, range.Parent, file, ct);
                foreach (var n in wanted.Keys.Where(k => k > length).ToList()) wanted.Remove(n);
            }
            if (wanted.Count == 0) continue;

            var args = new List<string> { "blame", "--porcelain", "-w", "-M", "-C" };
            if (ignoreRevs) args.AddRange(new[] { "--ignore-revs-file", ".git-blame-ignore-revs" });
            foreach (var (a, b) in Runs(wanted.Keys)) args.AddRange(new[] { "-L", $"{a},{b}" });
            args.AddRange(new[] { range.Parent, "--", file });

            var blame = await GitAsync(range.Path, ct, TimeSpan.FromMinutes(2), args.ToArray());
            if (!blame.Ok) continue;

            var subjects = new Dictionary<string, string>();
            string? current = null;
            foreach (var raw in blame.Stdout.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                var h = PorcelainHeader.Match(line);
                if (h.Success)
                {
                    current = h.Groups[1].Value;
                    var final = int.Parse(h.Groups[2].Value);
                    var weight = wanted.TryGetValue(final, out var w) ? w : 1;
                    var entry = bySha.TryGetValue(current, out var e) ? e : (Weight: 0.0, AllNeighbours: true, Subject: "", Files: new HashSet<string>());
                    entry.Weight += weight;
                    entry.AllNeighbours &= weight < 1;
                    entry.Files.Add(Prefixed(new RepoTarget(range.Repo, range.Path, "", false), file));
                    bySha[current] = entry;
                    total++;
                    continue;
                }
                if (current is not null && line.StartsWith("summary ")) subjects[current] = line[8..];
            }
            foreach (var (sha, subject) in subjects)
                if (bySha.TryGetValue(sha, out var e) && e.Subject.Length == 0) bySha[sha] = e with { Subject = subject };
        }

        return bySha
            // The all-zero sha is git's "not committed yet", which a blame at a commit never reports;
            // guarded anyway, because it would map to nothing and read as a commit.
            .Where(kv => kv.Key.Trim('0').Length > 0)
            .Select(kv => new BlamedCommit(range.Repo, kv.Key, kv.Value.Subject, kv.Value.Weight, kv.Value.AllNeighbours,
                kv.Value.Files.OrderBy(f => f).ToList()))
            .OrderByDescending(c => c.Weight)
            .ToList();
    }

    /// <summary>Consecutive line numbers folded into <c>(first, last)</c> runs, for <c>blame -L</c>.</summary>
    private static IEnumerable<(int, int)> Runs(IEnumerable<int> lines)
    {
        int? a = null, b = null;
        foreach (var n in lines.OrderBy(x => x))
        {
            if (a is null) { a = b = n; continue; }
            if (n == b + 1) { b = n; continue; }
            yield return (a.Value, b!.Value);
            a = b = n;
        }
        if (a is not null) yield return (a.Value, b!.Value);
    }

    private async Task<int> LineCountAsync(string repo, string rev, string file, CancellationToken ct)
    {
        var show = await GitAsync(repo, ct, "show", $"{rev}:{file}");
        if (!show.Ok) return 0;
        var text = show.Stdout;
        var n = text.Count(c => c == '\n');
        return text.Length > 0 && !text.EndsWith('\n') ? n + 1 : n;
    }

    /// <summary>
    /// The commits in <c>from..to</c>, oldest first, each with a patch id worked out the way
    /// <c>git patch-id --stable</c> does it in spirit - the changed lines with whitespace removed and
    /// no line numbers - so the same change rebased or squashed from one commit hashes the same.
    /// </summary>
    public async Task<IReadOnlyList<DeliveredCommit>> CommitsInAsync(string repoPath, string from, string to,
        CancellationToken ct = default)
    {
        var log = await GitAsync(repoPath, ct, "log", "--reverse", "--no-merges", "--format=%H%x09%s", $"{from}..{to}");
        if (!log.Ok) return Array.Empty<DeliveredCommit>();
        var result = new List<DeliveredCommit>();
        foreach (var line in log.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.TrimEnd('\r').Split('\t', 2);
            if (parts[0].Length < 40) continue;
            result.Add(new DeliveredCommit(parts[0], await PatchIdAsync(repoPath, parts[0], ct), parts.Length > 1 ? parts[1] : ""));
            if (result.Count >= 500) break;
        }
        return result;
    }

    /// <summary>One commit's patch id, as <see cref="CommitsInAsync"/> works it out. Null when git cannot show it.</summary>
    public async Task<string?> PatchIdAsync(string repoPath, string sha, CancellationToken ct = default)
    {
        var show = await GitAsync(repoPath, ct, TimeSpan.FromMinutes(1),
            "diff-tree", "-p", "--no-color", "--no-ext-diff", "-U0", "--no-commit-id", "-r", sha);
        return show.Ok ? PatchIdOf(show.Stdout) : null;
    }

    /// <summary>The patch id of a diff's text: its file headers and changed lines, whitespace dropped.</summary>
    public static string? PatchIdOf(string diff)
    {
        var sb = new StringBuilder();
        foreach (var raw in diff.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("diff --git ") || line.StartsWith("+++ ") || line.StartsWith("--- "))
            { sb.Append(line).Append('\n'); continue; }
            if (line.Length > 0 && (line[0] == '+' || line[0] == '-'))
                sb.Append(line[0]).Append(Regex.Replace(line[1..], @"\s+", "")).Append('\n');
        }
        if (sb.Length == 0) return null;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..40].ToLowerInvariant();
    }

    /// <summary>
    /// The commits a ticket delivered, per repo, from a board merge (<paramref name="mergeSha"/>'s
    /// second parent against its first) or else from its branch against its base ref.
    /// </summary>
    public async Task<IReadOnlyList<(string Repo, string Source, DeliveredCommit Commit)>> DeliveredAsync(
        string projectKey, string? mergeSha, string? branch, CancellationToken ct = default)
    {
        var cfg = _registry.GetProjectConfig(projectKey);
        var result = new List<(string, string, DeliveredCommit)>();
        foreach (var target in TargetsOf(cfg))
        {
            if (target.ReadOnly || !IsGitRepo(target.Path)) continue;

            if (mergeSha is { Length: > 0 } && await ShaOfAsync(target.Path, $"{mergeSha}^{{commit}}", ct) is { } merge)
            {
                var second = await ShaOfAsync(target.Path, $"{merge}^2", ct);
                if (second is not null)
                {
                    foreach (var c in await CommitsInAsync(target.Path, $"{merge}^1", second, ct)) result.Add((target.Dir, "merge", c));
                    continue;
                }
            }

            if (branch is { Length: > 0 } && await ShaOfAsync(target.Path, $"refs/heads/{branch}", ct) is { } tip
                && await BaseRefAsync(target.Path, cfg, target.BaseBranch, ct) is { } baseRef)
                foreach (var c in await CommitsInAsync(target.Path, baseRef, tip, ct)) result.Add((target.Dir, "branch", c));
        }
        return result;
    }

    /// <summary>Whether <paramref name="ancestor"/> is <paramref name="of"/> or one of its ancestors. False when either is gone.</summary>
    public async Task<bool> IsAncestorAsync(string projectKey, string repo, string ancestor, string of, CancellationToken ct = default)
    {
        var target = TargetOf(_registry.GetProjectConfig(projectKey), repo.Length == 0 ? null : repo, out _);
        if (target is null || !IsGitRepo(target.Path)) return false;
        return (await GitAsync(target.Path, ct, "merge-base", "--is-ancestor", ancestor, of)).ExitCode == 0;
    }
}
