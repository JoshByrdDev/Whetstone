using Whetstone.Api;
using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// The two things a phase must LEAVE BEHIND for the human, enforced rather than requested: the
/// refine phase's learning brief, and QA's captioned screenshots (or a stated reason there are
/// none). Both were skipped silently on a real ticket because nothing checked.
/// </summary>
public class HandoffRequirementTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<DashboardDbContext> _options;

    public HandoffRequirementTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _options = new DbContextOptionsBuilder<DashboardDbContext>().UseSqlite(_conn).Options;
        using var ctx = new DashboardDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _conn.Dispose();

    private DashboardDbContext NewCtx() => new(_options);

    /// <summary>
    /// A QaTesting ticket is seeded with one QA item already passed on this run, because the QA
    /// hand-off now demands a finished checklist as well as the screenshots. The shots tests below
    /// are about the shots; without this they would all fail on the checklist first and stop
    /// testing what they are named after. The checklist itself has its own tests further down.
    /// </summary>
    private async Task<int> Seed(TicketPhase phase, Action<Ticket>? setup = null)
    {
        await using var ctx = NewCtx();
        var t = new Ticket { ProjectKey = "alpha", Title = "t", Phase = phase };
        if (phase == TicketPhase.QaTesting)
            t.QaItems.Add(new QaItem { Text = "it works", Status = QaStatus.Passed, VerifiedAt = DateTime.UtcNow });
        setup?.Invoke(t);
        ctx.Tickets.Add(t);
        await ctx.SaveChangesAsync();
        return t.Id;
    }

    /// <summary>A rework bounce that happened <paramref name="at"/>, as the transition log records it.</summary>
    private async Task Bounced(int ticketId, DateTime at)
    {
        await using var ctx = NewCtx();
        ctx.PhaseTransitions.Add(new PhaseTransition
        {
            TicketId = ticketId, ProjectKey = "alpha",
            From = TicketPhase.Refining, To = TicketPhase.NeedsImplementation,
            Actor = TransitionActor.Worker, Backward = true, At = at,
        });
        await ctx.SaveChangesAsync();
    }

    /// <summary>
    /// The whole migration chain applies to an empty database. The waiver columns were added by
    /// hand (the dashboard was running and holding its own build output), so this is the check that
    /// the hand-written migration and the model snapshot actually agree with the entity.
    /// </summary>
    [Fact]
    public async Task Migrations_apply_and_create_the_waiver_columns()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        var options = new DbContextOptionsBuilder<DashboardDbContext>().UseSqlite(conn).Options;
        await using var ctx = new DashboardDbContext(options);

        await ctx.Database.MigrateAsync();

        var t = new Ticket
        {
            ProjectKey = "alpha", Title = "t", Phase = TicketPhase.QaTesting,
            QaShotsWaiver = "stored-procedure change with no UI surface",
            QaShotsWaiverAt = new DateTime(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc),
        };
        ctx.Tickets.Add(t);
        await ctx.SaveChangesAsync();

        var read = await ctx.Tickets.AsNoTracking().FirstAsync(x => x.Id == t.Id);
        Assert.Equal("stored-procedure change with no UI surface", read.QaShotsWaiver);
        Assert.NotNull(read.QaShotsWaiverAt);
    }

    // ---- The learning brief: required, no escape hatch ----

    [Fact]
    public async Task Refining_to_review_is_refused_without_a_learning_brief()
    {
        var id = await Seed(TicketPhase.Refining);

        await using var ctx = NewCtx();
        var ex = await Assert.ThrowsAsync<HandoffRequirementException>(() =>
            new TicketService(ctx).TransitionAsync(id, TicketPhase.NeedsReview, null, TransitionActor.Worker));

        Assert.Equal(TicketPhase.NeedsReview, ex.To);
        Assert.Contains("set_learning_brief", ex.Message);
        // Still a refused move, so every existing caller keeps handling it.
        Assert.IsAssignableFrom<InvalidTransitionException>(ex);
    }

    [Fact]
    public async Task Refining_to_review_succeeds_once_the_brief_is_written()
    {
        var id = await Seed(TicketPhase.Refining);

        await using var ctx = NewCtx();
        var svc = new TicketService(ctx);
        await svc.SetLearningBriefAsync(id, "## The area\nAdvisor types on ServiceProviders.");
        var t = await svc.TransitionAsync(id, TicketPhase.NeedsReview, null, TransitionActor.Worker);

        Assert.Equal(TicketPhase.NeedsReview, t!.Phase);
    }

    /// <summary>
    /// A brief written before the ticket went back for rework describes a version of the work that
    /// no longer exists — which is worse than none, because it reads as current.
    /// </summary>
    [Fact]
    public async Task A_brief_written_before_the_last_bounce_does_not_count()
    {
        var id = await Seed(TicketPhase.Refining, t =>
        {
            t.LearningBrief = "written on the previous pass";
            t.LearningBriefAt = new DateTime(2026, 8, 7, 10, 0, 0, DateTimeKind.Utc);
        });
        await Bounced(id, new DateTime(2026, 8, 7, 11, 0, 0, DateTimeKind.Utc));

        await using var ctx = NewCtx();
        var ex = await Assert.ThrowsAsync<HandoffRequirementException>(() =>
            new TicketService(ctx).TransitionAsync(id, TicketPhase.NeedsReview, null, TransitionActor.Worker));

        Assert.Contains("before it last went back for rework", ex.Message);
    }

    /// <summary>The rework routes are how a phase says "not finished" — they must never be gated.</summary>
    [Fact]
    public async Task Sending_a_ticket_back_for_rework_needs_no_brief()
    {
        var id = await Seed(TicketPhase.Refining);

        await using var ctx = NewCtx();
        var t = await new TicketService(ctx).TransitionAsync(id, TicketPhase.NeedsImplementation, null, TransitionActor.Worker);

        Assert.Equal(TicketPhase.NeedsImplementation, t!.Phase);
    }

    /// <summary>
    /// The requirement is aimed at the unattended pipeline. A person moving their own ticket by
    /// hand has already made the judgement this guard exists to force.
    /// </summary>
    [Fact]
    public async Task A_human_can_still_move_a_ticket_without_a_brief()
    {
        var id = await Seed(TicketPhase.Refining);

        await using var ctx = NewCtx();
        var t = await new TicketService(ctx).TransitionAsync(id, TicketPhase.NeedsReview, null, TransitionActor.Human);

        Assert.Equal(TicketPhase.NeedsReview, t!.Phase);
    }

    // ---- QA shots: required to be ANSWERED, not required to exist ----

    [Fact]
    public async Task Qa_cannot_hand_off_with_neither_shots_nor_a_reason()
    {
        var id = await Seed(TicketPhase.QaTesting);

        await using var ctx = NewCtx();
        var ex = await Assert.ThrowsAsync<HandoffRequirementException>(() =>
            new TicketService(ctx).TransitionAsync(id, TicketPhase.NeedsRefinement, null, TransitionActor.Worker));

        Assert.Contains("set_qa_shots", ex.Message);
    }

    [Fact]
    public async Task Shots_satisfy_the_qa_hand_off()
    {
        var id = await Seed(TicketPhase.QaTesting);

        await using var ctx = NewCtx();
        var svc = new TicketService(ctx);
        await svc.SetQaShotsAsync(id, new[] { ("0.png", "The Role dropdown now offers Enroller.") });
        var t = await svc.TransitionAsync(id, TicketPhase.NeedsRefinement, null, TransitionActor.Worker);

        Assert.Equal(TicketPhase.NeedsRefinement, t!.Phase);
    }

    /// <summary>
    /// The 8025/8026 case: a stored-procedure ticket with no screen to photograph. It is let
    /// through — but only once it has said so, in writing, on the ticket.
    /// </summary>
    [Fact]
    public async Task A_stated_reason_satisfies_the_qa_hand_off_for_work_with_nothing_to_show()
    {
        var id = await Seed(TicketPhase.QaTesting);

        await using var ctx = NewCtx();
        var svc = new TicketService(ctx);
        await svc.SetQaShotsAsync(id, Array.Empty<(string, string)>(),
            "stored-procedure change with no UI surface - verified from the returned rows");
        var t = await svc.TransitionAsync(id, TicketPhase.NeedsRefinement, null, TransitionActor.Worker);

        Assert.Equal(TicketPhase.NeedsRefinement, t!.Phase);
    }

    [Fact]
    public async Task A_reason_recorded_before_the_last_bounce_does_not_count()
    {
        var id = await Seed(TicketPhase.QaTesting, t =>
        {
            t.QaShotsWaiver = "backend only";
            t.QaShotsWaiverAt = new DateTime(2026, 8, 7, 10, 0, 0, DateTimeKind.Utc);
        });
        await Bounced(id, new DateTime(2026, 8, 7, 11, 0, 0, DateTimeKind.Utc));

        await using var ctx = NewCtx();
        var ex = await Assert.ThrowsAsync<HandoffRequirementException>(() =>
            new TicketService(ctx).TransitionAsync(id, TicketPhase.NeedsRefinement, null, TransitionActor.Worker));

        Assert.Contains("predates", ex.Message);
    }

    /// <summary>Failing QA is a report, not a hand-off — nothing to show is the whole point.</summary>
    [Fact]
    public async Task Failing_qa_back_to_implementation_needs_no_shots()
    {
        var id = await Seed(TicketPhase.QaTesting);

        await using var ctx = NewCtx();
        var t = await new TicketService(ctx).TransitionAsync(id, TicketPhase.NeedsImplementation, null, TransitionActor.Worker);

        Assert.Equal(TicketPhase.NeedsImplementation, t!.Phase);
    }

    /// <summary>Real shots always beat a reason, so attaching them must retire the reason.</summary>
    [Fact]
    public async Task Attaching_shots_clears_a_previously_recorded_reason()
    {
        var id = await Seed(TicketPhase.QaTesting);

        await using var ctx = NewCtx();
        var svc = new TicketService(ctx);
        await svc.SetQaShotsAsync(id, Array.Empty<(string, string)>(), "backend only");
        var t = await svc.SetQaShotsAsync(id, new[] { ("0.png", "It does have a screen after all.") });

        Assert.Null(t!.QaShotsWaiver);
        Assert.Null(t.QaShotsWaiverAt);
    }

    // ---- The QA checklist: finished, and finished against THIS branch ----

    /// <summary>Seed a QaTesting ticket carrying exactly the checklist described.</summary>
    private async Task<int> SeedWithChecklist(params (QaStatus Status, DateTime? VerifiedAt)[] items)
    {
        await using var ctx = NewCtx();
        var t = new Ticket { ProjectKey = "alpha", Title = "t", Phase = TicketPhase.QaTesting };
        var order = 0;
        foreach (var (status, verifiedAt) in items)
            t.QaItems.Add(new QaItem { Text = $"item {order}", Status = status, VerifiedAt = verifiedAt, Order = order++ });
        ctx.Tickets.Add(t);
        await ctx.SaveChangesAsync();

        // Every one of these tests is about a ticket that has finished its screenshots and is being
        // held up by the checklist alone, so the other requirement is satisfied up front.
        await using var svc = NewCtx();
        await new TicketService(svc).SetQaShotsAsync(t.Id, new[] { ("0.png", "The finished screen.") });
        return t.Id;
    }

    /// <summary>
    /// The stale-pass case. A checklist can be entirely green and still describe a build from
    /// several rework bounces ago; nothing downstream could tell that apart from a run that had just
    /// tested everything, which is how a regression reached a reviewer looking tested.
    /// </summary>
    [Fact]
    public async Task A_pass_recorded_before_the_last_bounce_does_not_let_qa_hand_off()
    {
        var id = await SeedWithChecklist((QaStatus.Passed, new DateTime(2026, 8, 27, 10, 0, 0, DateTimeKind.Utc)));
        await Bounced(id, new DateTime(2026, 8, 31, 22, 0, 0, DateTimeKind.Utc));

        await using var ctx = NewCtx();
        var ex = await Assert.ThrowsAsync<HandoffRequirementException>(() =>
            new TicketService(ctx).TransitionAsync(id, TicketPhase.NeedsRefinement, null, TransitionActor.Worker));

        Assert.Contains("went back for rework", ex.Message);
        Assert.Equal(TicketPhase.NeedsRefinement, ex.To);
    }

    [Fact]
    public async Task Re_testing_the_expired_item_clears_the_refusal()
    {
        var id = await SeedWithChecklist((QaStatus.Passed, new DateTime(2026, 8, 27, 10, 0, 0, DateTimeKind.Utc)));
        await Bounced(id, new DateTime(2026, 8, 31, 22, 0, 0, DateTimeKind.Utc));

        await using var ctx = NewCtx();
        var svc = new TicketService(ctx);
        var item = await ctx.QaItems.FirstAsync(q => q.TicketId == id);
        await svc.UpdateQaItemAsync(item.Id, null, QaStatus.Passed, null, null);

        var t = await svc.TransitionAsync(id, TicketPhase.NeedsRefinement, null, TransitionActor.Worker);
        Assert.Equal(TicketPhase.NeedsRefinement, t!.Phase);
    }

    /// <summary>A pass with no bounce behind it is not stale — only a bounce ages one out.</summary>
    [Fact]
    public async Task A_pass_on_a_ticket_that_never_bounced_is_current_however_old()
    {
        var id = await SeedWithChecklist((QaStatus.Passed, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));

        await using var ctx = NewCtx();
        var t = await new TicketService(ctx).TransitionAsync(id, TicketPhase.NeedsRefinement, null, TransitionActor.Worker);
        Assert.Equal(TicketPhase.NeedsRefinement, t!.Phase);
    }

    [Theory]
    [InlineData(QaStatus.Todo)]
    [InlineData(QaStatus.Failed)]
    [InlineData(QaStatus.Blocked)]
    public async Task An_unpassed_item_stops_the_clean_hand_off(QaStatus status)
    {
        var id = await SeedWithChecklist((QaStatus.Passed, DateTime.UtcNow), (status, null));

        await using var ctx = NewCtx();
        var ex = await Assert.ThrowsAsync<HandoffRequirementException>(() =>
            new TicketService(ctx).TransitionAsync(id, TicketPhase.NeedsRefinement, null, TransitionActor.Worker));

        Assert.Contains("not been taken to a Passed outcome", ex.Message);
    }

    /// <summary>The refusal names the items, because "something is unfinished" is not actionable.</summary>
    [Fact]
    public async Task The_refusal_names_the_items_to_go_back_to()
    {
        var id = await SeedWithChecklist((QaStatus.Passed, DateTime.UtcNow), (QaStatus.Todo, null));

        await using var ctx = NewCtx();
        var untested = await ctx.QaItems.FirstAsync(q => q.TicketId == id && q.Status == QaStatus.Todo);
        var ex = await Assert.ThrowsAsync<HandoffRequirementException>(() =>
            new TicketService(ctx).TransitionAsync(id, TicketPhase.NeedsRefinement, null, TransitionActor.Worker));

        Assert.Contains($"{untested.Id}", ex.Message);
    }

    /// <summary>
    /// A QA pass over an empty list proves nothing — which the skill has always said and nothing
    /// ever checked.
    /// </summary>
    [Fact]
    public async Task Qa_cannot_hand_off_a_ticket_with_no_checklist_at_all()
    {
        var id = await SeedWithChecklist();

        await using var ctx = NewCtx();
        var ex = await Assert.ThrowsAsync<HandoffRequirementException>(() =>
            new TicketService(ctx).TransitionAsync(id, TicketPhase.NeedsRefinement, null, TransitionActor.Worker));

        Assert.Contains("no QA checklist", ex.Message);
    }

    /// <summary>A person moving their own ticket by hand is not the failure any of this guards against.</summary>
    [Fact]
    public async Task A_human_may_still_move_a_ticket_with_an_unfinished_checklist()
    {
        var id = await SeedWithChecklist((QaStatus.Todo, null));

        await using var ctx = NewCtx();
        var t = await new TicketService(ctx).TransitionAsync(id, TicketPhase.NeedsRefinement, null, TransitionActor.Human);
        Assert.Equal(TicketPhase.NeedsRefinement, t!.Phase);
    }

    /// <summary>
    /// Failing back to implementation must never be gated on the checklist being finished — that is
    /// the route a run takes precisely BECAUSE it is not.
    /// </summary>
    [Fact]
    public async Task Failing_back_to_implementation_is_never_gated_on_the_checklist()
    {
        var id = await SeedWithChecklist((QaStatus.Failed, DateTime.UtcNow), (QaStatus.Todo, null));

        await using var ctx = NewCtx();
        var t = await new TicketService(ctx).TransitionAsync(id, TicketPhase.NeedsImplementation, null, TransitionActor.Worker);
        Assert.Equal(TicketPhase.NeedsImplementation, t!.Phase);
    }

    // ---- Recording an outcome dates it, and re-testing withdraws it ----

    [Fact]
    public async Task Recording_an_outcome_stamps_it_and_going_back_to_todo_withdraws_the_stamp()
    {
        var id = await SeedWithChecklist((QaStatus.Todo, null));

        await using var ctx = NewCtx();
        var svc = new TicketService(ctx);
        var item = await ctx.QaItems.FirstAsync(q => q.TicketId == id);

        var passed = await svc.UpdateQaItemAsync(item.Id, null, QaStatus.Passed, null, null);
        Assert.NotNull(passed!.VerifiedAt);

        var reopened = await svc.UpdateQaItemAsync(item.Id, null, QaStatus.Todo, null, null);
        Assert.Null(reopened!.VerifiedAt);
    }

    /// <summary>
    /// Picking an item back up is the retest, so it withdraws the verdict AND the claim about how
    /// the verdict was reached. Leaving either behind would let a run that died mid-retest still
    /// read as verified.
    /// </summary>
    [Fact]
    public async Task Starting_an_item_withdraws_its_verification()
    {
        var id = await SeedWithChecklist((QaStatus.Passed, DateTime.UtcNow));

        await using var ctx = NewCtx();
        var svc = new TicketService(ctx);
        var item = await ctx.QaItems.FirstAsync(q => q.TicketId == id);
        await svc.UpdateQaItemAsync(item.Id, null, QaStatus.Passed, "read the query plan", null, passedByInspection: true);

        var started = await svc.StartQaItemAsync(item.Id);

        Assert.Equal(QaStatus.Todo, started!.Status);
        Assert.Null(started.VerifiedAt);
        Assert.False(started.PassedByInspection);
        Assert.Equal("read the query plan", started.Note);   // the note survives, as it always has
    }

    /// <summary>
    /// A pass by inspection is a weaker claim, not a rejected one: it is recorded, it is displayed
    /// as its own thing, and it still lets the ticket through. Making it fatal would only buy runs
    /// that stop admitting it.
    /// </summary>
    [Fact]
    public async Task A_pass_by_inspection_is_recorded_and_still_hands_off()
    {
        var id = await SeedWithChecklist((QaStatus.Todo, null));

        await using var ctx = NewCtx();
        var svc = new TicketService(ctx);
        var item = await ctx.QaItems.FirstAsync(q => q.TicketId == id);
        var qi = await svc.UpdateQaItemAsync(item.Id, null, QaStatus.Passed, "import path nothing in the diff reaches",
            null, passedByInspection: true);
        Assert.True(qi!.PassedByInspection);

        var t = await svc.TransitionAsync(id, TicketPhase.NeedsRefinement, null, TransitionActor.Worker);
        Assert.Equal(TicketPhase.NeedsRefinement, t!.Phase);
    }

    /// <summary>The flag only distinguishes two kinds of PASS, so any other outcome drops it.</summary>
    [Fact]
    public async Task Failing_an_item_clears_the_inspection_flag()
    {
        var id = await SeedWithChecklist((QaStatus.Todo, null));

        await using var ctx = NewCtx();
        var svc = new TicketService(ctx);
        var item = await ctx.QaItems.FirstAsync(q => q.TicketId == id);
        await svc.UpdateQaItemAsync(item.Id, null, QaStatus.Passed, "by reading", null, passedByInspection: true);
        var failed = await svc.UpdateQaItemAsync(item.Id, null, QaStatus.Failed, "actually broken", null);

        Assert.False(failed!.PassedByInspection);
    }

    /// <summary>
    /// What the worker actually reads. `status` alone cannot answer "is this still true?", so the
    /// shape it gets on every get_ticket answers it directly.
    /// </summary>
    [Fact]
    public async Task Get_ticket_reports_an_expired_pass_as_unverified()
    {
        var id = await SeedWithChecklist(
            (QaStatus.Passed, new DateTime(2026, 8, 27, 10, 0, 0, DateTimeKind.Utc)),
            (QaStatus.Passed, new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc)));
        await Bounced(id, new DateTime(2026, 8, 31, 22, 0, 0, DateTimeKind.Utc));

        await using var ctx = NewCtx();
        var svc = new TicketService(ctx);
        var dto = TicketMapper.ToDetail((await svc.GetAsync(id))!, lastBackwardAt: await svc.LastBackwardAtAsync(id));

        Assert.False(dto.QaItems[0].Verified);   // passed before the bounce
        Assert.True(dto.QaItems[1].Verified);    // passed after it
        Assert.Equal("Passed", dto.QaItems[0].Status);   // the record itself is untouched
    }

    // ---- Code-review-only tickets are excused, not exempted by accident ----

    /// <summary>
    /// A ticket QA never ran cannot be asked for photographs of it running. Without this the flag
    /// would be self-defeating: every code-review-only ticket would reach the hand-off and be
    /// refused for missing screenshots of an app that was deliberately never started.
    /// </summary>
    [Fact]
    public async Task A_code_review_only_ticket_hands_off_with_no_shots_and_no_reason()
    {
        var id = await Seed(TicketPhase.QaTesting, t => t.SkipQaTesting = true);

        await using var ctx = NewCtx();
        var t = await new TicketService(ctx).TransitionAsync(id, TicketPhase.NeedsRefinement, null, TransitionActor.Worker);

        Assert.Equal(TicketPhase.NeedsRefinement, t!.Phase);
    }

    /// <summary>The excuse is per-ticket, not a hole in the rule — the flag off still refuses.</summary>
    [Fact]
    public async Task The_excuse_does_not_leak_to_tickets_without_the_flag()
    {
        var id = await Seed(TicketPhase.QaTesting, t => t.SkipQaTesting = false);

        await using var ctx = NewCtx();
        await Assert.ThrowsAsync<HandoffRequirementException>(() =>
            new TicketService(ctx).TransitionAsync(id, TicketPhase.NeedsRefinement, null, TransitionActor.Worker));
    }

    /// <summary>
    /// And the guide agrees with the enforcement. A requirement advertised to a worker but never
    /// applied is worse than none: it teaches the worker that the guide can be ignored.
    /// </summary>
    [Fact]
    public async Task The_guide_asks_a_code_review_only_ticket_for_nothing()
    {
        var id = await Seed(TicketPhase.QaTesting, t => t.SkipQaTesting = true);

        await using var ctx = NewCtx();
        var t = await new TicketService(ctx).GetAsync(id);
        var guide = TicketMapper.ToDetail(t!).NextPhaseGuide;

        Assert.Null(guide.Single(g => g.Phase == nameof(TicketPhase.NeedsRefinement)).Requirement);
    }

    [Fact]
    public async Task The_guide_still_asks_an_ordinary_ticket_for_shots()
    {
        var id = await Seed(TicketPhase.QaTesting);

        await using var ctx = NewCtx();
        var t = await new TicketService(ctx).GetAsync(id);
        var guide = TicketMapper.ToDetail(t!).NextPhaseGuide;

        Assert.Contains("QA shots", guide.Single(g => g.Phase == nameof(TicketPhase.NeedsRefinement)).Requirement);
    }

    // ---- Told in advance, not only on refusal ----

    [Fact]
    public async Task The_next_phase_guide_states_the_requirement_before_the_move_is_attempted()
    {
        var id = await Seed(TicketPhase.Refining);

        await using var ctx = NewCtx();
        var t = await new TicketService(ctx).GetAsync(id);
        var guide = TicketMapper.ToDetail(t!).NextPhaseGuide;

        var review = Assert.Single(guide, g => g.Phase == nameof(TicketPhase.NeedsReview));
        Assert.Contains("learning brief", review.Requirement);
        // Only the hand-offs that demand something carry one.
        Assert.Null(guide.Single(g => g.Phase == nameof(TicketPhase.NeedsImplementation)).Requirement);
    }
}
