using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// A ticket filed on a feature branch of its own, on a project that delivers by pull request, merged
/// straight into that feature branch and pushed (<see cref="ProjectConfig.MergeIntoFeatureBranches"/>,
/// <see cref="TicketGitService.MergeIntoFeatureBaseAsync"/>). The pull request stays the only way for
/// a ticket on the project's base; every repo is worked out before any is pushed; and the merge is
/// made on origin's feature branch, because that is what the next ticket of the feature forks from.
/// </summary>
public class FeatureBranchMergeTests : IDisposable
{
    private const string Feature = "feature/PROJ-123";
    private const string Fix = "jb/PROJ-124";
    private const string Plain = "jb/PROJ-125";

    private readonly string _root;
    private readonly string _project;
    private readonly string _data;
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<DashboardDbContext> _options;
    private readonly TicketGitService _svc;
    private readonly ProjectRegistry _registry;

    public FeatureBranchMergeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ccd-fmerge-" + Guid.NewGuid().ToString("N")[..8]);
        _project = Path.Combine(_root, "Alpha");
        _data = Path.Combine(_root, "data");
        foreach (var dir in new[] { "alpha-web", "alpha-api" }) NewRepo(dir);

        const string repos = """[ "alpha-web", "alpha-api" ]""";
        // alpha raises pull requests and lets a feature's tickets merge without one; bravo does not;
        // charlie merges every ticket locally already, so the switch has nothing to add there.
        WriteProject("alpha", $$"""{ "projectPath": {{Json(_project)}}, "baseBranch": "main", "repos": {{repos}}, "mergeIntoFeatureBranches": true }""");
        WriteProject("bravo", $$"""{ "projectPath": {{Json(_project)}}, "baseBranch": "main", "repos": {{repos}} }""");
        WriteProject("charlie", $$"""{ "projectPath": {{Json(_project)}}, "baseBranch": "main", "repos": {{repos}}, "mergeInsteadOfPr": true, "mergeIntoFeatureBranches": true }""");
        _registry = new ProjectRegistry(new DashboardPaths(_data));

        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _options = new DbContextOptionsBuilder<DashboardDbContext>().UseSqlite(_conn).Options;
        using (var ctx = new DashboardDbContext(_options))
        {
            ctx.Database.EnsureCreated();
            foreach (var key in new[] { "alpha", "bravo", "charlie" })
            {
                ctx.Tickets.Add(new Ticket { ProjectKey = key, Title = "A fix on the feature", Branch = Fix, BaseBranch = Feature });
                ctx.Tickets.Add(new Ticket { ProjectKey = key, Title = "A fix on main", Branch = Plain });
            }
            ctx.SaveChanges();
        }

        _svc = new TicketGitService(_registry, new GitWorktreeService(NullLogger<GitWorktreeService>.Instance),
            NullLogger<TicketGitService>.Instance, new DbBases(_options));
    }

    public void Dispose()
    {
        _conn.Dispose();
        try
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch { /* best effort */ }
    }

    [Fact]
    public void The_switch_applies_only_where_tickets_otherwise_go_by_pull_request()
    {
        Assert.True(_registry.GetProjectConfig("alpha").FeatureMergeOn);
        Assert.False(_registry.GetProjectConfig("bravo").FeatureMergeOn);
        Assert.True(_registry.GetProjectConfig("charlie").MergeIntoFeatureBranches);
        Assert.False(_registry.GetProjectConfig("charlie").FeatureMergeOn);
    }

    [Fact]
    public async Task Every_touched_repo_is_merged_into_origins_feature_branch_and_the_local_one_follows()
    {
        OnBranch("alpha-web", Fix, "web.txt", "W: the fix's web half");
        OnBranch("alpha-api", Fix, "api.txt", "A: the fix's api half");
        // Someone else's ticket landed on the feature since this machine last fetched.
        PushFromElsewhere("alpha-api", "other.txt", "O: another ticket of the feature");

        Assert.Equal(Feature, await _svc.FeatureBaseOfAsync("alpha", Fix));
        var r = await _svc.MergeIntoFeatureBaseAsync("alpha", Fix, "Merge the fix", null);

        Assert.True(r.Ok, r.Error);
        Assert.Equal(Feature, r.BaseBranch);
        Assert.Equal(new[] { "merged", "merged" }, r.Repos.Select(x => x.Outcome));
        foreach (var dir in new[] { "alpha-web", "alpha-api" })
        {
            var origin = Origin(dir);
            var onOrigin = Out(origin, "rev-parse", $"refs/heads/{Feature}");
            Assert.Equal(r.Repos.Single(x => x.Dir == dir).MergeSha, onOrigin);
            Assert.Equal("Merge the fix", Out(origin, "log", "-1", "--format=%s", Feature));
            // A merge commit: origin's old tip first, the ticket second.
            Assert.Equal(Out(Hub(dir), "rev-parse", Fix), Out(origin, "rev-parse", $"{Feature}^2"));
            // The local feature branch was not checked out anywhere, so it followed.
            Assert.Equal(onOrigin, Out(Hub(dir), "rev-parse", $"refs/heads/{Feature}"));
        }
        // The merge sits on what the other ticket pushed, not on this machine's stale copy.
        Assert.Contains("other.txt", Out(Origin("alpha-api"), "ls-tree", "--name-only", Feature));

        // Pressing again has nothing left to do, and says so rather than making an empty merge.
        var again = await _svc.MergeIntoFeatureBaseAsync("alpha", Fix, null, null);
        Assert.False(again.Ok);
        Assert.Contains("nothing to merge", again.Error);
    }

    [Fact]
    public async Task A_repo_the_ticket_did_not_touch_is_left_alone()
    {
        OnBranch("alpha-web", Fix, "web.txt", "W: only the web repo");
        var apiBefore = Out(Origin("alpha-api"), "rev-parse", $"refs/heads/{Feature}");

        var r = await _svc.MergeIntoFeatureBaseAsync("alpha", Fix, null, null);

        Assert.True(r.Ok, r.Error);
        Assert.Equal("merged", r.Repos.Single(x => x.Dir == "alpha-web").Outcome);
        Assert.Equal("already", r.Repos.Single(x => x.Dir == "alpha-api").Outcome);
        Assert.Equal(apiBefore, Out(Origin("alpha-api"), "rev-parse", $"refs/heads/{Feature}"));
    }

    [Fact]
    public async Task A_conflict_in_one_repo_changes_nothing_in_any_of_them()
    {
        OnBranch("alpha-web", Fix, "web.txt", "W: clean on its own");
        OnBranch("alpha-api", Fix, "shared.txt", "A: the ticket's line");
        PushFromElsewhere("alpha-api", "shared.txt", "O: a different line in the same place");
        var before = new[] { "alpha-web", "alpha-api" }.ToDictionary(d => d, d => Out(Origin(d), "rev-parse", $"refs/heads/{Feature}"));

        var r = await _svc.MergeIntoFeatureBaseAsync("alpha", Fix, null, null);

        Assert.False(r.Ok);
        Assert.Contains("Nothing was changed in any repo", r.Error);
        Assert.Equal("ready", r.Repos.Single(x => x.Dir == "alpha-web").Outcome);
        var api = r.Repos.Single(x => x.Dir == "alpha-api");
        Assert.Equal("conflict", api.Outcome);
        Assert.Contains("shared.txt", api.ConflictedFiles);
        foreach (var (dir, sha) in before)
            Assert.Equal(sha, Out(Origin(dir), "rev-parse", $"refs/heads/{Feature}"));
    }

    [Fact]
    public async Task A_ticket_on_the_projects_base_still_goes_by_pull_request()
    {
        OnBranch("alpha-web", Plain, "web.txt", "W: a fix on main", from: "main");
        Assert.Null(await _svc.FeatureBaseOfAsync("alpha", Plain));

        var r = await _svc.MergeIntoFeatureBaseAsync("alpha", Plain, null, null);
        Assert.False(r.Ok);
        Assert.Contains("pull request", r.Error);
    }

    [Fact]
    public async Task Nothing_merges_where_the_owner_has_not_switched_it_on()
    {
        OnBranch("alpha-web", Fix, "web.txt", "W: the fix");
        var before = Out(Origin("alpha-web"), "rev-parse", $"refs/heads/{Feature}");

        Assert.Null(await _svc.FeatureBaseOfAsync("bravo", Fix));
        var r = await _svc.MergeIntoFeatureBaseAsync("bravo", Fix, null, null);
        Assert.False(r.Ok);
        Assert.Contains("Turn it on", r.Error);
        Assert.Equal(before, Out(Origin("alpha-web"), "rev-parse", $"refs/heads/{Feature}"));
    }

    [Fact]
    public async Task A_local_feature_branch_that_is_checked_out_is_left_where_it_was()
    {
        OnBranch("alpha-web", Fix, "web.txt", "W: the fix");
        var hub = Hub("alpha-web");
        var localBefore = Out(hub, "rev-parse", $"refs/heads/{Feature}");
        Git(hub, "checkout", "-q", Feature);

        var r = await _svc.MergeIntoFeatureBaseAsync("alpha", Fix, null, null);

        Assert.True(r.Ok, r.Error);
        var web = r.Repos.Single(x => x.Dir == "alpha-web");
        Assert.Equal(web.MergeSha, Out(Origin("alpha-web"), "rev-parse", $"refs/heads/{Feature}"));
        Assert.Equal(localBefore, Out(hub, "rev-parse", $"refs/heads/{Feature}"));
        Assert.Contains("checked out", web.LocalNote);
    }

    // ---- fixture ---------------------------------------------------------------------------------

    private string Hub(string dir) => Path.Combine(_project, dir);
    private string Origin(string dir) => Path.Combine(_root, "origins", dir + ".git");

    /// <summary>A repo with an origin, main and the feature branch on both, and the ticket's branch off the feature.</summary>
    private void NewRepo(string dir)
    {
        var origin = Origin(dir);
        Directory.CreateDirectory(origin);
        Git(origin, "init", "-q", "--bare", "-b", "main");
        var hub = Hub(dir);
        Directory.CreateDirectory(hub);
        Init(hub);
        Git(hub, "remote", "add", "origin", origin);
        Commit(hub, "README.md", "init");
        Git(hub, "push", "-q", "-u", "origin", "main");
        Git(hub, "branch", Feature, "main");
        OnBranch(dir, Feature, "feature.txt", "F: the feature so far", from: null);
        Git(hub, "push", "-q", "origin", Feature);
        Git(hub, "fetch", "-q", "origin");
        Git(hub, "branch", Fix, Feature);
    }

    /// <summary>A commit on <paramref name="branch"/> in <paramref name="dir"/>, made in a throwaway worktree.</summary>
    private void OnBranch(string dir, string branch, string file, string subject, string? from = Feature)
    {
        var hub = Hub(dir);
        if (from is not null && Run(hub, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}").Code != 0)
            Git(hub, "branch", branch, from);
        var wt = Path.Combine(_root, "wt-" + Guid.NewGuid().ToString("N")[..6]);
        Git(hub, "worktree", "add", "-q", wt, branch);
        Commit(wt, file, subject);
        Git(hub, "worktree", "remove", "--force", wt);
    }

    /// <summary>Another machine pushing to origin's feature branch, which this one has not fetched.</summary>
    private void PushFromElsewhere(string dir, string file, string subject)
    {
        var other = Path.Combine(_root, "elsewhere-" + Guid.NewGuid().ToString("N")[..6]);
        Git(_root, "clone", "-q", "-b", Feature, Origin(dir), other);
        Init(other, initRepo: false);
        Commit(other, file, subject);
        Git(other, "push", "-q", "origin", Feature);
    }

    private void WriteProject(string key, string json)
    {
        Directory.CreateDirectory(Path.Combine(_data, "projects", key));
        File.WriteAllText(Path.Combine(_data, "projects", key, "project.defaults.json"), json);
    }

    private sealed class DbBases(DbContextOptions<DashboardDbContext> options) : ITicketBases
    {
        public async Task<string?> BaseOfBranchAsync(ProjectConfig cfg, string branch, CancellationToken ct = default)
        {
            await using var db = new DashboardDbContext(options);
            return await SliceBranches.ForBranchAsync(db, cfg, branch, ct);
        }
    }

    private static string Json(string s) => System.Text.Json.JsonSerializer.Serialize(s);

    private static void Init(string dir, bool initRepo = true)
    {
        if (initRepo) Git(dir, "init", "-q", "-b", "main");
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

    private static string Out(string dir, params string[] args)
    {
        var (code, output) = Run(dir, args);
        Assert.True(code == 0, $"git {string.Join(' ', args)} failed in {dir}: {output}");
        return output.Trim();
    }

    private static void Git(string dir, params string[] args) => Out(dir, args);

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
        return (p.ExitCode, p.ExitCode == 0 ? stdout : stdout + stderr);
    }
}
