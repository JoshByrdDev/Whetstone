using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// The aggregates the metrics view renders. Mostly arithmetic, but the arithmetic is the whole
/// product here - a bounce rate that quietly counts the wrong denominator is worse than no bounce
/// rate, because it will be believed.
/// </summary>
public class MetricsSummaryTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<DashboardDbContext> _options;

    public MetricsSummaryTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _options = new DbContextOptionsBuilder<DashboardDbContext>().UseSqlite(_conn).Options;
        using var ctx = new DashboardDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _conn.Dispose();
    private DashboardDbContext NewCtx() => new(_options);

    private static async Task<int> SeedTicket(DashboardDbContext ctx, string project = "alpha")
    {
        var t = new Ticket { ProjectKey = project, Title = "t", Phase = TicketPhase.Backlog };
        ctx.Tickets.Add(t);
        await ctx.SaveChangesAsync();
        return t.Id;
    }

    private static void Move(DashboardDbContext ctx, int ticketId, TicketPhase from, TicketPhase to,
        double daysAgo = 0, string project = "alpha", bool backfilled = false)
    {
        ctx.PhaseTransitions.Add(new PhaseTransition
        {
            TicketId = ticketId,
            ProjectKey = project,
            From = from,
            To = to,
            Backward = PhaseMachine.IsBackward(from, to),
            Backfilled = backfilled,
            At = DateTime.UtcNow.AddDays(-daysAgo),
        });
    }

    private static void Run(DashboardDbContext ctx, int ticketId, TicketPhase phase, double hoursAgo,
        double minutes, int input, int output, CycleOutcome outcome = CycleOutcome.Completed,
        string project = "alpha")
    {
        ctx.CycleRuns.Add(new CycleRun
        {
            TicketId = ticketId,
            ProjectKey = project,
            Phase = phase,
            Skill = PhaseMachine.SkillFor(phase),
            StartedAt = DateTime.UtcNow.AddHours(-hoursAgo),
            DurationSeconds = minutes * 60,
            InputTokens = input,
            OutputTokens = output,
            CacheReadTokens = 1000,
            CacheCreationTokens = 100,
            Outcome = outcome,
        });
    }

    [Fact]
    public async Task Bounce_rate_is_backward_moves_over_moves_out_of_that_phase()
    {
        await using (var ctx = NewCtx())
        {
            var id = await SeedTicket(ctx);
            // QA ran four times: three passes, one send-back.
            Move(ctx, id, TicketPhase.QaTesting, TicketPhase.NeedsRefinement, 3);
            Move(ctx, id, TicketPhase.QaTesting, TicketPhase.NeedsRefinement, 2);
            Move(ctx, id, TicketPhase.QaTesting, TicketPhase.NeedsRefinement, 1);
            Move(ctx, id, TicketPhase.QaTesting, TicketPhase.NeedsImplementation, 1);
            // A claim into QA is a move out of NeedsQa, and must not land in QaTesting's denominator.
            Move(ctx, id, TicketPhase.NeedsQa, TicketPhase.QaTesting, 4);
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = NewCtx();
        var s = await new MetricsService(ctx2).SummaryAsync(null);

        var qa = s.Bounces.Single(b => b.From == "QaTesting");
        Assert.Equal(4, qa.Total);
        Assert.Equal(1, qa.Backward);
        Assert.Equal(25.0, qa.Rate);

        // NeedsQa has exactly one legal exit and it is forwards, so it can only ever score 0% and
        // has no business on a list of where work gets sent back.
        Assert.DoesNotContain(s.Bounces, b => b.From == "NeedsQa");
    }

    [Fact]
    public async Task Only_phases_that_can_reject_are_listed()
    {
        await using (var ctx = NewCtx())
        {
            var id = await SeedTicket(ctx);
            Move(ctx, id, TicketPhase.Backlog, TicketPhase.NeedsSpeculation, 1);
            Move(ctx, id, TicketPhase.NeedsSpeculation, TicketPhase.Speculating, 1);
            Move(ctx, id, TicketPhase.NeedsImplementation, TicketPhase.Implementing, 1);
            // Unblocking looks like an ordinary move, and the matching block was never recorded by
            // the backfill - so Blocked would otherwise show exits it never had entries for.
            Move(ctx, id, TicketPhase.Blocked, TicketPhase.NeedsImplementation, 1);
            // A real gate, with both of its outcomes.
            Move(ctx, id, TicketPhase.QaTesting, TicketPhase.NeedsRefinement, 1);
            Move(ctx, id, TicketPhase.QaTesting, TicketPhase.NeedsImplementation, 1);
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = NewCtx();
        var s = await new MetricsService(ctx2).SummaryAsync(null);

        Assert.Equal(new[] { "QaTesting" }, s.Bounces.Select(b => b.From).ToArray());
        // The excluded rows are still counted in the totals - they happened, they are just not
        // evidence about rework.
        Assert.Equal(6, s.Quality.TotalTransitions);
    }

    [Fact]
    public async Task Blocked_edges_stay_out_of_the_bounce_rate()
    {
        await using (var ctx = NewCtx())
        {
            var id = await SeedTicket(ctx);
            Move(ctx, id, TicketPhase.Implementing, TicketPhase.Blocked, 1);
            Move(ctx, id, TicketPhase.Blocked, TicketPhase.NeedsImplementation, 1);
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = NewCtx();
        var s = await new MetricsService(ctx2).SummaryAsync(null);
        Assert.Equal(0, s.Quality.TotalBackward);          // stopping is not the same as rework
        Assert.Equal(1, s.Quality.Blocked);
    }

    [Fact]
    public async Task First_pass_QA_counts_tickets_QA_never_sent_back()
    {
        await using (var ctx = NewCtx())
        {
            var clean = await SeedTicket(ctx);
            Move(ctx, clean, TicketPhase.NeedsQa, TicketPhase.QaTesting, 2);
            Move(ctx, clean, TicketPhase.QaTesting, TicketPhase.NeedsRefinement, 2);

            var bounced = await SeedTicket(ctx);
            Move(ctx, bounced, TicketPhase.NeedsQa, TicketPhase.QaTesting, 3);
            Move(ctx, bounced, TicketPhase.QaTesting, TicketPhase.NeedsImplementation, 3);
            Move(ctx, bounced, TicketPhase.NeedsQa, TicketPhase.QaTesting, 1);
            Move(ctx, bounced, TicketPhase.QaTesting, TicketPhase.NeedsRefinement, 1);

            // Never reached QA at all - must not count either way.
            var young = await SeedTicket(ctx);
            Move(ctx, young, TicketPhase.Backlog, TicketPhase.NeedsSpeculation, 1);
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = NewCtx();
        var s = await new MetricsService(ctx2).SummaryAsync(null);
        Assert.Equal(2, s.Quality.QaAttempted);
        Assert.Equal(1, s.Quality.FirstPassQa);
    }

    [Fact]
    public async Task Phase_cost_uses_medians_so_one_timeout_cannot_distort_it()
    {
        await using (var ctx = NewCtx())
        {
            var id = await SeedTicket(ctx);
            Run(ctx, id, TicketPhase.Implementing, 5, 10, 1000, 2000);
            Run(ctx, id, TicketPhase.Implementing, 4, 12, 1000, 2000);
            Run(ctx, id, TicketPhase.Implementing, 3, 14, 1000, 2000);
            Run(ctx, id, TicketPhase.Implementing, 2, 120, 1000, 2000, CycleOutcome.Timeout);
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = NewCtx();
        var s = await new MetricsService(ctx2).SummaryAsync(null);
        var impl = s.PhaseCosts.Single(p => p.Phase == "Implementing");

        Assert.Equal(4, impl.Runs);
        Assert.Equal(13, impl.MedianMinutes);      // mean would be 39, which describes no run that happened
        Assert.Equal(1, impl.Timeouts);
        Assert.Equal(3, impl.Completed);
        Assert.Equal(3000, impl.MedianTokens);
    }

    [Fact]
    public async Task Token_windows_only_count_runs_inside_them()
    {
        await using (var ctx = NewCtx())
        {
            var id = await SeedTicket(ctx);
            Run(ctx, id, TicketPhase.Implementing, hoursAgo: 1, minutes: 10, input: 100, output: 10);
            Run(ctx, id, TicketPhase.Implementing, hoursAgo: 4, minutes: 10, input: 200, output: 20);
            Run(ctx, id, TicketPhase.Implementing, hoursAgo: 9, minutes: 10, input: 400, output: 40);   // outside 5h
            Run(ctx, id, TicketPhase.Implementing, hoursAgo: 40, minutes: 10, input: 800, output: 80);  // outside 24h
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = NewCtx();
        var s = await new MetricsService(ctx2).SummaryAsync(null);

        Assert.Equal(300, s.Last5Hours.Input);
        Assert.Equal(2, s.Last5Hours.Runs);
        Assert.Equal(700, s.Last24Hours.Input);
        Assert.Equal(3, s.Last24Hours.Runs);
    }

    [Fact]
    public async Task An_empty_pipeline_reports_no_data_rather_than_zeroes_that_look_like_findings()
    {
        await using var ctx = NewCtx();
        var s = await new MetricsService(ctx).SummaryAsync(null);

        Assert.False(s.HasCycleData);      // the view says "nothing has run yet", not "0 minutes"
        Assert.Equal(0, s.CycleCount);
        Assert.Empty(s.Bounces);
        Assert.Empty(s.PhaseCosts);
    }

    [Fact]
    public async Task Project_and_window_both_filter()
    {
        await using (var ctx = NewCtx())
        {
            var alpha = await SeedTicket(ctx, "alpha");
            var other = await SeedTicket(ctx, "other");
            Move(ctx, alpha, TicketPhase.QaTesting, TicketPhase.NeedsImplementation, 1, "alpha");
            Move(ctx, other, TicketPhase.QaTesting, TicketPhase.NeedsImplementation, 1, "other");
            Move(ctx, alpha, TicketPhase.QaTesting, TicketPhase.NeedsImplementation, 90, "alpha"); // old
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = NewCtx();
        Assert.Equal(2, (await new MetricsService(ctx2).SummaryAsync(null, 30)).Quality.TotalTransitions);
        Assert.Equal(1, (await new MetricsService(ctx2).SummaryAsync("alpha", 30)).Quality.TotalTransitions);
        Assert.Equal(3, (await new MetricsService(ctx2).SummaryAsync(null, 365)).Quality.TotalTransitions);
    }

    [Fact]
    public async Task Backfilled_rows_are_counted_but_declared()
    {
        // They feed the bounce rate because their phases and timings are real; they are declared
        // because their actor is a placeholder, so anything counting by actor must exclude them.
        await using (var ctx = NewCtx())
        {
            var id = await SeedTicket(ctx);
            Move(ctx, id, TicketPhase.QaTesting, TicketPhase.NeedsImplementation, 1, backfilled: true);
            Move(ctx, id, TicketPhase.QaTesting, TicketPhase.NeedsRefinement, 1);
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = NewCtx();
        var s = await new MetricsService(ctx2).SummaryAsync(null);
        Assert.Equal(2, s.Quality.TotalTransitions);
        Assert.Equal(1, s.Quality.TotalBackward);
        Assert.Equal(1, s.Quality.BackfilledTransitions);
    }

    [Fact]
    public async Task Recent_cycles_come_back_newest_first()
    {
        await using (var ctx = NewCtx())
        {
            var id = await SeedTicket(ctx);
            Run(ctx, id, TicketPhase.Implementing, 5, 10, 1, 1);
            Run(ctx, id, TicketPhase.QaTesting, 1, 10, 1, 1);
            Run(ctx, id, TicketPhase.Refining, 3, 10, 1, 1);
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = NewCtx();
        var recent = await new MetricsService(ctx2).RecentCyclesAsync(null, 2);
        Assert.Equal(2, recent.Count);
        Assert.Equal(TicketPhase.QaTesting, recent[0].Phase);
        Assert.Equal(TicketPhase.Refining, recent[1].Phase);
    }
}
