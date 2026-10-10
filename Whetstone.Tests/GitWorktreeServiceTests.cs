using System.Diagnostics;
using Whetstone.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// Reaping a finished ticket's worktree, against a real git repo.
///
/// The behaviour under test is mostly refusal. These directories are where the human picks a ticket
/// up to finish it by hand, so "remove it" must never become "remove it and whatever was in it" —
/// git's refusal to drop a dirty worktree is the last guard against a silently destroyed change,
/// and nothing here may pass --force to step around it.
/// </summary>
public class GitWorktreeServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _repo;
    private readonly GitWorktreeService _svc = new(NullLogger<GitWorktreeService>.Instance);

    public GitWorktreeServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ccd-wt-" + Guid.NewGuid().ToString("N")[..8]);
        _repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_repo);

        Git(_repo, "init", "-b", "main");
        Git(_repo, "config", "user.email", "test@example.com");
        Git(_repo, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(_repo, "README.md"), "hello\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "init");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string AddWorktree(string branch, string dirName)
    {
        var path = Path.Combine(_root, "wt", dirName);
        Git(_repo, "worktree", "add", "-b", branch, path, "main");
        return path;
    }

    [Fact]
    public void List_reports_the_main_checkout_and_each_worktree_with_its_branch()
    {
        var path = AddWorktree("jb/ALPHA-1", "jb-ALPHA-1");

        var all = _svc.List(_repo);

        Assert.Equal(2, all.Count);
        Assert.True(all[0].IsMain);
        var wt = Assert.Single(all, w => !w.IsMain);
        Assert.Equal("jb/ALPHA-1", wt.Branch);
        Assert.Equal(Path.GetFullPath(path), Path.GetFullPath(wt.Path));
    }

    [Fact]
    public void Remove_takes_out_a_clean_worktree_and_leaves_the_branch()
    {
        var path = AddWorktree("jb/ALPHA-2", "jb-ALPHA-2");

        var result = _svc.Remove(_repo, path);

        Assert.True(result.Removed);
        Assert.Null(result.Error);
        Assert.False(Directory.Exists(path));
        // The branch is the record of what was merged - removing the workspace must not remove it.
        Assert.Contains("jb/ALPHA-2", Git(_repo, "branch", "--list", "jb/ALPHA-2"));
    }

    [Fact]
    public void Remove_refuses_a_worktree_with_uncommitted_changes()
    {
        var path = AddWorktree("jb/ALPHA-3", "jb-ALPHA-3");
        File.WriteAllText(Path.Combine(path, "README.md"), "edited by hand, not yet committed\n");

        var result = _svc.Remove(_repo, path);

        Assert.False(result.Removed);
        Assert.NotNull(result.Error);
        Assert.True(Directory.Exists(path));
        Assert.Equal("edited by hand, not yet committed\n", File.ReadAllText(Path.Combine(path, "README.md")));
    }

    [Fact]
    public void Remove_refuses_a_worktree_holding_untracked_files()
    {
        // An uncommitted new file is just as much someone's work as an edit to a tracked one.
        var path = AddWorktree("jb/ALPHA-4", "jb-ALPHA-4");
        File.WriteAllText(Path.Combine(path, "scratch-notes.txt"), "wip\n");

        var result = _svc.Remove(_repo, path);

        Assert.False(result.Removed);
        Assert.True(File.Exists(Path.Combine(path, "scratch-notes.txt")));
    }

    [Fact]
    public void Remove_ignores_a_path_git_does_not_know_as_a_worktree()
    {
        // A ticket can carry a stale path: the user removed the worktree themselves, or edited the
        // record. Handing that to a delete is how an unrelated directory gets destroyed.
        var stranger = Path.Combine(_root, "not-a-worktree");
        Directory.CreateDirectory(stranger);
        File.WriteAllText(Path.Combine(stranger, "important.txt"), "keep me\n");

        var result = _svc.Remove(_repo, stranger);

        Assert.False(result.Removed);
        Assert.Null(result.Error);          // nothing to do, not a failure
        Assert.True(File.Exists(Path.Combine(stranger, "important.txt")));
    }

    [Fact]
    public void Remove_never_touches_the_main_checkout()
    {
        var result = _svc.Remove(_repo, _repo);

        Assert.False(result.Removed);
        Assert.True(File.Exists(Path.Combine(_repo, "README.md")));
    }

    [Fact]
    public void Remove_matches_the_worktree_regardless_of_separator_or_case()
    {
        var path = AddWorktree("jb/ALPHA-5", "jb-ALPHA-5");
        // git reports forward slashes on Windows; a ticket records what the heartbeat wrote.
        var awkward = path.Replace('\\', '/').ToUpperInvariant();

        Assert.True(_svc.Remove(_repo, awkward).Removed);
    }

    [Fact]
    public void List_on_a_directory_that_is_not_a_repo_is_empty_rather_than_a_throw()
        => Assert.Empty(_svc.List(Path.Combine(_root, "nope")));

    private static string Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit(30_000);
        return stdout;
    }
}
