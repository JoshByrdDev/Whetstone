using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// The pipeline's own numbers: which moves count as rework, that every phase change leaves a
/// countable row behind (including the second hop when a review gate is skipped), and that a
/// worker run is recorded whether or not it survived to report on itself.
/// </summary>
public class MetricsTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<DashboardDbContext> _options;

    public MetricsTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _options = new DbContextOptionsBuilder<DashboardDbContext>().UseSqlite(_conn).Options;
        using var ctx = new DashboardDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _conn.Dispose();

    private DashboardDbContext NewCtx() => new(_options);

    private static async Task<Ticket> Seed(DashboardDbContext ctx, TicketPhase phase,
        bool requiresImplementationReview = false)
    {
        var t = new Ticket
        {
            ProjectKey = "alpha",
            Title = "a ticket",
            Phase = phase,
            RequiresImplementationReview = requiresImplementationReview,
        };
        ctx.Tickets.Add(t);
        await ctx.SaveChangesAsync();
        return t;
    }

    // ---- IsBackward: the definition of rework ----------------------------------

    [Theory]
    // The real send-backs, one per gate that can reject.
    [InlineData(TicketPhase.QaTesting, TicketPhase.NeedsImplementation, true)]
    [InlineData(TicketPhase.Refining, TicketPhase.NeedsImplementation, true)]
    [InlineData(TicketPhase.Refining, TicketPhase.NeedsQa, true)]
    [InlineData(TicketPhase.NeedsReview, TicketPhase.NeedsImplementation, true)]
    [InlineData(TicketPhase.NeedsApproval, TicketPhase.NeedsSpeculation, true)]
    [InlineData(TicketPhase.NeedsImplementationReview, TicketPhase.NeedsImplementation, true)]
    [InlineData(TicketPhase.Done, TicketPhase.NeedsImplementation, true)]
    [InlineData(TicketPhase.Implementing, TicketPhase.NeedsSpeculation, true)]   // replan
    // Forward moves, including the two success hand-offs a worker is most likely to get wrong.
    [InlineData(TicketPhase.QaTesting, TicketPhase.NeedsRefinement, false)]
    [InlineData(TicketPhase.Refining, TicketPhase.NeedsReview, false)]
    [InlineData(TicketPhase.NeedsReview, TicketPhase.Done, false)]
    [InlineData(TicketPhase.Backlog, TicketPhase.NeedsSpeculation, false)]
    public void IsBackward_matches_pipeline_direction(TicketPhase from, TicketPhase to, bool expected)
        => Assert.Equal(expected, PhaseMachine.IsBackward(from, to));

    [Theory]
    [InlineData(TicketPhase.Implementing, TicketPhase.Blocked)]
    [InlineData(TicketPhase.QaTesting, TicketPhase.Blocked)]
    [InlineData(TicketPhase.Blocked, TicketPhase.NeedsImplementation)]
    [InlineData(TicketPhase.Blocked, TicketPhase.NeedsQa)]
    public void Blocked_edges_are_never_rework(TicketPhase from, TicketPhase to)
    {
        // Blocking means the work stopped, which is a different outcome from being sent back to be
        // redone - and unblocking is not the pipeline making progress it never made.
        Assert.False(PhaseMachine.IsBackward(from, to));
    }

    // ---- TransitionAsync writes the history ------------------------------------

    [Fact]
    public async Task Every_transition_is_recorded_with_its_actor()
    {
        int id;
        await using (var ctx = NewCtx()) id = (await Seed(ctx, TicketPhase.NeedsQa)).Id;

        await using (var ctx = NewCtx())
            await new TicketService(ctx).TransitionAsync(id, TicketPhase.QaTesting, "claimed by heartbeat");

        await using (var ctx = NewCtx())
            await new TicketService(ctx).TransitionAsync(id, TicketPhase.NeedsImplementation, "QA found failures",
                TransitionActor.Worker);

        await using var read = NewCtx();
        var rows = await read.PhaseTransitions.Where(p => p.TicketId == id).OrderBy(p => p.Id).ToListAsync();

        Assert.Equal(2, rows.Count);

        // The claim is the dashboard's own move, so it is System even though a worker triggered it.
        Assert.Equal(TransitionActor.System, rows[0].Actor);
        Assert.Equal(TicketPhase.NeedsQa, rows[0].From);
        Assert.Equal(TicketPhase.QaTesting, rows[0].To);
        Assert.False(rows[0].Backward);
        Assert.Equal("alpha", rows[0].ProjectKey);

        Assert.Equal(TransitionActor.Worker, rows[1].Actor);
        Assert.True(rows[1].Backward);
        Assert.Equal("QA found failures", rows[1].Reason);
    }

    [Fact]
    public async Task Skipped_review_gate_records_both_hops()
    {
        // The ticket does not want the implementation review, so the hand-off passes straight
        // through to NeedsQa. Both moves really happened and both need to be countable - otherwise
        // the history claims the ticket teleported from Implementing to NeedsQa.
        int id;
        await using (var ctx = NewCtx()) id = (await Seed(ctx, TicketPhase.Implementing)).Id;

        await using (var ctx = NewCtx())
            await new TicketService(ctx).TransitionAsync(id, TicketPhase.NeedsImplementationReview, null,
                TransitionActor.Worker);

        await using var read = NewCtx();
        var rows = await read.PhaseTransitions.Where(p => p.TicketId == id).OrderBy(p => p.Id).ToListAsync();

        Assert.Equal(2, rows.Count);
        Assert.Equal(TicketPhase.NeedsImplementationReview, rows[0].To);
        Assert.Equal(TransitionActor.Worker, rows[0].Actor);
        // The skip is the dashboard's routing decision, not the worker's.
        Assert.Equal(TicketPhase.NeedsQa, rows[1].To);
        Assert.Equal(TransitionActor.System, rows[1].Actor);
        Assert.False(rows[1].Backward);
    }

    [Fact]
    public async Task Illegal_transition_records_nothing()
    {
        int id;
        await using (var ctx = NewCtx()) id = (await Seed(ctx, TicketPhase.NeedsQa)).Id;

        await using (var ctx = NewCtx())
        {
            var svc = new TicketService(ctx);
            await Assert.ThrowsAsync<InvalidTransitionException>(
                () => svc.TransitionAsync(id, TicketPhase.Done, "skip to the end"));
        }

        await using var read = NewCtx();
        Assert.Empty(await read.PhaseTransitions.Where(p => p.TicketId == id).ToListAsync());
    }

    // ---- CycleRun ---------------------------------------------------------------

    private static MetricsService.CycleReport Report(int ticketId, TicketPhase phase,
        CycleOutcome outcome = CycleOutcome.Completed, int? inputTokens = 1000) =>
        new(ticketId, "alpha", phase, "implement", Resumed: false, Model: "claude-opus-5",
            StartedAt: DateTime.UtcNow.AddMinutes(-10), DurationSeconds: 600, Mutations: 4,
            Outcome: outcome, ApiDurationSeconds: 540, NumTurns: 42,
            InputTokens: inputTokens, OutputTokens: 2000, CacheReadTokens: 30000, CacheCreationTokens: 500);

    [Fact]
    public async Task Cycle_records_where_the_ticket_actually_landed()
    {
        int id;
        await using (var ctx = NewCtx()) id = (await Seed(ctx, TicketPhase.Implementing)).Id;

        // The worker hands off, THEN the cycle is reported - so PhaseAfter is the hand-off target,
        // which is what makes "did this run move the ticket?" a single-row question.
        await using (var ctx = NewCtx())
            await new TicketService(ctx).TransitionAsync(id, TicketPhase.NeedsImplementationReview, null,
                TransitionActor.Worker);

        await using (var ctx = NewCtx())
        {
            var run = await new MetricsService(ctx).RecordCycleAsync(Report(id, TicketPhase.Implementing));
            Assert.NotNull(run);
            Assert.Equal(TicketPhase.Implementing, run!.Phase);
            Assert.Equal(TicketPhase.NeedsQa, run.PhaseAfter);   // gate skipped straight through
            Assert.Equal(42, run.NumTurns);
            Assert.Equal(30000, run.CacheReadTokens);
            Assert.True(run.EndedAt >= run.StartedAt);
        }
    }

    [Fact]
    public async Task Cycle_keeps_where_its_context_went()
    {
        int id;
        await using (var ctx = NewCtx()) id = (await Seed(ctx, TicketPhase.QaTesting)).Id;

        await using (var ctx = NewCtx())
            await new MetricsService(ctx).RecordCycleAsync(Report(id, TicketPhase.QaTesting) with
            {
                PeakContextTokens = 184_000,
                ToolResultChars = 61_500,
                LargestToolResults =
                [
                    new("Bash", "pnpm check", 48_000),
                    new("mcp__playwright__browser_snapshot", null, 13_500, Images: 1),
                ],
            });

        // Read back through a fresh context: the list is a JSON column, so what matters is that it
        // survives the round trip, not that the object handed in still has it.
        await using (var ctx = NewCtx())
        {
            var run = (await new MetricsService(ctx).RecentCyclesAsync(null, 1)).Single();
            Assert.Equal(184_000, run.PeakContextTokens);
            Assert.Equal(61_500, run.ToolResultChars);
            var top = MetricsService.LargestToolResultsOf(run);
            Assert.Equal(["Bash", "mcp__playwright__browser_snapshot"], top.Select(t => t.Tool));
            Assert.Equal("pnpm check", top[0].Detail);
            Assert.Equal(1, top[1].Images);
        }
    }

    [Fact]
    public async Task A_run_that_never_reported_is_still_recorded()
    {
        // A worker killed at the timeout produces no result event, so it has no token counts. That
        // absence is a fact about the run; dropping the row would make the pipeline look flawless.
        int id;
        await using (var ctx = NewCtx()) id = (await Seed(ctx, TicketPhase.QaTesting)).Id;

        await using var ctx2 = NewCtx();
        var run = await new MetricsService(ctx2).RecordCycleAsync(
            new MetricsService.CycleReport(id, "alpha", TicketPhase.QaTesting, "qa-test", false,
                "claude-opus-5", DateTime.UtcNow.AddHours(-2), 7200, 0, CycleOutcome.Timeout));

        Assert.NotNull(run);
        Assert.Equal(CycleOutcome.Timeout, run!.Outcome);
        Assert.Null(run.NumTurns);
        Assert.Null(run.InputTokens);
        Assert.Null(run.PeakContextTokens);
        Assert.Empty(MetricsService.LargestToolResultsOf(run));
        Assert.Equal(TicketPhase.QaTesting, run.PhaseAfter);   // never handed off
    }

    // ---- Backfill from the old comment prose -------------------------------------

    private static async Task SeedComment(DashboardDbContext ctx, int ticketId, string body, DateTime at)
    {
        ctx.Comments.Add(new Comment { TicketId = ticketId, Author = AuthorKind.System, Body = body, CreatedAt = at });
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task Backfill_reads_the_old_phase_change_comments()
    {
        int id;
        var t0 = new DateTime(2026, 7, 20, 9, 0, 0, DateTimeKind.Utc);
        await using (var ctx = NewCtx())
        {
            id = (await Seed(ctx, TicketPhase.NeedsQa)).Id;
            await SeedComment(ctx, id, "Phase changed NeedsQa → QaTesting — claimed by heartbeat", t0);
            await SeedComment(ctx, id, "Phase changed QaTesting → NeedsImplementation — QA failed on the totals", t0.AddHours(1));
            await SeedComment(ctx, id, "The implement phase finished without committing its work.", t0.AddHours(2));
            await SeedComment(ctx, id, "Blocked: the database would not come up", t0.AddHours(3));
        }

        await using (var ctx = NewCtx())
            Assert.Equal(2, await new MetricsService(ctx).BackfillTransitionsAsync());

        await using var read = NewCtx();
        var rows = await read.PhaseTransitions.OrderBy(p => p.At).ToListAsync();

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.True(r.Backfilled));
        Assert.All(rows, r => Assert.Equal("alpha", r.ProjectKey));

        Assert.Equal(TicketPhase.NeedsQa, rows[0].From);
        Assert.Equal(TicketPhase.QaTesting, rows[0].To);
        Assert.Equal("claimed by heartbeat", rows[0].Reason);
        Assert.Equal(t0, rows[0].At);

        // The bounce the whole exercise exists to count.
        Assert.True(rows[1].Backward);
        Assert.Equal("QA failed on the totals", rows[1].Reason);
    }

    [Fact]
    public async Task Backfill_never_runs_twice()
    {
        int id;
        await using (var ctx = NewCtx())
        {
            id = (await Seed(ctx, TicketPhase.NeedsQa)).Id;
            await SeedComment(ctx, id, "Phase changed NeedsQa → QaTesting", DateTime.UtcNow);
        }

        await using (var ctx = NewCtx()) Assert.Equal(1, await new MetricsService(ctx).BackfillTransitionsAsync());
        // Startup runs it every time; the second call must find its own work already done rather
        // than double every historical move.
        await using (var ctx = NewCtx()) Assert.Equal(0, await new MetricsService(ctx).BackfillTransitionsAsync());

        await using var read = NewCtx();
        Assert.Single(await read.PhaseTransitions.ToListAsync());
    }

    [Fact]
    public async Task Backfill_stands_down_once_real_transitions_exist()
    {
        // A live transition beats reconstructed prose: if anything real has been recorded, the
        // backfill must not append a second, guessed copy of the same history underneath it.
        int id;
        await using (var ctx = NewCtx())
        {
            id = (await Seed(ctx, TicketPhase.NeedsQa)).Id;
            await SeedComment(ctx, id, "Phase changed NeedsSpeculation → Speculating", DateTime.UtcNow.AddDays(-1));
        }

        await using (var ctx = NewCtx())
            await new TicketService(ctx).TransitionAsync(id, TicketPhase.QaTesting, "claimed");

        await using (var ctx = NewCtx()) Assert.Equal(0, await new MetricsService(ctx).BackfillTransitionsAsync());

        await using var read = NewCtx();
        var rows = await read.PhaseTransitions.ToListAsync();
        Assert.Single(rows);
        Assert.False(rows[0].Backfilled);
    }

    [Fact]
    public async Task Cycle_for_a_deleted_ticket_is_dropped()
    {
        await using var ctx = NewCtx();
        Assert.Null(await new MetricsService(ctx).RecordCycleAsync(Report(4242, TicketPhase.Implementing)));
        Assert.Empty(await ctx.CycleRuns.ToListAsync());
    }

    [Fact]
    public async Task Deleting_a_ticket_takes_its_metrics_with_it()
    {
        int id;
        await using (var ctx = NewCtx()) id = (await Seed(ctx, TicketPhase.NeedsQa)).Id;

        await using (var ctx = NewCtx())
        {
            await new TicketService(ctx).TransitionAsync(id, TicketPhase.QaTesting, "claimed");
            await new MetricsService(ctx).RecordCycleAsync(Report(id, TicketPhase.QaTesting));
        }

        await using (var ctx = NewCtx()) await new TicketService(ctx).DeleteAsync(id);

        await using var read = NewCtx();
        Assert.Empty(await read.PhaseTransitions.ToListAsync());
        Assert.Empty(await read.CycleRuns.ToListAsync());
    }
}
