using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// The worktree the board makes for a merge's checks when a ticket has none on this machine - every
/// ticket a worker host built. Against real git: it has to be the branch's tip, set up the way a
/// worker's is, holding no branch, and gone again afterwards.
/// </summary>
public class StewardCheckTreeTests : IDisposable
{
    private readonly string _root;
    private readonly string _repo;
    private readonly TicketGitService _svc;

    public StewardCheckTreeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ccd-ct-" + Guid.NewGuid().ToString("N")[..8]);
        _repo = Path.Combine(_root, "repo");
        var data = Path.Combine(_root, "data");

        Directory.CreateDirectory(_repo);
        Git("init", "-b", "main");
        Git("config", "user.email", "test@example.com");
        Git("config", "user.name", "Test");
        File.WriteAllText(Path.Combine(_repo, ".gitignore"), ".cache/\nsetup.txt\n");
        File.WriteAllText(Path.Combine(_repo, "README.md"), "hello\n");
        Git("add", "-A");
        Git("commit", "-m", "init");
        Git("checkout", "-q", "-b", "ticket-1");
        File.WriteAllText(Path.Combine(_repo, "work.txt"), "the ticket's work\n");
        Git("add", "-A");
        Git("commit", "-m", "work");
        Git("checkout", "-q", "main");
        // Untracked in the hub, and named by worktreeCopyFiles: a worker's worktree gets a copy.
        Directory.CreateDirectory(Path.Combine(_repo, ".cache", "geo"));
        File.WriteAllText(Path.Combine(_repo, ".cache", "geo", "a.json"), "{}");

        Directory.CreateDirectory(Path.Combine(data, "projects", "alpha"));
        File.WriteAllText(Path.Combine(data, "projects", "alpha", "project.defaults.json"), $$"""
            {
              "displayName": "Alpha",
              "projectPath": {{System.Text.Json.JsonSerializer.Serialize(_repo)}},
              "worktreeRoot": {{System.Text.Json.JsonSerializer.Serialize(Path.Combine(_root, "wt"))}},
              "baseBranch": "main",
              "mergeInsteadOfPr": true,
              "worktreeCopyFiles": [".cache/geo"],
              "worktreeSetup": ["echo ready> setup.txt"]
            }
            """);
        File.WriteAllText(Path.Combine(data, "config.defaults.json"), """{ "baseBranch": "main" }""");

        _svc = new TicketGitService(new ProjectRegistry(new DashboardPaths(data)),
            new GitWorktreeService(NullLogger<GitWorktreeService>.Instance), NullLogger<TicketGitService>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
    }

    [Fact]
    public async Task It_is_the_branch_tip_set_up_like_a_workers_and_holding_no_branch_then_it_is_gone()
    {
        var tree = await _svc.CheckTreeAsync("alpha", "ticket-1");
        Assert.Null(tree.Error);
        var dir = tree.Dir!;

        Assert.Equal(Git("rev-parse", "ticket-1"), GitIn(dir, "rev-parse", "HEAD"));
        Assert.True(File.Exists(Path.Combine(dir, "work.txt")));
        Assert.True(File.Exists(Path.Combine(dir, ".cache", "geo", "a.json")));
        Assert.True(File.Exists(Path.Combine(dir, "setup.txt")));
        // Detached: the branch can still be moved or handed back while the checks run.
        Assert.Equal("", GitIn(dir, "branch", "--show-current"));

        // Something a check left behind does not keep it.
        File.WriteAllText(Path.Combine(dir, "work.txt"), "changed by a check\n");
        await tree.Release();
        Assert.False(Directory.Exists(dir));
        Assert.DoesNotContain(dir.Replace('\\', '/'), Git("worktree", "list"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task One_left_behind_by_an_earlier_merge_is_replaced()
    {
        var first = await _svc.CheckTreeAsync("alpha", "ticket-1");
        var second = await _svc.CheckTreeAsync("alpha", "ticket-1");
        Assert.Null(second.Error);
        Assert.Equal(first.Dir, second.Dir);
        await second.Release();
        Assert.False(Directory.Exists(second.Dir));
    }

    [Fact]
    public async Task A_setup_that_fails_says_so_and_leaves_nothing()
    {
        var cfg = Path.Combine(_root, "data", "projects", "alpha", "project.defaults.json");
        File.WriteAllText(cfg, File.ReadAllText(cfg).Replace("echo ready> setup.txt", "exit 3"));

        var tree = await _svc.CheckTreeAsync("alpha", "ticket-1");
        Assert.Null(tree.Dir);
        Assert.Contains("exit 3", tree.Error);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_root, "wt")));
    }

    [Fact]
    public async Task A_branch_that_is_not_there_is_refused()
    {
        var tree = await _svc.CheckTreeAsync("alpha", "ticket-nope");
        Assert.Null(tree.Dir);
        Assert.Contains("ticket-nope", tree.Error);
    }

    private string Git(params string[] args) => GitIn(_repo, args);

    private static string GitIn(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed");
        return o.Trim();
    }
}
