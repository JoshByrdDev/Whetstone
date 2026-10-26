using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Whetstone.Api;
using Whetstone.Models;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// Step 3 of docs/multi-repo-plan.md: the board's git actions on a project of several repos, against
/// real ones - a folder holding alpha-web, alpha-api (on develop) and a read-only alpha-infra, each
/// with a bare origin beside it, and worktrees laid out as data/worker-repos.ps1 lays them out.
///
/// The touched cases are the same as the ones in data/worker-repos.tests.ps1, on purpose: the worker
/// delivers the repos it calls touched and the board shows a person the ones it does, and this is
/// where the two rules are held to the same answers.
/// </summary>
public class MultiRepoGitTests : IDisposable
{
    private const string Branch = "jb/PROJ-1";

    private readonly string _root;
    private readonly string _project;
    private readonly string _workspace;
    private readonly string _solo;
    private readonly ProjectRegistry _registry;
    private readonly TicketGitService _svc;

    public MultiRepoGitTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ccd-multi-" + Guid.NewGuid().ToString("N")[..8]);
        _project = Path.Combine(_root, "Alpha");
        _workspace = Path.Combine(_root, "wt", "jb-PROJ-1");
        var data = Path.Combine(_root, "data");

        NewRepo("alpha-web");
        NewRepo("alpha-api", extraBase: "develop");
        NewRepo("alpha-infra");
        _solo = NewRepo("solo", under: _root);

        WriteProject(data, "alpha", $$"""
            {
              "displayName": "Alpha",
              "projectPath": {{Json(_project)}},
              "baseBranch": "main",
              "repos": [
                "alpha-web",
                { "dir": "alpha-api", "baseBranch": "develop" },
                { "dir": "alpha-infra", "readOnly": true }
              ]
            }
            """);
        // One repo of the same folder whose base exists nowhere.
        WriteProject(data, "bravo", $$"""
            { "displayName": "Bravo", "projectPath": {{Json(_project)}}, "repos": [ { "dir": "alpha-api", "baseBranch": "gone" } ] }
            """);
        WriteProject(data, "solo", $$"""
            { "displayName": "Solo", "projectPath": {{Json(_solo)}}, "baseBranch": "main" }
            """);
        File.WriteAllText(Path.Combine(data, "config.defaults.json"), """{ "baseBranch": "main" }""");

        _registry = new ProjectRegistry(new DashboardPaths(data));
        _svc = new TicketGitService(_registry, new GitWorktreeService(NullLogger<GitWorktreeService>.Instance),
            NullLogger<TicketGitService>.Instance);
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch { /* best effort */ }
    }

    // ---- which repo -------------------------------------------------------------------------

    [Fact]
    public void A_project_of_one_is_its_own_target_and_names_none()
    {
        var cfg = _registry.GetProjectConfig("solo");
        var target = _svc.TargetOf(cfg, null, out var error);
        Assert.Null(error);
        Assert.Equal("", target!.Dir);
        Assert.Equal(_solo, target.Path);
        Assert.Equal("main", target.BaseBranch);

        Assert.Null(_svc.TargetOf(cfg, "alpha-web", out error));
        Assert.Contains("is one repo", error);
    }

    [Fact]
    public void A_project_of_several_needs_its_repo_named()
    {
        var cfg = _registry.GetProjectConfig("alpha");

        Assert.Null(_svc.TargetOf(cfg, null, out var error));
        Assert.Contains("alpha-web, alpha-api, alpha-infra", error);
        Assert.Null(_svc.TargetOf(cfg, "alpha-docs", out error));
        Assert.Contains("no repo named 'alpha-docs'", error);

        var api = _svc.TargetOf(cfg, "ALPHA-API", out error);
        Assert.Null(error);
        Assert.Equal("alpha-api", api!.Dir);
        Assert.Equal(Path.Combine(_project, "alpha-api"), api.Path);
        Assert.Equal("develop", api.BaseBranch);
        Assert.Equal("main", _svc.TargetOf(cfg, "alpha-web", out _)!.BaseBranch);
    }

    [Fact]
    public void Nothing_is_written_to_a_read_only_repo()
    {
        var cfg = _registry.GetProjectConfig("alpha");
        Assert.NotNull(_svc.TargetOf(cfg, "alpha-infra", out _));
        Assert.Null(_svc.WriteTargetOf(cfg, "alpha-infra", out var error));
        Assert.Contains("read-only", error);
    }

    // ---- touched: the same cases as worker-repos.tests.ps1 ----------------------------------

    [Fact]
    public async Task A_fresh_workspace_has_touched_nothing()
    {
        OpenWorkspace();
        var touches = await _svc.TouchedAsync("alpha", Branch);

        Assert.Equal(new[] { "alpha-web", "alpha-api", "alpha-infra" }, touches.Select(t => t.Dir));
        Assert.All(touches, t => Assert.False(t.Touched));
        Assert.Equal("origin/develop", touches[1].BaseRef);
        Assert.True(touches[2].ReadOnly);
    }

    [Fact]
    public async Task A_commit_or_an_untracked_file_touches_a_repo_and_an_ignored_one_does_not()
    {
        OpenWorkspace();
        Commit(Wt("alpha-web"), "feature.txt", "web: the feature");
        File.WriteAllText(Path.Combine(Wt("alpha-api"), "setup-count.txt"), "ignored\n");

        Assert.Equal(new[] { "alpha-web" }, await TouchedDirs());

        File.WriteAllText(Path.Combine(Wt("alpha-api"), "new-file.cs"), "x\n");
        var touches = await _svc.TouchedAsync("alpha", Branch);
        Assert.Equal(new[] { "alpha-web", "alpha-api" }, touches.Where(t => t.Touched).Select(t => t.Dir));
        Assert.Equal(1, touches[0].Ahead);
        Assert.Equal(1, touches[1].Dirty);
    }

    [Fact]
    public async Task A_read_only_repo_is_never_touched_even_with_changes_in_it()
    {
        OpenWorkspace();
        File.AppendAllText(Path.Combine(Wt("alpha-infra"), "README.md"), "edited\n");
        Assert.Empty(await TouchedDirs());
    }

    [Fact]
    public async Task A_branch_whose_commits_are_all_in_its_base_is_untouched()
    {
        OpenWorkspace();
        Commit(Wt("alpha-web"), "feature.txt", "web: the feature");

        // Its PR merged, as a merge commit, by someone else.
        var merger = Path.Combine(_root, "merger");
        Git(_root, "clone", "-q", Origin("alpha-web"), merger);
        Identity(merger);
        Git(merger, "fetch", "-q", Path.Combine(_project, "alpha-web"), Branch);
        Git(merger, "merge", "-q", "--no-ff", "-m", "Merged PR: the feature", "FETCH_HEAD");
        Git(merger, "push", "-q", "origin", "main");
        Git(Path.Combine(_project, "alpha-web"), "fetch", "-q", "origin");

        Assert.Empty(await TouchedDirs());
    }

    [Fact]
    public async Task A_repo_whose_base_exists_nowhere_counts_as_touched()
    {
        OpenWorkspace();
        var touch = Assert.Single(await _svc.TouchedAsync("bravo", Branch));
        Assert.True(touch.Touched);
        Assert.Null(touch.BaseRef);
    }

    [Fact]
    public async Task A_repo_without_the_branch_is_untouched()
    {
        Assert.Empty(await TouchedDirs());
    }

    // ---- one repo per call ------------------------------------------------------------------

    [Fact]
    public async Task The_preflight_is_one_repo_measured_against_its_own_base()
    {
        OpenWorkspace();
        Commit(Wt("alpha-api"), "Endpoint.cs", "api: the endpoint");

        var api = await _svc.LocalPreflightAsync("alpha", Branch, member: "alpha-api");
        Assert.True(api.Ok, api.Error);
        Assert.Equal(Path.Combine(_project, "alpha-api"), api.RepoPath);
        Assert.Equal("develop", api.BaseBranch);
        Assert.Equal("origin/develop", api.BaseRef);
        Assert.Equal(1, api.Ahead);
        // Inside one repo's own answer, a path is relative to that repo.
        Assert.Equal("Endpoint.cs", Assert.Single(api.Files).Path);

        var none = await _svc.LocalPreflightAsync("alpha", Branch);
        Assert.False(none.Ok);
        Assert.Contains("say which one", none.Error);
    }

    [Fact]
    public async Task Writes_go_to_the_named_repo_and_never_to_a_read_only_one()
    {
        OpenWorkspace();
        Commit(Wt("alpha-web"), "feature.txt", "web: the feature");

        var pr = await _svc.CreateAsync("alpha", Branch, "PROJ-1: the feature", null, member: "alpha-web");
        Assert.True(pr.Ok, pr.Error);
        Assert.Equal(Sha(Wt("alpha-web"), "HEAD"), Sha(Origin("alpha-web"), Branch));
        Assert.Equal("", Sha(Origin("alpha-api"), Branch));

        Assert.Contains("read-only", (await _svc.CreateAsync("alpha", Branch, "t", null, member: "alpha-infra")).Error);
        Assert.Contains("read-only", (await _svc.RebaseAsync("alpha", Branch, member: "alpha-infra")).Error);
        Assert.Contains("read-only", (await _svc.ForcePushAsync("alpha", Branch, member: "alpha-infra")).Error);
        Assert.Contains("read-only", (await _svc.CheckoutAsync("alpha", Branch, member: "alpha-infra")).Error);
        Assert.Contains("say which one", (await _svc.CreateAsync("alpha", Branch, "t", null)).Error);
        Assert.Contains("say which one", (await _svc.BaseCheckAsync("alpha", Branch)).Error);
    }

    [Fact]
    public async Task A_rebase_replays_one_repo_onto_its_own_base()
    {
        OpenWorkspace();
        Commit(Wt("alpha-api"), "Endpoint.cs", "api: the endpoint");
        Upstream("alpha-api", "develop", "api: landed on develop meanwhile");
        Upstream("alpha-web", "main", "web: landed on main meanwhile");

        var api = await _svc.RebaseAsync("alpha", Branch, member: "alpha-api");
        Assert.True(api.Ok, api.Error);
        Assert.Equal("origin/develop", api.BaseRef);
        Assert.True(File.Exists(Path.Combine(Wt("alpha-api"), "upstream.txt")));
        Assert.False(File.Exists(Path.Combine(Wt("alpha-web"), "upstream.txt")));
    }

    // ---- paths that name their repo ---------------------------------------------------------

    [Fact]
    public async Task Files_listed_across_the_ticket_name_their_repo_first()
    {
        OpenWorkspace();
        Commit(Wt("alpha-web"), "feature.txt", "web: the feature");
        Commit(Wt("alpha-api"), "Endpoint.cs", "api: the endpoint");

        var (files, error) = await _svc.ChangedFilesAsync("alpha", Branch);
        Assert.Null(error);
        Assert.Equal(new[] { "alpha-web/feature.txt", "alpha-api/Endpoint.cs" }, files.Select(f => f.Path));

        // A commit is in the one repo that made it.
        var (one, _) = await _svc.ChangedFilesAsync("alpha", Sha(Wt("alpha-api"), "HEAD"));
        Assert.Equal(new[] { "alpha-api/Endpoint.cs" }, one.Select(f => f.Path));
    }

    [Fact]
    public async Task One_file_is_read_from_the_repo_its_path_starts_with()
    {
        OpenWorkspace();
        Commit(Wt("alpha-web"), "feature.txt", "web: the feature");

        var diff = await _svc.FileDiffAsync("alpha", Branch, "alpha-web/feature.txt");
        Assert.Null(diff.Error);
        Assert.Equal("alpha-web/feature.txt", diff.Path);
        Assert.Equal(1, diff.Added);

        var (text, textError) = await _svc.FileAtAsync("alpha", Branch, @"alpha-web\feature.txt");
        Assert.Null(textError);
        Assert.Contains("web: the feature", text);

        Assert.Contains("does not start with one of", (await _svc.FileDiffAsync("alpha", Branch, "feature.txt")).Error);
        Assert.Contains("does not start with one of", (await _svc.FileDiffAsync("alpha", Branch, "alpha-docs/x.md")).Error);
    }

    [Fact]
    public async Task A_project_of_one_reads_paths_exactly_as_before()
    {
        Git(_solo, "checkout", "-q", "-b", Branch);
        Commit(_solo, "feature.txt", "solo: the feature");
        Git(_solo, "checkout", "-q", "main");

        var diff = await _svc.FileDiffAsync("solo", Branch, "feature.txt");
        Assert.Null(diff.Error);
        Assert.Equal("feature.txt", diff.Path);
        var (files, _) = await _svc.ChangedFilesAsync("solo", Branch);
        Assert.Equal(new[] { "feature.txt" }, files.Select(f => f.Path));
    }

    // ---- refusals and the description -------------------------------------------------------

    [Fact]
    public void Delivery_modes_a_project_of_several_does_not_have_are_refused()
    {
        Assert.Contains("several repos", GitEndpoints.RefuseOnMultiRepo(_registry, "alpha", "merge it into its base"));
        Assert.Null(GitEndpoints.RefuseOnMultiRepo(_registry, "solo", "merge it into its base"));
    }

    [Fact]
    public void A_pr_description_names_the_other_repos_and_the_branch_they_share()
    {
        var t = new Ticket { ProjectKey = "alpha", Branch = Branch, IssueKey = "1234", IssueUrl = "https://example.com/1234" };

        var several = PrDescription.Compose(t, "What it does.", null, "AB#1234", new[] { "alpha-api" });
        Assert.Contains("on branch `jb/PROJ-1` in each. Also in: alpha-api.", several);
        Assert.True(several.IndexOf("Also in", StringComparison.Ordinal) < several.IndexOf("[AB#1234]", StringComparison.Ordinal));

        Assert.Equal(PrDescription.Compose(t, "What it does.", null, "AB#1234"),
            PrDescription.Compose(t, "What it does.", null, "AB#1234", Array.Empty<string>()));
    }

    // ---- fixture ----------------------------------------------------------------------------

    /// <summary>The ticket's workspace as data/worker-repos.ps1 makes it: the branch in each writable repo, the read-only one detached.</summary>
    private void OpenWorkspace()
    {
        Directory.CreateDirectory(_workspace);
        Git(Path.Combine(_project, "alpha-web"), "worktree", "add", "-q", "-b", Branch, "--no-track", Wt("alpha-web"), "origin/main");
        Git(Path.Combine(_project, "alpha-api"), "worktree", "add", "-q", "-b", Branch, "--no-track", Wt("alpha-api"), "origin/develop");
        Git(Path.Combine(_project, "alpha-infra"), "worktree", "add", "-q", "--detach", Wt("alpha-infra"), "origin/main");
        foreach (var dir in new[] { "alpha-web", "alpha-api" }) Identity(Wt(dir));
    }

    private async Task<string[]> TouchedDirs() =>
        (await _svc.TouchedAsync("alpha", Branch)).Where(t => t.Touched).Select(t => t.Dir).ToArray();

    private string Wt(string dir) => Path.Combine(_workspace, dir);
    private string Origin(string dir) => Path.Combine(_root, "origins", dir + ".git");

    private string NewRepo(string name, string? extraBase = null, string? under = null)
    {
        var hub = Path.Combine(under ?? _project, name);
        var origin = Origin(name);
        Directory.CreateDirectory(origin);
        Git(origin, "init", "-q", "--bare", "-b", "main");
        Directory.CreateDirectory(hub);
        Git(hub, "init", "-q", "-b", "main");
        Identity(hub);
        Git(hub, "remote", "add", "origin", origin);
        File.WriteAllText(Path.Combine(hub, ".gitignore"), "setup-*.txt\n");
        Commit(hub, "README.md", $"init {name}");
        Git(hub, "push", "-q", "-u", "origin", "main");
        if (extraBase is not null)
        {
            Git(hub, "checkout", "-q", "-b", extraBase);
            Commit(hub, $"{extraBase}.txt", $"only on {extraBase}");
            Git(hub, "push", "-q", "-u", "origin", extraBase);
            Git(hub, "checkout", "-q", "main");
        }
        return hub;
    }

    /// <summary>Someone else's commit landing on origin's <paramref name="baseBranch"/>, as a merged PR does.</summary>
    private void Upstream(string dir, string baseBranch, string subject)
    {
        var other = Path.Combine(_root, "other-" + Guid.NewGuid().ToString("N")[..6]);
        Git(_root, "clone", "-q", "-b", baseBranch, Origin(dir), other);
        Identity(other);
        Commit(other, "upstream.txt", subject);
        Git(other, "push", "-q", "origin", baseBranch);
    }

    private static void WriteProject(string data, string key, string json)
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

    private static string Sha(string dir, string rev)
    {
        var (code, output) = Run(dir, "rev-parse", "--verify", "--quiet", rev);
        return code == 0 ? output.Trim() : "";
    }

    private static void Git(string dir, params string[] args)
    {
        var (code, output) = Run(dir, args);
        if (code != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed in {dir}: {output}");
    }

    private static (int Code, string Output) Run(string dir, params string[] args)
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
        return (p.ExitCode, stdout + stderr);
    }
}
