using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// The escape log: every time a person sends back work the pipeline called done, written with the
/// move, classified afterwards by a person - so what gets past the whole pipeline can be counted,
/// by the kind of miss it was.
/// </summary>
public class EscapeLogTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<DashboardDbContext> _options;

    public EscapeLogTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _options = new DbContextOptionsBuilder<DashboardDbContext>().UseSqlite(_conn).Options;
        using var ctx = new DashboardDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _conn.Dispose();

    private DashboardDbContext NewCtx() => new(_options);

    private async Task<int> Seed(TicketPhase phase, string project = "alpha", TicketTrack track = TicketTrack.Delivery)
    {
        await using var ctx = NewCtx();
        var t = new Ticket { ProjectKey = project, Title = "Search step", Phase = phase, Branch = "ticket/1", Track = track };
        ctx.Tickets.Add(t);
        await ctx.SaveChangesAsync();
        return t.Id;
    }

    private async Task<List<EscapeEvent>> Escapes(int ticketId)
    {
        await using var ctx = NewCtx();
        return await ctx.EscapeEvents.AsNoTracking().Where(e => e.TicketId == ticketId).OrderBy(e => e.Id).ToListAsync();
    }

    [Theory]
    [InlineData(TicketPhase.NeedsReview, TicketPhase.NeedsImplementation)]
    [InlineData(TicketPhase.Done, TicketPhase.NeedsImplementation)]
    public async Task A_person_sending_finished_work_back_logs_one_escape(TicketPhase from, TicketPhase to)
    {
        var id = await Seed(from);
        await using (var ctx = NewCtx())
        {
            var svc = new TicketService(ctx);
            await svc.AddCommentAsync(id, "Right-align the badge.", AuthorKind.Human);
            await svc.TransitionAsync(id, to, "Owner note: right-align the badge", TransitionActor.Human,
                build: "alpha-web@abc1234, alpha-api@def5678");
        }

        var e = Assert.Single(await Escapes(id));
        Assert.Equal(from, e.FromPhase);
        Assert.Equal(to, e.ToPhase);
        Assert.Equal("alpha", e.ProjectKey);
        Assert.Equal("Owner note: right-align the badge", e.Reason);
        Assert.Equal("alpha-web@abc1234, alpha-api@def5678", e.Build);
        Assert.NotNull(e.NoteCommentId);
        Assert.Null(e.Kind);
        Assert.False(e.Backfilled);
    }

    [Theory]
    [InlineData(TransitionActor.Worker)]
    [InlineData(TransitionActor.System)]
    [InlineData(TransitionActor.Steward)]
    public async Task A_move_nobody_on_the_board_made_is_not_an_escape(TransitionActor actor)
    {
        var id = await Seed(TicketPhase.NeedsReview);
        await using (var ctx = NewCtx())
            await new TicketService(ctx).TransitionAsync(id, TicketPhase.NeedsImplementation, "back", actor);

        Assert.Empty(await Escapes(id));
    }

    [Fact]
    public async Task A_forward_move_a_block_or_a_shelving_is_not_an_escape()
    {
        var done = await Seed(TicketPhase.NeedsReview);
        var blocked = await Seed(TicketPhase.NeedsReview);
        var forward = await Seed(TicketPhase.NeedsRefinement);
        await using (var ctx = NewCtx())
        {
            var svc = new TicketService(ctx);
            await svc.TransitionAsync(done, TicketPhase.Done, "signed off", TransitionActor.Human);
            await svc.TransitionAsync(blocked, TicketPhase.Blocked, "waiting on a credential", TransitionActor.Human);
            await svc.TransitionAsync(forward, TicketPhase.Refining, null, TransitionActor.Human);
        }

        Assert.Empty(await Escapes(done));
        Assert.Empty(await Escapes(blocked));
        Assert.Empty(await Escapes(forward));
        Assert.False(EscapeEvent.IsEscape(TicketTrack.Delivery, TicketPhase.NeedsReview, TicketPhase.Backlog, TransitionActor.Human));
    }

    [Fact]
    public void The_review_track_s_trip_back_is_the_author_s_fix_not_an_escape() =>
        Assert.False(EscapeEvent.IsEscape(TicketTrack.Review, TicketPhase.NeedsReview, TicketPhase.NeedsPeerReview, TransitionActor.Human));

    [Fact]
    public async Task A_comment_older_than_the_note_window_is_not_taken_as_the_note()
    {
        var id = await Seed(TicketPhase.NeedsReview);
        await using (var ctx = NewCtx())
        {
            ctx.Comments.Add(new Comment { TicketId = id, Author = AuthorKind.Human, Body = "old", CreatedAt = DateTime.UtcNow.AddHours(-2) });
            ctx.Comments.Add(new Comment { TicketId = id, Author = AuthorKind.Claude, Body = "worker", CreatedAt = DateTime.UtcNow });
            await ctx.SaveChangesAsync();
            await new TicketService(ctx).TransitionAsync(id, TicketPhase.NeedsImplementation, null, TransitionActor.Human);
        }

        Assert.Null(Assert.Single(await Escapes(id)).NoteCommentId);
    }

    [Fact]
    public async Task A_classification_round_trips_and_can_be_cleared()
    {
        var id = await Seed(TicketPhase.NeedsReview);
        await using (var ctx = NewCtx())
            await new TicketService(ctx).TransitionAsync(id, TicketPhase.NeedsImplementation, "PR review", TransitionActor.Human);
        var escapeId = (await Escapes(id))[0].Id;

        await using (var ctx = NewCtx())
        {
            var e = await new TicketService(ctx).ClassifyEscapeAsync(escapeId, EscapeKind.OutsideContract, EscapeSource.PrReview, " hyphenated EIN ");
            Assert.NotNull(e);
        }
        await using (var ctx = NewCtx())
        {
            var list = await new TicketService(ctx).EscapesAsync("alpha", null, null, unclassifiedOnly: false);
            var v = Assert.Single(list);
            Assert.Equal(EscapeKind.OutsideContract, v.Event.Kind);
            Assert.Equal(EscapeSource.PrReview, v.Event.Source);
            Assert.Equal("hyphenated EIN", v.Event.ClassifierNote);
            Assert.NotNull(v.Event.ClassifiedAt);
            Assert.Equal("Search step", v.TicketTitle);
            Assert.Empty(await new TicketService(ctx).EscapesAsync("alpha", null, null, unclassifiedOnly: true));
            Assert.Empty(await new TicketService(ctx).EscapesAsync("bravo", null, null, unclassifiedOnly: false));
        }

        await using (var ctx = NewCtx())
            await new TicketService(ctx).ClassifyEscapeAsync(escapeId, null, EscapeSource.Owner, "ignored");
        var cleared = Assert.Single(await Escapes(id));
        Assert.Null(cleared.Kind);
        Assert.Null(cleared.Source);
        Assert.Null(cleared.ClassifiedAt);
    }

    /// <summary>A ticket sent back from review by a person and claimed by an implementation run holding its lease.</summary>
    private async Task<(int TicketId, int RunId)> SentBackAndClaimed(string reason = "Owner note: keep the grid one height")
    {
        var id = await Seed(TicketPhase.NeedsReview);
        await using (var ctx = NewCtx())
        {
            var svc = new TicketService(ctx);
            await svc.TransitionAsync(id, TicketPhase.NeedsImplementation, reason, TransitionActor.Human);
            await svc.TransitionAsync(id, TicketPhase.Implementing, "claimed by heartbeat", TransitionActor.System);
        }
        await using (var ctx = NewCtx())
        {
            var l = new ResourceLease { ResourceKey = ResourceLeaseService.TicketKey(id), TicketId = id, ExpiresAt = DateTime.UtcNow.AddHours(1) };
            ctx.ResourceLeases.Add(l);
            await ctx.SaveChangesAsync();
            return (id, l.Id);
        }
    }

    private async Task Forward(int id)
    {
        await using var ctx = NewCtx();
        await new TicketService(ctx).TransitionAsync(id, TicketPhase.NeedsImplementationReview, "implemented", TransitionActor.Worker);
    }

    [Fact]
    public async Task A_run_classifies_the_escape_that_sent_its_ticket_back_and_is_recorded_as_the_run()
    {
        var (id, run) = await SentBackAndClaimed();
        await using (var ctx = NewCtx())
        {
            var (e, problem) = await new TicketService(ctx).ClassifyEscapeByRunAsync(id, EscapeKind.UiPolish, EscapeSource.Owner,
                "The grid changed height on every keystroke; nothing in QA looked at the screen in motion.");
            Assert.Null(problem);
            Assert.NotNull(e);
        }

        var stored = Assert.Single(await Escapes(id));
        Assert.Equal(EscapeKind.UiPolish, stored.Kind);
        Assert.Equal(EscapeSource.Owner, stored.Source);
        Assert.Equal(TransitionActor.Worker, stored.ClassifiedBy);
        Assert.Equal(run, stored.ClassifiedByRunId);
        Assert.NotNull(stored.ClassifiedAt);
        Assert.False(stored.Overridden);
    }

    [Theory]
    [InlineData("The owner now wants the badge on the right.")]
    [InlineData("The owner said \"ok\" to it.")]
    [InlineData("   ")]
    public async Task A_requirement_change_that_quotes_nothing_is_refused(string why)
    {
        var (id, _) = await SentBackAndClaimed();
        await using (var ctx = NewCtx())
        {
            var (e, problem) = await new TicketService(ctx).ClassifyEscapeByRunAsync(id, EscapeKind.RequirementChange, EscapeSource.Owner, why);
            Assert.Null(e);
            Assert.NotNull(problem);
        }
        Assert.Null(Assert.Single(await Escapes(id)).Kind);
    }

    [Fact]
    public async Task A_requirement_change_that_quotes_the_owner_is_taken()
    {
        var (id, _) = await SentBackAndClaimed();
        await using var ctx = NewCtx();
        var (e, problem) = await new TicketService(ctx).ClassifyEscapeByRunAsync(id, EscapeKind.RequirementChange, EscapeSource.Owner,
            "The note asks for “right-align the Already added badge”, which neither the description nor the master notes mention.");
        Assert.Null(problem);
        Assert.Equal(EscapeKind.RequirementChange, e!.Kind);
    }

    [Fact]
    public async Task The_hand_off_out_of_implementing_waits_for_the_escape_that_started_the_cycle()
    {
        var (id, _) = await SentBackAndClaimed();

        var refused = await Assert.ThrowsAsync<HandoffRequirementException>(() => Forward(id));
        Assert.Contains("classify_escape", refused.Message);

        await using (var ctx = NewCtx())
            await new TicketService(ctx).ClassifyEscapeByRunAsync(id, EscapeKind.Logic, EscapeSource.PrReview, "A plain defect the diff review should have seen.");
        await Forward(id);

        await using (var ctx = NewCtx())
            Assert.NotEqual(TicketPhase.Implementing, (await ctx.Tickets.FindAsync(id))!.Phase);
    }

    [Fact]
    public async Task Blocking_and_going_back_are_never_held_by_the_escape()
    {
        var (id, _) = await SentBackAndClaimed();
        await using var ctx = NewCtx();
        var t = await new TicketService(ctx).TransitionAsync(id, TicketPhase.Blocked,
            "Which of the two layouts in the note does the owner want: the stacked one or the side-by-side one?", TransitionActor.Worker);
        Assert.Equal(TicketPhase.Blocked, t!.Phase);
    }

    [Fact]
    public async Task An_older_escape_does_not_hold_a_later_cycle()
    {
        var (id, _) = await SentBackAndClaimed();
        await using (var ctx = NewCtx())
        {
            var svc = new TicketService(ctx);
            // The run that picked up the send-back went to QA without classifying (as a run from
            // before the gate would have), and QA sent it back: this cycle was started by QA, not by
            // the escape, so the escape no longer holds it.
            var t = await ctx.Tickets.FindAsync(id);
            t!.Phase = TicketPhase.QaTesting;
            await ctx.SaveChangesAsync();
            await svc.TransitionAsync(id, TicketPhase.NeedsImplementation, "QA failed item 3", TransitionActor.Worker);
            await svc.TransitionAsync(id, TicketPhase.Implementing, "claimed by heartbeat", TransitionActor.System);
        }

        await Forward(id);
        Assert.Null(Assert.Single(await Escapes(id)).Kind);
    }

    [Fact]
    public async Task A_backfilled_escape_never_holds_a_hand_off()
    {
        var id = await Seed(TicketPhase.Implementing);
        var at = DateTime.UtcNow.AddMinutes(-20);
        await using (var ctx = NewCtx())
        {
            ctx.PhaseTransitions.Add(new PhaseTransition { TicketId = id, ProjectKey = "alpha", From = TicketPhase.NeedsReview, To = TicketPhase.NeedsImplementation, Actor = TransitionActor.Human, Backward = true, At = at });
            ctx.EscapeEvents.Add(new EscapeEvent { TicketId = id, ProjectKey = "alpha", FromPhase = TicketPhase.NeedsReview, ToPhase = TicketPhase.NeedsImplementation, At = at, Backfilled = true });
            await ctx.SaveChangesAsync();
        }

        await Forward(id);
        await using (var ctx = NewCtx())
            Assert.NotEqual(TicketPhase.Implementing, (await ctx.Tickets.FindAsync(id))!.Phase);
    }

    [Fact]
    public async Task A_person_correcting_a_run_s_call_is_recorded_as_a_person_s_override()
    {
        var (id, _) = await SentBackAndClaimed();
        await using (var ctx = NewCtx())
            await new TicketService(ctx).ClassifyEscapeByRunAsync(id, EscapeKind.UiPolish, EscapeSource.Owner, "Looked like polish.");
        var escapeId = (await Escapes(id))[0].Id;

        await using (var ctx = NewCtx())
            await new TicketService(ctx).ClassifyEscapeAsync(escapeId, EscapeKind.OutsideContract, EscapeSource.PrReview, "The fake accepted a format the service does not.");

        var e = Assert.Single(await Escapes(id));
        Assert.Equal(EscapeKind.OutsideContract, e.Kind);
        Assert.Equal(TransitionActor.Human, e.ClassifiedBy);
        Assert.Null(e.ClassifiedByRunId);
        Assert.True(e.Overridden);
    }

    [Fact]
    public async Task Classifying_an_escape_that_does_not_exist_finds_nothing()
    {
        await using var ctx = NewCtx();
        Assert.Null(await new TicketService(ctx).ClassifyEscapeAsync(999, EscapeKind.Logic, null, null));
    }

    [Fact]
    public async Task The_backfill_reads_only_moves_a_person_made_and_runs_once()
    {
        var id = await Seed(TicketPhase.NeedsImplementation);
        var at = DateTime.UtcNow.AddDays(-1);
        await using (var ctx = NewCtx())
        {
            ctx.Comments.Add(new Comment { TicketId = id, Author = AuthorKind.Human, Body = "grid height", CreatedAt = at.AddMinutes(-1) });
            ctx.PhaseTransitions.AddRange(
                new PhaseTransition { TicketId = id, ProjectKey = "alpha", From = TicketPhase.NeedsReview, To = TicketPhase.NeedsImplementation, Actor = TransitionActor.Human, Backward = true, At = at, Reason = "grid height" },
                // A person's forward move, a worker's backward one, and a reconstructed row whose actor is a placeholder.
                new PhaseTransition { TicketId = id, ProjectKey = "alpha", From = TicketPhase.NeedsReview, To = TicketPhase.Done, Actor = TransitionActor.Human, At = at },
                new PhaseTransition { TicketId = id, ProjectKey = "alpha", From = TicketPhase.QaTesting, To = TicketPhase.NeedsImplementation, Actor = TransitionActor.Worker, Backward = true, At = at },
                new PhaseTransition { TicketId = id, ProjectKey = "alpha", From = TicketPhase.NeedsReview, To = TicketPhase.NeedsImplementation, Actor = TransitionActor.Human, Backward = true, Backfilled = true, At = at });
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = NewCtx())
            Assert.Equal(1, await new TicketService(ctx).BackfillEscapesAsync());
        await using (var ctx = NewCtx())
            Assert.Equal(0, await new TicketService(ctx).BackfillEscapesAsync());

        var e = Assert.Single(await Escapes(id));
        Assert.True(e.Backfilled);
        Assert.Equal("grid height", e.Reason);
        Assert.NotNull(e.NoteCommentId);
        Assert.Null(e.Build);
    }
}
