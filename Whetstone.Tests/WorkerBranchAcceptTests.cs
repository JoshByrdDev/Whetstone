using System.Diagnostics;
using System.IO.Compression;
using Whetstone.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// The board host taking a worker host's branch (<see cref="TicketGitService.AcceptWorkerBranchAsync"/>),
/// against a real repo and a real second clone pushing to it - plus the project kit a worker host is
/// served, which must carry what a worker reads and nothing that holds a credential.
/// <para>
/// The refusals are the point. Each one is a way the hand-back could overwrite work: something that
/// moved on the board host while the worker host was busy, a person's uncommitted edits in a
/// worktree, a person's own checkout.
/// </para>
/// </summary>
public class WorkerBranchAcceptTests : IDisposable
{
    private readonly string _root;
    private readonly string _data;
    private readonly string _repo;
    private readonly string _clone;
    private readonly TicketGitService _svc;

    public WorkerBranchAcceptTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "whetstone-accept-" + Guid.NewGuid().ToString("N")[..8]);
        _data = Path.Combine(_root, "data");
        _repo = Path.Combine(_root, "repo");
        _clone = Path.Combine(_root, "clone");

        Directory.CreateDirectory(_repo);
        Git(_repo, "init", "-b", "main");
        Identity(_repo);
        File.WriteAllText(Path.Combine(_repo, "README.md"), "hello\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "init");
        Git(_root, "clone", "--quiet", "-o", "board", _repo, _clone);
        Identity(_clone);

        Directory.CreateDirectory(Path.Combine(_data, "projects", "alpha"));
        File.WriteAllText(Path.Combine(_data, "projects", "alpha", "project.defaults.json"),
            $$"""{ "displayName": "Alpha", "projectPath": {{System.Text.Json.JsonSerializer.Serialize(_repo)}}, "baseBranch": "main" }""");
        File.WriteAllText(Path.Combine(_data, "config.defaults.json"), """{ "baseBranch": "main" }""");

        _svc = new TicketGitService(new ProjectRegistry(new DashboardPaths(_data)),
            new GitWorktreeService(NullLogger<GitWorktreeService>.Instance), NullLogger<TicketGitService>.Instance);
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch { /* best effort */ }
    }

    private static void Identity(string dir)
    {
        Git(dir, "config", "user.email", "test@example.com");
        Git(dir, "config", "user.name", "Test");
        Git(dir, "config", "commit.gpgsign", "false");
    }

    private static string Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return stdout.Trim();
    }

    private static string Sha(string dir, string rev) => Git(dir, "rev-parse", "--verify", "--quiet", rev);

    private static void Commit(string dir, string file, string text)
    {
        File.AppendAllText(Path.Combine(dir, file), text + "\n");
        Git(dir, "add", "-A");
        Git(dir, "commit", "-m", text);
    }

    /// <summary>What a worker host's run ends with: its commit pushed to an incoming ref here.</summary>
    private string HandBack(string branch, string text, string runId = "run1")
    {
        Git(_clone, "checkout", "--quiet", "-B", branch, $"board/{branch}");
        if (Sha(_clone, $"board/{branch}") == "") Git(_clone, "checkout", "--quiet", "-B", branch, "board/main");
        Commit(_clone, "work.txt", text);
        Git(_clone, "push", "--quiet", "board", $"HEAD:refs/whetstone/incoming/{runId}");
        return Sha(_clone, "HEAD");
    }

    private void FetchClone() => Git(_clone, "fetch", "--quiet", "board");

    [Fact]
    public async Task A_new_branch_is_created_when_none_was_expected()
    {
        var sha = HandBack("t-1", "new ticket");
        var r = await _svc.AcceptWorkerBranchAsync("alpha", "t-1", "refs/whetstone/incoming/run1", sha, null);
        Assert.True(r.Ok, r.Why);
        Assert.Equal(sha, Sha(_repo, "refs/heads/t-1"));
        Assert.Equal("", Sha(_repo, "refs/whetstone/incoming/run1"));
    }

    [Fact]
    public async Task A_branch_no_board_host_worktree_holds_is_moved_as_a_ref()
    {
        Git(_repo, "branch", "t-2");
        FetchClone();
        var old = Sha(_repo, "t-2");
        var sha = HandBack("t-2", "worker host work");
        var r = await _svc.AcceptWorkerBranchAsync("alpha", "t-2", "refs/whetstone/incoming/run1", sha, old);
        Assert.True(r.Ok, r.Why);
        Assert.Equal(sha, Sha(_repo, "t-2"));
    }

    [Fact]
    public async Task A_branch_in_a_clean_board_host_worktree_moves_with_its_files()
    {
        var wt = Path.Combine(_root, "wt-3");
        Git(_repo, "worktree", "add", "--quiet", "-b", "t-3", wt, "main");
        FetchClone();
        var old = Sha(_repo, "t-3");
        var sha = HandBack("t-3", "from the worker host");

        var r = await _svc.AcceptWorkerBranchAsync("alpha", "t-3", "refs/whetstone/incoming/run1", sha, old);
        Assert.True(r.Ok, r.Why);
        Assert.Equal(sha, Sha(wt, "HEAD"));
        Assert.Contains("from the worker host", File.ReadAllText(Path.Combine(wt, "work.txt")));
        Assert.Equal("", Git(wt, "status", "--porcelain"));
    }

    [Fact]
    public async Task A_dirty_board_host_worktree_outside_the_root_refuses_and_names_the_files()
    {
        var wt = Path.Combine(_root, "wt-4");
        Git(_repo, "worktree", "add", "--quiet", "-b", "t-4", wt, "main");
        File.WriteAllText(Path.Combine(wt, "README.md"), "a person's edit\n");
        FetchClone();
        var old = Sha(_repo, "t-4");
        var sha = HandBack("t-4", "worker host");

        var r = await _svc.AcceptWorkerBranchAsync("alpha", "t-4", "refs/whetstone/incoming/run1", sha, old);
        Assert.False(r.Ok);
        Assert.Contains("uncommitted", r.Why);
        Assert.Contains("README.md", r.Why);
        Assert.Null(r.SetAside);
        Assert.Equal(old, Sha(_repo, "t-4"));
        Assert.Equal("a person's edit\n", File.ReadAllText(Path.Combine(wt, "README.md")).Replace("\r\n", "\n"));
    }

    // A run on the board host stopped before committing, and the ticket then went to a worker host.
    // The worker host never starts a run here again, so nothing else would ever clean this tree.
    [Fact]
    public async Task Leftovers_in_the_fleets_own_worktree_are_stashed_and_the_branch_moves()
    {
        var wt = Path.Combine(_root, ".worktrees", "alpha", "t-9");
        Git(_repo, "worktree", "add", "--quiet", "-b", "t-9", wt, "main");
        File.WriteAllText(Path.Combine(wt, "README.md"), "a stopped run's edit\n");
        File.WriteAllText(Path.Combine(wt, "half.txt"), "never committed\n");
        FetchClone();
        var old = Sha(_repo, "t-9");
        var sha = HandBack("t-9", "from the worker host");

        var r = await _svc.AcceptWorkerBranchAsync("alpha", "t-9", "refs/whetstone/incoming/run1", sha, old);
        Assert.True(r.Ok, r.Why);
        Assert.Equal(sha, Sha(wt, "HEAD"));
        Assert.Equal("", Git(wt, "status", "--porcelain"));

        Assert.NotNull(r.SetAside);
        Assert.Equal(Sha(_repo, "refs/stash"), r.SetAside!.Sha);
        Assert.Contains("README.md", r.SetAside.Files);
        Assert.Contains("half.txt", r.SetAside.Files);
        // Kept, not lost: the stash holds the tracked edit and the untracked file.
        Assert.Contains("a stopped run's edit", Git(_repo, "show", $"{r.SetAside.Sha}:README.md"));
        Assert.Contains("never committed", Git(_repo, "show", $"{r.SetAside.Sha}^3:half.txt"));
    }

    /// <summary>A fleet worktree for <paramref name="branch"/> left stopped mid-rebase on a conflict with main.</summary>
    private string StoppedRebase(string branch)
    {
        var wt = Path.Combine(_root, ".worktrees", "alpha", branch);
        Git(_repo, "worktree", "add", "--quiet", "-b", branch, wt, "main");
        Identity(wt);
        File.WriteAllText(Path.Combine(wt, "README.md"), "the ticket's line\n");
        Git(wt, "commit", "-qam", "the ticket");
        File.WriteAllText(Path.Combine(_repo, "README.md"), "main's line\n");
        Git(_repo, "commit", "-qam", "main moved");
        Git(wt, "rebase", "main");
        Assert.True(Directory.Exists(Path.Combine(_repo, ".git", "worktrees", branch, "rebase-merge")), "setup: the rebase stopped");
        return wt;
    }

    // A run here was killed mid-rebase and the ticket went to a worker host. Its hand-back clears the
    // rebase without rewinding the branch, and the worktree ends on the branch it hands back.
    [Fact]
    public async Task A_rebase_a_killed_run_left_stopped_here_is_kept_and_cleared_by_the_hand_back()
    {
        var wt = StoppedRebase("t-11");
        FetchClone();
        var old = Sha(_repo, "t-11");
        var sha = HandBack("t-11", "from the worker host");

        var r = await _svc.AcceptWorkerBranchAsync("alpha", "t-11", "refs/whetstone/incoming/run1", sha, old);
        Assert.True(r.Ok, r.Why);
        Assert.Equal(sha, Sha(_repo, "t-11"));
        Assert.Equal(sha, Sha(wt, "HEAD"));
        Assert.Equal("t-11", Git(wt, "symbolic-ref", "--short", "HEAD"));
        Assert.False(Directory.Exists(Path.Combine(_repo, ".git", "worktrees", "t-11", "rebase-merge")));
        var kept = Assert.Single(r.Stopped!);
        Assert.StartsWith("refs/whetstone/stopped/t-11/", kept.Ref);
        Assert.Contains("<<<<<<<", Git(_repo, "show", $"{kept.Ref}:README.md"));
        Assert.Contains(kept.Ref, TicketGitService.SayStoppedRebaseKept(kept, "t-11"));
    }

    // The case that loses work: the branch moved on (an earlier hand-back) while a rebase sat stopped.
    // An abort would wind it back to where the rebase began; clearing must leave it where it is.
    [Fact]
    public async Task Clearing_a_stopped_rebase_never_rewinds_a_branch_that_moved_since()
    {
        var wt = StoppedRebase("t-12");
        var began = Sha(_repo, "t-12");
        var moved = Git(_repo, "commit-tree", "-p", began, "-m", "handed back meanwhile", Sha(_repo, "t-12^{tree}"));
        Git(_repo, "update-ref", "refs/heads/t-12", moved);

        var refused = await _svc.RebaseAsync("alpha", "t-12");
        Assert.False(refused.Ok);
        Assert.Contains("stuck in the middle of a rebase", refused.Error);
        Assert.Equal(moved, Sha(_repo, "t-12"));

        var (kept, why) = await _svc.ClearStoppedRebasesAsync("alpha", "t-12");
        Assert.Null(why);
        Assert.Single(kept);
        Assert.Equal(moved, Sha(_repo, "t-12"));
        Assert.Equal(moved, Sha(wt, "HEAD"));
        Assert.Equal("", Git(wt, "status", "--porcelain"));
    }

    [Fact]
    public async Task A_stopped_rebase_outside_the_worktree_root_is_left_for_its_person()
    {
        var wt = Path.Combine(_root, "mine");
        Git(_repo, "worktree", "add", "--quiet", "-b", "t-13", wt, "main");
        Identity(wt);
        File.WriteAllText(Path.Combine(wt, "README.md"), "the ticket's line\n");
        Git(wt, "commit", "-qam", "the ticket");
        File.WriteAllText(Path.Combine(_repo, "README.md"), "main's line\n");
        Git(_repo, "commit", "-qam", "main moved");
        Git(wt, "rebase", "main");

        var (kept, why) = await _svc.ClearStoppedRebasesAsync("alpha", "t-13");
        Assert.Empty(kept);
        Assert.Contains("may be a person's", why);
        Assert.True(Directory.Exists(Path.Combine(_repo, ".git", "worktrees", "mine", "rebase-merge")));
    }

    [Fact]
    public async Task A_clean_hand_back_sets_nothing_aside()
    {
        var wt = Path.Combine(_root, ".worktrees", "alpha", "t-10");
        Git(_repo, "worktree", "add", "--quiet", "-b", "t-10", wt, "main");
        FetchClone();
        var old = Sha(_repo, "t-10");
        var sha = HandBack("t-10", "worker host");

        var r = await _svc.AcceptWorkerBranchAsync("alpha", "t-10", "refs/whetstone/incoming/run1", sha, old);
        Assert.True(r.Ok, r.Why);
        Assert.Null(r.SetAside);
        Assert.Equal("", Sha(_repo, "refs/stash"));
    }

    [Fact]
    public async Task A_branch_that_moved_on_the_board_host_meanwhile_refuses()
    {
        var wt = Path.Combine(_root, "wt-5");
        Git(_repo, "worktree", "add", "--quiet", "-b", "t-5", wt, "main");
        Identity(wt);
        FetchClone();
        var old = Sha(_repo, "t-5");
        var sha = HandBack("t-5", "worker host");
        Commit(wt, "board.txt", "the board host moved it");

        var r = await _svc.AcceptWorkerBranchAsync("alpha", "t-5", "refs/whetstone/incoming/run1", sha, old);
        Assert.False(r.Ok);
        Assert.Contains("moved", r.Why);
        Assert.NotEqual(sha, Sha(_repo, "t-5"));
    }

    [Fact]
    public async Task A_branch_created_on_the_board_host_meanwhile_refuses()
    {
        var sha = HandBack("t-6", "worker host, new ticket");
        Git(_repo, "branch", "t-6");
        var r = await _svc.AcceptWorkerBranchAsync("alpha", "t-6", "refs/whetstone/incoming/run1", sha, null);
        Assert.False(r.Ok);
        Assert.Contains("created", r.Why);
    }

    [Fact]
    public async Task A_branch_checked_out_in_the_persons_own_checkout_refuses()
    {
        Git(_repo, "checkout", "--quiet", "-b", "t-7");
        FetchClone();
        var old = Sha(_repo, "t-7");
        var sha = HandBack("t-7", "worker host");
        var r = await _svc.AcceptWorkerBranchAsync("alpha", "t-7", "refs/whetstone/incoming/run1", sha, old);
        Assert.False(r.Ok);
        Assert.Contains("person's checkout", r.Why);
        Assert.Equal(old, Sha(_repo, "t-7"));
    }

    [Fact]
    public async Task The_commit_named_must_be_the_one_that_was_pushed()
    {
        HandBack("t-8", "worker host");
        var r = await _svc.AcceptWorkerBranchAsync("alpha", "t-8", "refs/whetstone/incoming/run1", Sha(_repo, "main"), null);
        Assert.False(r.Ok);
        Assert.False((await _svc.AcceptWorkerBranchAsync("alpha", "t-8", "refs/whetstone/incoming/nope", Sha(_clone, "HEAD"), null)).Ok);
        Assert.False((await _svc.AcceptWorkerBranchAsync("alpha", "t-8", "refs/heads/main", Sha(_repo, "main"), null)).Ok);
        Assert.False((await _svc.AcceptWorkerBranchAsync("alpha", "bad..name", "refs/whetstone/incoming/run1", Sha(_clone, "HEAD"), null)).Ok);
    }

    // ---- The project kit ------------------------------------------------------------

    [Fact]
    public void The_kit_carries_skills_guard_and_notes_and_nothing_with_a_credential()
    {
        var project = Path.Combine(_data, "projects", "alpha");
        Directory.CreateDirectory(Path.Combine(project, "skills", "implement"));
        File.WriteAllText(Path.Combine(project, "skills", "implement", "skill.md"), "# implement");
        File.WriteAllText(Path.Combine(project, "guard.ps1"), "# guard");
        File.WriteAllText(Path.Combine(project, "project.json"), """{ "password": "x" }""");
        File.WriteAllText(Path.Combine(project, "reflect-prompt.md"), "reflect");
        File.WriteAllText(Path.Combine(project, "improvement-suggestions.md"), "s");
        var notes = Path.Combine(_root, "notes");
        Directory.CreateDirectory(notes);
        File.WriteAllText(Path.Combine(notes, "MEMORY.md"), "- [a](a.md)");
        File.WriteAllText(Path.Combine(notes, "a.md"), "a fact");

        using var zip = new ZipArchive(new MemoryStream(ProjectKit.Build(project, notes)));
        var names = zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "_notes/MEMORY.md", "_notes/a.md", "guard.ps1", "skills/implement/skill.md" }, names);
    }
}
