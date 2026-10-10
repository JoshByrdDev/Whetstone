using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// Exercises the heartbeat's project-agnostic claim against a real (in-memory SQLite) DB:
/// highest-priority-band-first then most-progressed-first ordering, resume-in-place,
/// oldest-updated tiebreak, the missing-skill → block-and-skip guard, project and priority-band
/// filtering, and the empty case.
/// </summary>
public class TicketServiceClaimTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<DashboardDbContext> _options;

    public TicketServiceClaimTests()
    {
        // A single kept-open connection = one shared in-memory DB across contexts.
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _options = new DbContextOptionsBuilder<DashboardDbContext>().UseSqlite(_conn).Options;
        using var ctx = new DashboardDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _conn.Dispose();

    private DashboardDbContext NewCtx() => new(_options);

    private static bool AllSkillsExist(string project, string skill) => true;

    private static async Task<Ticket> Seed(DashboardDbContext ctx, string project, TicketPhase phase, DateTime updated,
        TicketPriority priority = TicketPriority.Normal)
    {
        var t = new Ticket
        {
            ProjectKey = project, Title = $"{project}:{phase}", Phase = phase,
            Priority = priority, CreatedAt = updated, UpdatedAt = updated,
        };
        ctx.Tickets.Add(t);
        await ctx.SaveChangesAsync();
        return t;
    }

    [Fact]
    public async Task Claims_most_progressed_ticket_across_projects()
    {
        var now = DateTime.UtcNow;
        await using (var ctx = NewCtx())
        {
            await Seed(ctx, "alpha", TicketPhase.NeedsSpeculation, now);   // rank 1
            await Seed(ctx, "other", TicketPhase.NeedsImplementation, now); // rank 4 — wins
        }

        await using var svcCtx = NewCtx();
        var result = await new TicketService(svcCtx).ClaimNextAsync(null, AllSkillsExist);

        Assert.NotNull(result);
        Assert.Equal("other", result!.Ticket.ProjectKey);
        Assert.Equal(TicketPhase.Implementing, result.Ticket.Phase); // locked into -ing
        Assert.Equal("implement", result.Skill);
        Assert.False(result.Resumed);
    }

    [Fact]
    public async Task Resumes_an_in_progress_ticket_in_place()
    {
        int id;
        await using (var ctx = NewCtx())
            id = (await Seed(ctx, "other", TicketPhase.Implementing, DateTime.UtcNow)).Id;

        await using var svcCtx = NewCtx();
        var result = await new TicketService(svcCtx).ClaimNextAsync(null, AllSkillsExist);

        Assert.NotNull(result);
        Assert.True(result!.Resumed);
        Assert.Equal(id, result.Ticket.Id);
        Assert.Equal(TicketPhase.Implementing, result.Ticket.Phase); // unchanged
        Assert.Equal("implement", result.Skill);
    }

    [Fact]
    public async Task In_progress_resume_outranks_a_fresh_lower_phase()
    {
        var now = DateTime.UtcNow;
        await using (var ctx = NewCtx())
        {
            await Seed(ctx, "a", TicketPhase.NeedsImplementation, now); // rank 4
            await Seed(ctx, "b", TicketPhase.Implementing, now);        // rank 5 — wins (resume)
        }

        await using var svcCtx = NewCtx();
        var result = await new TicketService(svcCtx).ClaimNextAsync(null, AllSkillsExist);

        Assert.Equal("b", result!.Ticket.ProjectKey);
        Assert.True(result.Resumed);
    }

    [Fact]
    public async Task Ties_within_a_phase_break_to_oldest_updated()
    {
        var older = DateTime.UtcNow.AddHours(-2);
        var newer = DateTime.UtcNow;
        int oldId;
        await using (var ctx = NewCtx())
        {
            oldId = (await Seed(ctx, "a", TicketPhase.NeedsImplementation, older)).Id;
            await Seed(ctx, "b", TicketPhase.NeedsImplementation, newer);
        }

        await using var svcCtx = NewCtx();
        var result = await new TicketService(svcCtx).ClaimNextAsync(null, AllSkillsExist);

        Assert.Equal(oldId, result!.Ticket.Id);
    }

    [Fact]
    public async Task A_higher_band_outranks_every_amount_of_progress_below_it()
    {
        var now = DateTime.UtcNow;
        int highId;
        await using (var ctx = NewCtx())
        {
            // Nearly finished, but Normal.
            await Seed(ctx, "a", TicketPhase.NeedsRefinement, now);
            // Barely started, but High — and that is the whole point of the band.
            highId = (await Seed(ctx, "b", TicketPhase.NeedsSpeculation, now, TicketPriority.High)).Id;
        }

        await using var svcCtx = NewCtx();
        var result = await new TicketService(svcCtx).ClaimNextAsync(null, AllSkillsExist);

        Assert.Equal(highId, result!.Ticket.Id);
    }

    [Fact]
    public async Task Inside_one_band_the_most_progressed_still_wins()
    {
        var now = DateTime.UtcNow;
        int aheadId;
        await using (var ctx = NewCtx())
        {
            await Seed(ctx, "a", TicketPhase.NeedsSpeculation, now, TicketPriority.High);
            aheadId = (await Seed(ctx, "b", TicketPhase.NeedsQa, now, TicketPriority.High)).Id;
            // A Critical ticket the claim cannot use must not shadow the band below it.
            await Seed(ctx, "c", TicketPhase.Done, now, TicketPriority.Critical);
        }

        await using var svcCtx = NewCtx();
        var result = await new TicketService(svcCtx).ClaimNextAsync(null, AllSkillsExist);

        Assert.Equal(aheadId, result!.Ticket.Id);
    }

    [Fact]
    public async Task A_lower_band_waits_behind_the_default_one()
    {
        var now = DateTime.UtcNow;
        int normalId;
        await using (var ctx = NewCtx())
        {
            // Further along AND older, so every older tiebreak would pick it — the band is the
            // only thing holding it back.
            await Seed(ctx, "a", TicketPhase.NeedsQa, now.AddHours(-4), TicketPriority.Lowest);
            normalId = (await Seed(ctx, "b", TicketPhase.NeedsSpeculation, now)).Id;
        }

        await using var svcCtx = NewCtx();
        var result = await new TicketService(svcCtx).ClaimNextAsync(null, AllSkillsExist);

        Assert.Equal(normalId, result!.Ticket.Id);
    }

    [Fact]
    public async Task Missing_skill_blocks_the_ticket_and_claims_the_next()
    {
        var now = DateTime.UtcNow;
        int alphaId, exactId;
        await using (var ctx = NewCtx())
        {
            alphaId = (await Seed(ctx, "alpha", TicketPhase.NeedsQa, now)).Id;            // higher rank, but no skill
            exactId = (await Seed(ctx, "other", TicketPhase.NeedsImplementation, now)).Id; // gets claimed instead
        }

        bool SkillExists(string project, string skill) => project != "alpha";

        await using (var svcCtx = NewCtx())
        {
            var result = await new TicketService(svcCtx).ClaimNextAsync(null, SkillExists);
            Assert.Equal(exactId, result!.Ticket.Id);
            Assert.Equal(TicketPhase.Implementing, result.Ticket.Phase);
        }

        await using (var check = NewCtx())
        {
            var alpha = await check.Tickets.FindAsync(alphaId);
            Assert.Equal(TicketPhase.Blocked, alpha!.Phase);
            Assert.Contains("qa-test", alpha.BlockedReason);
        }
    }

    [Fact]
    public async Task Project_filter_restricts_the_claim()
    {
        var now = DateTime.UtcNow;
        await using (var ctx = NewCtx())
        {
            await Seed(ctx, "alpha", TicketPhase.NeedsQa, now);           // higher rank, filtered out
            await Seed(ctx, "other", TicketPhase.NeedsSpeculation, now);
        }

        await using var svcCtx = NewCtx();
        var result = await new TicketService(svcCtx).ClaimNextAsync(new[] { "other" }, AllSkillsExist);

        Assert.Equal("other", result!.Ticket.ProjectKey);
    }

    /// <summary>
    /// The band filter decides what is workable AT ALL, not merely what goes first — so a Normal
    /// ticket nearer the finish line is passed over entirely while the fleet is set to High.
    /// This is the whole difference between the filter and the ordering it sits above.
    /// </summary>
    [Fact]
    public async Task Priority_filter_restricts_the_claim()
    {
        var now = DateTime.UtcNow;
        await using (var ctx = NewCtx())
        {
            await Seed(ctx, "a", TicketPhase.NeedsQa, now, TicketPriority.Normal);           // higher rank, wrong band
            await Seed(ctx, "b", TicketPhase.NeedsSpeculation, now, TicketPriority.High);
        }

        await using var svcCtx = NewCtx();
        var result = await new TicketService(svcCtx).ClaimNextAsync(
            null, AllSkillsExist, priorityFilter: new[] { TicketPriority.High });

        Assert.Equal(TicketPriority.High, result!.Ticket.Priority);
        Assert.Equal(TicketPhase.Speculating, result.Ticket.Phase);
    }

    /// <summary>Multi-select: two bands on duty, the rest passed over — and priority still orders them.</summary>
    [Fact]
    public async Task Priority_filter_accepts_several_bands_and_still_orders_them()
    {
        var now = DateTime.UtcNow;
        await using (var ctx = NewCtx())
        {
            await Seed(ctx, "a", TicketPhase.NeedsQa, now, TicketPriority.Normal);           // off duty
            await Seed(ctx, "b", TicketPhase.NeedsSpeculation, now, TicketPriority.High);
            await Seed(ctx, "c", TicketPhase.NeedsSpeculation, now, TicketPriority.Critical); // wins on band
        }

        await using var svcCtx = NewCtx();
        var result = await new TicketService(svcCtx).ClaimNextAsync(
            null, AllSkillsExist, priorityFilter: new[] { TicketPriority.Critical, TicketPriority.High });

        Assert.Equal(TicketPriority.Critical, result!.Ticket.Priority);
    }

    /// <summary>
    /// A ticket in an off-duty band is passed over, never blocked: the band is a fact about how the
    /// fleet is set today, not a fault in the ticket, and switching it back on must be the only
    /// thing needed to make it workable again — no human unblocking a queue's worth by hand.
    /// </summary>
    [Fact]
    public async Task Off_duty_band_is_passed_over_not_blocked()
    {
        int id;
        await using (var ctx = NewCtx())
            id = (await Seed(ctx, "a", TicketPhase.NeedsImplementation, DateTime.UtcNow, TicketPriority.Low)).Id;

        await using (var svcCtx = NewCtx())
            Assert.Null(await new TicketService(svcCtx).ClaimNextAsync(
                null, AllSkillsExist, priorityFilter: new[] { TicketPriority.High }));

        await using (var check = NewCtx())
        {
            var t = await check.Tickets.FindAsync(id);
            Assert.Equal(TicketPhase.NeedsImplementation, t!.Phase);   // untouched, not Blocked
            Assert.Equal(0, t.HeartbeatAttempts);                      // and it cost the ticket nothing
        }

        // Band back on duty: the same ticket is claimable with nothing else changed.
        await using (var svcCtx = NewCtx())
        {
            var result = await new TicketService(svcCtx).ClaimNextAsync(
                null, AllSkillsExist, priorityFilter: new[] { TicketPriority.High, TicketPriority.Low });
            Assert.Equal(id, result!.Ticket.Id);
        }
    }

    /// <summary>An empty band list is the unset case, and means every band — never "no work".</summary>
    [Fact]
    public async Task Empty_priority_filter_means_every_band()
    {
        await using (var ctx = NewCtx())
            await Seed(ctx, "a", TicketPhase.NeedsImplementation, DateTime.UtcNow, TicketPriority.Lowest);

        await using var svcCtx = NewCtx();
        var result = await new TicketService(svcCtx).ClaimNextAsync(
            null, AllSkillsExist, priorityFilter: Array.Empty<TicketPriority>());

        Assert.Equal(TicketPriority.Lowest, result!.Ticket.Priority);
    }

    [Fact]
    public async Task Returns_null_when_nothing_is_workable()
    {
        var now = DateTime.UtcNow;
        await using (var ctx = NewCtx())
        {
            await Seed(ctx, "a", TicketPhase.NeedsApproval, now); // human gate
            await Seed(ctx, "b", TicketPhase.NeedsReview, now);   // human gate
            await Seed(ctx, "c", TicketPhase.Done, now);
            await Seed(ctx, "d", TicketPhase.Backlog, now);
            await Seed(ctx, "e", TicketPhase.Blocked, now);
        }

        await using var svcCtx = NewCtx();
        Assert.Null(await new TicketService(svcCtx).ClaimNextAsync(null, AllSkillsExist));
    }

    [Fact]
    public async Task Fresh_claim_seeds_attempt_count_to_one()
    {
        await using (var ctx = NewCtx())
            await Seed(ctx, "a", TicketPhase.NeedsImplementation, DateTime.UtcNow);

        await using var svcCtx = NewCtx();
        var result = await new TicketService(svcCtx).ClaimNextAsync(null, AllSkillsExist, maxAttempts: 3);

        Assert.False(result!.Resumed);
        Assert.Equal(1, result.Ticket.HeartbeatAttempts);
    }

    [Fact]
    public async Task Resume_below_the_cap_increments_and_proceeds()
    {
        int id;
        await using (var ctx = NewCtx())
        {
            var t = await Seed(ctx, "a", TicketPhase.Implementing, DateTime.UtcNow);
            t.HeartbeatAttempts = 1;
            await ctx.SaveChangesAsync();
            id = t.Id;
        }

        await using var svcCtx = NewCtx();
        var result = await new TicketService(svcCtx).ClaimNextAsync(null, AllSkillsExist, maxAttempts: 3);

        Assert.True(result!.Resumed);
        Assert.Equal(id, result.Ticket.Id);
        Assert.Equal(2, result.Ticket.HeartbeatAttempts);
        Assert.Equal(TicketPhase.Implementing, result.Ticket.Phase);
    }

    [Fact]
    public async Task Resume_after_a_run_that_advanced_the_ticket_resets_the_attempt_count()
    {
        // The big-ticket case: every run ends still Implementing, but each one ticks work items off.
        // That is not a wedged ticket and must not be counted toward the cap.
        var id = await ClaimedTicketWithTwoAttempts();

        // The run does something: one of the two work items is finished.
        await using (var work = NewCtx())
        {
            var t = await work.Tickets.Include(x => x.WorkItems).FirstAsync(x => x.Id == id);
            t.WorkItems.First(w => !w.Done).Done = true;
            await work.SaveChangesAsync();
        }

        await using var svcCtx = NewCtx();
        var result = await new TicketService(svcCtx).ClaimNextAsync(null, AllSkillsExist, maxAttempts: 3);

        Assert.True(result!.Resumed);
        Assert.Equal(id, result.Ticket.Id);
        Assert.Equal(1, result.Ticket.HeartbeatAttempts);
        Assert.Equal(TicketPhase.Implementing, result.Ticket.Phase);
    }

    [Fact]
    public async Task Resume_after_a_run_that_changed_nothing_still_counts_toward_the_cap()
    {
        // The failure the cap exists for — a crash, an instant exit, a worker going in circles.
        // Nothing about the ticket moved between claims, so the attempt counts.
        var id = await ClaimedTicketWithTwoAttempts();   // and then the run achieves nothing at all

        await using var svcCtx = NewCtx();
        var result = await new TicketService(svcCtx).ClaimNextAsync(null, AllSkillsExist, maxAttempts: 3);

        Assert.True(result!.Resumed);
        Assert.Equal(id, result.Ticket.Id);
        Assert.Equal(3, result.Ticket.HeartbeatAttempts);
    }

    [Fact]
    public async Task Runs_refused_by_the_session_limit_never_reach_the_cap()
    {
        // #71: three launches straight into the account's session limit blocked the ticket in a
        // minute. Each refused run hands its attempt back, so the same three launches leave it
        // workable and still on its first real attempt.
        var id = await ClaimedTicketWithTwoAttempts();

        for (var run = 0; run < 3; run++)
        {
            await using (var refund = NewCtx())
                await new TicketService(refund).RefundAttemptAsync(id, "session limit");

            await using var svcCtx = NewCtx();
            var result = await new TicketService(svcCtx).ClaimNextAsync(null, AllSkillsExist, maxAttempts: 3);
            Assert.NotNull(result);
            Assert.Equal(id, result!.Ticket.Id);
            Assert.Equal(2, result.Ticket.HeartbeatAttempts);
        }
    }

    [Fact]
    public async Task A_refund_never_takes_the_count_below_zero()
    {
        int id;
        await using (var ctx = NewCtx())
            id = (await Seed(ctx, "a", TicketPhase.Implementing, DateTime.UtcNow)).Id;

        await using var svcCtx = NewCtx();
        Assert.Equal(0, await new TicketService(svcCtx).RefundAttemptAsync(id, null));
        Assert.Null(await new TicketService(svcCtx).RefundAttemptAsync(9999, null));
    }

    /// <summary>
    /// An Implementing ticket with two unfinished work items, already claimed once so it carries a
    /// real progress mark, wound forward to attempt 2 of 3 — one short of the cap, so the next claim
    /// either resets the count or blocks the ticket depending only on what changed in between.
    /// </summary>
    private async Task<int> ClaimedTicketWithTwoAttempts()
    {
        int id;
        await using (var ctx = NewCtx())
        {
            var t = await Seed(ctx, "a", TicketPhase.NeedsImplementation, DateTime.UtcNow);
            t.WorkItems.Add(new WorkItem { Text = "one", Done = false });
            t.WorkItems.Add(new WorkItem { Text = "two", Done = false });
            await ctx.SaveChangesAsync();
            id = t.Id;
        }

        await using (var claimCtx = NewCtx())
            await new TicketService(claimCtx).ClaimNextAsync(null, AllSkillsExist, maxAttempts: 3);

        await using (var wind = NewCtx())
        {
            var t = await wind.Tickets.FindAsync(id);
            t!.HeartbeatAttempts = 2;
            await wind.SaveChangesAsync();
        }

        return id;
    }

    [Fact]
    public async Task A_ticket_that_keeps_advancing_is_never_blocked_by_the_cap()
    {
        // Five runs, each finishing one work item: with the cap counting resumes this blocked on the
        // fourth. It must survive all five.
        int id;
        await using (var ctx = NewCtx())
        {
            var t = await Seed(ctx, "a", TicketPhase.NeedsImplementation, DateTime.UtcNow);
            for (var i = 0; i < 5; i++) t.WorkItems.Add(new WorkItem { Text = $"item {i}", Done = false });
            await ctx.SaveChangesAsync();
            id = t.Id;
        }

        for (var run = 0; run < 5; run++)
        {
            await using (var svcCtx = NewCtx())
            {
                var result = await new TicketService(svcCtx).ClaimNextAsync(null, AllSkillsExist, maxAttempts: 3);
                Assert.NotNull(result);
                Assert.Equal(TicketPhase.Implementing, result!.Ticket.Phase);
            }

            // The run does its work: one more item ticked off.
            await using (var work = NewCtx())
            {
                var t = await work.Tickets.Include(x => x.WorkItems).FirstAsync(x => x.Id == id);
                t.WorkItems.OrderBy(w => w.Id).First(w => !w.Done).Done = true;
                await work.SaveChangesAsync();
            }
        }

        await using var check = NewCtx();
        var final = await check.Tickets.FindAsync(id);
        Assert.Equal(TicketPhase.Implementing, final!.Phase);
    }

    [Fact]
    public async Task Wedged_ticket_is_blocked_once_it_hits_the_attempt_cap()
    {
        int id;
        // Already resumed maxAttempts times and still in its "-ing" phase — the next pickup blocks it
        // instead of resuming (and re-billing) forever.
        await using (var ctx = NewCtx())
        {
            var t = await Seed(ctx, "a", TicketPhase.Implementing, DateTime.UtcNow);
            t.HeartbeatAttempts = 3;
            await ctx.SaveChangesAsync();
            id = t.Id;
        }

        await using (var svcCtx = NewCtx())
        {
            // The wedged ticket is the only workable one — blocking it leaves nothing to claim.
            var result = await new TicketService(svcCtx).ClaimNextAsync(null, AllSkillsExist, maxAttempts: 3);
            Assert.Null(result);
        }

        await using (var check = NewCtx())
        {
            var t = await check.Tickets.FindAsync(id);
            Assert.Equal(TicketPhase.Blocked, t!.Phase);
            Assert.Contains("3 heartbeat attempts", t.BlockedReason);
        }
    }
}
