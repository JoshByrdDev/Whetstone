using System.Text.Json;

namespace Whetstone.Services;

/// <summary>
/// One git repo of a project's code. A project that names none in <c>repos</c> has exactly one,
/// whose <see cref="Dir"/> is empty and whose folder is <see cref="ProjectConfig.ProjectPath"/>
/// itself - the set of one that every project was before there could be more. See
/// docs/multi-repo-plan.md.
/// </summary>
/// <param name="Dir">The member's folder, directly under <c>projectPath</c>. Empty for the set of one.</param>
/// <param name="BaseBranch">Already fallen back to the project's own; empty still means the global one.</param>
/// <param name="ReadOnly">In every workspace for reading, as a detached worktree with no branch, and never delivered.</param>
public sealed record RepoMember(
    string Dir, string BaseBranch, string[] WorktreeSetup, string[] WorktreeCopyFiles, bool ReadOnly)
{
    /// <summary>The member's hub - the human's own checkout of it - or empty with no project path.</summary>
    public string HubPath(string projectPath)
    {
        var root = projectPath?.Trim().TrimEnd('\\', '/') ?? "";
        if (root.Length == 0) return "";
        return Path.GetFullPath(Dir.Length == 0 ? root : Path.Combine(root, Dir));
    }
}

/// <summary>
/// Reads <c>repos</c>, and says what is wrong with where a project's code is.
/// <para>
/// A problem here is a refusal, not a warning: the claim route passes the project over and the
/// Setup check names why. The one that reaches a single-repo project is a <c>projectPath</c> that
/// exists and is not a git repo. The worker used to run in such a folder directly, outside any
/// worktree - which, pointed at a folder of several repos, meant inside the human's own checkouts.
/// </para>
/// </summary>
public static class RepoLayout
{
    /// <summary>How the check looks at the disk, swapped out in tests.</summary>
    public sealed record Probe(
        Func<string, bool> DirectoryExists,
        Func<string, bool> IsGitRepo,
        Func<string, IEnumerable<string>> ChildDirectories);

    /// <summary>The real disk. A repo is a folder with a <c>.git</c> in it, as <c>GitWorktreeService</c> asks.</summary>
    public static readonly Probe Disk = new(
        Directory.Exists,
        dir => !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir) && Path.Exists(Path.Combine(dir, ".git")),
        dir =>
        {
            try { return Directory.EnumerateDirectories(dir).ToList(); }
            catch { return Array.Empty<string>(); }
        });

    /// <summary>
    /// The <c>repos</c> list as written: each entry an object, or a bare folder name for one that
    /// needs nothing else. An entry that is neither is kept with an empty <c>dir</c>, so it is refused
    /// by <see cref="Problems"/> rather than quietly dropped - a dropped member is a repo the agent
    /// would never be shown. Absent or empty is no list, which is the set of one.
    /// </summary>
    public static RepoMember[] Read(Dictionary<string, JsonElement> cfg)
    {
        if (!cfg.TryGetValue("repos", out var v) || v.ValueKind != JsonValueKind.Array)
            return Array.Empty<RepoMember>();

        return v.EnumerateArray().Select(e => e.ValueKind switch
        {
            JsonValueKind.String => new RepoMember(e.GetString()!.Trim(), "", Array.Empty<string>(), Array.Empty<string>(), false),
            JsonValueKind.Object => new RepoMember(
                Str(e, "dir").Trim(),
                Str(e, "baseBranch").Trim(),
                StrArray(e, "worktreeSetup"),
                StrArray(e, "worktreeCopyFiles"),
                e.TryGetProperty("readOnly", out var ro) && ro.ValueKind == JsonValueKind.True),
            _ => new RepoMember("", "", Array.Empty<string>(), Array.Empty<string>(), false),
        }).ToArray();
    }

    /// <summary>
    /// Everything that stops this project being worked, each a sentence that says what to change.
    /// Empty when it can be. An unconfigured project (no <c>projectPath</c>, and no <c>repos</c>) is
    /// not this check's business, and neither is a path that does not exist yet.
    /// </summary>
    public static IReadOnlyList<string> Problems(ProjectConfig cfg, Probe probe)
    {
        var problems = new List<string>();
        var root = cfg.ProjectPath?.Trim().TrimEnd('\\', '/') ?? "";

        if (!cfg.IsMultiRepo)
        {
            if (root.Length == 0 || !probe.DirectoryExists(root) || probe.IsGitRepo(root)) return problems;
            var beneath = probe.ChildDirectories(root).Where(probe.IsGitRepo).Select(Path.GetFileName).ToList();
            problems.Add(beneath.Count > 0
                ? $"'{root}' is not a git repo, but {string.Join(", ", beneath)} beneath it are: this looks like a project of several repos. List them under \"repos\" (docs/multi-repo-plan.md), or point projectPath at one of them."
                : $"'{root}' is not a git repo, so a worker would run in it directly, outside any worktree. Point projectPath at the repo.");
            return problems;
        }

        if (cfg.IsTfvc)
            problems.Add("A project of several repos cannot use repoType tfvc.");
        if (cfg.MergeInsteadOfPr)
            problems.Add("A project of several repos delivers by pull request: merging several repos locally is not atomic, so mergeInsteadOfPr is refused.");
        if (root.Length == 0)
        {
            problems.Add("\"repos\" needs projectPath: the folder the repos are in.");
            return problems;
        }
        if (probe.IsGitRepo(root))
            problems.Add($"'{root}' is itself a git repo. The repos of a project of several repos sit side by side in a folder that is not one.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (m, i) in cfg.ConfiguredRepos.Select((m, i) => (m, i + 1)))
        {
            if (!IsPlainFolderName(m.Dir))
            {
                problems.Add(m.Dir.Length == 0
                    ? $"repos entry {i} names no folder: give it a \"dir\"."
                    : $"repos entry {i}, '{m.Dir}', must be a folder directly under projectPath.");
                continue;
            }
            if (!seen.Add(m.Dir))
            {
                problems.Add($"'{m.Dir}' is listed in repos more than once.");
                continue;
            }
            var hub = m.HubPath(root);
            if (!probe.IsGitRepo(hub))
                problems.Add(probe.DirectoryExists(hub)
                    ? $"'{hub}' is in repos but is not a git repo."
                    : $"'{hub}' is in repos but does not exist.");
        }

        if (cfg.ConfiguredRepos.All(m => m.ReadOnly))
            problems.Add("Every repo is readOnly, so no ticket could deliver anything. At least one must be writable.");

        return problems;
    }

    /// <summary>
    /// Why the worker cannot run this project yet, even with nothing wrong in it. The worker learns
    /// several repos in step 4 of docs/multi-repo-plan.md; until then a claim for one would run in
    /// <c>projectPath</c>, which is not a repo, so it is passed over. Removed with that step.
    /// </summary>
    public static string? NotYetRunnable(ProjectConfig cfg) => cfg.IsMultiRepo
        ? "Its config is read, but the worker does not run a project of several repos yet (docs/multi-repo-plan.md, step 4), so its tickets are passed over."
        : null;

    /// <summary>
    /// The projects a claim may take tickets from: <paramref name="candidates"/> less every one with
    /// a <see cref="Problems"/> or that is <see cref="NotYetRunnable"/>. Their tickets are passed
    /// over, never blocked - the fault is the project's config, not the ticket, and it clears the
    /// moment the config is fixed.
    /// </summary>
    public static string[] Claimable(IEnumerable<string> candidates, Func<string, ProjectConfig> configFor, Probe probe) =>
        candidates
            .Where(key =>
            {
                var cfg = configFor(key);
                return NotYetRunnable(cfg) is null && Problems(cfg, probe).Count == 0;
            })
            .ToArray();

    /// <summary>One folder name: not empty, not rooted, no separator, not a dot segment.</summary>
    private static bool IsPlainFolderName(string dir) =>
        dir.Length > 0 && dir != "." && dir != ".."
        && !Path.IsPathRooted(dir)
        && dir.IndexOfAny(new[] { '\\', '/', ':' }) < 0
        && dir.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static string Str(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string[] StrArray(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToArray()
            : Array.Empty<string>();
}
