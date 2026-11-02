using Whetstone.Api;
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
    public async Task Cycle_keeps_its_window_and_compactions_and_the_drawer_reads_them_back()
    {
        int id;
        await using (var ctx = NewCtx()) id = (await Seed(ctx, TicketPhase.QaTesting)).Id;

        await using (var ctx = NewCtx())
        {
            var m = new MetricsService(ctx);
            await m.RecordCycleAsync(Report(id, TicketPhase.QaTesting) with
                { PeakContextTokens = 486_000, ContextWindowTokens = 1_000_000, Compactions = 0 });
            // A provider that reports no window: unknown, not zero. A nonsense window is not kept.
            await m.RecordCycleAsync(Report(id, TicketPhase.QaTesting) with
                { PeakContextTokens = 90_000, ContextWindowTokens = 0, Compactions = -1 });
        }

        await using (var ctx = NewCtx())
        {
            var runs = await TicketRunContext.ForTicketAsync(ctx, id);
            Assert.Equal(2, runs.Count);
            Assert.Equal((486_000, 1_000_000, 0), (runs[0].Peak!.Value, runs[0].Window!.Value, runs[0].Compactions!.Value));
            Assert.Equal("QaTesting", runs[0].Phase);
            Assert.Null(runs[1].Window);
            Assert.Null(runs[1].Compactions);
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

    // ---- Why a run happened, which time round, and what it left ----------------------

    [Theory]
    [InlineData(TicketPhase.NeedsSpeculation, TicketPhase.NeedsImplementation, null, RunEntryReasons.Forward)]
    [InlineData(TicketPhase.QaTesting, TicketPhase.NeedsImplementation, "QA found failures", RunEntryReasons.QaFailed)]
    [InlineData(TicketPhase.Refining, TicketPhase.NeedsImplementation, "gaps", RunEntryReasons.RefineSentBack)]
    [InlineData(TicketPhase.Refining, TicketPhase.NeedsQa, "re-test", RunEntryReasons.RefineSentBack)]
    [InlineData(TicketPhase.NeedsReview, TicketPhase.NeedsImplementation, "rename it", RunEntryReasons.ReviewChanges)]
    [InlineData(TicketPhase.NeedsReview, TicketPhase.NeedsImplementation,
        "Branch conflicts with main and cannot be merged as it stands.", RunEntryReasons.Conflict)]
    [InlineData(TicketPhase.Blocked, TicketPhase.NeedsImplementation, "answered", RunEntryReasons.Unblocked)]
    public void Entry_reason_reads_the_move_that_queued_the_run(TicketPhase from, TicketPhase to, string? reason, string want)
    {
        var move = new PhaseTransition { From = from, To = to, Reason = reason, Backward = PhaseMachine.IsBackward(from, to) };
        Assert.Equal(want, RunEntryReasons.Of(move));
    }

    private async Task AddMove(int ticketId, TicketPhase from, TicketPhase to, DateTime at, string? reason = null,
        TransitionActor actor = TransitionActor.System)
    {
        await using var ctx = NewCtx();
        ctx.PhaseTransitions.Add(new PhaseTransition
        {
            TicketId = ticketId, ProjectKey = "alpha", From = from, To = to, At = at, Reason = reason, Actor = actor,
            Backward = PhaseMachine.IsBackward(from, to),
        });
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task A_run_knows_why_it_happened_and_which_time_round_it_is()
    {
        int id;
        await using (var ctx = NewCtx()) id = (await Seed(ctx, TicketPhase.Implementing)).Id;
        var t0 = DateTime.UtcNow.AddHours(-3);

        // First time round: speculation handed it on, then the claim took it.
        await AddMove(id, TicketPhase.Speculating, TicketPhase.NeedsImplementation, t0, actor: TransitionActor.Worker);
        await AddMove(id, TicketPhase.NeedsImplementation, TicketPhase.Implementing, t0.AddMinutes(1));
        CycleRun first, second, other;
        await using (var ctx = NewCtx())
            first = (await new MetricsService(ctx).RecordCycleAsync(Report(id, TicketPhase.Implementing) with { StartedAt = t0.AddMinutes(1) }))!;

        // QA fails it back; the second implement run is rework, and its second pass.
        await AddMove(id, TicketPhase.Implementing, TicketPhase.NeedsQa, t0.AddMinutes(30), actor: TransitionActor.Worker);
        await AddMove(id, TicketPhase.NeedsQa, TicketPhase.QaTesting, t0.AddMinutes(31));
        await using (var ctx = NewCtx())
            other = (await new MetricsService(ctx).RecordCycleAsync(
                Report(id, TicketPhase.QaTesting) with { Skill = "qa-test", StartedAt = t0.AddMinutes(31) }))!;
        await AddMove(id, TicketPhase.QaTesting, TicketPhase.NeedsImplementation, t0.AddMinutes(50), "QA found failures", TransitionActor.Worker);
        await AddMove(id, TicketPhase.NeedsImplementation, TicketPhase.Implementing, t0.AddMinutes(51));
        await using (var ctx = NewCtx())
            second = (await new MetricsService(ctx).RecordCycleAsync(Report(id, TicketPhase.Implementing) with { StartedAt = t0.AddMinutes(51) }))!;

        Assert.Equal((1, RunEntryReasons.Forward), (first.Pass!.Value, first.EntryReason));
        Assert.Equal((1, RunEntryReasons.Forward), (other.Pass!.Value, other.EntryReason));
        Assert.Equal((2, RunEntryReasons.QaFailed), (second.Pass!.Value, second.EntryReason));

        // A run that picked up where an unfinished one left off is resumed, whatever queued it.
        await using (var ctx = NewCtx())
        {
            var resumed = (await new MetricsService(ctx).RecordCycleAsync(
                Report(id, TicketPhase.Implementing) with { Resumed = true, StartedAt = t0.AddMinutes(90) }))!;
            Assert.Equal((3, RunEntryReasons.Resumed), (resumed.Pass!.Value, resumed.EntryReason));
        }
    }

    [Fact]
    public async Task The_heartbeats_own_moves_are_the_systems()
    {
        // The worker script's conflict send-back used to go through the people's route and be logged
        // as a person asking for changes. Through the heartbeat's route it is System's, and a run it
        // queues reads as a conflict.
        int id;
        await using (var ctx = NewCtx()) id = (await Seed(ctx, TicketPhase.NeedsReview)).Id;
        await using (var ctx = NewCtx())
            await new TicketService(ctx).TransitionAsync(id, TicketPhase.NeedsImplementation,
                "Branch conflicts with main and cannot be merged as it stands.", TransitionActor.System);

        await using var read = NewCtx();
        var move = await read.PhaseTransitions.SingleAsync(p => p.TicketId == id);
        Assert.Equal(TransitionActor.System, move.Actor);
        Assert.Equal(RunEntryReasons.Conflict, RunEntryReasons.Of(move));
    }

    [Fact]
    public async Task A_run_keeps_how_it_was_set_up_and_what_it_left()
    {
        int id;
        await using (var ctx = NewCtx()) id = (await Seed(ctx, TicketPhase.Implementing)).Id;
        var files = Enumerable.Range(0, MetricsService.MaxChangedFiles + 50).Select(i => $"src/f{i}.cs").ToList();

        await using (var ctx = NewCtx())
            await new MetricsService(ctx).RecordCycleAsync(Report(id, TicketPhase.Implementing) with
            {
                Detail = new MetricsService.CycleRunDetail(
                    Provider: "claude", Effort: "high", CliVersion: "2.9.1", SkillHash: "abc123abc123",
                    PromptHash: "def456def456", SessionId: "5f1c2a", PrepSeconds: 41.2, StartupSeconds: 6.5,
                    HandoffSeconds: 12, StartRebase: "rebased", BaseBehind: 3, FilesChanged: 250,
                    LinesAdded: 900, LinesRemoved: 40, RunCommits: 2, ChangedFiles: files),
            });

        await using (var ctx = NewCtx())
        {
            var run = await ctx.CycleRuns.SingleAsync(c => c.TicketId == id);
            Assert.Equal(("claude", "high", "2.9.1", "5f1c2a"), (run.Provider, run.Effort, run.CliVersion, run.SessionId));
            Assert.Equal(("abc123abc123", "def456def456"), (run.SkillHash, run.PromptHash));
            Assert.Equal((41.2, 6.5, 12.0), (run.PrepSeconds!.Value, run.StartupSeconds!.Value, run.HandoffSeconds!.Value));
            Assert.Equal(("rebased", 3), (run.StartRebase, run.BaseBehind!.Value));
            Assert.Equal((250, 900, 40, 2), (run.FilesChanged!.Value, run.LinesAdded!.Value, run.LinesRemoved!.Value, run.RunCommits!.Value));
            // The count is the whole change; the list keeps the first few hundred paths and no more.
            var kept = System.Text.Json.JsonSerializer.Deserialize<List<string>>(run.ChangedFilesJson!)!;
            Assert.Equal(MetricsService.MaxChangedFiles, kept.Count);
            Assert.Equal("src/f0.cs", kept[0]);
        }
    }

    [Fact]
    public async Task A_run_that_sent_no_detail_has_none()
    {
        int id;
        await using (var ctx = NewCtx()) id = (await Seed(ctx, TicketPhase.Implementing)).Id;
        await using var ctx2 = NewCtx();
        var run = (await new MetricsService(ctx2).RecordCycleAsync(Report(id, TicketPhase.Implementing)))!;
        Assert.Null(run.Provider);
        Assert.Null(run.PrepSeconds);
        Assert.Null(run.FilesChanged);
        Assert.Null(run.ChangedFilesJson);
        Assert.Equal(1, run.Pass);
        Assert.Null(run.EntryReason);   // no move on record
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
