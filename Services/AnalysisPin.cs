using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Whetstone.Models;

namespace Whetstone.Services;

/// <summary>One repo's commit and tree, as an analysis of a ticket across several repos read it.</summary>
public sealed record RepoHead(string Dir, string Head, string Tree);

/// <summary>
/// Which code an analysis read: what <see cref="TicketAnalysis.HeadSha"/> and
/// <see cref="TicketAnalysis.TreeSha"/> are compared against to say whether it is still about the
/// code on the branch, and whether refinement may count it.
/// <para>
/// A project of one repo is pinned exactly as it always was: the HEAD and the tree of the directory
/// the analysis reads. A project of several reads a workspace, which is a folder of repos and has no
/// HEAD of its own, so its pin is the HEAD and tree of each repo the ticket has changed - commits
/// ahead of that repo's base - kept in <see cref="Repos"/>. <see cref="HeadSha"/> and
/// <see cref="TreeSha"/> are then fingerprints of that list, so every comparison that has always
/// been made on those two fields (the gate's "is there an analysis of this code", the staleness
/// badge, the tree match that survives a squash) works on it unchanged. A repo the ticket left
/// alone is not in the pin: it is brought up to its base every phase, and that is not a change to
/// what was analysed.
/// </para>
/// <para>
/// A fingerprint is not a commit. Anything that reads code AT the pin - a file's diff, its text -
/// looks the repo up in <see cref="Repos"/> by the path's first segment
/// (<see cref="TicketGitService"/>'s <c>repoHeads</c>), never at <see cref="HeadSha"/>.
/// </para>
/// </summary>
public sealed record AnalysisPin(string? HeadSha, string? TreeSha, IReadOnlyList<RepoHead> Repos)
{
    public static readonly AnalysisPin None = new(null, null, Array.Empty<RepoHead>());

    /// <summary>Each pinned repo's commit, by its folder; null for a pin of one repo or of none.</summary>
    public IReadOnlyDictionary<string, string>? HeadsByRepo =>
        Repos.Count == 0 ? null : Repos.ToDictionary(r => r.Dir, r => r.Head, StringComparer.OrdinalIgnoreCase);

    /// <summary>The code on disk for this ticket now, read the same way the analysis run pins it.</summary>
    public static AnalysisPin Of(Ticket t, ProjectConfig cfg)
    {
        if (!cfg.IsMultiRepo)
        {
            var dir = AnalysisRunner.ReadDir(t, cfg);
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return None;
            var head = Git(dir, "rev-parse", "HEAD");
            return head is null ? None : new AnalysisPin(head, Git(dir, "rev-parse", "HEAD^{tree}"), Array.Empty<RepoHead>());
        }

        var workspace = t.WorktreePath;
        if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace)) return None;

        var repos = new List<RepoHead>();
        foreach (var m in cfg.Repos.Where(m => !m.ReadOnly))
        {
            var wt = Path.Combine(workspace, m.Dir);
            if (!Directory.Exists(wt) || Git(wt, "rev-parse", "HEAD") is not { } head) continue;
            var baseBranch = m.BaseBranch.Length > 0 ? m.BaseBranch : "main";
            // origin's when there is one: a project of several delivers by pull request, and that is
            // the rule Resolve-BaseRef and BaseRefAsync apply to such a project.
            var baseRef = Git(wt, "rev-parse", "--verify", "--quiet", $"refs/remotes/origin/{baseBranch}") is not null
                ? $"origin/{baseBranch}" : baseBranch;
            if (!int.TryParse(Git(wt, "rev-list", "--count", $"{baseRef}..HEAD"), out var ahead) || ahead == 0) continue;
            if (Git(wt, "rev-parse", "HEAD^{tree}") is not { } tree) continue;
            repos.Add(new RepoHead(m.Dir, head, tree));
        }
        return FromRepos(repos);
    }

    /// <summary>The pin of these repos: their fingerprints, or <see cref="None"/> for none.</summary>
    public static AnalysisPin FromRepos(IReadOnlyList<RepoHead> repos) =>
        repos.Count == 0
            ? None
            : new AnalysisPin(Fingerprint(repos.Select(r => $"{r.Dir} {r.Head}")),
                              Fingerprint(repos.Select(r => $"{r.Dir} {r.Tree}")), repos);

    /// <summary>
    /// Forty hex characters, the length of a commit, so it fits where one is stored and shows as one
    /// shortened. Over the repos in config order, which is fixed for a project.
    /// </summary>
    private static string Fingerprint(IEnumerable<string> lines) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines)))).ToLowerInvariant();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>What <see cref="TicketAnalysis.RepoHeadsJson"/> holds: null for a pin of one repo.</summary>
    public string? ReposJson() => Repos.Count == 0 ? null : JsonSerializer.Serialize(Repos, Json);

    /// <summary>Each repo's commit from a stored <see cref="TicketAnalysis.RepoHeadsJson"/>, or null.</summary>
    public static IReadOnlyDictionary<string, string>? HeadsFrom(string? reposJson)
    {
        if (string.IsNullOrWhiteSpace(reposJson)) return null;
        try
        {
            var repos = JsonSerializer.Deserialize<List<RepoHead>>(reposJson, Json);
            return repos is { Count: > 0 }
                ? repos.ToDictionary(r => r.Dir, r => r.Head, StringComparer.OrdinalIgnoreCase)
                : null;
        }
        catch (JsonException) { return null; }
    }

    private static string? Git(string dir, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = ProcessText.Utf8, StandardErrorEncoding = ProcessText.Utf8,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEnd().Trim();
            p.StandardError.ReadToEnd();
            p.WaitForExit(10_000);
            return p.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch { return null; }
    }
}
