using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// The human → worker channel: a note the ticket's owner leaves is delivered to the next run,
/// outranks the ticket, and holds that run's hand-off until it is answered.
/// <para>
/// The rules pinned here are the ones that make a note different from a comment. A comment is a
/// thing that was said; a note is a thing that must be dealt with, and everything below is about
/// that difference — it survives a run that read it and died, it cannot be worked past, and it costs
/// a real answer rather than the word "done".
/// </para>
/// </summary>
public class OwnerNoteTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<DashboardDbContext> _options;

    public OwnerNoteTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _options = new DbContextOptionsBuilder<DashboardDbContext>().UseSqlite(_conn).Options;
        using var ctx = new DashboardDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _conn.Dispose();

    private DashboardDbContext NewCtx() => new(_options);

    private async Task<int> Seed(TicketPhase phase = TicketPhase.Implementing)
    {
        await using var ctx = NewCtx();
        var t = new Ticket { ProjectKey = "alpha", Title = "t", Phase = phase };
        ctx.Tickets.Add(t);
        await ctx.SaveChangesAsync();
        return t.Id;
    }

    private async Task<int> Note(int ticketId, string body = "The header still overlaps the filter row on mobile.")
    {
        await using var ctx = NewCtx();
        var c = await new TicketService(ctx).AddCommentAsync(ticketId, body, AuthorKind.Human);
        return c!.Id;
    }

    // ---- What counts as outstanding ----

    [Fact]
    public async Task An_owner_note_holds_the_phases_handoff()
    {
        var id = await Seed(TicketPhase.Implementing);
        await Note(id);

        await using var ctx = NewCtx();
        var ex = await Assert.ThrowsAsync<HandoffRequirementException>(() =>
            new TicketService(ctx).TransitionAsync(id, TicketPhase.NeedsImplementationReview, null, TransitionActor.Worker));

        Assert.Contains("acknowledge_note", ex.Message);
        Assert.Equal(TicketPhase.NeedsImplementationReview, ex.To);
    }

    [Fact]
    public async Task Answering_the_note_releases_the_handoff()
    {
        var id = await Seed(TicketPhase.Implementing);
        var noteId = await Note(id);

        await using (var ctx = NewCtx())
        {
            var (note, problem) = await new TicketService(ctx).AcknowledgeNoteAsync(
                noteId, "Moved the filter row below the header and pinned it, so the two no longer overlap on a narrow screen.");
            Assert.Null(problem);
            Assert.NotNull(note!.AcknowledgedAt);
        }

        await using var ctx2 = NewCtx();
        var t = await new TicketService(ctx2).TransitionAsync(
            id, TicketPhase.NeedsImplementationReview, null, TransitionActor.Worker);

        Assert.Equal(TicketPhase.NeedsQa, t!.Phase);  // gate off by default, so it routes on
    }

    /// <summary>
    /// The whole reason acknowledgement exists rather than delivery being enough: a run can read a
    /// note and then die, and the note has to survive that. Delivery is not consumption.
    /// </summary>
    [Fact]
    public async Task Delivery_alone_does_not_clear_a_note()
    {
        var id = await Seed(TicketPhase.Implementing);
        await Note(id);

        DateTime firstDelivery;
        await using (var ctx = NewCtx())
        {
            var svc0 = new TicketService(ctx);
            var outstanding = await svc0.UnansweredOwnerNotesAsync(id);
            Assert.Single(outstanding);
            // Unstamped at render time, which is what lets the prompt tell a fresh note from one a
            // previous run already saw and did not answer.
            Assert.Null(outstanding[0].DeliveredAt);
            await svc0.MarkOwnerNotesDeliveredAsync(outstanding);
            firstDelivery = outstanding[0].DeliveredAt!.Value;
        }

        // A second claim gets it again, still unanswered, still stamped with the FIRST delivery —
        // "how long has this been ignored" is the reading that matters to whoever wrote it.
        await using var ctx2 = NewCtx();
        var svc = new TicketService(ctx2);
        var again = await svc.UnansweredOwnerNotesAsync(id);
        Assert.Single(again);
        await svc.MarkOwnerNotesDeliveredAsync(again);
        Assert.Equal(firstDelivery, again[0].DeliveredAt);
        await Assert.ThrowsAsync<HandoffRequirementException>(() =>
            svc.TransitionAsync(id, TicketPhase.NeedsImplementationReview, null, TransitionActor.Worker));
    }

    [Fact]
    public async Task Only_the_owners_notes_are_gating()
    {
        var id = await Seed(TicketPhase.Implementing);
        await using (var ctx = NewCtx())
        {
            var svc = new TicketService(ctx);
            await svc.AddCommentAsync(id, "Working through item 3 of 5; nothing surprising so far.", AuthorKind.Claude);
            await svc.AddCommentAsync(id, "Phase changed NeedsImplementation to Implementing", AuthorKind.System);
        }

        await using var ctx2 = NewCtx();
        var t = await new TicketService(ctx2).TransitionAsync(
            id, TicketPhase.NeedsImplementationReview, null, TransitionActor.Worker);

        Assert.Equal(TicketPhase.NeedsQa, t!.Phase);
    }

    /// <summary>
    /// A run that cannot honestly continue must always be able to say so — including when the thing
    /// it cannot do is the note itself, which is the case where blocking matters most.
    /// </summary>
    [Fact]
    public async Task Blocking_is_never_held_by_a_note()
    {
        var id = await Seed(TicketPhase.Implementing);
        await Note(id);

        await using var ctx = NewCtx();
        var t = await new TicketService(ctx).TransitionAsync(id, TicketPhase.Blocked,
            "The note asks for a mobile layout, but this repo has no mobile breakpoint defined anywhere.",
            TransitionActor.Worker);

        Assert.Equal(TicketPhase.Blocked, t!.Phase);
    }

    /// <summary>
    /// A person moving their own ticket is not the failure this guards against — they wrote the note,
    /// and they are entitled to decide it no longer matters.
    /// </summary>
    [Fact]
    public async Task A_human_move_is_not_held_by_a_note()
    {
        var id = await Seed(TicketPhase.Implementing);
        await Note(id);

        await using var ctx = NewCtx();
        var t = await new TicketService(ctx).TransitionAsync(
            id, TicketPhase.NeedsImplementationReview, null, TransitionActor.Human);

        Assert.NotEqual(TicketPhase.Implementing, t!.Phase);
    }

    /// <summary>
    /// A note left on a ticket sitting in a queue is not holding anything yet: nothing is running,
    /// and the run that picks it up gets it delivered. Gating the claim itself would stop the ticket
    /// reaching the only thing that can answer the note.
    /// </summary>
    [Fact]
    public async Task A_note_on_a_queued_ticket_does_not_gate_the_claim()
    {
        var id = await Seed(TicketPhase.NeedsImplementation);
        await Note(id);

        await using var ctx = NewCtx();
        var t = await new TicketService(ctx).TransitionAsync(
            id, TicketPhase.Implementing, null, TransitionActor.Worker);

        Assert.Equal(TicketPhase.Implementing, t!.Phase);
    }

    // ---- What counts as an answer ----

    [Theory]
    [InlineData("done")]
    [InlineData("Fixed.")]
    [InlineData("acknowledged")]
    [InlineData("ok")]
    [InlineData("yes did that")]
    public async Task Filler_is_not_an_answer(string response)
    {
        var id = await Seed();
        var noteId = await Note(id);

        await using var ctx = NewCtx();
        var (note, problem) = await new TicketService(ctx).AcknowledgeNoteAsync(noteId, response);

        Assert.NotNull(problem);
        Assert.Null(note!.AcknowledgedAt);
    }

    [Fact]
    public async Task Only_a_human_note_can_be_acknowledged()
    {
        var id = await Seed();
        int claudeNoteId;
        await using (var ctx = NewCtx())
            claudeNoteId = (await new TicketService(ctx).AddCommentAsync(id, "Half done.", AuthorKind.Claude))!.Id;

        await using var ctx2 = NewCtx();
        var (_, problem) = await new TicketService(ctx2).AcknowledgeNoteAsync(
            claudeNoteId, "I have read my own note and I agree with it entirely.");

        Assert.NotNull(problem);
        Assert.Contains("not by the ticket", problem);
    }

    // ---- Rendering for the prompt ----

    [Fact]
    public void Nothing_outstanding_renders_nothing()
        => Assert.Null(TicketService.RenderOwnerNotes(Array.Empty<Comment>()));

    [Fact]
    public void The_rendered_block_carries_the_body_the_id_and_the_rule()
    {
        var rendered = TicketService.RenderOwnerNotes(new[]
        {
            new Comment { Id = 42, Author = AuthorKind.Human, Body = "Use the existing formatter.", Phase = TicketPhase.Refining },
        })!;

        Assert.Contains("Use the existing formatter.", rendered);
        Assert.Contains("42", rendered);                     // the id acknowledge_note needs
        Assert.Contains("acknowledge_note", rendered);
        Assert.Contains("cannot hand this ticket off", rendered);
    }

    // ---- The review gate's change request ----

    [Fact]
    public async Task Requesting_changes_writes_the_note_the_items_and_the_move_together()
    {
        var id = await Seed(TicketPhase.NeedsReview);

        await using (var ctx = NewCtx())
            await new TicketService(ctx).RequestChangesAsync(
                id, "The totals row rounds down; it should round half-up.",
                new[] { "Round the totals row half-up", "Add a test for the .5 case" },
                shortLoop: false);

        await using var ctx2 = NewCtx();
        var t = await new TicketService(ctx2).GetAsync(id);

        Assert.Equal(TicketPhase.NeedsImplementation, t!.Phase);
        Assert.Equal(2, t.WorkItems.Count);
        Assert.All(t.WorkItems, w => Assert.False(w.Done));
        // The note is a directive like any other: it gates the run that picks this up.
        Assert.Single(HandoffRequirements.UnansweredOwnerNotes(t));
    }

    [Fact]
    public async Task The_short_loop_returns_the_next_implementation_straight_to_review()
    {
        var id = await Seed(TicketPhase.NeedsReview);

        await using (var ctx = NewCtx())
            await new TicketService(ctx).RequestChangesAsync(
                id, "Just rename the column header to Net.", new[] { "Rename the column header" }, shortLoop: true);

        // The run answers the note, then hands off exactly as it always does — to
        // NeedsImplementationReview. The routing is the dashboard's decision, not the worker's.
        await using (var ctx = NewCtx())
        {
            var svc = new TicketService(ctx);
            var t0 = await svc.GetAsync(id);
            var note = HandoffRequirements.UnansweredOwnerNotes(t0!).Single();
            await svc.AcknowledgeNoteAsync(note.Id, "Renamed the column header to Net in the grid definition and updated its test.");
            await svc.TransitionAsync(id, TicketPhase.Implementing, null, TransitionActor.Worker);
        }

        await using var ctx2 = NewCtx();
        var t = await new TicketService(ctx2).TransitionAsync(
            id, TicketPhase.NeedsImplementationReview, null, TransitionActor.Worker);

        Assert.Equal(TicketPhase.NeedsReview, t!.Phase);
        // Spent on arrival: the next change request decides for itself.
        Assert.False(t.ShortLoopReview);
    }

    [Fact]
    public async Task Without_the_short_loop_the_work_goes_round_through_qa()
    {
        var id = await Seed(TicketPhase.NeedsReview);

        await using (var ctx = NewCtx())
            await new TicketService(ctx).RequestChangesAsync(
                id, "The totals row rounds down; it should round half-up.", null, shortLoop: false);

        await using (var ctx = NewCtx())
        {
            var svc = new TicketService(ctx);
            var t0 = await svc.GetAsync(id);
            var note = HandoffRequirements.UnansweredOwnerNotes(t0!).Single();
            await svc.AcknowledgeNoteAsync(note.Id, "Switched the totals row to away-from-zero rounding and covered the .5 case in tests.");
            await svc.TransitionAsync(id, TicketPhase.Implementing, null, TransitionActor.Worker);
        }

        await using var ctx2 = NewCtx();
        var t = await new TicketService(ctx2).TransitionAsync(
            id, TicketPhase.NeedsImplementationReview, null, TransitionActor.Worker);

        Assert.Equal(TicketPhase.NeedsQa, t!.Phase);
    }

    [Fact]
    public async Task A_change_request_can_raise_the_band_the_work_comes_back_in()
    {
        var id = await Seed(TicketPhase.NeedsReview);

        await using (var ctx = NewCtx())
            await new TicketService(ctx).RequestChangesAsync(
                id, "The totals row rounds down; customers are seeing it today.",
                new[] { "Round the totals row half-up" }, shortLoop: true,
                priority: TicketPriority.Critical);

        await using var ctx2 = NewCtx();
        var t = await new TicketService(ctx2).GetAsync(id);

        // The band has to be in place by the time the ticket is back in the queue, or the first
        // claim after the move reads the old one.
        Assert.Equal(TicketPhase.NeedsImplementation, t!.Phase);
        Assert.Equal(TicketPriority.Critical, t.Priority);
    }

    [Fact]
    public async Task A_change_request_that_says_nothing_about_priority_leaves_it_alone()
    {
        var id = await Seed(TicketPhase.NeedsReview);

        await using (var ctx = NewCtx())
        {
            var t0 = await ctx.Tickets.FindAsync(id);
            t0!.Priority = TicketPriority.High;
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = NewCtx())
            await new TicketService(ctx).RequestChangesAsync(
                id, "Rename the column header to Net.", null, shortLoop: false);

        await using var ctx2 = NewCtx();
        var t = await new TicketService(ctx2).GetAsync(id);

        // Not reset to Normal: a review with nothing to say about urgency must not quietly demote
        // a ticket somebody had already moved up.
        Assert.Equal(TicketPriority.High, t!.Priority);
    }

    [Fact]
    public async Task A_replan_goes_to_speculation_and_holds_the_new_plan_for_the_owner()
    {
        var id = await Seed(TicketPhase.NeedsReview);

        await using (var ctx = NewCtx())
            await new TicketService(ctx).RequestChangesAsync(
                id, "Courtney wants several sources saved at once. Decide (a) or (b) with me first.",
                new[] { "Save one mix to several sources" }, shortLoop: false, replan: true);

        await using (var ctx = NewCtx())
        {
            var t = await new TicketService(ctx).GetAsync(id);
            Assert.Equal(TicketPhase.NeedsSpeculation, t!.Phase);
            // Forced on even though the ticket was seeded without it: the point of a replan is
            // that the owner sees the plan before anything is built.
            Assert.True(t.RequiresSpeculationReview);
            // The note reaches speculation the same way it reaches implementation.
            Assert.Single(HandoffRequirements.UnansweredOwnerNotes(t));
            Assert.Single(t.WorkItems);
        }

        // Speculation answers the note and hands off; the forced gate holds it for approval.
        await using (var ctx = NewCtx())
        {
            var svc = new TicketService(ctx);
            await svc.TransitionAsync(id, TicketPhase.Speculating, null, TransitionActor.Worker);
            var note = HandoffRequirements.UnansweredOwnerNotes((await svc.GetAsync(id))!).Single();
            await svc.AcknowledgeNoteAsync(note.Id, "Planned the multi-source save; blocked the (a)/(b) choice as a question.");
        }
        await using var ctx2 = NewCtx();
        var after = await new TicketService(ctx2).TransitionAsync(id, TicketPhase.NeedsApproval, null, TransitionActor.Worker);
        Assert.Equal(TicketPhase.NeedsApproval, after!.Phase);
    }

    [Fact]
    public async Task A_replan_cannot_also_take_the_short_loop()
    {
        var id = await Seed(TicketPhase.NeedsReview);

        await using var ctx = NewCtx();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new TicketService(ctx).RequestChangesAsync(id, "Plan the new thing.", null, shortLoop: true, replan: true));
    }

    [Fact]
    public async Task Changes_cannot_be_requested_from_a_phase_that_is_not_at_a_review()
    {
        var id = await Seed(TicketPhase.Speculating);

        await using var ctx = NewCtx();
        await Assert.ThrowsAsync<InvalidTransitionException>(() =>
            new TicketService(ctx).RequestChangesAsync(id, "Change the thing please.", null, shortLoop: false));
    }
}
