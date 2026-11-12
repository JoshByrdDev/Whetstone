using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Whetstone.Api;
using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// A ticket's own base branch (<see cref="Ticket.BaseBranch"/>): a feature's tickets fork from, are
/// measured against and are delivered into a feature branch, while every other ticket of the same
/// project stays on the project's base. The one rule is ticket, then slice, then project
/// (<see cref="SliceBranches.ForTicketAsync"/>); a started ticket's base cannot change; and a project
/// delivered by pull request needs the branch on origin in every writable repo.
/// <para>
/// The worker's half is in data/worker-base.tests.ps1 ("a ticket's own base").
/// </para>
/// </summary>
public class TicketBaseBranchTests : IDisposable
{
    private const string Feature = "feature/PROJ-123";
    private const string Slice = "slice/13-cards";

    private readonly string _root;
    private readonly string _hub;
    private readonly string _data;
    private readonly ProjectRegistry _registry;
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<DashboardDbContext> _options;
    private readonly TicketGitService _svc;

    public TicketBaseBranchTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ccd-tbase-" + Guid.NewGuid().ToString("N")[..8]);
        _hub = Path.Combine(_root, "hub");
        var origin = Path.Combine(_root, "origin.git");
        _data = Path.Combine(_root, "data");
        Directory.CreateDirectory(origin);
        Git(origin, "init", "-q", "--bare", "-b", "main");
        Directory.CreateDirectory(_hub);
        Init(_hub);
        Git(_hub, "remote", "add", "origin", origin);
        Commit(_hub, "README.md", "init");
        Git(_hub, "push", "-q", "-u", "origin", "main");

        // alpha raises pull requests; bravo merges locally and has slice branches switched on.
        WriteProject("alpha", $$"""{ "projectPath": {{Json(_hub)}}, "baseBranch": "main" }""");
        WriteProject("bravo", $$"""{ "projectPath": {{Json(_hub)}}, "baseBranch": "main", "mergeInsteadOfPr": true, "sliceBranches": true }""");
        _registry = new ProjectRegistry(new DashboardPaths(_data));

        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _options = new DbContextOptionsBuilder<DashboardDbContext>().UseSqlite(_conn).Options;
        using (var ctx = new DashboardDbContext(_options)) ctx.Database.EnsureCreated();

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

    // ---- the rule: ticket, then slice, then project ------------------------------------------

    [Fact]
    public async Task A_tickets_own_base_outranks_its_slice_and_the_project()
    {
        await using var db = new DashboardDbContext(_options);
        db.StewardUnits.Add(new StewardUnit { ProjectKey = "bravo", Unit = 13, Branch = Slice, AgreedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var bravo = _registry.GetProjectConfig("bravo");

        Assert.Equal(Feature, await SliceBranches.ForTicketAsync(db, bravo,
            new Ticket { ProjectKey = "bravo", PlanUnit = 13, BaseBranch = Feature }));
        Assert.Equal(Slice, await SliceBranches.ForTicketAsync(db, bravo,
            new Ticket { ProjectKey = "bravo", PlanUnit = 13 }));
        Assert.Null(await SliceBranches.ForTicketAsync(db, bravo, new Ticket { ProjectKey = "bravo" }));
    }

    [Fact]
    public async Task A_tickets_own_base_works_on_a_project_without_slice_branches()
    {
        await using var db = new DashboardDbContext(_options);
        var alpha = _registry.GetProjectConfig("alpha");
        Assert.False(alpha.SliceBranchesOn);

        Assert.Equal(Feature, await SliceBranches.ForTicketAsync(db, alpha,
            new Ticket { ProjectKey = "alpha", BaseBranch = "  " + Feature + " " }));
        Assert.Null(await SliceBranches.ForTicketAsync(db, alpha, new Ticket { ProjectKey = "alpha", BaseBranch = " " }));
    }

    [Fact]
    public async Task A_feature_ticket_is_measured_against_its_base_and_its_neighbour_against_the_project()
    {
        // main: init, M. feature: init, F (pushed). On it: a-feat, then A. Off main: a-plain, then P.
        Git(_hub, "branch", Feature, "main");
        OnBranch(Feature, "feature.txt", "F: the feature so far");
        Git(_hub, "push", "-q", "origin", Feature);
        Git(_hub, "branch", "a-feat", Feature);
        OnBranch("a-feat", "mine.txt", "A: this ticket");
        Git(_hub, "branch", "a-plain", "main");
        OnBranch("a-plain", "other.txt", "P: another ticket of the project");
        Commit(_hub, "fix.txt", "M: a fix on main");
        Git(_hub, "push", "-q", "origin", "main");
        await using (var db = new DashboardDbContext(_options))
        {
            db.Tickets.AddRange(
                new Ticket { ProjectKey = "alpha", Title = "Feature story", Branch = "a-feat", BaseBranch = Feature },
                new Ticket { ProjectKey = "alpha", Title = "Unrelated", Branch = "a-plain" });
            await db.SaveChangesAsync();
        }

        var feat = await _svc.BaseCheckAsync("alpha", "a-feat");
        Assert.True(feat.Ok, feat.Error);
        Assert.Equal($"origin/{Feature}", feat.BaseRef);
        Assert.Equal((0, 1), (feat.Behind, feat.Ahead));
        Assert.Equal(Feature, await _svc.TicketBaseBranchAsync("alpha", "a-feat"));

        var plain = await _svc.BaseCheckAsync("alpha", "a-plain");
        Assert.Equal("origin/main", plain.BaseRef);
        Assert.Equal((1, 1), (plain.Behind, plain.Ahead));
        Assert.Equal("main", await _svc.TicketBaseBranchAsync("alpha", "a-plain"));

        // The PR dialog's preflight compares against the same ref.
        var pre = await _svc.PreflightAsync("alpha", "a-feat");
        Assert.Equal(Feature, pre.BaseBranch);
    }

    // ---- fixed once the ticket has a branch --------------------------------------------------

    [Fact]
    public async Task A_tickets_base_can_change_until_it_has_a_branch_and_never_after()
    {
        await using var ctx = new DashboardDbContext(_options);
        var svc = new TicketService(ctx);
        var t = await svc.CreateAsync("alpha", "Story", null, null, null, TicketPhase.Backlog, baseBranch: Feature);
        Assert.Equal(Feature, t.BaseBranch);

        Assert.Equal(TicketService.BaseBranchChange.Unchanged, await svc.SetBaseBranchAsync(t.Id, Feature));
        Assert.Equal(TicketService.BaseBranchChange.Set, await svc.SetBaseBranchAsync(t.Id, ""));
        Assert.Null((await svc.GetAsync(t.Id))!.BaseBranch);
        Assert.Equal(TicketService.BaseBranchChange.Set, await svc.SetBaseBranchAsync(t.Id, Feature));

        await svc.UpdateAsync(t.Id, null, null, null, null, "jb/123", null);
        Assert.Equal(TicketService.BaseBranchChange.Started, await svc.SetBaseBranchAsync(t.Id, "feature/other"));
        Assert.Equal(TicketService.BaseBranchChange.Started, await svc.SetBaseBranchAsync(t.Id, null));
        Assert.Equal(Feature, (await svc.GetAsync(t.Id))!.BaseBranch);
    }

    [Fact]
    public async Task A_worktree_alone_also_fixes_the_base_and_a_review_ticket_has_none()
    {
        await using var ctx = new DashboardDbContext(_options);
        var svc = new TicketService(ctx);
        var t = await svc.CreateAsync("alpha", "Story", null, null, null, TicketPhase.Backlog);
        var row = await ctx.Tickets.FindAsync(t.Id);
        row!.WorktreePath = Path.Combine(_root, "wt");
        await ctx.SaveChangesAsync();
        Assert.Equal(TicketService.BaseBranchChange.Started, await svc.SetBaseBranchAsync(t.Id, Feature));

        await Assert.ThrowsAsync<ArgumentException>(() => svc.CreateAsync("alpha", null, null, null, null,
            TicketPhase.Backlog, track: TicketTrack.Review, reviewTarget: new ReviewTarget { SourceBranch = "theirs" },
            baseBranch: Feature));
    }

    [Fact]
    public async Task Moving_a_ticket_to_another_project_leaves_its_base_behind()
    {
        await using var ctx = new DashboardDbContext(_options);
        var svc = new TicketService(ctx);
        var t = await svc.CreateAsync("alpha", "Story", null, null, null, TicketPhase.Backlog, baseBranch: Feature);
        var moved = await svc.UpdateAsync(t.Id, null, null, null, null, null, "bravo");
        Assert.Null(moved!.BaseBranch);
    }

    // ---- the branch has to be where the ticket's git looks ------------------------------------

    [Fact]
    public async Task A_pull_request_project_refuses_a_base_that_is_not_on_origin_and_names_the_repo()
    {
        Git(_hub, "branch", "feature/local-only", "main");
        var no = await _svc.TicketBaseProblemAsync("alpha", "feature/local-only");
        Assert.NotNull(no);
        Assert.Contains("not on origin", no);
        Assert.Contains("pull request", no);

        Assert.Contains("not on origin", await _svc.TicketBaseProblemAsync("alpha", "feature/nowhere"));
    }

    [Fact]
    public async Task A_pull_request_project_takes_a_base_pushed_since_the_last_fetch()
    {
        Assert.NotNull(await _svc.TicketBaseProblemAsync("alpha", Feature, fetch: false));
        Git(_hub, "branch", Feature, "main");
        // Pushed from another clone: this hub's origin/ refs know nothing of it until it fetches.
        var other = Path.Combine(_root, "other");
        Git(_root, "clone", "-q", Path.Combine(_root, "origin.git"), other);
        Init(other, initRepo: false);
        Git(other, "push", "-q", "origin", $"main:refs/heads/{Feature}");

        Assert.NotNull(await _svc.TicketBaseProblemAsync("alpha", Feature, fetch: false));
        Assert.Null(await _svc.TicketBaseProblemAsync("alpha", Feature));
    }

    [Fact]
    public async Task A_project_that_merges_locally_takes_a_local_branch()
    {
        Git(_hub, "branch", "feature/local-only", "main");
        Assert.Null(await _svc.TicketBaseProblemAsync("bravo", "feature/local-only"));
        Assert.Contains("locally or on origin", await _svc.TicketBaseProblemAsync("bravo", "feature/nowhere"));
    }

    [Fact]
    public async Task Names_that_are_not_a_tickets_to_choose_are_refused()
    {
        Assert.Contains("not a branch name", await _svc.TicketBaseProblemAsync("alpha", "two words"));
        Assert.Contains("not a branch name", await _svc.TicketBaseProblemAsync("alpha", "-x"));
        Assert.Contains("not a branch name", await _svc.TicketBaseProblemAsync("alpha", "a..b"));
        Assert.Contains("slice", await _svc.TicketBaseProblemAsync("bravo", Slice));
    }

    [Fact]
    public async Task The_projects_own_base_is_stored_as_no_base_of_its_own()
    {
        Assert.Equal((null, null), await TicketEndpoints.OwnBaseAsync(_svc, "alpha", "main"));
        Assert.Equal((null, null), await TicketEndpoints.OwnBaseAsync(_svc, "alpha", "  "));
        Git(_hub, "branch", Feature, "main");
        Git(_hub, "push", "-q", "origin", Feature);
        Assert.Equal((Feature, null), await TicketEndpoints.OwnBaseAsync(_svc, "alpha", $" {Feature} "));
        var (none, why) = await TicketEndpoints.OwnBaseAsync(_svc, "alpha", "feature/nowhere");
        Assert.Null(none);
        Assert.NotNull(why);
    }

    [Fact]
    public async Task On_a_project_of_several_repos_every_writable_repo_needs_the_base_and_a_read_only_one_is_not_asked()
    {
        // A folder of three repos: api and web are written, docs only read. The feature is pushed in
        // api alone, so web is the one named.
        var multi = Path.Combine(_root, "multi");
        foreach (var dir in new[] { "alpha-api", "alpha-web", "alpha-docs" })
        {
            var bare = Path.Combine(_root, dir + ".git");
            Directory.CreateDirectory(bare);
            Git(bare, "init", "-q", "--bare", "-b", "main");
            var repo = Path.Combine(multi, dir);
            Directory.CreateDirectory(repo);
            Init(repo);
            Git(repo, "remote", "add", "origin", bare);
            Commit(repo, "README.md", "init");
            Git(repo, "push", "-q", "-u", "origin", "main");
        }
        WriteProject("charlie", $$"""
            { "projectPath": {{Json(multi)}}, "baseBranch": "main",
              "repos": [ { "dir": "alpha-api" }, { "dir": "alpha-web" }, { "dir": "alpha-docs", "readOnly": true } ] }
            """);
        var registry = new ProjectRegistry(new DashboardPaths(_data));
        var svc = new TicketGitService(registry, new GitWorktreeService(NullLogger<GitWorktreeService>.Instance),
            NullLogger<TicketGitService>.Instance, new DbBases(_options));

        Git(Path.Combine(multi, "alpha-api"), "push", "-q", "origin", $"main:refs/heads/{Feature}");
        var no = await svc.TicketBaseProblemAsync("charlie", Feature);
        Assert.Contains("alpha-web", no);

        Git(Path.Combine(multi, "alpha-web"), "push", "-q", "origin", $"main:refs/heads/{Feature}");
        Assert.Null(await svc.TicketBaseProblemAsync("charlie", Feature));
    }

    // ---- plumbing ----------------------------------------------------------------------------

    private sealed class DbBases(DbContextOptions<DashboardDbContext> options) : ITicketBases
    {
        public async Task<string?> BaseOfBranchAsync(ProjectConfig cfg, string branch, CancellationToken ct = default)
        {
            await using var db = new DashboardDbContext(options);
            return await SliceBranches.ForBranchAsync(db, cfg, branch, ct);
        }
    }

    private void OnBranch(string branch, string file, string subject)
    {
        var wt = Path.Combine(_root, "wt-" + Guid.NewGuid().ToString("N")[..6]);
        Git(_hub, "worktree", "add", "-q", wt, branch);
        Commit(wt, file, subject);
        Git(_hub, "worktree", "remove", "--force", wt);
    }

    private void WriteProject(string key, string json)
    {
        Directory.CreateDirectory(Path.Combine(_data, "projects", key));
        File.WriteAllText(Path.Combine(_data, "projects", key, "project.defaults.json"), json);
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

    private static void Git(string dir, params string[] args)
    {
        var (code, output) = Run(dir, args);
        Assert.True(code == 0, $"git {string.Join(' ', args)} failed in {dir}: {output}");
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
