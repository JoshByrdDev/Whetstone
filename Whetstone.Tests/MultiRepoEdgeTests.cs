using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Whetstone.Services.Tracker;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// Step 5 of docs/multi-repo-plan.md: what sits around a ticket's workspace on a project of several
/// repos - reaping it once the ticket is long Done, the analysis being told where it is reading,
/// and a QA copy refusing a repo that reaches outside its data root.
/// </summary>
public class MultiRepoEdgeTests : IDisposable
{
    private static readonly DateTime Now = new(2030, 1, 20, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Keep = TimeSpan.FromDays(7);
    private static readonly string[] Members = { "alpha-web", "alpha-api", "alpha-infra" };

    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<DashboardDbContext> _options;
    private readonly string _root;
    private readonly string _project;
    private readonly GitWorktreeService _svc = new(NullLogger<GitWorktreeService>.Instance);

    public MultiRepoEdgeTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _options = new DbContextOptionsBuilder<DashboardDbContext>().UseSqlite(_conn).Options;
        using var ctx = new DashboardDbContext(_options);
        ctx.Database.EnsureCreated();

        _root = Path.Combine(Path.GetTempPath(), "ccd-multi-edge-" + Guid.NewGuid().ToString("N")[..8]);
        _project = Path.Combine(_root, "Alpha");
        foreach (var dir in Members)
        {
            var hub = Path.Combine(_project, dir);
            Directory.CreateDirectory(hub);
            Git(hub, "init", "-q", "-b", "main");
            Git(hub, "config", "user.email", "test@example.com");
            Git(hub, "config", "user.name", "Test");
            File.WriteAllText(Path.Combine(hub, "README.md"), dir + "\n");
            Git(hub, "add", "-A");
            Git(hub, "commit", "-q", "-m", "init");
        }
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

    // ---- the reaper -------------------------------------------------------------------------

    [Fact]
    public async Task A_long_done_workspace_goes_repo_by_repo_and_then_the_folder()
    {
        var (id, workspace) = await Seed("jb/PROJ-1", TicketPhase.Done, daysAgo: 10);
        File.WriteAllText(Path.Combine(workspace, "CLAUDE.md"), "# the workspace\n");

        Assert.Equal(1, await Reap());

        Assert.False(Directory.Exists(workspace));
        Assert.Null(Recorded(id));
        foreach (var dir in Members.Take(2))
            Assert.True(Branches(Path.Combine(_project, dir)).Contains("jb/PROJ-1"), $"{dir} keeps its branch");
        Assert.All(Members, dir => Assert.DoesNotContain(Path.Combine(workspace, dir), Worktrees(Path.Combine(_project, dir))));
    }

    [Fact]
    public async Task One_repo_with_changes_keeps_the_whole_workspace()
    {
        var (id, workspace) = await Seed("jb/PROJ-2", TicketPhase.Done, daysAgo: 10);
        // Not the first in the list, so a reaper that removed as it went would already have taken alpha-web.
        File.WriteAllText(Path.Combine(workspace, "alpha-api", "left.txt"), "uncommitted\n");

        Assert.Equal(0, await Reap());

        Assert.All(Members, dir => Assert.True(Directory.Exists(Path.Combine(workspace, dir)), $"{dir} is still there"));
        Assert.NotNull(Recorded(id));
    }

    [Fact]
    public async Task A_recently_done_workspace_is_left_alone()
    {
        var (id, workspace) = await Seed("jb/PROJ-3", TicketPhase.Done, daysAgo: 1);
        Assert.Equal(0, await Reap());
        Assert.True(Directory.Exists(workspace));
        Assert.NotNull(Recorded(id));
    }

    [Fact]
    public async Task A_workspace_already_removed_by_hand_just_has_its_record_cleared()
    {
        var (id, workspace) = await Seed("jb/PROJ-4", TicketPhase.Done, daysAgo: 10);
        foreach (var dir in Members) Git(Path.Combine(_project, dir), "worktree", "remove", "--force", Path.Combine(workspace, dir));
        Directory.Delete(workspace, recursive: true);

        await Reap();
        Assert.Null(Recorded(id));
    }

    // ---- the analysis prompt ----------------------------------------------------------------

    private static Ticket AnalysisTicket() => new()
    {
        Id = 7, ProjectKey = "alpha", Title = "Default signers", Phase = TicketPhase.NeedsRefinement,
        Branch = "jb/PROJ-1", IssueKey = "1234",
    };

    private static string AnalysisPrompt(ProjectConfig cfg, string readDir) =>
        AnalysisRunner.BuildPrompt(AnalysisTicket(), cfg, AnalysisLens.Full, readDir, "Default signers", "the description",
            DescriptionSegmenter.Compose(new[] { new DescriptionSegmenter.Source(null, null, null) }), Array.Empty<IssueRef>());

    [Fact]
    public void A_project_of_one_is_told_exactly_what_it_always_was()
    {
        var prompt = AnalysisPrompt(new ProjectConfig { ProjectPath = @"C:\repos\alpha", DisplayName = "Alpha", BaseBranch = "main" }, @"C:\repos\alpha");

        Assert.Contains("- Repo root:        C:/repos/alpha\n- Branch:           jb/PROJ-1      Base: main\n- This repo's notes store", prompt);
        Assert.Contains("1. `git log --oneline main..HEAD` and `git diff main...HEAD --stat`, then the", prompt);
        Assert.Contains("3. This repo's `CLAUDE.md` and its notes store", prompt);
        Assert.DoesNotContain("several repos", prompt);
    }

    [Fact]
    public void A_project_of_several_is_told_it_reads_a_workspace_of_repos_each_with_its_base()
    {
        var cfg = new ProjectConfig
        {
            ProjectPath = @"C:\repos\Alpha", DisplayName = "Alpha", BaseBranch = "main",
            ConfiguredRepos = new[]
            {
                new RepoMember("alpha-web", "", Array.Empty<string>(), Array.Empty<string>(), false),
                new RepoMember("alpha-api", "develop", Array.Empty<string>(), Array.Empty<string>(), false),
                new RepoMember("alpha-infra", "", Array.Empty<string>(), Array.Empty<string>(), true),
            },
        };
        var prompt = AnalysisPrompt(cfg, @"C:\wt\jb-PROJ-1");

        Assert.Contains("- Repo root:        C:/repos/Alpha - a folder of several repos, not itself a repo", prompt);
        Assert.Contains("Base: each repo's own, below\n- Repos, each its own git repo:\n  - `alpha-web` - base `main`\n  - `alpha-api` - base `develop`\n  - `alpha-infra` - read-only", prompt);
        Assert.Contains("its repo's folder first (`alpha-web/src/...`)", prompt);
        Assert.Contains("1. In each repo the ticket changed, `git -C <repo> log --oneline <its base>..HEAD`", prompt);
        Assert.Contains("3. Each repo's own `CLAUDE.md`, the workspace's at its root if there is one, and its notes store", prompt);
        Assert.DoesNotContain("\r", prompt);
    }

    // ---- a QA copy --------------------------------------------------------------------------

    [Fact]
    public void A_qa_copy_takes_a_project_of_several_wholly_inside_it_and_refuses_one_reaching_out()
    {
        var data = Path.Combine(_root, "qa-data");
        var inside = new ProjectConfig
        {
            Name = "charlie", ProjectPath = Path.Combine(data, "repos", "charlie"),
            WorktreeRoot = Path.Combine(data, "worktrees", "charlie"), MemoryDir = Path.Combine(data, "notes"),
            ConfiguredRepos = new[] { new RepoMember("charlie-web", "", Array.Empty<string>(), Array.Empty<string>(), false) },
        };
        Assert.Null(QaIsolation.Escapes(inside, data));

        var climbing = new ProjectConfig
        {
            Name = inside.Name, ProjectPath = inside.ProjectPath, WorktreeRoot = inside.WorktreeRoot, MemoryDir = inside.MemoryDir,
            ConfiguredRepos = new[]
            {
                new RepoMember("charlie-web", "", Array.Empty<string>(), Array.Empty<string>(), false),
                new RepoMember(@"..\..\..\..\elsewhere", "", Array.Empty<string>(), Array.Empty<string>(), false),
            },
        };
        Assert.Contains(@"repo '..\..\..\..\elsewhere'", QaIsolation.Escapes(climbing, data));

        var rooted = new ProjectConfig
        {
            Name = inside.Name, ProjectPath = inside.ProjectPath, WorktreeRoot = inside.WorktreeRoot, MemoryDir = inside.MemoryDir,
            ConfiguredRepos = new[] { new RepoMember(@"D:\real-repo", "", Array.Empty<string>(), Array.Empty<string>(), false) },
        };
        Assert.NotNull(QaIsolation.Escapes(rooted, data));
    }

    // ---- fixture ----------------------------------------------------------------------------

    /// <summary>A Done ticket with a workspace laid out as data/worker-repos.ps1 lays it out.</summary>
    private async Task<(int Id, string Workspace)> Seed(string branch, TicketPhase phase, double daysAgo)
    {
        var workspace = Path.Combine(_root, "wt", branch.Replace('/', '-'));
        Directory.CreateDirectory(workspace);
        Git(Path.Combine(_project, "alpha-web"), "worktree", "add", "-q", "-b", branch, Path.Combine(workspace, "alpha-web"), "main");
        Git(Path.Combine(_project, "alpha-api"), "worktree", "add", "-q", "-b", branch, Path.Combine(workspace, "alpha-api"), "main");
        Git(Path.Combine(_project, "alpha-infra"), "worktree", "add", "-q", "--detach", Path.Combine(workspace, "alpha-infra"), "main");

        using var ctx = new DashboardDbContext(_options);
        var at = Now - TimeSpan.FromDays(daysAgo);
        var t = new Ticket
        {
            ProjectKey = "alpha", Title = branch, Phase = phase, Branch = branch,
            WorktreePath = workspace.Replace('\\', '/'), UpdatedAt = at,
        };
        ctx.Tickets.Add(t);
        await ctx.SaveChangesAsync();
        ctx.PhaseTransitions.Add(new PhaseTransition { TicketId = t.Id, ProjectKey = "alpha", From = TicketPhase.NeedsReview, To = phase, At = at });
        await ctx.SaveChangesAsync();
        return (t.Id, workspace);
    }

    private async Task<int> Reap()
    {
        using var ctx = new DashboardDbContext(_options);
        return await WorktreeReaper.ReapAsync(ctx, _svc, _ => _project, Keep, Now, NullLogger.Instance,
            membersFor: _ => Members);
    }

    private string? Recorded(int id)
    {
        using var ctx = new DashboardDbContext(_options);
        return ctx.Tickets.Single(t => t.Id == id).WorktreePath;
    }

    private static string Branches(string repo) => Run(repo, "branch", "--format=%(refname:short)");

    private static string[] Worktrees(string repo) =>
        Run(repo, "worktree", "list", "--porcelain").Split('\n')
            .Where(l => l.StartsWith("worktree "))
            .Select(l => Path.GetFullPath(l["worktree ".Length..].Trim()))
            .ToArray();

    private static void Git(string dir, params string[] args) => Run(dir, args);

    private static string Run(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed in {dir}: {stderr}");
        return stdout;
    }
}
