using System.Diagnostics;

namespace Whetstone.Services;

/// <summary>One worktree hanging off a repo, as git reports it.</summary>
public record WorktreeEntry(string Path, string? Branch, bool IsMain);

/// <summary>Outcome of trying to remove a worktree. <c>Removed</c> false with no error = nothing to do.</summary>
public record WorktreeRemoval(bool Removed, string? Error = null);

/// <summary>
/// Reads and removes the git worktrees the heartbeat creates for tickets.
///
/// Git is the registry, not us: <c>git worktree list</c> already knows every worktree and the
/// branch it holds, and the user is free to add, move or delete them by hand at any time. A ticket's
/// recorded <see cref="Models.Ticket.WorktreePath"/> is therefore a hint to be checked, never a fact
/// to be acted on blindly.
///
/// Creation lives in the heartbeat, which is the process that owns branch naming. This side only
/// reads and cleans up.
/// </summary>
public class GitWorktreeService
{
    private readonly ILogger<GitWorktreeService> _log;

    public GitWorktreeService(ILogger<GitWorktreeService> log) => _log = log;

    /// <summary>Every worktree registered against <paramref name="repoDir"/>, main checkout first.</summary>
    public IReadOnlyList<WorktreeEntry> List(string repoDir)
    {
        var result = new List<WorktreeEntry>();
        if (!IsGitRepo(repoDir)) return result;

        var (ok, stdout, _) = Run(repoDir, "worktree", "list", "--porcelain");
        if (!ok) return result;

        string? path = null;
        string? branch = null;
        void Flush()
        {
            if (path is null) return;
            result.Add(new WorktreeEntry(path, branch, result.Count == 0));
            path = null;
            branch = null;
        }

        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) { Flush(); continue; }
            if (line.StartsWith("worktree ")) { Flush(); path = line["worktree ".Length..].Trim(); }
            else if (line.StartsWith("branch refs/heads/")) branch = line["branch refs/heads/".Length..].Trim();
        }
        Flush();
        return result;
    }

    /// <summary>Drop registrations for worktrees whose directory no longer exists.</summary>
    public void Prune(string repoDir)
    {
        if (!IsGitRepo(repoDir)) return;
        Run(repoDir, "worktree", "prune");
    }

    /// <summary>
    /// Remove a ticket's worktree once its work is finished and merged.
    ///
    /// Deliberately never <c>--force</c>. The user picks tickets up by hand in these directories to
    /// make final adjustments, so a worktree with uncommitted changes is far more likely to be
    /// someone mid-edit than leftover junk — and git's own refusal to remove a dirty worktree is the
    /// last thing standing between that and a silently destroyed change. If it will not come out
    /// cleanly, say so and leave it alone.
    ///
    /// The branch is kept. It costs nothing, it is the record of what was merged, and re-creating a
    /// worktree from it later is one command.
    /// </summary>
    public WorktreeRemoval Remove(string repoDir, string worktreePath)
    {
        if (!IsGitRepo(repoDir)) return new WorktreeRemoval(false);
        if (string.IsNullOrWhiteSpace(worktreePath)) return new WorktreeRemoval(false);

        // Only ever remove something git agrees is a worktree of THIS repo. Without this check a
        // stale or hand-edited path on a ticket would be handed straight to a delete.
        var known = List(repoDir).FirstOrDefault(w =>
            !w.IsMain && PathsEqual(w.Path, worktreePath));
        if (known is null)
        {
            Prune(repoDir);
            return new WorktreeRemoval(false);
        }

        var (ok, _, stderr) = Run(repoDir, "worktree", "remove", known.Path);
        if (ok)
        {
            _log.LogInformation("Removed worktree {Path}", known.Path);
            return new WorktreeRemoval(true);
        }

        var reason = string.IsNullOrWhiteSpace(stderr) ? "git refused to remove it" : stderr.Trim();
        _log.LogWarning("Could not remove worktree {Path}: {Reason}", known.Path, reason);
        return new WorktreeRemoval(false, reason);
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a.Replace('/', Path.DirectorySeparatorChar))),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b.Replace('/', Path.DirectorySeparatorChar))),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsGitRepo(string dir) =>
        !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir) && Path.Exists(Path.Combine(dir, ".git"));

    private (bool Ok, string Stdout, string Stderr) Run(string workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = ProcessText.Utf8,
            StandardErrorEncoding = ProcessText.Utf8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            using var p = Process.Start(psi);
            if (p is null) return (false, "", "could not start git");
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            // Generous but bounded: these are all local metadata operations, not network calls.
            if (!p.WaitForExit(30_000)) { try { p.Kill(true); } catch { } return (false, stdout, "git timed out"); }
            return (p.ExitCode == 0, stdout, stderr);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "git {Args} failed in {Dir}", string.Join(' ', args), workingDir);
            return (false, "", ex.Message);
        }
    }
}
