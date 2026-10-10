using System.Text.RegularExpressions;

namespace Whetstone.Services;

/// <summary>Where a working-tree change in a TFVC hub came from, as far as a sync is concerned.</summary>
public static class SyncSource
{
    /// <summary>TFVC holds nothing pending or detected for it: the disk is the server's. The sync commits it.</summary>
    public const string Tfvc = "tfvc";
    /// <summary>A pending change of the human's own. Left alone - it reaches git with its check-in.</summary>
    public const string Pending = "pending";
    /// <summary>On disk, and TFVC has only detected it - the server does not have it. Left alone.</summary>
    public const string Detected = "detected";
    /// <summary>Outside the mapped folders: the mirror's own files. Left alone.</summary>
    public const string GitOnly = "git-only";
    /// <summary>TFVC's answer could not be read. Any of these stops the sync.</summary>
    public const string Unknown = "unknown";
}

/// <summary>One uncommitted change in the hub. <paramref name="Git"/> is <c>modified</c>, <c>deleted</c> or <c>added</c>.</summary>
public record SyncFile(string Path, string Git, string Source, string? TfvcChange);

/// <summary>What a sync would commit, and what it would leave alone.</summary>
public record TfvcSyncPreview(
    bool Ok,
    string? Error,
    string Project,
    string RepoPath,
    string BaseBranch,
    IReadOnlyList<SyncFile> Files,
    bool TfvcChecked,
    string? TfvcError,
    IReadOnlyList<string> TfvcProblems,
    string SuggestedMessage);

/// <summary>
/// Outcome of a sync. <paramref name="Changed"/> is the refusal that means the hub moved between the
/// list the human read and the commit - nothing was committed, and the list needs reading again.
/// </summary>
public record TfvcSyncResult(
    bool Ok,
    string? Error,
    bool Changed,
    string? CommitSha,
    string? CommitSubject,
    int Committed,
    IReadOnlyList<string> Output);

/// <summary>
/// Sync with TFVC: bring git main level with the server after a Get Latest.
///
/// <para>
/// A Get Latest writes other people's check-ins straight into the hub's working tree, where git
/// sees them as uncommitted changes - so git main falls behind TFVC, and the check-in flow refuses
/// to stage onto a dirty base. What makes this safe to automate is that TFVC can tell the two kinds
/// of change apart: a file a Get Latest wrote matches the server, so <c>tf status</c> reports nothing
/// for it, while the human's own work is a pending change or, for a file the server has never had, a
/// detected one. So a sync commits exactly the changes TFVC is silent about, inside the mapped
/// folders, and leaves everything else where it is.
/// </para>
///
/// <para>
/// Two steps, and the second re-checks the first. The preview is read-only; the commit recomputes it
/// and refuses unless the set it would commit is exactly the set the human was shown, so nothing
/// that arrived in between is swept in unseen. It refuses outright while a ticket's merge is staged
/// (the commit would carry the merge) or while anything is already in git's index, because what it
/// commits has to be what it listed and nothing more.
/// </para>
///
/// <para>
/// One honest gap: tf also says nothing about a file it does not track at all - one matched by
/// <c>.tfignore</c> - so such a file, edited, would be listed as the server's. It is listed, though,
/// and the human reads the list before anything is committed.
/// </para>
/// </summary>
public partial class TicketGitService
{
    public async Task<TfvcSyncPreview> TfvcSyncPreviewAsync(string projectKey, CancellationToken ct = default)
    {
        var cfg = _registry.GetProjectConfig(projectKey);
        var repo = cfg.ProjectPath?.Trim().TrimEnd('\\', '/') ?? "";
        var baseBranch = ResolveBaseBranch(cfg);
        var message = $"Sync with TFVC after the {DateTime.Now:yyyy-MM-dd} Get Latest";

        TfvcSyncPreview Fail(string error) => new(
            false, error, projectKey, repo, baseBranch, Array.Empty<SyncFile>(), false, null, Array.Empty<string>(), message);

        if (!cfg.IsTfvc) return Fail($"{cfg.DisplayName} is a git project - there is no TFVC to sync with.");
        if (!IsGitRepo(repo)) return Fail($"{cfg.DisplayName} has no git repo at '{repo}'.");

        var hub = await ReadHubAsync(repo, "", ct);
        if (hub.Branch != baseBranch)
            return Fail($"The hub at {repo} is on '{(hub.Branch.Length == 0 ? "a detached HEAD" : hub.Branch)}', not '{baseBranch}'. " +
                        "A sync commits onto the base, so put the hub back on it first.");
        if (hub.MergeHead is not null)
            return Fail($"A merge is staged in {repo} - a ticket's check-in is in progress. Record or abort it first: " +
                        "a sync commit would carry the merge with it.");
        if (await RebaseInProgressAsync(repo, ct))
            return Fail($"A rebase is in progress in {repo}. Finish or abort it first.");

        var staged = hub.Entries.Where(e => e.Index != ' ').Select(e => e.Path).ToList();
        if (staged.Count > 0)
            return Fail($"{staged.Count} file(s) are already staged in git's index in {repo} " +
                        $"({string.Join(", ", staged.Take(5))}{(staged.Count > 5 ? ", ..." : "")}). A sync commits exactly " +
                        $"what it lists, so unstage them first: git -C \"{repo}\" reset -q");

        // New files a Get Latest brought in are untracked to git. Only the mapped folders are worth
        // asking about - anything else cannot have come from the server - and -uall names each file
        // rather than a folder, because tf is asked about files.
        var args = new List<string> { "status", "--porcelain=v1", "-z", "--untracked-files=all" };
        if (cfg.TfvcFolders.Length > 0)
        {
            args.Add("--");
            args.AddRange(cfg.TfvcFolders.Select(f => f.Replace('\\', '/').Trim('/')).Where(f => f.Length > 0));
        }
        var untracked = ParsePorcelainZ((await GitAsync(repo, ct, TimeSpan.FromSeconds(60), args.ToArray())).Stdout)
            .Where(e => e.Index == '?')
            .Select(e => e.Path);

        var changes = hub.Entries
            .Where(e => e.Worktree != ' ' && !e.Unmerged)
            .Select(e => (e.Path, Status: e.Worktree == 'D' ? FileStatus.Deleted : FileStatus.Modified))
            .Concat(untracked.Select(p => (Path: p, Status: FileStatus.Added)))
            .GroupBy(c => c.Path, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

        var asked = changes.Select(c =>
        {
            var inTfvc = cfg.InTfvc(c.Path);
            return new CheckinFile(c.Path, c.Status, null, inTfvc, false,
                inTfvc ? TfvcState.Unknown : TfvcState.NotInTfvc, null, null, null);
        }).ToList();

        var view = await QueryTfvcAsync(repo, asked, ct);
        var files = view.Files
            .Select(f => new SyncFile(f.Path, f.Status, !f.InTfvc ? SyncSource.GitOnly : view.Error is not null ? SyncSource.Unknown : f.Tfvc switch
            {
                TfvcState.Clean => SyncSource.Tfvc,
                TfvcState.Pending => SyncSource.Pending,
                TfvcState.Detected => SyncSource.Detected,
                _ => SyncSource.Unknown,
            }, f.TfvcChange))
            .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new TfvcSyncPreview(true, null, projectKey, repo, baseBranch, files,
            asked.Any(f => f.InTfvc), view.Error, view.Problems, message);
    }

    /// <summary>
    /// Commit the server's changes: exactly <paramref name="expected"/>, which must still be exactly
    /// what the preview lists as coming from TFVC. See the class remarks for why each refusal exists.
    /// </summary>
    public async Task<TfvcSyncResult> TfvcSyncAsync(
        string projectKey, IReadOnlyCollection<string> expected, string? message, CancellationToken ct = default)
    {
        var output = new List<string>();
        TfvcSyncResult Fail(string error, bool changed = false) => new(false, error, changed, null, null, 0, output);

        var preview = await TfvcSyncPreviewAsync(projectKey, ct);
        if (!preview.Ok) return Fail(preview.Error!);
        if (preview.TfvcError is not null)
            return Fail($"Could not ask TFVC which changes are the server's: {preview.TfvcError} Nothing was committed.");
        if (preview.TfvcProblems.Count > 0 || preview.Files.Any(f => f.Source == SyncSource.Unknown))
            return Fail("TFVC said something this could not read, so it cannot tell the server's changes from yours. Nothing was committed. " +
                        string.Join(" | ", preview.TfvcProblems));

        var sync = preview.Files.Where(f => f.Source == SyncSource.Tfvc).Select(f => f.Path).ToList();
        if (sync.Count == 0) return Fail("Nothing to sync - no change in the hub is one TFVC reports as the server's.");
        if (!new HashSet<string>(expected, StringComparer.Ordinal).SetEquals(sync))
            return Fail("The hub has changed since this list was read. Nothing was committed - review the list again.", changed: true);

        var repo = preview.RepoPath;
        var paths = sync.ToArray();

        // Literal pathspecs: these are file names off disk, and a name with a '*' or a leading ':' in
        // it must mean that file rather than a pattern.
        foreach (var chunk in paths.Chunk(100))
        {
            var add = await GitAsync(repo, ct, TimeSpan.FromMinutes(2), new[] { "--literal-pathspecs", "add", "-A", "--" }.Concat(chunk).ToArray());
            AddLines(output, add);
            if (!add.Ok)
            {
                await UnstageAsync(repo, paths, ct);
                return Fail(Explain(add, "could not stage the sync"));
            }
        }

        // The index has to hold the sync and nothing else, or the commit says something the list did not.
        var cached = (await GitAsync(repo, ct, "diff", "--cached", "--name-only", "--no-renames", "-z")).Stdout
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (!new HashSet<string>(cached, StringComparer.Ordinal).SetEquals(sync))
        {
            await UnstageAsync(repo, paths, ct);
            return Fail("What git staged did not match the list, so nothing was committed. Read the list again.", changed: true);
        }

        var subject = Regex.Replace(message ?? "", @"\s+", " ").Trim();
        if (subject.Length == 0) subject = preview.SuggestedMessage;
        var kept = preview.Files.Count - sync.Count;
        var body = $"{sync.Count} file(s) a Get Latest brought into the working tree, each one TFVC reports with nothing pending " +
                   $"or detected - so the disk is the server's, and git {preview.BaseBranch} catches up to it." +
                   (kept > 0 ? $" Left out: {kept} change(s) that are not the server's (pending, local-only or git-only)." : "");

        output.Add($"git -C \"{repo}\" commit -m \"{subject}\"");
        var commit = await GitAsync(repo, ct, TimeSpan.FromMinutes(2), "-c", "core.editor=true", "commit", "-m", subject, "-m", body);
        AddLines(output, commit);
        if (!commit.Ok)
        {
            await UnstageAsync(repo, paths, ct);
            return Fail(Explain(commit, "could not commit the sync"));
        }

        var sha = (await GitAsync(repo, ct, "rev-parse", "HEAD")).Stdout.Trim();
        _log.LogInformation("Synced {Count} file(s) from TFVC into {Repo} as {Sha}", sync.Count, repo, Short(sha));
        return new TfvcSyncResult(true, null, false, sha, subject, sync.Count, output);
    }

    /// <summary>Put the index back after a sync that stopped half way. The working tree is never touched.</summary>
    private async Task UnstageAsync(string repo, string[] paths, CancellationToken ct)
    {
        foreach (var chunk in paths.Chunk(100))
            await GitAsync(repo, ct, new[] { "--literal-pathspecs", "reset", "-q", "--" }.Concat(chunk).ToArray());
    }
}
