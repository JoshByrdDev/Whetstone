using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Whetstone.Persistence;

namespace Whetstone.Services;

/// <summary>One commit that changed a template since a project's copy was last synced to it.</summary>
public sealed record TemplateCommit(string Sha, string Subject, string Date);

/// <summary>
/// Where one project's copy of one template stands.
/// </summary>
/// <param name="File">Project-relative, e.g. <c>skills/implement/skill.md</c>.</param>
/// <param name="Status"><c>current</c>, <c>behind</c> (the template moved since the copy's baseline),
/// or <c>untracked</c> (no baseline, and no version of the template the copy matches).</param>
/// <param name="TemplateDiff">For <c>behind</c>: the template's own change, baseline to now.</param>
/// <param name="CopyDiff">For <c>untracked</c>: the copy against the template as it is now.</param>
/// <param name="Conflicts">For <c>behind</c>: how many places a three-way merge cannot settle; 0 means Apply is clean.</param>
public sealed record SkillDriftItem(
    string File, string Status, string? SyncedAt, string? How, string? BaseCommit,
    IReadOnlyList<TemplateCommit> Commits, string? TemplateDiff, string? CopyDiff, int? Conflicts);

/// <summary>One project's count, for the badge on the Skills tab.</summary>
public sealed record SkillDriftSummary(string Project, int Behind, int Untracked);

/// <summary>
/// Whether a project's copies of the skill templates have fallen behind the templates.
/// <para>
/// The scaffolder copies a template into a project once and never again, so a change to the
/// template reaches no project that already exists. This is how anyone finds out. It does not go
/// by file dates: git stamps every file it writes with "now", so a pull makes every template look
/// newer, and a local edit to a copy would make it look current when it never took the change.
/// Instead each copy records the template text it was last synced to - its baseline - in
/// <c>template-base/</c> beside the project's skills, and a copy is behind exactly when the template
/// is no longer that text.
/// </para>
/// <para>
/// The baseline is kept as text, not as a commit, so that the template's own change can be shown
/// and three-way merged into the copy wherever this runs: a QA copy's templates are not in git, a
/// template can be edited and not yet committed, and another person's clone may not have the
/// commit. The commit is recorded beside it when there is one, for the "why" in the log.
/// </para>
/// </summary>
public class SkillDriftService
{
    public const string BaseDirName = "template-base";
    private const string SyncFileName = "sync.json";

    /// <summary>Every write of a project's sync record goes through this, so two polls cannot interleave.</summary>
    private static readonly object WriteLock = new();

    /// <summary>
    /// A copy that matched no version of its template, remembered by both texts' hashes so a poll
    /// does not walk the template's history again until one of them changes.
    /// </summary>
    private static readonly Dictionary<string, string> Unmatched = new();

    private readonly DashboardPaths _paths;
    private readonly Func<string, string> _displayName;

    public SkillDriftService(DashboardPaths paths, ProjectRegistry registry)
        : this(paths, key => DisplayName(registry, key)) { }

    public SkillDriftService(DashboardPaths paths, Func<string, string> displayName) =>
        (_paths, _displayName) = (paths, displayName);

    public static string DisplayName(ProjectRegistry registry, string key) =>
        registry.GetMergedConfig(key).TryGetValue("displayName", out var d) && d.ValueKind == JsonValueKind.String
            ? d.GetString() is { Length: > 0 } s ? s : key
            : key;

    /// <summary>Every file the scaffolder copies from a template: (project-relative path, template, copy).</summary>
    public static IEnumerable<(string Rel, string Template, string Copy)> Tracked(DashboardPaths paths, string project)
    {
        foreach (var skill in PhaseMachine.AllSkills.Append("steward"))
            yield return ($"skills/{skill}/skill.md", paths.SkillTemplate(skill), paths.SkillFile(project, skill));
        yield return ("reflect-prompt.md", paths.ReflectPromptTemplate, paths.ReflectPromptFile(project));
    }

    /// <summary>The same token substitution the scaffolder applies when it writes a copy.</summary>
    public static string Fill(string content, string project, string displayName) => content
        .Replace("{{PROJECT_NAME}}", string.IsNullOrWhiteSpace(displayName) ? project : displayName)
        .Replace("{{PROJECT_KEY}}", project);

    // ---- Reading -------------------------------------------------------------------------------

    public IReadOnlyList<SkillDriftSummary> Summary()
    {
        var list = new List<SkillDriftSummary>();
        foreach (var project in _paths.DiscoverProjects())
        {
            var items = Items(project, detail: false);
            var behind = items.Count(i => i.Status == "behind");
            var untracked = items.Count(i => i.Status == "untracked");
            list.Add(new SkillDriftSummary(project, behind, untracked));
        }
        return list;
    }

    /// <summary>
    /// Where each of a project's copies stands. A copy with no baseline gets one here when the
    /// template, now or at some commit, is exactly what the copy holds - that is a fact about the
    /// copy, not a decision, so there is nobody to ask. <paramref name="detail"/> adds the diffs,
    /// the log and the merge preview, which cost a git call each.
    /// </summary>
    public IReadOnlyList<SkillDriftItem> Items(string project, bool detail = true)
    {
        var name = _displayName(project);
        var sync = ReadSync(_paths, project);
        var items = new List<SkillDriftItem>();

        foreach (var (rel, templatePath, copyPath) in Tracked(_paths, project))
        {
            if (!File.Exists(templatePath) || !File.Exists(copyPath)) continue;
            var template = Norm(File.ReadAllText(templatePath));
            var copy = Norm(File.ReadAllText(copyPath));

            if (!sync.TryGetValue(rel, out var entry) || ReadBase(project, rel) is not { } baseText)
            {
                if (Bootstrap(project, name, rel, templatePath, template, copy) is { } found)
                    (entry, baseText) = found;
                else
                {
                    items.Add(new SkillDriftItem(rel, "untracked", null, null, null, Array.Empty<TemplateCommit>(),
                        null, detail ? Diff(copy, Fill(template, project, name)) : null, null));
                    continue;
                }
            }

            if (Hash(baseText) == Hash(template))
            {
                items.Add(new SkillDriftItem(rel, "current", entry.SyncedAt, entry.How, entry.Commit,
                    Array.Empty<TemplateCommit>(), null, null, null));
                continue;
            }

            IReadOnlyList<TemplateCommit> commits = Array.Empty<TemplateCommit>();
            string? templateDiff = null;
            int? conflicts = null;
            if (detail)
            {
                commits = CommitsSince(templatePath, entry);
                templateDiff = Diff(baseText, template);
                conflicts = Merge(copy, Fill(baseText, project, name), Fill(template, project, name)).Conflicts;
            }
            items.Add(new SkillDriftItem(rel, "behind", entry.SyncedAt, entry.How, entry.Commit,
                commits, templateDiff, null, conflicts));
        }
        return items;
    }

    private (SyncEntry, string)? Bootstrap(string project, string name, string rel, string templatePath, string template, string copy)
    {
        var copyHash = Hash(copy);
        if (Fill(template, project, name) == copy)
            return (Record(_paths, project, rel, template, TemplateCommitFor(templatePath, template), "matched"), template);

        var key = $"{_paths.DataRoot}|{project}|{rel}";
        var stamp = $"{copyHash}|{Hash(template)}";
        lock (Unmatched)
            if (Unmatched.TryGetValue(key, out var seen) && seen == stamp) return null;

        foreach (var (sha, text) in TemplateHistory(templatePath))
        {
            if (Fill(text, project, name) != copy) continue;
            return (Record(_paths, project, rel, text, sha, "matched"), text);
        }
        lock (Unmatched) Unmatched[key] = stamp;
        return null;
    }

    // ---- Changing ------------------------------------------------------------------------------

    public sealed record ApplyResult(bool Ok, int Conflicts, string? Preview, string? Error);

    /// <summary>
    /// Merges the template's change since the baseline into the copy, and moves the baseline up -
    /// only when the merge is clean. A conflicted merge writes nothing and hands back the text with
    /// its conflict markers, for a person to port by hand.
    /// </summary>
    public ApplyResult Apply(string project, string rel)
    {
        if (Find(project, rel) is not { } t) return new(false, 0, null, "Not a file this project copies from a template.");
        if (!File.Exists(t.Template) || !File.Exists(t.Copy)) return new(false, 0, null, "The template or the copy is missing.");
        if (ReadBase(project, rel) is not { } baseText)
            return new(false, 0, null, "This copy has no baseline to merge from. Port by hand, then mark it synced.");

        var name = _displayName(project);
        var template = Norm(File.ReadAllText(t.Template));
        var rawCopy = File.ReadAllText(t.Copy);
        var merged = Merge(Norm(rawCopy), Fill(baseText, project, name), Fill(template, project, name));
        if (merged.Text is null) return new(false, 0, null, merged.Error ?? "git merge-file failed.");
        if (merged.Conflicts > 0) return new(false, merged.Conflicts, merged.Text, null);

        // Back in the line endings the copy was written in, so the project's own diff is the change alone.
        var text = rawCopy.Contains("\r\n") ? merged.Text.Replace("\n", "\r\n") : merged.Text;
        lock (WriteLock) File.WriteAllText(t.Copy, text);
        Record(_paths, project, rel, template, TemplateCommitFor(t.Template, template), "merged");
        return new(true, 0, null, null);
    }

    /// <summary>
    /// Moves the baseline to the template as it is now without touching the copy: the change was
    /// ported by hand, or it is not one this project wants. Either way the next template change
    /// raises the alert again.
    /// </summary>
    public string? MarkSynced(string project, string rel, string how)
    {
        if (Find(project, rel) is not { } t) return "Not a file this project copies from a template.";
        if (!File.Exists(t.Template)) return "The template is missing.";
        var template = Norm(File.ReadAllText(t.Template));
        Record(_paths, project, rel, template, TemplateCommitFor(t.Template, template), how);
        lock (Unmatched) Unmatched.Remove($"{_paths.DataRoot}|{project}|{rel}");
        return null;
    }

    /// <summary>The scaffolder's half: a copy it has just written is synced to the template it came from.</summary>
    public static void RecordScaffolded(DashboardPaths paths, string project, string rel, string templatePath)
    {
        var template = Norm(File.ReadAllText(templatePath));
        Record(paths, project, rel, template, TemplateCommitFor(templatePath, template), "scaffolded");
    }

    private (string Rel, string Template, string Copy)? Find(string project, string rel)
    {
        foreach (var t in Tracked(_paths, project))
            if (t.Rel == rel) return t;
        return null;
    }

    // ---- The baseline on disk --------------------------------------------------------------------

    public sealed class SyncEntry
    {
        [JsonPropertyName("hash")] public string Hash { get; set; } = "";
        [JsonPropertyName("commit")] public string? Commit { get; set; }
        [JsonPropertyName("syncedAt")] public string SyncedAt { get; set; } = "";
        /// <summary>scaffolded, matched, merged, ported or skipped.</summary>
        [JsonPropertyName("how")] public string How { get; set; } = "";
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static string BaseDir(DashboardPaths paths, string project) => Path.Combine(paths.ProjectDir(project), BaseDirName);

    private static Dictionary<string, SyncEntry> ReadSync(DashboardPaths paths, string project)
    {
        var file = Path.Combine(BaseDir(paths, project), SyncFileName);
        try
        {
            return File.Exists(file)
                ? JsonSerializer.Deserialize<Dictionary<string, SyncEntry>>(File.ReadAllText(file)) ?? new()
                : new();
        }
        catch (JsonException) { return new(); }
    }

    private string? ReadBase(string project, string rel)
    {
        var file = Path.Combine(BaseDir(_paths, project), rel);
        return File.Exists(file) ? Norm(File.ReadAllText(file)) : null;
    }

    private static SyncEntry Record(DashboardPaths paths, string project, string rel, string template, string? commit, string how)
    {
        lock (WriteLock)
        {
            var dir = BaseDir(paths, project);
            var baseFile = Path.Combine(dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(baseFile)!);
            File.WriteAllText(baseFile, template);

            var sync = ReadSync(paths, project);
            var entry = new SyncEntry
            {
                Hash = Hash(template), Commit = commit, How = how,
                SyncedAt = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"),
            };
            sync[rel] = entry;
            var sorted = sync.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => kv.Value);
            File.WriteAllText(Path.Combine(dir, SyncFileName), JsonSerializer.Serialize(sorted, Json) + "\n");
            return entry;
        }
    }

    // ---- Text ----------------------------------------------------------------------------------

    /// <summary>LF only: a checkout with autocrlf and one without hold the same template.</summary>
    public static string Norm(string s) => s.Replace("\r\n", "\n");

    public static string Hash(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Norm(s)))).ToLowerInvariant();

    /// <summary>A unified diff from <paramref name="from"/> to <paramref name="to"/>, hunks only; null when they agree.</summary>
    public static string? Diff(string from, string to)
    {
        if (from == to) return null;
        using var a = new TempText(from);
        using var b = new TempText(to);
        var (code, stdout, _) = Git(Path.GetTempPath(), "diff", "--no-index", "--no-color", "-U3", a.Path, b.Path);
        if (code is not (0 or 1)) return null;
        var at = stdout.IndexOf("\n@@", StringComparison.Ordinal);
        return at < 0 ? (stdout.StartsWith("@@") ? stdout : null) : stdout[(at + 1)..];
    }

    /// <summary>
    /// <c>git merge-file</c>: takes the change from <paramref name="baseText"/> to
    /// <paramref name="theirs"/> and lays it over <paramref name="mine"/>. Its exit code is the
    /// number of conflicts, and it needs no repository.
    /// </summary>
    public static (string? Text, int Conflicts, string? Error) Merge(string mine, string baseText, string theirs)
    {
        using var m = new TempText(mine);
        using var o = new TempText(baseText);
        using var t = new TempText(theirs);
        var (code, stdout, stderr) = Git(Path.GetTempPath(), "merge-file", "-p",
            "-L", "this project's copy", "-L", "template before", "-L", "template now", m.Path, o.Path, t.Path);
        if (code < 0 || code > 127) return (null, 0, stderr.Trim());
        return (Norm(stdout), code, null);
    }

    // ---- The template's history ------------------------------------------------------------------

    /// <summary>The repository the template lives in, and its path there; null when it is not tracked (a QA copy).</summary>
    private static (string Root, string Rel)? TemplateRepo(string templatePath)
    {
        var dir = Path.GetDirectoryName(templatePath)!;
        var (code, rel, _) = Git(dir, "ls-files", "--full-name", "--", Path.GetFileName(templatePath));
        if (code != 0 || string.IsNullOrWhiteSpace(rel)) return null;
        var (code2, root, _) = Git(dir, "rev-parse", "--show-toplevel");
        if (code2 != 0) return null;
        return (root.Trim(), rel.Trim());
    }

    /// <summary>The last commit whose template is exactly this text, or null when the text is not committed.</summary>
    private static string? TemplateCommitFor(string templatePath, string template)
    {
        if (TemplateRepo(templatePath) is not { } repo) return null;
        var (code, sha, _) = Git(repo.Root, "log", "-1", "--format=%H", "--", repo.Rel);
        if (code != 0 || string.IsNullOrWhiteSpace(sha)) return null;
        sha = sha.Trim();
        var (code2, text, _) = Git(repo.Root, "show", $"{sha}:{repo.Rel}");
        return code2 == 0 && Norm(text) == template ? sha : null;
    }

    /// <summary>Every committed version of the template, newest first - capped, since this is a search, not an audit.</summary>
    private static IEnumerable<(string Sha, string Text)> TemplateHistory(string templatePath, int max = 300)
    {
        if (TemplateRepo(templatePath) is not { } repo) yield break;
        var (code, log, _) = Git(repo.Root, "log", $"-{max}", "--format=%H", "--", repo.Rel);
        if (code != 0) yield break;
        foreach (var sha in log.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var (c, text, _) = Git(repo.Root, "show", $"{sha}:{repo.Rel}");
            if (c == 0) yield return (sha, Norm(text));
        }
    }

    /// <summary>The commits that changed the template since the baseline - by commit when there is one, by date when not.</summary>
    private static IReadOnlyList<TemplateCommit> CommitsSince(string templatePath, SyncEntry entry)
    {
        if (TemplateRepo(templatePath) is not { } repo) return Array.Empty<TemplateCommit>();
        var args = new List<string> { "log", "--format=%h%x1f%s%x1f%cs" };
        if (entry.Commit is { Length: > 0 } c) args.Add($"{c}..HEAD");
        else if (entry.SyncedAt is { Length: > 0 } at) args.Add($"--since={at}");
        else return Array.Empty<TemplateCommit>();
        args.Add("--");
        args.Add(repo.Rel);
        var (code, log, _) = Git(repo.Root, args.ToArray());
        if (code != 0) return Array.Empty<TemplateCommit>();
        return log.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('\x1f'))
            .Where(p => p.Length == 3)
            .Select(p => new TemplateCommit(p[0], p[1], p[2].Trim()))
            .ToList();
    }

    // ---- Plumbing ----------------------------------------------------------------------------------

    private static (int Code, string Stdout, string Stderr) Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = ProcessText.Utf8,
            StandardErrorEncoding = ProcessText.Utf8,
        };
        // Diffs and merges of text that never came from a working tree: no autocrlf, no pager.
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.autocrlf=false");
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return (-1, "", "could not start git");
            var stderrTask = p.StandardError.ReadToEndAsync();
            var stdout = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(TimeSpan.FromSeconds(30))) { try { p.Kill(true); } catch { } return (-1, stdout, "git timed out"); }
            return (p.ExitCode, stdout, stderrTask.Result);
        }
        catch (Exception ex) { return (-1, "", ex.Message); }
    }

    /// <summary>A temp file holding some text, gone when disposed.</summary>
    private sealed class TempText : IDisposable
    {
        public string Path { get; }
        public TempText(string text)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"whetstone-drift-{Guid.NewGuid():N}.md");
            File.WriteAllText(Path, text, new UTF8Encoding(false));
        }
        public void Dispose() { try { File.Delete(Path); } catch { } }
    }
}
