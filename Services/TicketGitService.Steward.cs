using System.Text.RegularExpressions;

namespace Whetstone.Services;

/// <summary>One unit of a project's plan, as committed on its base branch.</summary>
/// <param name="Number">The leading number of its file name: units are worked in this order.</param>
/// <param name="Agreed">A person has marked it agreed. Only an agreed unit may be worked.</param>
/// <param name="Draft">It says it is a draft: written, and waiting for its owner.</param>
public record PlanUnit(int Number, string File, bool Agreed, bool Draft, IReadOnlyList<string> Outcomes, string Text);

/// <summary>One outcome of a unit, with the sentence its spec gives it.</summary>
public record PlanOutcomeText(string Outcome, string Text);

/// <summary>The outcomes that stand under one heading of a unit's spec.</summary>
public record PlanGroup(string Heading, IReadOnlyList<PlanOutcomeText> Outcomes);

/// <summary>What a ticket's branch changes against its base, by name and by count.</summary>
/// <param name="Tip">The branch's head commit.</param>
/// <param name="ChangeId">
/// The change itself, as a hash of its diff with the line numbers and blob ids left out: the same
/// after a rebase that moved nothing of the ticket's own, different after any rework of it. What an
/// owner's clearance of a flag is tied to (<see cref="Whetstone.Models.StewardClearance"/>).
/// </param>
public record BranchChange(IReadOnlyList<string> Files, int Behind, int Ahead, string Stat, string BaseRef,
    string? Tip = null, string? ChangeId = null);

/// <summary>Outcome of bringing another branch into the base before a steward's run.</summary>
public record SyncFromResult(string Answer, int Commits, IReadOnlyList<string> Files, string? Why);

/// <summary>Outcome of one command run on a ticket's worktree before it is merged.</summary>
/// <param name="Output">The last lines of what the check printed.</param>
/// <param name="Failures">
/// The lines from the WHOLE output that name what failed - a failing test, an assertion, a compiler
/// error - picked out because the tail alone often ends on a summary that names none of them.
/// </param>
public record CheckRun(bool Ok, bool TimedOut, string Output, IReadOnlyList<string>? Failures = null);

/// <summary>Picks out of a check's whole output the lines that say what failed.</summary>
public static class CheckFailures
{
    private static readonly System.Text.RegularExpressions.Regex Failing = new(
        @"(^\s*(FAIL|FAILED|not ok)\b)|(^\s*[\u25CF\u2715\u2717\u00D7\u274C]\s)|AssertionError|Assertion failed|" +
        @"\bExpected[: ]|\bReceived:|\berror (TS|CS)\d+|\b[1-9]\d* (failed|failing)\b|^\s*Error:|\bFailed [A-Za-z_.]+\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Each line that names a failure, with the line after it for context, in order, each cut to a
    /// readable length and the whole held to <paramref name="max"/> lines.
    /// </summary>
    public static IReadOnlyList<string> Pick(IReadOnlyList<string> lines, int max = 60)
    {
        var picked = new List<string>();
        var taken = new HashSet<int>();
        for (var i = 0; i < lines.Count && picked.Count < max; i++)
        {
            if (!Failing.IsMatch(lines[i])) continue;
            foreach (var j in new[] { i, i + 1 })
            {
                if (j >= lines.Count || !taken.Add(j) || picked.Count >= max) continue;
                var l = lines[j].TrimEnd();
                picked.Add(l.Length > 300 ? l[..300] + " ..." : l);
            }
        }
        return picked;
    }
}

/// <summary>
/// The git a project's steward needs, and nothing else it could ask for.
/// <para>
/// Everything here reads the base branch as COMMITTED, in the hub, and the two things that write
/// (<see cref="SyncFromAsync"/> and <see cref="CommitFileAsync"/>) refuse unless the hub is on its
/// base branch with a clean tree - the same condition <see cref="MergeAsync"/> holds, for the same
/// reason: the hub is a person's checkout too, and nothing unattended moves it or commits over
/// work in progress.
/// </para>
/// </summary>
public partial class TicketGitService
{
    private (ProjectConfig Cfg, string Repo, string Base) HubOf(string projectKey)
    {
        var cfg = _registry.GetProjectConfig(projectKey);
        return (cfg, cfg.ProjectPath?.Trim().TrimEnd('\\', '/') ?? "", ResolveBaseBranch(cfg));
    }

    /// <summary>Why the hub cannot be written to right now, or null when it can.</summary>
    private async Task<string?> HubNotWritableAsync(string repo, string baseBranch, CancellationToken ct)
    {
        if (!IsGitRepo(repo)) return $"There is no git repo at '{repo}'.";
        var head = (await GitAsync(repo, ct, "symbolic-ref", "--quiet", "--short", "HEAD")).Stdout.Trim();
        if (head != baseBranch)
            return $"{repo} is on '{(head.Length > 0 ? head : "a detached HEAD")}', not '{baseBranch}'.";
        if ((await GitAsync(repo, ct, "rev-parse", "--verify", "--quiet", "MERGE_HEAD")).Ok)
            return $"A merge is in progress in {repo}.";
        var dirty = (await GitAsync(repo, ct, "status", "--porcelain", "--untracked-files=no")).Stdout
            .Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToList();
        return dirty.Count > 0
            ? $"{repo} has uncommitted changes: {string.Join(", ", dirty.Take(8).Select(l => l.Length > 3 ? l[3..] : l))}"
            : null;
    }

    /// <summary>The base branch's head in the hub, or null when it cannot be read.</summary>
    public async Task<string?> BaseHeadAsync(string projectKey, CancellationToken ct = default)
    {
        var (_, repo, baseBranch) = HubOf(projectKey);
        if (!IsGitRepo(repo)) return null;
        var r = await GitAsync(repo, ct, "rev-parse", "--verify", "--quiet", $"refs/heads/{baseBranch}");
        return r.Ok ? r.Stdout.Trim() : null;
    }

    /// <summary>
    /// The project's plan: every unit spec in the configured folder, read from the base branch's
    /// last commit. Empty when the project has no plan configured or the folder does not exist yet.
    /// </summary>
    public Task<IReadOnlyList<PlanUnit>> PlanAsync(string projectKey, CancellationToken ct = default) =>
        PlanOfAsync(projectKey, _registry.GetProjectConfig(projectKey).Steward.Plan, ct);

    /// <summary>
    /// The units of one plan - the one being worked or one already finished - read the same way.
    /// </summary>
    public async Task<IReadOnlyList<PlanUnit>> PlanOfAsync(string projectKey, StewardPlan plan, CancellationToken ct = default)
    {
        var (_, repo, baseBranch) = HubOf(projectKey);
        if (!plan.Configured || !IsGitRepo(repo)) return Array.Empty<PlanUnit>();

        var ls = await GitAsync(repo, ct, "ls-tree", "--name-only", $"refs/heads/{baseBranch}", $"{plan.UnitsDir}/");
        if (!ls.Ok) return Array.Empty<PlanUnit>();

        var unitFile = new Regex(plan.UnitFile, RegexOptions.IgnoreCase);
        var units = new List<PlanUnit>();
        foreach (var path in ls.Stdout.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
        {
            var m = unitFile.Match(path[(path.LastIndexOf('/') + 1)..]);
            if (!m.Success || m.Groups.Count < 2 || !int.TryParse(m.Groups[1].Value, out var number)) continue;
            var show = await GitAsync(repo, ct, "show", $"refs/heads/{baseBranch}:{path}");
            if (!show.Ok) continue;
            units.Add(ReadUnit(plan, number, path, show.Stdout));
        }
        return units.OrderBy(u => u.Number).ToList();
    }

    /// <summary>One spec's text as a unit. Agreed wins over draft: a spec cannot be both, and a
    /// stray "draft" further down an agreed one must not un-agree it.</summary>
    public static PlanUnit ReadUnit(StewardPlan plan, int number, string file, string text)
    {
        const RegexOptions o = RegexOptions.IgnoreCase | RegexOptions.Multiline;
        var agreed = Regex.IsMatch(text, plan.Agreed, o);
        var outcomes = Regex.Matches(text, plan.Outcome, RegexOptions.Multiline)
            .Select(m => m.Groups.Count > 1 ? m.Groups[1].Value : m.Value).Distinct().ToList();
        return new PlanUnit(number, file, agreed, !agreed && Regex.IsMatch(text, plan.Draft, o), outcomes, text);
    }

    /// <summary>
    /// A draft spec's text with its status rewritten to agreed, or null when that cannot be done by
    /// rule. The bold run that says "draft" is replaced whole by the plan's agreed line, and the
    /// result has to read as agreed and not as a draft by the plan's own patterns - so a spec
    /// whose status is written some other way is left for its owner to edit by hand, never guessed at.
    /// </summary>
    public static string? MarkAgreed(StewardPlan plan, string text, DateTime day)
    {
        const RegexOptions o = RegexOptions.IgnoreCase | RegexOptions.Multiline;
        var m = Regex.Match(text, plan.Draft, o);
        if (!m.Success) return null;
        // The status is a bold run: from where the draft pattern starts to the "**" that closes it,
        // on the same line.
        var lineEnd = text.IndexOf('\n', m.Index);
        if (lineEnd < 0) lineEnd = text.Length;
        var from = m.Index + (m.Value.StartsWith("**") ? 2 : 0);
        var close = text.IndexOf("**", Math.Min(from, text.Length), StringComparison.Ordinal);
        if (close < 0 || close >= lineEnd) return null;

        var line = plan.AgreedLine.Replace("{date}", day.ToString("yyyy-MM-dd"));
        var marked = text[..m.Index] + line + text[(close + 2)..];
        var read = ReadUnit(plan, 0, "", marked);
        return read.Agreed && !Regex.IsMatch(marked, plan.Draft, o) ? marked : null;
    }

    /// <summary>A unit's own name: its spec's first heading, or its file name when it has none.</summary>
    public static string UnitTitle(PlanUnit unit)
    {
        var m = Regex.Match(unit.Text, @"^#\s+(.+?)\s*$", RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value.Trim() : Path.GetFileNameWithoutExtension(unit.File);
    }

    /// <summary>
    /// A unit's outcomes as its spec lays them out: each with its sentence, under the heading it
    /// stands beneath. An id is taken once, where it is first defined, as <see cref="ReadUnit"/>
    /// counts it. An outcome's sentence runs on over the lines that continue its bullet.
    /// </summary>
    public static IReadOnlyList<PlanGroup> UnitGroups(StewardPlan plan, PlanUnit unit)
    {
        var outcome = new Regex(plan.Outcome);
        var groups = new List<(string Heading, List<PlanOutcomeText> Outcomes)>();
        var seen = new HashSet<string>();
        var heading = "";
        (string Id, System.Text.StringBuilder Text)? open = null;

        void Close()
        {
            if (open is not { } o) return;
            if (groups.Count == 0 || groups[^1].Heading != heading) groups.Add((heading, new()));
            groups[^1].Outcomes.Add(new(o.Id, Regex.Replace(o.Text.ToString(), @"\s+", " ").Trim()));
            open = null;
        }

        foreach (var raw in unit.Text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var h = Regex.Match(line, @"^#{1,6}\s+(.+?)\s*$");
            var m = outcome.Match(line);
            if (h.Success) { Close(); heading = h.Groups[1].Value; }
            else if (m.Success)
            {
                Close();
                var id = m.Groups.Count > 1 ? m.Groups[1].Value : m.Value;
                if (seen.Add(id)) open = (id, new System.Text.StringBuilder(line[(m.Index + m.Length)..]));
            }
            // A blank line or the next bullet ends the sentence; an indented line carries it on.
            else if (open is not null && line.Length > 0 && char.IsWhiteSpace(line[0]) && line.Trim().Length > 0
                     && !Regex.IsMatch(line, @"^\s*[-*]\s")) open.Value.Text.Append(' ').Append(line.Trim());
            else Close();
        }
        Close();
        return groups.Select(g => new PlanGroup(g.Heading, g.Outcomes)).ToList();
    }

    /// <summary>
    /// One file as committed on the base branch, or null when it is not there. For reading a
    /// plan's own document; nothing is decided from what this returns.
    /// </summary>
    public async Task<string?> FileAtBaseAsync(string projectKey, string path, CancellationToken ct = default)
    {
        var (_, repo, baseBranch) = HubOf(projectKey);
        var rel = path.Replace('\\', '/').Trim('/');
        if (rel.Length == 0 || rel.Split('/').Contains("..") || !IsGitRepo(repo)) return null;
        var show = await GitAsync(repo, ct, "show", $"refs/heads/{baseBranch}:{rel}");
        return show.Ok ? show.Stdout : null;
    }

    /// <summary>
    /// What <paramref name="branch"/> changes against the project's base ref, or null when the
    /// branch does not exist. The base ref is the same one a worker forks from and a person reviews
    /// against (<see cref="BaseRefAsync"/>).
    /// </summary>
    public async Task<BranchChange?> BranchChangeAsync(string projectKey, string branch, CancellationToken ct = default)
    {
        var (cfg, repo, baseBranch) = HubOf(projectKey);
        if (string.IsNullOrWhiteSpace(branch) || !IsGitRepo(repo)) return null;
        if (!(await GitAsync(repo, ct, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}")).Ok) return null;
        var baseRef = await BaseRefAsync(repo, cfg, baseBranch, ct);
        if (baseRef is null) return null;

        var range = $"{baseRef}...{branch}";
        var files = (await GitAsync(repo, ct, "diff", "--name-only", "--no-renames", range)).Stdout
            .Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var (behind, ahead) = await CountAsync(repo, baseRef, branch, ct);
        var stat = (await GitAsync(repo, ct, "diff", "--shortstat", range)).Stdout.Trim();
        var tip = (await GitAsync(repo, ct, "rev-parse", $"refs/heads/{branch}")).Stdout.Trim();
        var diff = await GitAsync(repo, ct, TimeSpan.FromSeconds(60), "diff", "--no-renames", "--no-color", range);
        return new BranchChange(files, behind, ahead, stat, baseRef, tip, diff.Ok ? ChangeIdOf(diff.Stdout) : null);
    }

    /// <summary>
    /// A diff's identity across rebases, like <c>git patch-id</c>: the hunk headers' line numbers and
    /// the <c>index</c> lines' blob ids are what a rebase moves without changing the change.
    /// </summary>
    public static string ChangeIdOf(string diff)
    {
        var kept = diff.Replace("\r\n", "\n").Split('\n')
            .Where(l => !l.StartsWith("index ", StringComparison.Ordinal))
            .Select(l => l.StartsWith("@@", StringComparison.Ordinal) ? Regex.Replace(l, @"^@@[^@]*@@", "@@") : l);
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", kept)));
        return Convert.ToHexString(hash)[..40].ToLowerInvariant();
    }

    /// <summary>
    /// The branch's change as one diff, cut at <paramref name="maxChars"/>. For one file when
    /// <paramref name="path"/> is given - which is how a reader gets the rest of a diff that was cut.
    /// </summary>
    public async Task<(string Text, bool Cut, int Length)?> BranchDiffAsync(
        string projectKey, string branch, string? path, int maxChars, CancellationToken ct = default)
    {
        var (cfg, repo, baseBranch) = HubOf(projectKey);
        if (string.IsNullOrWhiteSpace(branch) || !IsGitRepo(repo)) return null;
        var baseRef = await BaseRefAsync(repo, cfg, baseBranch, ct);
        if (baseRef is null) return null;
        var args = new List<string> { "diff", $"{baseRef}...{branch}" };
        if (!string.IsNullOrWhiteSpace(path)) { args.Add("--"); args.Add(path.Trim()); }
        var r = await GitAsync(repo, ct, TimeSpan.FromSeconds(60), args.ToArray());
        if (!r.Ok) return null;
        return r.Stdout.Length > maxChars ? (r.Stdout[..maxChars], true, r.Stdout.Length) : (r.Stdout, false, r.Stdout.Length);
    }

    /// <summary>How many commits <paramref name="from"/> has that the base does not.</summary>
    public async Task<int> AheadOfBaseAsync(string projectKey, string from, CancellationToken ct = default)
    {
        var (_, repo, baseBranch) = HubOf(projectKey);
        if (string.IsNullOrWhiteSpace(from) || !IsGitRepo(repo)) return 0;
        if (!(await GitAsync(repo, ct, "rev-parse", "--verify", "--quiet", $"refs/heads/{from}")).Ok) return 0;
        var r = await GitAsync(repo, ct, "rev-list", "--count", $"refs/heads/{baseBranch}..refs/heads/{from}");
        return r.Ok && int.TryParse(r.Stdout.Trim(), out var n) ? n : 0;
    }

    /// <summary>
    /// Merge the local branch <paramref name="from"/> into the base, in the hub. A conflict is
    /// aborted and changes nothing. With <paramref name="dryRun"/> it only says what it would do.
    /// </summary>
    public async Task<SyncFromResult> SyncFromAsync(string projectKey, string from, bool dryRun, CancellationToken ct = default)
    {
        var (_, repo, baseBranch) = HubOf(projectKey);
        var none = Array.Empty<string>();
        var waiting = await AheadOfBaseAsync(projectKey, from, ct);
        if (waiting == 0) return new("up-to-date", 0, none, null);
        if (dryRun) return new("proposed", waiting, none, $"merge {waiting} commit(s) from {from} into {baseBranch}");
        if (await HubNotWritableAsync(repo, baseBranch, ct) is { } no) return new("refused", waiting, none, no);

        var merge = await GitAsync(repo, ct, TimeSpan.FromMinutes(2),
            "merge", "--no-ff", "--no-edit", "-m", $"Merge {from} into {baseBranch}", from);
        if (!merge.Ok)
        {
            var conflicted = (await GitAsync(repo, ct, "diff", "--name-only", "--diff-filter=U")).Stdout
                .Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            await GitAsync(repo, ct, "merge", "--abort");
            return new("conflict", waiting, conflicted, "nothing was changed");
        }
        var files = (await GitAsync(repo, ct, "diff", "--name-only", "HEAD~1", "HEAD")).Stdout
            .Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        return new("merged", waiting, files, null);
    }

    /// <summary>
    /// Write one file in the hub and commit exactly that file to the base branch. Returns the new
    /// commit, "unchanged" when the file already said this, or a refusal.
    /// </summary>
    public async Task<(string? Sha, bool Unchanged, string? Refusal)> CommitFileAsync(
        string projectKey, string relativePath, string content, string message, CancellationToken ct = default)
    {
        var (_, repo, baseBranch) = HubOf(projectKey);
        if (await HubNotWritableAsync(repo, baseBranch, ct) is { } no) return (null, false, no);

        var rel = relativePath.Replace('\\', '/').TrimStart('/');
        var full = Path.GetFullPath(Path.Combine(repo, rel));
        if (!full.StartsWith(Path.GetFullPath(repo) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return (null, false, $"'{relativePath}' is outside the repo.");

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, content.Replace("\r\n", "\n").TrimEnd() + "\n", ct);
        await GitAsync(repo, ct, "add", "--", rel);
        if ((await GitAsync(repo, ct, "diff", "--cached", "--quiet", "--", rel)).Ok) return (null, true, null);

        var commit = await GitAsync(repo, ct, "commit", "--quiet", "-m", message, "--", rel);
        if (!commit.Ok)
        {
            // Leave nothing staged or half-written behind: the hub was clean when this began.
            await GitAsync(repo, ct, "reset", "--quiet", "--", rel);
            await GitAsync(repo, ct, "checkout", "--", rel);
            return (null, false, Explain(commit, "could not commit the file"));
        }
        return ((await GitAsync(repo, ct, "rev-parse", "--short", "HEAD")).Stdout.Trim(), false, null);
    }

    /// <summary>Whether <paramref name="worktree"/> is checked out at the tip of <paramref name="branch"/>.</summary>
    public async Task<bool> WorktreeAtTipAsync(string projectKey, string worktree, string branch, CancellationToken ct = default)
    {
        var (_, repo, _) = HubOf(projectKey);
        if (!Directory.Exists(worktree) || !IsGitRepo(repo)) return false;
        var tip = await GitAsync(repo, ct, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}");
        var at = await GitAsync(worktree, ct, "rev-parse", "HEAD");
        return tip.Ok && at.Ok && tip.Stdout.Trim() == at.Stdout.Trim();
    }

    /// <summary>
    /// Run one of a project's merge checks in <paramref name="dir"/>. The output is the tail of
    /// both streams, which is where a failing test run says what failed.
    /// </summary>
    public async Task<CheckRun> RunCheckAsync(string dir, string command, TimeSpan timeout, CancellationToken ct = default)
    {
        var r = await RunAsync("cmd.exe", dir, timeout, ct, "/d", "/c", command);
        var lines = $"{r.Stdout}\n{r.Stderr}".Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToList();
        return new CheckRun(r.Ok, r.ExitCode == -1 && r.Stderr.Contains("did not finish"),
            string.Join("\n", lines.Skip(Math.Max(0, lines.Count - 40))),
            r.Ok ? null : CheckFailures.Pick(lines));
    }
}
