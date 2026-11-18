using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// The board's answer to a worker host asking which of its worktrees may go (<see cref="HostWorktrees"/>).
/// The runner removes only what this says may go, so the cases that matter most are the ones it
/// must keep: a ticket still in flight, one finished too recently, a review another pass still reads.
/// </summary>
public class WorkerHostWorktreeTests : IDisposable
{
    private static readonly DateTime Now = new(2030, 1, 20, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Keep = TimeSpan.FromDays(2);

    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<DashboardDbContext> _options;

    public WorkerHostWorktreeTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _options = new DbContextOptionsBuilder<DashboardDbContext>().UseSqlite(_conn).Options;
        using var ctx = new DashboardDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _conn.Dispose();

    private async Task<int> Seed(TicketPhase phase, double daysAgo, string? branch = null, string? review = null, string project = "alpha")
    {
        using var ctx = new DashboardDbContext(_options);
        var at = Now - TimeSpan.FromDays(daysAgo);
        var t = new Ticket { ProjectKey = project, Title = branch ?? review ?? "t", Phase = phase, Branch = branch, UpdatedAt = at };
        if (review is not null) t.ReviewTarget = new ReviewTarget { SourceBranch = review };
        ctx.Tickets.Add(t);
        await ctx.SaveChangesAsync();
        ctx.PhaseTransitions.Add(new PhaseTransition { TicketId = t.Id, ProjectKey = project, From = TicketPhase.NeedsReview, To = phase, At = at });
        await ctx.SaveChangesAsync();
        return t.Id;
    }

    private async Task<HostWorktreeVerdict> Ask(HostWorktreeItem item, TimeSpan? keep = null, string project = "alpha")
    {
        using var ctx = new DashboardDbContext(_options);
        return Assert.Single(await HostWorktrees.DecideAsync(ctx, project, new[] { item }, keep ?? Keep, Now));
    }

    private static HostWorktreeItem Branch(string b) => new(BranchNaming.WorktreeDirName(b), "branch", Branch: b);

    [Fact]
    public async Task A_ticket_done_past_the_retention_may_go()
    {
        await Seed(TicketPhase.Done, 3, branch: "jb/PROJ-1");
        var v = await Ask(Branch("jb/PROJ-1"));
        Assert.True(v.Reap);
        Assert.Equal("jb-PROJ-1", v.Name);
    }

    [Fact]
    public async Task A_ticket_done_recently_is_kept()
    {
        await Seed(TicketPhase.Done, 1, branch: "tk-1");
        Assert.False((await Ask(Branch("tk-1"))).Reap);
    }

    [Fact]
    public async Task A_ticket_still_in_flight_is_kept_however_old()
    {
        await Seed(TicketPhase.NeedsQa, 30, branch: "tk-2");
        var v = await Ask(Branch("tk-2"));
        Assert.False(v.Reap);
        Assert.Contains("NeedsQa", v.Why);
    }

    [Fact]
    public async Task Done_is_measured_from_the_last_arrival_not_the_first()
    {
        var id = await Seed(TicketPhase.Done, 10, branch: "tk-3");
        using (var ctx = new DashboardDbContext(_options))
        {
            // Reopened and finished again yesterday.
            ctx.PhaseTransitions.Add(new PhaseTransition { TicketId = id, ProjectKey = "alpha", From = TicketPhase.NeedsReview, To = TicketPhase.Done, At = Now.AddDays(-1) });
            await ctx.SaveChangesAsync();
        }
        Assert.False((await Ask(Branch("tk-3"))).Reap);
    }

    [Fact]
    public async Task A_branch_no_ticket_names_may_go()
    {
        var v = await Ask(Branch("renamed-away"));
        Assert.True(v.Reap);
        Assert.Contains("no ticket", v.Why);
    }

    [Fact]
    public async Task A_branch_is_looked_for_in_the_project_asked_about_only()
    {
        await Seed(TicketPhase.Implementing, 0, branch: "tk-4", project: "bravo");
        Assert.True((await Ask(Branch("tk-4"))).Reap);
        Assert.False((await Ask(Branch("tk-4"), project: "bravo")).Reap);
    }

    [Fact]
    public async Task A_retention_of_zero_keeps_everything()
    {
        await Seed(TicketPhase.Done, 300, branch: "tk-5");
        Assert.False((await Ask(Branch("tk-5"), TimeSpan.Zero)).Reap);
        Assert.False((await Ask(Branch("nobody"), TimeSpan.Zero)).Reap);
    }

    [Fact]
    public async Task A_review_worktree_stays_while_any_review_of_its_branch_is_open()
    {
        await Seed(TicketPhase.Done, 10, review: "feature/x");
        await Seed(TicketPhase.NeedsReview, 0, review: "feature/x");
        var item = new HostWorktreeItem("review-feature-x", "review");
        Assert.False((await Ask(item)).Reap);
    }

    [Fact]
    public async Task A_review_worktree_goes_once_every_review_of_it_is_done_past_the_retention()
    {
        await Seed(TicketPhase.Done, 10, review: "feature/x");
        await Seed(TicketPhase.Done, 5, review: "feature/x");
        Assert.True((await Ask(new HostWorktreeItem("review-feature-x", "review"))).Reap);
    }

    [Fact]
    public async Task A_walkthrough_checkout_goes_when_its_ticket_has_not_moved_for_the_retention()
    {
        var idle = await Seed(TicketPhase.AwaitingWork, 5);
        var busy = await Seed(TicketPhase.NeedsScouting, 0);
        var done = await Seed(TicketPhase.Done, 1);
        Assert.True((await Ask(new HostWorktreeItem($"walkthrough-ticket-{idle}", "base", TicketId: idle))).Reap);
        Assert.False((await Ask(new HostWorktreeItem($"walkthrough-ticket-{busy}", "base", TicketId: busy))).Reap);
        Assert.False((await Ask(new HostWorktreeItem($"walkthrough-ticket-{done}", "base", TicketId: done))).Reap);
        Assert.True((await Ask(new HostWorktreeItem("walkthrough-ticket-999", "base", TicketId: 999))).Reap);
    }

    [Fact]
    public async Task Anything_it_cannot_place_is_kept()
    {
        Assert.False((await Ask(new HostWorktreeItem("odd", "branch"))).Reap);   // no branch named
        Assert.False((await Ask(new HostWorktreeItem("odd", "mystery"))).Reap);
        Assert.False((await Ask(new HostWorktreeItem("walkthrough-ticket-x", "base"))).Reap);
    }

    private async Task<HostWorktreeVerdict> AskPressed(HostWorktreeItem item)
    {
        using var ctx = new DashboardDbContext(_options);
        return Assert.Single(await HostWorktrees.DecideAsync(ctx, "alpha", new[] { item }, Keep, Now,
            pressed: "only 5 GB free on /, under the 40 GB reaping line (worktreeReapBelowGb)"));
    }

    [Fact]
    public async Task Under_the_reaping_line_a_ticket_done_recently_may_go_and_says_why()
    {
        await Seed(TicketPhase.Done, 0.1, branch: "tk-press");
        var v = await AskPressed(Branch("tk-press"));
        Assert.True(v.Reap);
        Assert.Contains("reaping line", v.Why);
    }

    [Fact]
    public async Task Under_the_reaping_line_a_ticket_still_in_flight_is_still_kept()
    {
        await Seed(TicketPhase.Implementing, 0.1, branch: "tk-busy");
        Assert.False((await AskPressed(Branch("tk-busy"))).Reap);
    }
}

/// <summary>The low-disk guard (<see cref="DiskFloor"/>): what holds a machine, and what it says.</summary>
public class WorkerHostDiskTests
{
    private const string Board = "1111111111111111111111111111111111111111";
    private readonly DateTime _now = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
    private const long Gb = 1024L * 1024 * 1024;

    [Fact]
    public void The_floor_defaults_to_ten_and_reads_the_global_setting()
    {
        Assert.Equal(10, DiskFloor.FloorGb(new Dictionary<string, JsonElement>()));
        var g = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{ "workerDiskFloorGb": 25 }""")!;
        Assert.Equal(25, DiskFloor.FloorGb(g));
    }

    [Fact]
    public void A_drive_under_the_floor_says_which_and_how_much()
    {
        var why = DiskFloor.Low(new[] { new HostDisk("D:\\", 50 * Gb), new HostDisk("C:\\", 3 * Gb + Gb / 5) }, 10);
        Assert.NotNull(why);
        Assert.Contains("3.2 GB free on C:\\", why);
        Assert.Contains("10 GB floor", why);
        Assert.Null(DiskFloor.Low(new[] { new HostDisk("C:\\", 11 * Gb) }, 10));
        Assert.Null(DiskFloor.Low(new[] { new HostDisk("C:\\", 1 * Gb) }, 0));      // off
        Assert.Null(DiskFloor.Low(null, 10));                                       // never said
    }

    [Fact]
    public void The_reaping_line_defaults_to_forty_and_says_which_drive_is_under_it()
    {
        Assert.Equal(40, DiskFloor.ReapBelowGb(new Dictionary<string, JsonElement>()));
        Assert.Equal(0, DiskFloor.ReapBelowGb(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{ "worktreeReapBelowGb": 0 }""")!));
        var why = DiskFloor.Pressed(new[] { new HostDisk("/", 12 * Gb) }, 40);
        Assert.Contains("12 GB free on /", why);
        Assert.Contains("worktreeReapBelowGb", why);
        Assert.Null(DiskFloor.Pressed(new[] { new HostDisk("/", 41 * Gb) }, 40));
        Assert.Null(DiskFloor.Pressed(new[] { new HostDisk("/", 1 * Gb) }, 0));    // off
        Assert.Null(DiskFloor.Pressed(null, 40));                                  // never said
    }

    [Fact]
    public void Measuring_this_machine_finds_its_drive_once()
    {
        var tmp = Path.GetTempPath();
        var disks = DiskFloor.Measure(new[] { tmp, Path.Combine(tmp, "not-made-yet", "deeper"), "" });
        var d = Assert.Single(disks);
        Assert.True(d.FreeBytes > 0);
    }

    private WorkerHostService Service(double floor = 10)
    {
        var cfg = WorkerHostConfig.Read(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            """{ "workerHosts": [{ "name": "bravo-host", "slots": 2, "projects": ["alpha"] }] }""")!);
        return new WorkerHostService(() => cfg, () => Board, () => _now, diskFloorGb: () => floor);
    }

    [Fact]
    public async Task A_host_under_the_floor_is_given_nothing_new_and_says_why()
    {
        var svc = Service();
        await svc.CheckInAsync("bravo-host", Board, Array.Empty<HostRun>(), null, TimeSpan.Zero, disks: new[] { new HostDisk("C:\\", 2 * Gb, 500 * Gb) });
        var h = Assert.Single(svc.Snapshot());
        Assert.False(h.Dispatchable);
        Assert.Contains("2 GB free on C:\\", h.DiskLow);
        Assert.Equal(2 * Gb, Assert.Single(h.Disks!).FreeBytes);
    }

    [Fact]
    public async Task A_check_in_that_says_nothing_of_its_drives_keeps_what_it_last_said()
    {
        var svc = Service();
        await svc.CheckInAsync("bravo-host", Board, Array.Empty<HostRun>(), null, TimeSpan.Zero, disks: new[] { new HostDisk("C:\\", 2 * Gb) });
        await svc.CheckInAsync("bravo-host", Board, Array.Empty<HostRun>(), null, TimeSpan.Zero);
        Assert.False(Assert.Single(svc.Snapshot()).Dispatchable);

        await svc.CheckInAsync("bravo-host", Board, Array.Empty<HostRun>(), null, TimeSpan.Zero, disks: new[] { new HostDisk("C:\\", 40 * Gb) });
        var h = Assert.Single(svc.Snapshot());
        Assert.True(h.Dispatchable);
        Assert.Null(h.DiskLow);
    }

    [Fact]
    public async Task A_runner_that_reports_no_drives_is_not_held()
    {
        var svc = Service();
        await svc.CheckInAsync("bravo-host", Board, Array.Empty<HostRun>(), null, TimeSpan.Zero);
        Assert.True(Assert.Single(svc.Snapshot()).Dispatchable);
    }

    [Fact]
    public async Task A_floor_of_zero_holds_nobody()
    {
        var svc = Service(floor: 0);
        await svc.CheckInAsync("bravo-host", Board, Array.Empty<HostRun>(), null, TimeSpan.Zero, disks: new[] { new HostDisk("C:\\", 1) });
        Assert.True(Assert.Single(svc.Snapshot()).Dispatchable);
    }
}

/// <summary>
/// A start the orchestrator gave up on is not handed to a runner that checks in late
/// (<see cref="Whetstone.Api.WorkerHostEndpoints.DropReleasedStarts"/>).
/// </summary>
public class WorkerHostStaleStartTests
{
    private const string Board = "1111111111111111111111111111111111111111";

    [Fact]
    public void A_start_whose_claim_was_released_is_dropped_and_the_rest_kept()
    {
        var orders = new[]
        {
            new HostOrder("start", 11, 801, null),
            new HostOrder("start", 12, 802, null),
            new HostOrder("kill", 13, 700, null),
        };
        var kept = Whetstone.Api.WorkerHostEndpoints.DropReleasedStarts(orders, new HashSet<int> { 802 }, out var dropped);
        Assert.Equal(new[] { 801 }, dropped);
        Assert.Equal(new[] { 802, 700 }, kept.Select(o => o.RunId));
    }

    [Fact]
    public void Nothing_released_leaves_the_orders_as_they_are()
    {
        var orders = new[] { new HostOrder("start", 11, 801, null) };
        Assert.Same(orders, Whetstone.Api.WorkerHostEndpoints.DropReleasedStarts(orders, new HashSet<int> { 801 }, out var dropped));
        Assert.Empty(dropped);
    }

    [Fact]
    public async Task A_dropped_start_no_longer_counts_as_out()
    {
        var now = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
        var cfg = WorkerHostConfig.Read(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            """{ "workerHosts": [{ "name": "bravo-host", "slots": 2 }] }""")!);
        var svc = new WorkerHostService(() => cfg, () => Board, () => now);
        svc.Enqueue("bravo-host", new HostOrder("start", 11, 801, null));
        var taken = await svc.CheckInAsync("bravo-host", Board, Array.Empty<HostRun>(), null, TimeSpan.Zero);
        Assert.Single(taken!);
        Assert.Equal(1, Assert.Single(svc.Snapshot()).Out);
        svc.ForgetHanded("bravo-host", new[] { 801 });
        Assert.Equal(0, Assert.Single(svc.Snapshot()).Out);
    }
}
