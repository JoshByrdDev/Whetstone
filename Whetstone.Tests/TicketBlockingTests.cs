using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// Blocking as a two-way conversation: a worker stops the ticket with a question, a human answers by
/// unblocking it, and the run that picks it up is shown both halves. The state machine has always
/// allowed the move from anywhere (see <see cref="PhaseMachineTests"/>); these cover what makes it
/// usable — a reason that says something, a recommended way back, and the answer surviving the trip.
/// </summary>
public class TicketBlockingTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<DashboardDbContext> _options;

    public TicketBlockingTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _options = new DbContextOptionsBuilder<DashboardDbContext>().UseSqlite(_conn).Options;
        using var ctx = new DashboardDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _conn.Dispose();

    private DashboardDbContext NewCtx() => new(_options);

    private async Task<int> Seed(TicketPhase phase)
    {
        await using var ctx = NewCtx();
        var t = new Ticket { ProjectKey = "alpha", Title = "t", Phase = phase };
        ctx.Tickets.Add(t);
        await ctx.SaveChangesAsync();
        return t.Id;
    }

    private const string RealReason =
        "AC 3 says the export is CSV but the description says XLSX - which one does the button produce?";

    // ---- The reason is the interface, so it has to say something ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Blocked")]
    [InlineData("stuck")]
    [InlineData("unclear.")]
    [InlineData("see comment")]
    [InlineData("build broken")]   // long enough to read, too short to act on
    public async Task A_worker_cannot_block_a_ticket_without_saying_why(string? reason)
    {
        var id = await Seed(TicketPhase.Implementing);

        await using var ctx = NewCtx();
        var svc = new TicketService(ctx);

        var ex = await Assert.ThrowsAsync<HandoffRequirementException>(
            () => svc.TransitionAsync(id, TicketPhase.Blocked, reason, TransitionActor.Worker));

        // Refused as "the move is right, you are missing something" — the caller that reads this as
        // an illegal move goes looking for another phase, which is the one wrong response.
        Assert.Equal(TicketPhase.Blocked, ex.To);
        Assert.Contains(TicketPhase.Blocked, ex.Allowed);

        await using var check = NewCtx();
        Assert.Equal(TicketPhase.Implementing, (await check.Tickets.FindAsync(id))!.Phase);
    }

    [Fact]
    public async Task A_worker_blocks_freely_once_it_has_asked_a_real_question()
    {
        var id = await Seed(TicketPhase.Implementing);

        await using var ctx = NewCtx();
        var t = await new TicketService(ctx)
            .TransitionAsync(id, TicketPhase.Blocked, RealReason, TransitionActor.Worker);

        Assert.Equal(TicketPhase.Blocked, t!.Phase);
        Assert.Equal(RealReason, t.BlockedReason);
        Assert.Equal(TicketPhase.Implementing, t.PhaseBeforeBlock);
    }

    [Fact]
    public async Task Blocking_out_of_qa_is_never_refused_for_missing_shots()
    {
        // The escape hatch has to stay open for a run that cannot produce the artefacts its normal
        // hand-off demands — that is the situation it exists for.
        var id = await Seed(TicketPhase.QaTesting);

        await using var ctx = NewCtx();
        var svc = new TicketService(ctx);

        await Assert.ThrowsAsync<HandoffRequirementException>(
            () => svc.TransitionAsync(id, TicketPhase.NeedsRefinement, null, TransitionActor.Worker));

        var t = await svc.TransitionAsync(id, TicketPhase.Blocked,
            "The AC contradicts itself on what the grid shows, so there is no pass or fail to record.",
            TransitionActor.Worker);
        Assert.Equal(TicketPhase.Blocked, t!.Phase);
    }

    [Fact]
    public async Task A_person_blocking_their_own_ticket_is_not_held_to_the_reason()
    {
        // They are the one who would have read it. The board still shows something rather than blank.
        var id = await Seed(TicketPhase.NeedsQa);

        await using var ctx = NewCtx();
        var t = await new TicketService(ctx).TransitionAsync(id, TicketPhase.Blocked, null, TransitionActor.Human);

        Assert.Equal(TicketPhase.Blocked, t!.Phase);
        Assert.Equal("Blocked", t.BlockedReason);
    }

    // ---- The way back ----

    [Theory]
    [InlineData(TicketPhase.Speculating, TicketPhase.NeedsSpeculation)]
    [InlineData(TicketPhase.Implementing, TicketPhase.NeedsImplementation)]
    [InlineData(TicketPhase.QaTesting, TicketPhase.NeedsQa)]
    [InlineData(TicketPhase.Refining, TicketPhase.NeedsRefinement)]
    public void Work_blocked_mid_phase_resumes_from_that_phases_queue(TicketPhase blockedIn, TicketPhase expected)
        => Assert.Equal(expected, PhaseMachine.ResumeAfterBlock(blockedIn));

    [Theory]
    [InlineData(TicketPhase.NeedsApproval)]
    [InlineData(TicketPhase.NeedsReview)]
    [InlineData(TicketPhase.Backlog)]
    public void A_ticket_blocked_while_waiting_resumes_where_it_stood(TicketPhase blockedIn)
        => Assert.Equal(blockedIn, PhaseMachine.ResumeAfterBlock(blockedIn));

    [Fact]
    public void Nothing_is_recommended_when_there_is_nothing_to_infer_from()
    {
        Assert.Null(PhaseMachine.ResumeAfterBlock(null));
        // Blocked out of Done: there is no work to pick back up, so the human chooses.
        Assert.Null(PhaseMachine.ResumeAfterBlock(TicketPhase.Done));
    }

    /// <summary>
    /// Every resume recommendation has to be a move the ticket could actually make — checked on the
    /// track the phase belongs to, because that is the only track a ticket sitting in it can be on.
    /// A phase both tracks share must be legal on both, since either kind of ticket can block there.
    /// </summary>
    [Fact]
    public void Every_recommendation_is_a_legal_unblock()
    {
        foreach (var p in Enum.GetValues<TicketPhase>())
        {
            var to = PhaseMachine.ResumeAfterBlock(p);
            if (to is null) continue;

            var tracks = PhaseMachine.TrackOf(p) is { } own
                ? new[] { own }
                : new[] { TicketTrack.Delivery, TicketTrack.Review };

            foreach (var track in tracks)
                Assert.True(PhaseMachine.CanTransition(TicketPhase.Blocked, to.Value, track),
                    $"Blocked -> {to} (recommended after {p}) is not a legal move for a {track} ticket");
        }
    }

    // ---- The answer reaches the next run ----

    [Fact]
    public async Task The_question_and_its_answer_are_reported_to_the_run_that_picks_the_ticket_up()
    {
        var id = await Seed(TicketPhase.Implementing);

        await using var ctx = NewCtx();
        var svc = new TicketService(ctx);
        await svc.TransitionAsync(id, TicketPhase.Blocked, RealReason, TransitionActor.Worker);
        await svc.TransitionAsync(id, TicketPhase.NeedsImplementation, "XLSX wins - the AC is stale.", TransitionActor.Human);

        var episode = await svc.LastBlockEpisodeAsync(id);

        Assert.NotNull(episode);
        Assert.Equal(TicketPhase.Implementing, episode!.BlockedIn);
        Assert.Equal(RealReason, episode.Reason);
        Assert.Equal("XLSX wins - the AC is stale.", episode.Answer);
        Assert.Equal(TicketPhase.NeedsImplementation, episode.ResumedInto);

        var rendered = TicketService.RenderBlockEpisode(episode);
        Assert.Contains("XLSX wins", rendered);
        Assert.Contains(RealReason, rendered);
    }

    [Fact]
    public async Task An_unblock_with_no_note_still_reports_the_question()
    {
        var id = await Seed(TicketPhase.Refining);

        await using var ctx = NewCtx();
        var svc = new TicketService(ctx);
        await svc.TransitionAsync(id, TicketPhase.Blocked, RealReason, TransitionActor.Worker);
        await svc.TransitionAsync(id, TicketPhase.NeedsRefinement, "   ", TransitionActor.Human);

        var episode = await svc.LastBlockEpisodeAsync(id);

        Assert.NotNull(episode);
        Assert.Null(episode!.Answer);
        // The worker is told the note is missing rather than left to infer it from silence.
        Assert.Contains("did not leave a note", TicketService.RenderBlockEpisode(episode));
    }

    [Fact]
    public async Task A_ticket_that_is_still_blocked_reports_nothing()
    {
        var id = await Seed(TicketPhase.Implementing);

        await using var ctx = NewCtx();
        var svc = new TicketService(ctx);
        await svc.TransitionAsync(id, TicketPhase.Blocked, RealReason, TransitionActor.Worker);

        Assert.Null(await svc.LastBlockEpisodeAsync(id));
        Assert.Null(TicketService.RenderBlockEpisode(null));
    }

    [Fact]
    public async Task A_never_blocked_ticket_reports_nothing()
    {
        var id = await Seed(TicketPhase.Implementing);

        await using var ctx = NewCtx();
        Assert.Null(await new TicketService(ctx).LastBlockEpisodeAsync(id));
    }

    [Fact]
    public async Task The_answer_survives_the_claim_that_starts_the_next_run()
    {
        var id = await Seed(TicketPhase.Implementing);

        await using var ctx = NewCtx();
        var svc = new TicketService(ctx);
        await svc.TransitionAsync(id, TicketPhase.Blocked, RealReason, TransitionActor.Worker);
        await svc.TransitionAsync(id, TicketPhase.NeedsImplementation, "XLSX.", TransitionActor.Human);

        // What the heartbeat does to lock the ticket. It is not the phase finishing, so the answer is
        // still owed to the run that is about to read it.
        await svc.TransitionAsync(id, TicketPhase.Implementing, "claimed by heartbeat");

        Assert.NotNull(await svc.LastBlockEpisodeAsync(id));
    }

    [Fact]
    public async Task The_answer_stops_being_reported_once_a_phase_has_acted_on_it()
    {
        var id = await Seed(TicketPhase.Implementing);

        await using var ctx = NewCtx();
        var svc = new TicketService(ctx);
        await svc.TransitionAsync(id, TicketPhase.Blocked, RealReason, TransitionActor.Worker);
        await svc.TransitionAsync(id, TicketPhase.NeedsImplementation, "XLSX.", TransitionActor.Human);
        await svc.TransitionAsync(id, TicketPhase.Implementing, "claimed by heartbeat");
        // The implement phase finished and handed off: the answer has been used, and repeating it to
        // QA and refinement for the rest of the ticket's life would be noise they cannot act on.
        await svc.TransitionAsync(id, TicketPhase.NeedsImplementationReview, null);

        Assert.Null(await svc.LastBlockEpisodeAsync(id));
    }

    [Fact]
    public async Task Only_the_most_recent_block_is_reported()
    {
        var id = await Seed(TicketPhase.Implementing);

        await using var ctx = NewCtx();
        var svc = new TicketService(ctx);
        await svc.TransitionAsync(id, TicketPhase.Blocked, "First question, since settled and stale.", TransitionActor.Worker);
        await svc.TransitionAsync(id, TicketPhase.NeedsImplementation, "answer one", TransitionActor.Human);
        await svc.TransitionAsync(id, TicketPhase.Blocked, RealReason, TransitionActor.Worker);
        await svc.TransitionAsync(id, TicketPhase.NeedsImplementation, "answer two", TransitionActor.Human);

        var episode = await svc.LastBlockEpisodeAsync(id);

        Assert.Equal(RealReason, episode!.Reason);
        Assert.Equal("answer two", episode.Answer);
    }

    // ---- Unblocking gives the ticket a fresh set of attempts ----

    [Fact]
    public async Task An_unblocked_ticket_is_not_still_carrying_its_block()
    {
        var id = await Seed(TicketPhase.QaTesting);

        await using var ctx = NewCtx();
        var svc = new TicketService(ctx);
        await svc.TransitionAsync(id, TicketPhase.Blocked, RealReason, TransitionActor.Worker);
        var t = await svc.TransitionAsync(id, TicketPhase.NeedsQa, "answered", TransitionActor.Human);

        // BlockedReason describes the present, and the present is no longer blocked. The history of
        // the episode lives in the transition log (see LastBlockEpisodeAsync), which is what lets
        // this be cleared without losing the conversation.
        Assert.Null(t!.BlockedReason);
        Assert.Null(t.PhaseBeforeBlock);
    }

    // ---- Asking in questions rather than in prose ----
    //
    // The shape is additive by construction: BlockedReason is RENDERED from the questions, so every
    // reader downstream of a block — the board, the comment trail, the prompt the next run gets —
    // keeps working off one string and never learns that questions exist. These tests exist mostly
    // to hold that property down, because the moment the two can disagree the structure stops being
    // free and starts being a second source of truth.

    private static BlockPayload RealQuestions => new(
        "I cannot plan this without guessing at the ingest channel.",
        [
            new BlockQuestion(
                "Which ingest channel - a new endpoint, or a file processor?",
                Why: "The description says \"via API or other methods\", which is two builds.",
                Options:
                [
                    new BlockOption("New authenticated endpoint on Alpha.Web"),
                    new BlockOption("New IProcessingService in Alpha.FileOps", "Follows ContributionsProcessor.", Recommended: true),
                ]),
        ],
        ["The parent story or epic"]);

    [Fact]
    public async Task A_worker_can_block_with_questions_instead_of_a_reason()
    {
        var id = await Seed(TicketPhase.Speculating);

        await using var ctx = NewCtx();
        var t = await new TicketService(ctx).TransitionAsync(
            id, TicketPhase.Blocked, RealQuestions.Preamble, TransitionActor.Worker, RealQuestions);

        Assert.Equal(TicketPhase.Blocked, t!.Phase);
        Assert.NotNull(t.BlockedQuestionsJson);

        // The prose is not a summary of the questions - it is all of them, so a reader who never
        // sees the UI loses nothing. The preamble, the question, its evidence, both options, the
        // lettering, the recommendation and the ask all have to survive the flattening.
        Assert.Contains("cannot plan this without guessing", t.BlockedReason);
        Assert.Contains("Which ingest channel", t.BlockedReason);
        Assert.Contains("two builds", t.BlockedReason);
        Assert.Contains("**(a)** New authenticated endpoint", t.BlockedReason);
        Assert.Contains("**(b)** New IProcessingService", t.BlockedReason);
        Assert.Contains("*(recommended)*", t.BlockedReason);
        Assert.Contains("The parent story or epic", t.BlockedReason);
    }

    [Theory]
    // Nothing to answer.
    [InlineData("")]
    // A well-shaped list of nothing: the free-text floor applies per question, or the structure
    // becomes a way to launder exactly the block that was refused before it.
    [InlineData("huh")]
    [InlineData("why?")]
    public async Task A_question_list_is_held_to_the_same_bar_as_a_reason(string question)
    {
        var id = await Seed(TicketPhase.Speculating);
        var payload = new BlockPayload(null,
            question.Length == 0 ? [] : [new BlockQuestion(question)]);

        await using var ctx = NewCtx();
        var ex = await Assert.ThrowsAsync<HandoffRequirementException>(
            () => new TicketService(ctx).TransitionAsync(id, TicketPhase.Blocked, null, TransitionActor.Worker, payload));

        Assert.Equal(TicketPhase.Blocked, ex.To);

        await using var check = NewCtx();
        Assert.Equal(TicketPhase.Speculating, (await check.Tickets.FindAsync(id))!.Phase);
    }

    [Fact]
    public async Task A_question_that_runs_to_a_paragraph_is_sent_back_to_its_why()
    {
        // The one rule the whole feature rests on. A 4,000-character block that arrives as a single
        // "question" is the wall of text again, wearing the shape that was supposed to replace it.
        var id = await Seed(TicketPhase.Speculating);
        var payload = new BlockPayload(null, [new BlockQuestion(new string('x', 40) + " " + new string('y', 200))]);

        await using var ctx = NewCtx();
        var ex = await Assert.ThrowsAsync<HandoffRequirementException>(
            () => new TicketService(ctx).TransitionAsync(id, TicketPhase.Blocked, null, TransitionActor.Worker, payload));

        Assert.Contains("'why'", ex.Message);
    }

    [Fact]
    public async Task Two_recommendations_on_one_question_is_no_recommendation()
    {
        // An unanswered question falls back to the recommended option. Two of them and there is
        // nothing to fall back to, which is the case the default was for.
        var id = await Seed(TicketPhase.Speculating);
        var payload = new BlockPayload(null, [new BlockQuestion(
            "Which enum groups the imported fees?",
            Options: [new BlockOption("FeeCategoryEnum", Recommended: true), new BlockOption("FeeTypeEnum", Recommended: true)])]);

        await using var ctx = NewCtx();
        await Assert.ThrowsAsync<HandoffRequirementException>(
            () => new TicketService(ctx).TransitionAsync(id, TicketPhase.Blocked, null, TransitionActor.Worker, payload));
    }

    [Fact]
    public async Task An_open_question_needs_no_options()
    {
        // Three invented choices are worse than a text box. The bar is on the question, not the list.
        var id = await Seed(TicketPhase.Speculating);
        var payload = new BlockPayload(null, [new BlockQuestion("Which third-party system is sending this data?")]);

        await using var ctx = NewCtx();
        var t = await new TicketService(ctx).TransitionAsync(id, TicketPhase.Blocked, null, TransitionActor.Worker, payload);

        Assert.Equal(TicketPhase.Blocked, t!.Phase);
    }

    [Fact]
    public async Task The_questions_reach_the_next_run_even_though_the_ticket_stops_carrying_them()
    {
        // BlockedReason and BlockedQuestionsJson are both cleared on unblock, so the transition log
        // is the only surviving copy. It has to hold the RENDERED questions rather than the bare
        // preamble it was passed, or the run that picks the ticket up is quoted one sentence of a
        // block that asked four things.
        var id = await Seed(TicketPhase.Speculating);

        await using var ctx = NewCtx();
        var svc = new TicketService(ctx);
        await svc.TransitionAsync(id, TicketPhase.Blocked, RealQuestions.Preamble, TransitionActor.Worker, RealQuestions);
        var t = await svc.TransitionAsync(id, TicketPhase.NeedsSpeculation,
            "**1. Which ingest channel?**\n(b) New IProcessingService in Alpha.FileOps", TransitionActor.Human);

        Assert.Null(t!.BlockedReason);
        Assert.Null(t.BlockedQuestionsJson);

        var episode = await svc.LastBlockEpisodeAsync(id);
        Assert.Contains("Which ingest channel", episode!.Reason);
        Assert.Contains("**(b)** New IProcessingService", episode.Reason);
        Assert.Contains("(b) New IProcessingService", episode.Answer);

        var rendered = TicketService.RenderBlockEpisode(episode);
        Assert.Contains("Which ingest channel", rendered);
        Assert.Contains("What they said", rendered);
    }

    [Fact]
    public void A_payload_that_will_not_parse_costs_nothing_but_the_clicking()
    {
        // The drawer degrades to the prose rather than taking the ticket down with it.
        Assert.Null(BlockQuestions.Parse("{ not json"));
        Assert.Null(BlockQuestions.Parse(null));
        Assert.Null(BlockQuestions.Parse("""{"questions":[]}"""));

        var round = BlockQuestions.Parse(BlockQuestions.Serialize(RealQuestions));
        Assert.Equal("Which ingest channel - a new endpoint, or a file processor?", round!.Questions[0].Question);
        Assert.True(round.Questions[0].Options![1].Recommended);
    }
}
