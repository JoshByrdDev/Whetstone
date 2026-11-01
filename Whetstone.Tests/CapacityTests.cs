using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// The capacity meter: the claim's trace says why each waiting ticket was passed over, and the
/// classifier turns a place's state plus its last claim into the one reason its free berths were
/// free. The order of the classifier's checks is the judgment, so most of these pin that order.
/// </summary>
public class CapacityTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<DashboardDbContext> _options;
    private static readonly DateTime Now = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    public CapacityTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _options = new DbContextOptionsBuilder<DashboardDbContext>().UseSqlite(_conn).Options;
        using var ctx = new DashboardDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _conn.Dispose();

    private static PlaceInput Board(int slots = 3, int busy = 0, bool running = true, string state = "idle",
        string? sleep = null, bool paused = false, ClaimSeen? claim = null) =>
        new(CapacityPlaces.Board, slots, busy, running, state, sleep, paused, null, claim);

    private static ClaimSeen Missed(Action<ClaimTrace> fill, int minutesAgo = 1)
    {
        var t = new ClaimTrace();
        fill(t);
        return new ClaimSeen(Now.AddMinutes(-minutesAgo), false, t);
    }

    // ---- The classifier ----

    [Fact]
    public void A_full_place_has_no_reason()
    {
        Assert.Null(CapacityClassifier.Classify(Board(busy: 3), Now).Reason);
    }

    [Fact]
    public void Off_duty_outranks_anything_a_claim_said()
    {
        var claim = Missed(t => t.Skip(ClaimSkip.Resource, "alpha:app-stack"));
        Assert.Equal(CapacityReasons.Stopped, CapacityClassifier.Classify(Board(running: false, claim: claim), Now).Reason);
        Assert.Equal(CapacityReasons.OffHours, CapacityClassifier.Classify(Board(state: "off-hours", claim: claim), Now).Reason);
    }

    [Fact]
    public void A_usage_limit_sleep_is_told_from_an_idle_one_by_its_text()
    {
        var claim = Missed(_ => { });
        Assert.Equal(CapacityReasons.UsageLimit,
            CapacityClassifier.Classify(Board(state: "sleeping", sleep: "Usage limit until 2026-11-01 20:02", claim: claim), Now).Reason);
        Assert.Equal(CapacityReasons.NothingReady,
            CapacityClassifier.Classify(Board(state: "sleeping", sleep: "Sleeping 12m", claim: claim), Now).Reason);
    }

    [Fact]
    public void A_host_that_cannot_take_work_says_why_before_any_claim_does()
    {
        var host = new PlaceInput("bravo-host", 6, 0, true, "idle", null, false, CapacityReasons.HostBehind, Missed(_ => { }));
        Assert.Equal(CapacityReasons.HostBehind, CapacityClassifier.Classify(host, Now).Reason);
    }

    [Fact]
    public void Tickets_already_being_worked_are_not_waiting()
    {
        var claim = Missed(t => { t.Workable = 2; t.Skip(ClaimSkip.Running); t.Skip(ClaimSkip.Running); });
        Assert.Equal(CapacityReasons.NothingReady, CapacityClassifier.Classify(Board(claim: claim), Now).Reason);
    }

    [Fact]
    public void The_reason_that_kept_most_tickets_waiting_names_the_sample()
    {
        var claim = Missed(t =>
        {
            t.Skip(ClaimSkip.Resource, "alpha:app-stack");
            t.Skip(ClaimSkip.Dependency);
            t.Skip(ClaimSkip.Dependency);
        });
        var v = CapacityClassifier.Classify(Board(claim: claim), Now);
        Assert.Equal(CapacityReasons.Dependency, v.Reason);
        Assert.Equal(2, v.Detail!["Dependency"]);
        Assert.Equal(1, v.Detail!["alpha:app-stack"]);
    }

    [Fact]
    public void A_tie_goes_to_the_busy_resource()
    {
        var claim = Missed(t => { t.Skip(ClaimSkip.Dependency); t.Skip(ClaimSkip.Resource, "alpha:app-stack"); });
        Assert.Equal(CapacityReasons.Resource, CapacityClassifier.Classify(Board(claim: claim), Now).Reason);
    }

    [Fact]
    public void A_phase_skipped_on_the_board_while_a_provider_is_paused_is_the_usage_limit()
    {
        var claim = Missed(t => t.Skip(ClaimSkip.Skill, "qa-test"));
        Assert.Equal(CapacityReasons.UsageLimit, CapacityClassifier.Classify(Board(paused: true, claim: claim), Now).Reason);

        var host = new PlaceInput("bravo-host", 6, 0, true, "idle", null, true, null, claim);
        Assert.Equal(CapacityReasons.NotHere, CapacityClassifier.Classify(host, Now).Reason);
    }

    [Fact]
    public void A_recent_successful_claim_is_still_filling_and_an_old_one_says_nothing()
    {
        Assert.Equal(CapacityReasons.Filling,
            CapacityClassifier.Classify(Board(claim: new ClaimSeen(Now.AddSeconds(-30), true, null)), Now).Reason);
        Assert.Equal(CapacityReasons.Unknown,
            CapacityClassifier.Classify(Board(claim: new ClaimSeen(Now.AddMinutes(-10), true, null)), Now).Reason);
        Assert.Equal(CapacityReasons.Unknown,
            CapacityClassifier.Classify(Board(claim: Missed(_ => { }, minutesAgo: 120)), Now).Reason);
        Assert.Equal(CapacityReasons.Unknown, CapacityClassifier.Classify(Board(), Now).Reason);
    }

    [Fact]
    public void Nothing_this_machine_may_run_is_its_own_reason()
    {
        var claim = new ClaimSeen(Now, false, new ClaimTrace { NothingHere = true });
        Assert.Equal(CapacityReasons.NotHere, CapacityClassifier.Classify(Board(claim: claim), Now).Reason);
    }

    // ---- The claim's trace ----

    private async Task<Ticket> Seed(DashboardDbContext ctx, TicketPhase phase, int minutesAgo)
    {
        var t = new Ticket
        {
            ProjectKey = "alpha", Title = $"alpha:{phase}:{minutesAgo}", Phase = phase,
            CreatedAt = Now.AddMinutes(-minutesAgo), UpdatedAt = Now.AddMinutes(-minutesAgo),
        };
        ctx.Tickets.Add(t);
        await ctx.SaveChangesAsync();
        return t;
    }

    [Fact]
    public async Task The_trace_says_why_each_waiting_ticket_was_passed_over()
    {
        await using var ctx = new DashboardDbContext(_options);
        var qa = await Seed(ctx, TicketPhase.NeedsQa, 30);
        var blocker = await Seed(ctx, TicketPhase.NeedsReview, 40);
        var built = await Seed(ctx, TicketPhase.NeedsImplementation, 20);
        ctx.TicketDependencies.Add(new TicketDependency
        {
            TicketId = built.Id, BlockerTicketId = blocker.Id, Origin = DependencyOrigin.Local, CreatedAt = Now,
        });
        await ctx.SaveChangesAsync();

        var leases = new ResourceLeaseService(ctx);
        Assert.NotNull(await leases.TryAcquireAllAsync(new[] { "alpha:app-stack" }, 999, "human", TimeSpan.FromHours(1)));

        var svc = new TicketService(ctx, dependencies: new TicketDependencyService(ctx), leases: leases);
        var trace = new ClaimTrace();
        var result = await svc.ClaimNextAsync(null, (_, _) => true,
            resourcesFor: (_, skill) => skill == "qa-test" ? new[] { "alpha:app-stack" } : Array.Empty<string>(),
            trace: trace);

        Assert.Null(result);
        Assert.Equal(2, trace.Workable);
        Assert.Equal(1, trace.Skips[ClaimSkip.Resource]);
        Assert.Equal(1, trace.Skips[ClaimSkip.Dependency]);
        Assert.Equal(1, trace.Held["alpha:app-stack"]);

        // Passing over is all it did: the QA ticket is where it was, and holds nothing.
        Assert.Equal(TicketPhase.NeedsQa, (await ctx.Tickets.AsNoTracking().FirstAsync(t => t.Id == qa.Id)).Phase);
        Assert.Null(await leases.HolderOfAsync(ResourceLeaseService.TicketKey(qa.Id)));
    }

    // ---- The summary ----

    [Fact]
    public void The_summary_counts_free_slot_minutes_by_reason()
    {
        var samples = new List<CapacitySample>
        {
            new() { At = Now, Place = CapacityPlaces.Board, Slots = 3, Busy = 1, Reason = CapacityReasons.Resource },
            new() { At = Now.AddMinutes(1), Place = CapacityPlaces.Board, Slots = 3, Busy = 3 },
            new() { At = Now, Place = "bravo-host", Slots = 6, Busy = 0, Reason = CapacityReasons.HostBehind },
        };
        var r = CapacitySummary.Build(samples, Now.AddDays(-1), Now, at => DateOnly.FromDateTime(at));

        Assert.Equal(12, r.Total.SlotMinutes);
        Assert.Equal(4, r.Total.Busy);
        Assert.Equal(2, r.Total.Idle[CapacityReasons.Resource]);
        Assert.Equal(6, r.Total.Idle[CapacityReasons.HostBehind]);
        Assert.Equal(CapacityPlaces.Board, r.Places[0].Place);
        Assert.Single(r.Days);
    }
}
