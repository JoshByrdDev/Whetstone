using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// Returning a mid-flight ticket to speculation because the requirements moved under it.
/// The point of doing this in one service call is that the three effects — the review gate goes on,
/// the phase goes back, the stale baseline is dropped — cannot end up half-applied.
/// </summary>
public class RespeculationTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<DashboardDbContext> _options;

    public RespeculationTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _options = new DbContextOptionsBuilder<DashboardDbContext>().UseSqlite(_conn).Options;
        using var ctx = new DashboardDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _conn.Dispose();

    private DashboardDbContext NewCtx() => new(_options);

    private async Task<int> Seed(TicketPhase phase, bool specReview = false, string? snapshot = "{\"Key\":\"T-1\"}")
    {
        await using var ctx = NewCtx();
        var t = new Ticket
        {
            ProjectKey = "alpha",
            Title = "t",
            IssueKey = "T-1",
            Phase = phase,
            RequiresSpeculationReview = specReview,
            IssueSnapshot = snapshot,
            IssueSnapshotAt = snapshot is null ? null : DateTime.UtcNow,
        };
        ctx.Tickets.Add(t);
        await ctx.SaveChangesAsync();
        return t.Id;
    }

    [Theory]
    [InlineData(TicketPhase.Speculating)]
    [InlineData(TicketPhase.Implementing)]
    [InlineData(TicketPhase.QaTesting)]
    [InlineData(TicketPhase.Refining)]
    public void Any_in_progress_phase_can_return_to_speculation(TicketPhase from)
        => Assert.True(PhaseMachine.CanTransition(from, TicketPhase.NeedsSpeculation));

    [Theory]
    [InlineData(TicketPhase.NeedsImplementation)]
    [InlineData(TicketPhase.NeedsQa)]
    [InlineData(TicketPhase.Done)]
    public void A_queued_or_finished_ticket_cannot_jump_to_speculation(TicketPhase from)
        => Assert.False(PhaseMachine.CanTransition(from, TicketPhase.NeedsSpeculation));

    // NeedsReview is the exception to the above, and only for the reviewer: the replan edge is
    // theirs (TicketService.RequestChangesAsync), so the worker's respeculation door stays shut there.
    [Fact]
    public async Task The_final_review_gate_replans_through_the_reviewer_not_the_worker_door()
    {
        Assert.True(PhaseMachine.CanTransition(TicketPhase.NeedsReview, TicketPhase.NeedsSpeculation));

        var id = await Seed(TicketPhase.NeedsReview, specReview: false);
        await using (var ctx = NewCtx())
            await Assert.ThrowsAsync<InvalidTransitionException>(() =>
                new TicketService(ctx).RequestRespeculationAsync(id, "acceptance criteria rewritten"));

        await using var check = NewCtx();
        var t = await check.Tickets.FindAsync(id);
        Assert.Equal(TicketPhase.NeedsReview, t!.Phase);
        Assert.False(t.RequiresSpeculationReview);
    }

    [Fact]
    public async Task Respeculation_moves_the_ticket_back_and_forces_the_review_gate_on()
    {
        var id = await Seed(TicketPhase.Implementing, specReview: false);

        await using (var ctx = NewCtx())
            await new TicketService(ctx).RequestRespeculationAsync(id, "acceptance criteria rewritten");

        await using var check = NewCtx();
        var t = await check.Tickets.FindAsync(id);
        Assert.Equal(TicketPhase.NeedsSpeculation, t!.Phase);
        Assert.True(t.RequiresSpeculationReview);
    }

    [Fact]
    public async Task Respeculation_leaves_an_already_required_review_on()
    {
        var id = await Seed(TicketPhase.QaTesting, specReview: true);

        await using (var ctx = NewCtx())
            await new TicketService(ctx).RequestRespeculationAsync(id, "scope changed");

        await using var check = NewCtx();
        Assert.True((await check.Tickets.FindAsync(id))!.RequiresSpeculationReview);
    }

    /// <summary>The old baseline described the plan we are throwing away; keeping it would diff the
    /// new plan against the wrong version and under-report the next change.</summary>
    [Fact]
    public async Task Respeculation_drops_the_stale_jira_baseline()
    {
        var id = await Seed(TicketPhase.Implementing);

        await using (var ctx = NewCtx())
            await new TicketService(ctx).RequestRespeculationAsync(id, "requirement removed");

        await using var check = NewCtx();
        var t = await check.Tickets.FindAsync(id);
        Assert.Null(t!.IssueSnapshot);
        Assert.Null(t.IssueSnapshotAt);
    }

    [Fact]
    public async Task Respeculation_records_why_on_the_ticket()
    {
        var id = await Seed(TicketPhase.Implementing);

        await using (var ctx = NewCtx())
            await new TicketService(ctx).RequestRespeculationAsync(id, "the export format changed to CSV");

        await using var check = NewCtx();
        var comments = await check.Comments.Where(c => c.TicketId == id).ToListAsync();
        Assert.Contains(comments, c => c.Body.Contains("the export format changed to CSV"));
    }

    [Fact]
    public async Task Respeculation_on_a_missing_ticket_returns_null()
    {
        await using var ctx = NewCtx();
        Assert.Null(await new TicketService(ctx).RequestRespeculationAsync(4242, "gone"));
    }

    /// <summary>Without a Jira service the capture is skipped rather than failing the transition —
    /// the pipeline must keep working for tickets with no Jira link at all.</summary>
    [Fact]
    public async Task Leaving_speculation_without_a_jira_service_still_transitions()
    {
        var id = await Seed(TicketPhase.Speculating, snapshot: null);

        await using (var ctx = NewCtx())
            await new TicketService(ctx).TransitionAsync(id, TicketPhase.NeedsApproval, null);

        await using var check = NewCtx();
        var t = await check.Tickets.FindAsync(id);
        // Gate is off by default, so it skips straight on to implementation.
        Assert.Equal(TicketPhase.NeedsImplementation, t!.Phase);
        Assert.Null(t.IssueSnapshot);
    }
}
