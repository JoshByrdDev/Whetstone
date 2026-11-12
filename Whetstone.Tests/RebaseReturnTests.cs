using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// Rebase returns: finished, reviewed work that left review only because its branch no longer
/// merged with the base. The run that rebases it says whether the rebase reaches what QA and
/// refinement passed, and the hand-off goes by that - straight back to review, or on to QA.
/// </summary>
public class RebaseReturnTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<DashboardDbContext> _options;

    private const string Conflict = "Branch conflicts with main and cannot be merged as it stands.";

    public RebaseReturnTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _options = new DbContextOptionsBuilder<DashboardDbContext>().UseSqlite(_conn).Options;
        using var ctx = new DashboardDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _conn.Dispose();

    private DashboardDbContext NewCtx() => new(_options);

    private async Task<int> Seed(TicketPhase phase, bool requiresImplementationReview = false)
    {
        await using var ctx = NewCtx();
        var t = new Ticket
        {
            ProjectKey = "alpha", Title = "Search step", Phase = phase, Branch = "ticket/1",
            RequiresImplementationReview = requiresImplementationReview,
        };
        ctx.Tickets.Add(t);
        await ctx.SaveChangesAsync();
        return t.Id;
    }

    private async Task<List<RebaseReturn>> Returns(int ticketId)
    {
        await using var ctx = NewCtx();
        return await ctx.RebaseReturns.AsNoTracking().Where(r => r.TicketId == ticketId).OrderBy(r => r.Id).ToListAsync();
    }

    private async Task<int> ReturnedAndClaimed(string reason = Conflict, TransitionActor actor = TransitionActor.System,
        bool requiresImplementationReview = false)
    {
        var id = await Seed(TicketPhase.NeedsReview, requiresImplementationReview);
        await using var ctx = NewCtx();
        var svc = new TicketService(ctx);
        await svc.TransitionAsync(id, TicketPhase.NeedsImplementation, reason, actor);
        await svc.TransitionAsync(id, TicketPhase.Implementing, "claimed by heartbeat", TransitionActor.System);
        return id;
    }

    private async Task<Ticket> Forward(int id)
    {
        await using var ctx = NewCtx();
        return (await new TicketService(ctx).TransitionAsync(id, TicketPhase.NeedsImplementationReview, "implemented", TransitionActor.Worker))!;
    }

    private async Task Judge(int id, RebaseVerdict verdict)
    {
        await using var ctx = NewCtx();
        var (r, problem) = await new TicketService(ctx).JudgeRebaseReturnAsync(id, verdict,
            "Brought in 3f2a1c9 (merch export in core/index.ts) and 9be0d14 (a new books page): neither reads the search step.");
        Assert.Null(problem);
        Assert.NotNull(r);
    }

    [Theory]
    [InlineData(TransitionActor.System)]
    [InlineData(TransitionActor.Worker)]
    [InlineData(TransitionActor.Steward)]
    public async Task Reviewed_work_sent_back_for_a_conflict_logs_one_return(TransitionActor actor)
    {
        var id = await Seed(TicketPhase.NeedsReview);
        await using (var ctx = NewCtx())
            await new TicketService(ctx).TransitionAsync(id, TicketPhase.NeedsImplementation, Conflict, actor);

        var r = Assert.Single(await Returns(id));
        Assert.Equal(actor, r.Actor);
        Assert.Equal(Conflict, r.Reason);
        Assert.Null(r.Verdict);
        Assert.Null(r.HandedOffTo);
    }

    [Fact]
    public async Task A_person_s_send_back_a_change_or_a_conflict_found_before_review_is_not_a_return()
    {
        var human = await Seed(TicketPhase.NeedsReview);
        var change = await Seed(TicketPhase.NeedsReview);
        var inQa = await Seed(TicketPhase.QaTesting);
        await using (var ctx = NewCtx())
        {
            var svc = new TicketService(ctx);
            await svc.TransitionAsync(human, TicketPhase.NeedsImplementation, Conflict, TransitionActor.Human);
            await svc.TransitionAsync(change, TicketPhase.NeedsImplementation, "Changes requested: the badge is misaligned", TransitionActor.Steward);
            await svc.TransitionAsync(inQa, TicketPhase.NeedsImplementation, Conflict, TransitionActor.System);
        }

        Assert.Empty(await Returns(human));
        Assert.Empty(await Returns(change));
        Assert.Empty(await Returns(inQa));
    }

    [Fact]
    public async Task The_hand_off_waits_for_the_run_to_judge_the_rebase()
    {
        var id = await ReturnedAndClaimed();

        var refused = await Assert.ThrowsAsync<HandoffRequirementException>(() => Forward(id));
        Assert.Contains("judge_rebase_return", refused.Message);

        await Judge(id, RebaseVerdict.Retest);
        Assert.NotEqual(TicketPhase.Implementing, (await Forward(id)).Phase);
    }

    [Fact]
    public async Task Back_to_review_goes_straight_home_past_qa_and_refinement()
    {
        var id = await ReturnedAndClaimed();
        await Judge(id, RebaseVerdict.BackToReview);

        var t = await Forward(id);

        Assert.Equal(TicketPhase.NeedsReview, t.Phase);
        var r = Assert.Single(await Returns(id));
        Assert.Equal(RebaseVerdict.BackToReview, r.Verdict);
        Assert.Equal(TicketPhase.NeedsReview, r.HandedOffTo);
        Assert.NotNull(r.HandedOffAt);
        await using var ctx = NewCtx();
        Assert.Contains(await ctx.PhaseTransitions.Where(p => p.TicketId == id).Select(p => p.Reason).ToListAsync(),
            x => x == TicketService.RebaseOnlyReason);
    }

    [Fact]
    public async Task Back_to_review_wins_over_the_implementation_review_gate()
    {
        var id = await ReturnedAndClaimed(requiresImplementationReview: true);
        await Judge(id, RebaseVerdict.BackToReview);

        Assert.Equal(TicketPhase.NeedsReview, (await Forward(id)).Phase);
    }

    [Fact]
    public async Task Retest_goes_on_to_qa()
    {
        var id = await ReturnedAndClaimed();
        await Judge(id, RebaseVerdict.Retest);

        Assert.Equal(TicketPhase.NeedsQa, (await Forward(id)).Phase);
        Assert.Equal(TicketPhase.NeedsImplementationReview, Assert.Single(await Returns(id)).HandedOffTo);
    }

    [Fact]
    public async Task A_steward_s_rebase_only_send_back_is_a_return()
    {
        var id = await ReturnedAndClaimed(
            "Changes requested: Sent back to NeedsImplementation (1 of 5): rebase only - the branch conflicts with its base.",
            TransitionActor.Steward);

        Assert.Equal(TransitionActor.Steward, Assert.Single(await Returns(id)).Actor);
        await Assert.ThrowsAsync<HandoffRequirementException>(() => Forward(id));
    }

    [Fact]
    public async Task A_verdict_without_a_reason_or_outside_its_cycle_is_refused()
    {
        var id = await ReturnedAndClaimed();
        var plain = await Seed(TicketPhase.Implementing);
        await using var ctx = NewCtx();
        var svc = new TicketService(ctx);

        var (r1, p1) = await svc.JudgeRebaseReturnAsync(id, RebaseVerdict.BackToReview, "fine");
        Assert.Null(r1);
        Assert.Contains("why", p1);

        var (r2, p2) = await svc.JudgeRebaseReturnAsync(plain, RebaseVerdict.BackToReview,
            "Nothing the base brought in reaches this ticket's behaviour at all, by reading.");
        Assert.Null(r2);
        Assert.Contains("nothing to judge", p2);
    }

    [Fact]
    public async Task A_later_cycle_that_is_not_a_rebase_is_not_held()
    {
        var id = await ReturnedAndClaimed();
        await Judge(id, RebaseVerdict.Retest);
        await Forward(id);
        await using (var ctx = NewCtx())
        {
            var svc = new TicketService(ctx);
            await svc.TransitionAsync(id, TicketPhase.QaTesting, "claimed", TransitionActor.System);
            await svc.TransitionAsync(id, TicketPhase.NeedsImplementation, "QA item 12 failed: the badge overlaps", TransitionActor.Worker);
            await svc.TransitionAsync(id, TicketPhase.Implementing, "claimed", TransitionActor.System);
        }

        Assert.Equal(TicketPhase.NeedsQa, (await Forward(id)).Phase);
        Assert.Single(await Returns(id));
    }
    private async Task PassedEarlier(int id)
    {
        await using var ctx = NewCtx();
        ctx.QaItems.Add(new QaItem
        {
            TicketId = id, Text = "the search step finds a book", Status = QaStatus.Passed,
            VerifiedAt = DateTime.UtcNow.AddHours(-1),
        });
        await ctx.SaveChangesAsync();
    }

    private async Task<(DateTime? One, bool InBoard, string? Verdict)> Currency(int id)
    {
        await using var ctx = NewCtx();
        var svc = new TicketService(ctx);
        var one = await svc.LastBackwardAtAsync(id);
        var board = await svc.LastBackwardAtAsync(new[] { id });
        var q = await ctx.QaItems.AsNoTracking().SingleAsync(x => x.TicketId == id);
        return (one, board.ContainsKey(id), HandoffRequirements.VerdictOf(q, one));
    }

    [Fact]
    public async Task Back_to_review_keeps_the_passes_it_came_home_on_current()
    {
        var id = await ReturnedAndClaimed();
        await PassedEarlier(id);
        await Judge(id, RebaseVerdict.BackToReview);
        await Forward(id);

        var (one, inBoard, verdict) = await Currency(id);
        Assert.Null(one);
        Assert.False(inBoard);
        Assert.NotEqual("Stale", verdict);
    }

    [Fact]
    public async Task Retest_still_makes_the_passes_before_it_stale()
    {
        var id = await ReturnedAndClaimed();
        await PassedEarlier(id);
        await Judge(id, RebaseVerdict.Retest);
        await Forward(id);

        var (one, inBoard, verdict) = await Currency(id);
        Assert.NotNull(one);
        Assert.True(inBoard);
        Assert.Equal("Stale", verdict);
    }

    [Fact]
    public async Task A_return_not_yet_handed_off_still_counts()
    {
        var id = await ReturnedAndClaimed();
        await PassedEarlier(id);
        await Judge(id, RebaseVerdict.BackToReview);

        Assert.Equal("Stale", (await Currency(id)).Verdict);
    }
}
