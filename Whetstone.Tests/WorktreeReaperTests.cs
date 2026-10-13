using System.Diagnostics;
using System.Text.Json;
using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// Taking back the worktree of a ticket that has been Done long enough, against a real git repo.
///
/// As with removal itself, most of what is specified here is what must be left alone: a ticket
/// finished recently, one still in flight, one whose worktree holds something uncommitted, and a
/// worktree that a second, unfinished ticket also names.
/// </summary>
public class WorktreeReaperTests : IDisposable
{
    private static readonly DateTime Now = new(2030, 1, 20, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Keep = TimeSpan.FromDays(7);

    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<DashboardDbContext> _options;
    private readonly string _root;
    private readonly string _repo;
    private readonly GitWorktreeService _svc = new(NullLogger<GitWorktreeService>.Instance);

    public WorktreeReaperTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _options = new DbContextOptionsBuilder<DashboardDbContext>().UseSqlite(_conn).Options;
        using var ctx = new DashboardDbContext(_options);
        ctx.Database.EnsureCreated();

        _root = Path.Combine(Path.GetTempPath(), "ccd-reap-" + Guid.NewGuid().ToString("N")[..8]);
        _repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_repo);
        Git(_repo, "init", "-b", "main");
        Git(_repo, "config", "user.email", "test@example.com");
        Git(_repo, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(_repo, ".gitignore"), "node_modules/\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "init");
    }

    public void Dispose()
    {
        _conn.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private DashboardDbContext NewCtx() => new(_options);

    /// <summary>A ticket with a real worktree, which reached <paramref name="phase"/> that many days ago.</summary>
    private async Task<(int Id, string Path)> Seed(string branch, TicketPhase phase, double daysAgo, string? sharePath = null)
    {
        var path = sharePath;
        if (path is null)
        {
            path = Path.Combine(_root, "wt", branch);
            Git(_repo, "worktree", "add", "-b", branch, path, "main");
        }

        using var ctx = NewCtx();
        var at = Now - TimeSpan.FromDays(daysAgo);
        var t = new Ticket
        {
            ProjectKey = "alpha",
            Title = branch,
            Phase = phase,
            Branch = branch,
            // What the heartbeat records: forward slashes, as git prints them.
            WorktreePath = path.Replace('\\', '/'),
            UpdatedAt = at,
        };
        ctx.Tickets.Add(t);
        await ctx.SaveChangesAsync();
        ctx.PhaseTransitions.Add(new PhaseTransition
        {
            TicketId = t.Id, ProjectKey = "alpha", From = TicketPhase.NeedsReview, To = phase, At = at,
        });
        await ctx.SaveChangesAsync();
        return (t.Id, path);
    }

    private async Task<int> Reap()
    {
        using var ctx = NewCtx();
        return await WorktreeReaper.ReapAsync(ctx, _svc, _ => _repo, Keep, Now, NullLogger.Instance);
    }

    private string? Recorded(int id)
    {
        using var ctx = NewCtx();
        return ctx.Tickets.Single(t => t.Id == id).WorktreePath;
    }

    [Fact]
    public async Task A_ticket_done_longer_than_the_retention_loses_its_worktree_and_keeps_its_branch()
    {
        var (id, path) = await Seed("ticket-1", TicketPhase.Done, daysAgo: 8);
        // What actually fills one: ignored, and so not something git counts as a change.
        Directory.CreateDirectory(Path.Combine(path, "node_modules", "pkg"));
        File.WriteAllText(Path.Combine(path, "node_modules", "pkg", "index.js"), "module.exports = 1\n");

        Assert.Equal(1, await Reap());

        Assert.False(Directory.Exists(path));
        Assert.Contains("ticket-1", Git(_repo, "branch", "--list", "ticket-1"));
        Assert.Null(Recorded(id));
    }

    [Fact]
    public async Task A_ticket_done_recently_keeps_its_worktree()
    {
        var (id, path) = await Seed("ticket-2", TicketPhase.Done, daysAgo: 6);

        Assert.Equal(0, await Reap());

        Assert.True(Directory.Exists(path));
        Assert.NotNull(Recorded(id));
    }

    [Fact]
    public async Task A_ticket_still_in_flight_keeps_its_worktree_however_old()
    {
        var (_, path) = await Seed("ticket-3", TicketPhase.NeedsReview, daysAgo: 30);

        Assert.Equal(0, await Reap());

        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public async Task The_clock_runs_from_the_last_time_it_reached_done()
    {
        // Finished a month ago, reopened, finished again yesterday.
        var (id, path) = await Seed("ticket-4", TicketPhase.Done, daysAgo: 30);
        using (var ctx = NewCtx())
        {
            ctx.PhaseTransitions.Add(new PhaseTransition
            {
                TicketId = id, ProjectKey = "alpha", From = TicketPhase.NeedsReview, To = TicketPhase.Done,
                At = Now - TimeSpan.FromDays(1),
            });
            await ctx.SaveChangesAsync();
        }

        Assert.Equal(0, await Reap());

        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public async Task Uncommitted_work_in_an_old_worktree_is_left_where_it_is()
    {
        var (id, path) = await Seed("ticket-5", TicketPhase.Done, daysAgo: 30);
        File.WriteAllText(Path.Combine(path, "notes.txt"), "a final adjustment, not yet committed\n");

        Assert.Equal(0, await Reap());

        Assert.True(File.Exists(Path.Combine(path, "notes.txt")));
        Assert.NotNull(Recorded(id));
    }

    [Fact]
    public async Task A_worktree_an_unfinished_ticket_also_names_is_kept()
    {
        var (_, path) = await Seed("ticket-6", TicketPhase.Done, daysAgo: 30);
        await Seed("ticket-7", TicketPhase.QaTesting, daysAgo: 1, sharePath: path);

        Assert.Equal(0, await Reap());

        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public async Task A_worktree_already_removed_by_hand_just_has_its_record_cleared()
    {
        var (id, path) = await Seed("ticket-8", TicketPhase.Done, daysAgo: 30);
        Git(_repo, "worktree", "remove", path);

        Assert.Equal(0, await Reap());

        Assert.Null(Recorded(id));
    }

    [Theory]
    [InlineData("{}", 2)]
    [InlineData("{\"worktreeRetentionDays\": 30}", 30)]
    [InlineData("{\"worktreeRetentionDays\": 0}", 0)]       // off
    [InlineData("{\"worktreeRetentionDays\": \"soon\"}", 2)]
    public void Retention_is_read_from_the_global_config(string json, int expected)
        => Assert.Equal(expected,
            WorktreeReaper.RetentionDays(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!));

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
