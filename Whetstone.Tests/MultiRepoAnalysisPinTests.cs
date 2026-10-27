using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Whetstone.Models;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// Which code a Classroom analysis read - <see cref="AnalysisPin"/> - against real repos: one repo
/// pinned exactly as it always was, and a workspace of several pinned by each changed repo's commit
/// and tree, with every read of the code at the pin going to that repo's own commit.
/// </summary>
public class MultiRepoAnalysisPinTests : IDisposable
{
    private const string Branch = "jb/PROJ-1";

    private readonly string _root;
    private readonly string _project;
    private readonly string _workspace;
    private readonly string _solo;
    private readonly ProjectRegistry _registry;
    private readonly TicketGitService _git;

    public MultiRepoAnalysisPinTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ccd-pin-" + Guid.NewGuid().ToString("N")[..8]);
        _project = Path.Combine(_root, "Alpha");
        _workspace = Path.Combine(_root, "wt", "jb-PROJ-1");
        var data = Path.Combine(_root, "data");
        foreach (var dir in new[] { "alpha-web", "alpha-api", "alpha-infra" }) NewRepo(Path.Combine(_project, dir), dir);
        _solo = NewRepo(Path.Combine(_root, "solo"), "solo");

        Write(data, "alpha", $$"""
            { "displayName": "Alpha", "projectPath": {{Json(_project)}}, "baseBranch": "main",
              "repos": [ "alpha-web", "alpha-api", { "dir": "alpha-infra", "readOnly": true } ] }
            """);
        Write(data, "solo", $$"""{ "displayName": "Solo", "projectPath": {{Json(_solo)}}, "baseBranch": "main" }""");
        File.WriteAllText(Path.Combine(data, "config.defaults.json"), """{ "baseBranch": "main" }""");
        _registry = new ProjectRegistry(new DashboardPaths(data));
        _git = new TicketGitService(_registry, new GitWorktreeService(NullLogger<GitWorktreeService>.Instance),
            NullLogger<TicketGitService>.Instance);

        Directory.CreateDirectory(_workspace);
        foreach (var dir in new[] { "alpha-web", "alpha-api" })
        {
            Git(Path.Combine(_project, dir), "worktree", "add", "-q", "-b", Branch, "--no-track", Wt(dir), "origin/main");
            Identity(Wt(dir));
        }
        Git(Path.Combine(_project, "alpha-infra"), "worktree", "add", "-q", "--detach", Wt("alpha-infra"), "origin/main");
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

    private Ticket Ticket(string key = "alpha") => new()
    {
        Id = 1, ProjectKey = key, Branch = Branch, Phase = TicketPhase.NeedsRefinement,
        WorktreePath = key == "alpha" ? _workspace : _solo,
    };

    private AnalysisPin Pin(string key = "alpha") => AnalysisPin.Of(Ticket(key), _registry.GetProjectConfig(key));

    // ---- one repo: exactly as before ---------------------------------------------------------

    [Fact]
    public void A_project_of_one_is_pinned_to_its_head_and_tree()
    {
        var pin = Pin("solo");
        Assert.Equal(Rev(_solo, "HEAD"), pin.HeadSha);
        Assert.Equal(Rev(_solo, "HEAD^{tree}"), pin.TreeSha);
        Assert.Empty(pin.Repos);
        Assert.Null(pin.ReposJson());
        Assert.Null(pin.HeadsByRepo);
    }

    // ---- several repos ------------------------------------------------------------------------

    [Fact]
    public void A_workspace_with_nothing_changed_has_no_pin()
    {
        var pin = Pin();
        Assert.Null(pin.HeadSha);
        Assert.Null(pin.TreeSha);
    }

    [Fact]
    public void Only_the_repos_the_ticket_changed_are_pinned()
    {
        Commit(Wt("alpha-web"), "feature.txt", "web: the feature");
        var pin = Pin();

        var only = Assert.Single(pin.Repos);
        Assert.Equal("alpha-web", only.Dir);
        Assert.Equal(Rev(Wt("alpha-web"), "HEAD"), only.Head);
        Assert.Equal(Rev(Wt("alpha-web"), "HEAD^{tree}"), only.Tree);
        // A fingerprint, the length of a commit, and not any repo's commit.
        Assert.Matches("^[0-9a-f]{40}$", pin.HeadSha!);
        Assert.NotEqual(only.Head, pin.HeadSha);
        Assert.Equal(only.Head, AnalysisPin.HeadsFrom(pin.ReposJson())!["alpha-web"]);
    }

    [Fact]
    public void An_untouched_repo_brought_up_to_its_base_does_not_move_the_pin()
    {
        Commit(Wt("alpha-web"), "feature.txt", "web: the feature");
        var before = Pin();

        Upstream("alpha-api", "api: merged by someone else");
        Git(Wt("alpha-api"), "fetch", "-q", "origin");
        Git(Wt("alpha-api"), "merge", "-q", "--ff-only", "origin/main");

        var after = Pin();
        Assert.Equal(before.HeadSha, after.HeadSha);
        Assert.Equal(before.TreeSha, after.TreeSha);
    }

    [Fact]
    public void A_squash_moves_the_head_fingerprint_and_keeps_the_tree_one()
    {
        Commit(Wt("alpha-web"), "feature.txt", "web: part one");
        Commit(Wt("alpha-web"), "feature.txt", "web: part two");
        var before = Pin();

        Git(Wt("alpha-web"), "reset", "-q", "--soft", "origin/main");
        Git(Wt("alpha-web"), "commit", "-q", "-m", "web: the feature, squashed");
        var after = Pin();

        Assert.NotEqual(before.HeadSha, after.HeadSha);
        Assert.Equal(before.TreeSha, after.TreeSha);
        // And the staleness rule, which compares trees first, calls it the same code.
        var analysis = new TicketAnalysis { PhaseAtGeneration = TicketPhase.NeedsRefinement, HeadSha = before.HeadSha, TreeSha = before.TreeSha };
        Assert.False(TicketAnalysisService.IsStale(analysis, Ticket(), after.HeadSha, after.TreeSha));
    }

    [Fact]
    public void A_second_repo_changing_moves_the_pin()
    {
        Commit(Wt("alpha-web"), "feature.txt", "web: the feature");
        var one = Pin();
        Commit(Wt("alpha-api"), "Endpoint.cs", "api: the endpoint");
        var two = Pin();

        Assert.Equal(new[] { "alpha-web", "alpha-api" }, two.Repos.Select(r => r.Dir));
        Assert.NotEqual(one.HeadSha, two.HeadSha);
        Assert.NotEqual(one.TreeSha, two.TreeSha);
        var analysis = new TicketAnalysis { PhaseAtGeneration = TicketPhase.NeedsRefinement, HeadSha = one.HeadSha, TreeSha = one.TreeSha };
        Assert.True(TicketAnalysisService.IsStale(analysis, Ticket(), two.HeadSha, two.TreeSha));
    }

    // ---- reading the code at the pin ------------------------------------------------------------

    [Fact]
    public async Task Files_are_read_at_their_own_repos_pinned_commit_not_the_tip()
    {
        Commit(Wt("alpha-web"), "feature.txt", "web: what the analysis read");
        Commit(Wt("alpha-api"), "Endpoint.cs", "api: what the analysis read");
        var pin = Pin();
        var heads = AnalysisPin.HeadsFrom(pin.ReposJson());
        // The branch moves on after the analysis was written.
        Commit(Wt("alpha-web"), "feature.txt", "web: written afterwards");

        var diff = await _git.FileDiffAsync("alpha", pin.HeadSha!, "alpha-web/feature.txt", repoHeads: heads);
        Assert.Null(diff.Error);
        Assert.Equal(1, diff.Added);
        Assert.DoesNotContain("written afterwards", diff.Patch);

        var (text, error) = await _git.FileAtAsync("alpha", pin.HeadSha!, "alpha-api/Endpoint.cs", repoHeads: heads);
        Assert.Null(error);
        Assert.Contains("api: what the analysis read", text);

        var (files, listError) = await _git.ChangedFilesAsync("alpha", pin.HeadSha!, repoHeads: heads);
        Assert.Null(listError);
        Assert.Equal(new[] { "alpha-web/feature.txt", "alpha-api/Endpoint.cs" }, files.Select(f => f.Path));
        Assert.Equal(1, files[0].Added);
    }

    [Fact]
    public async Task Without_the_pins_a_fingerprint_names_no_commit()
    {
        Commit(Wt("alpha-web"), "feature.txt", "web: the feature");
        var pin = Pin();
        Assert.NotNull((await _git.FileDiffAsync("alpha", pin.HeadSha!, "alpha-web/feature.txt")).Error);
    }

    // ---- fixture --------------------------------------------------------------------------------

    private string Wt(string dir) => Path.Combine(_workspace, dir);
    private string Origin(string name) => Path.Combine(_root, "origins", name + ".git");

    private string NewRepo(string hub, string name)
    {
        var origin = Origin(name);
        Directory.CreateDirectory(origin);
        Git(origin, "init", "-q", "--bare", "-b", "main");
        Directory.CreateDirectory(hub);
        Git(hub, "init", "-q", "-b", "main");
        Identity(hub);
        Git(hub, "remote", "add", "origin", origin);
        Commit(hub, "README.md", $"init {name}");
        Git(hub, "push", "-q", "-u", "origin", "main");
        return hub;
    }

    private void Upstream(string dir, string subject)
    {
        var other = Path.Combine(_root, "other-" + Guid.NewGuid().ToString("N")[..6]);
        Git(_root, "clone", "-q", Origin(dir), other);
        Identity(other);
        Commit(other, "upstream.txt", subject);
        Git(other, "push", "-q", "origin", "main");
    }

    private static void Write(string data, string key, string json)
    {
        Directory.CreateDirectory(Path.Combine(data, "projects", key));
        File.WriteAllText(Path.Combine(data, "projects", key, "project.defaults.json"), json);
    }

    private static string Json(string s) => System.Text.Json.JsonSerializer.Serialize(s);

    private static void Identity(string dir)
    {
        Git(dir, "config", "user.email", "test@example.com");
        Git(dir, "config", "user.name", "Test");
        Git(dir, "config", "commit.gpgsign", "false");
    }

    private static void Commit(string dir, string file, string subject)
    {
        File.AppendAllText(Path.Combine(dir, file), subject + "\n");
        Git(dir, "add", "-A");
        Git(dir, "commit", "-q", "-m", subject);
    }

    private static string Rev(string dir, string rev) => Run(dir, "rev-parse", rev).Trim();

    private static void Git(string dir, params string[] args) => Run(dir, args);

    private static string Run(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed in {dir}: {stderr}");
        return stdout;
    }
}
