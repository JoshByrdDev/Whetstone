using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// The Review track: its phase path, the isolation between the two tracks, the claim ordering that
/// isolation makes possible, and the hand-off rule that differs most from the delivery pipeline.
/// </summary>
public class ReviewTrackTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<DashboardDbContext> _options;

    public ReviewTrackTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _options = new DbContextOptionsBuilder<DashboardDbContext>().UseSqlite(_conn).Options;
        using var ctx = new DashboardDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _conn.Dispose();

    private DashboardDbContext NewCtx() => new(_options);
    private static bool AllSkillsExist(string project, string skill) => true;

    // ---- The path -----------------------------------------------------------

    [Theory]
    [InlineData(TicketPhase.Backlog, TicketPhase.NeedsPeerReview)]
    [InlineData(TicketPhase.NeedsPeerReview, TicketPhase.PeerReviewing)]
    [InlineData(TicketPhase.PeerReviewing, TicketPhase.NeedsReview)]
    [InlineData(TicketPhase.NeedsReview, TicketPhase.NeedsPeerReview)]   // they pushed - look again
    [InlineData(TicketPhase.NeedsReview, TicketPhase.Done)]
    public void Review_track_path_is_legal(TicketPhase from, TicketPhase to)
        => Assert.True(PhaseMachine.CanTransition(from, to, TicketTrack.Review));

    [Fact]
    public void Review_pass_has_no_backward_edge_of_its_own()
    {
        // Findings ARE the deliverable, so a review that turned up defects still hands off forward.
        // Blocked stays reachable, for a branch that genuinely cannot be judged.
        var next = PhaseMachine.NextPhases(TicketPhase.PeerReviewing, TicketTrack.Review).ToList();
        Assert.Equal(new[] { TicketPhase.NeedsReview, TicketPhase.Blocked }, next);
    }

    // ---- The two tracks cannot reach each other -----------------------------

    [Theory]
    [InlineData(TicketPhase.Backlog, TicketPhase.NeedsPeerReview)]
    [InlineData(TicketPhase.NeedsReview, TicketPhase.NeedsPeerReview)]
    public void Delivery_ticket_cannot_step_onto_the_review_track(TicketPhase from, TicketPhase to)
        => Assert.False(PhaseMachine.CanTransition(from, to, TicketTrack.Delivery));

    [Theory]
    [InlineData(TicketPhase.Backlog, TicketPhase.NeedsSpeculation)]
    [InlineData(TicketPhase.NeedsReview, TicketPhase.NeedsImplementation)]
    [InlineData(TicketPhase.NeedsReview, TicketPhase.NeedsRefinement)]
    public void Review_ticket_cannot_step_onto_the_delivery_track(TicketPhase from, TicketPhase to)
        => Assert.False(PhaseMachine.CanTransition(from, to, TicketTrack.Review));

    [Fact]
    public void Review_ticket_cannot_be_sent_back_to_speculation()
        // The replan escape is reachable from any in-progress DELIVERY phase. A review has no plan
        // to redo, and the track filter is what keeps that escape off it.
        => Assert.False(PhaseMachine.CanTransition(
            TicketPhase.PeerReviewing, TicketPhase.NeedsSpeculation, TicketTrack.Review));

    [Fact]
    public void NextPhases_and_CanTransition_agree_on_both_tracks()
    {
        foreach (var track in new[] { TicketTrack.Delivery, TicketTrack.Review })
            foreach (var from in Enum.GetValues<TicketPhase>())
                foreach (var to in PhaseMachine.NextPhases(from, track))
                    Assert.True(PhaseMachine.CanTransition(from, to, track),
                        $"{from} -> {to} listed but not allowed for a {track} ticket");
    }

    [Fact]
    public void Every_phase_a_review_ticket_can_reach_is_on_its_track()
    {
        foreach (var from in Enum.GetValues<TicketPhase>())
            foreach (var to in PhaseMachine.NextPhases(from, TicketTrack.Review))
                Assert.True(PhaseMachine.IsOnTrack(to, TicketTrack.Review),
                    $"{from} -> {to} takes a review ticket off its own track");
    }

    // ---- Claim wiring -------------------------------------------------------

    [Fact]
    public void Review_pair_claims_and_dispatches_its_own_skill()
    {
        Assert.Equal(TicketPhase.PeerReviewing, PhaseMachine.ClaimTarget(TicketPhase.NeedsPeerReview));
        Assert.Equal("review-pr", PhaseMachine.SkillFor(TicketPhase.PeerReviewing));
        Assert.True(PhaseMachine.IsActionable(TicketPhase.NeedsPeerReview));
        Assert.True(PhaseMachine.IsInProgress(TicketPhase.PeerReviewing));
        Assert.Equal(TicketPhase.NeedsPeerReview, PhaseMachine.ResumeAfterBlock(TicketPhase.PeerReviewing));
    }

    [Fact]
    public void Review_skill_is_optional_so_existing_projects_stay_scaffolded()
    {
        // Adding it to RequiredSkills would mark every project without the file unscaffolded.
        Assert.DoesNotContain("review-pr", PhaseMachine.RequiredSkills);
        Assert.Contains("review-pr", PhaseMachine.OptionalSkills);
        Assert.Contains("review-pr", PhaseMachine.AllSkills);
    }

    [Fact]
    public void Review_working_phases_rank_alongside_implementation_on_purpose()
    {
        // The tie is what makes the claim's track tiebreak reachable at all - see ProgressRank.
        Assert.Equal(PhaseMachine.ProgressRank(TicketPhase.NeedsImplementation),
                     PhaseMachine.ProgressRank(TicketPhase.NeedsPeerReview));
        Assert.Equal(PhaseMachine.ProgressRank(TicketPhase.Implementing),
                     PhaseMachine.ProgressRank(TicketPhase.PeerReviewing));
    }

    // ---- Claim ordering: priority, then progress, then delivery -------------

    private static async Task<Ticket> Seed(DashboardDbContext ctx, TicketPhase phase, TicketTrack track,
        TicketPriority priority = TicketPriority.Normal, string? branch = null)
    {
        var t = new Ticket
        {
            ProjectKey = "alpha", Title = $"{track}:{phase}", Phase = phase,
            Track = track, Priority = priority,
            CreatedAt = DateTime.UtcNow.AddHours(-1), UpdatedAt = DateTime.UtcNow.AddHours(-1),
        };
        if (track == TicketTrack.Review)
            t.ReviewTarget = new ReviewTarget { SourceBranch = branch ?? "someone/their-branch" };
        ctx.Tickets.Add(t);
        await ctx.SaveChangesAsync();
        return t;
    }

    [Fact]
    public async Task Delivery_wins_when_priority_and_progress_tie()
    {
        int reviewId, deliveryId;
        await using (var ctx = NewCtx())
        {
            // Same band, same rank (4). Review seeded FIRST so an ordering that ignored the track
            // would hand back the review ticket on the older-updated tiebreak.
            reviewId = (await Seed(ctx, TicketPhase.NeedsPeerReview, TicketTrack.Review)).Id;
            deliveryId = (await Seed(ctx, TicketPhase.NeedsImplementation, TicketTrack.Delivery)).Id;
        }

        await using var svcCtx = NewCtx();
        var claim = await new TicketService(svcCtx).ClaimNextAsync(null, AllSkillsExist);

        Assert.NotNull(claim);
        Assert.Equal(deliveryId, claim!.Ticket.Id);
        Assert.NotEqual(reviewId, claim.Ticket.Id);
    }

    [Fact]
    public async Task Priority_still_outranks_the_track()
    {
        int reviewId;
        await using (var ctx = NewCtx())
        {
            reviewId = (await Seed(ctx, TicketPhase.NeedsPeerReview, TicketTrack.Review, TicketPriority.High)).Id;
            await Seed(ctx, TicketPhase.NeedsImplementation, TicketTrack.Delivery, TicketPriority.Normal);
        }

        await using var svcCtx = NewCtx();
        var claim = await new TicketService(svcCtx).ClaimNextAsync(null, AllSkillsExist);

        Assert.NotNull(claim);
        Assert.Equal(reviewId, claim!.Ticket.Id);
        Assert.Equal("review-pr", claim.Skill);
    }

    [Fact]
    public async Task Progress_still_outranks_the_track()
    {
        // A review already under way beats a delivery ticket that has not started: the
        // finish-what-you-started rule is above the track, exactly as it is above age.
        int reviewId;
        await using (var ctx = NewCtx())
        {
            reviewId = (await Seed(ctx, TicketPhase.PeerReviewing, TicketTrack.Review)).Id;   // rank 5
            await Seed(ctx, TicketPhase.NeedsImplementation, TicketTrack.Delivery);            // rank 4
        }

        await using var svcCtx = NewCtx();
        var claim = await new TicketService(svcCtx).ClaimNextAsync(null, AllSkillsExist);

        Assert.NotNull(claim);
        Assert.Equal(reviewId, claim!.Ticket.Id);
    }

    // ---- The hand-off -------------------------------------------------------

    private static Ticket ReviewTicketAt(TicketPhase phase) => new()
    {
        Id = 1, ProjectKey = "alpha", Phase = phase, Track = TicketTrack.Review,
        ReviewTarget = new ReviewTarget { SourceBranch = "someone/their-branch" },
    };

    [Fact]
    public void Review_handoff_is_refused_without_a_verdict()
    {
        var t = ReviewTicketAt(TicketPhase.PeerReviewing);
        t.SkipQaTesting = true;   // isolate the verdict rule from the checklist one

        var unmet = HandoffRequirements.Unmet(t, TicketPhase.PeerReviewing, TicketPhase.NeedsReview, null);

        Assert.NotNull(unmet);
        Assert.Contains("no verdict", unmet!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_code_review_only_pass_needs_nothing_but_its_verdict()
    {
        var t = ReviewTicketAt(TicketPhase.PeerReviewing);
        t.SkipQaTesting = true;
        t.ReviewTarget!.Verdict = ReviewVerdict.Approve;
        t.ReviewTarget.VerdictAt = DateTime.UtcNow;

        Assert.Null(HandoffRequirements.Unmet(t, TicketPhase.PeerReviewing, TicketPhase.NeedsReview, null));
    }

    [Fact]
    public void A_failed_check_does_not_block_the_review_handoff()
    {
        // THE rule that separates this track from delivery QA: a defect found in someone else's
        // branch is the finished product of the pass, not a reason to hold it.
        var t = ReviewTicketAt(TicketPhase.PeerReviewing);
        t.ReviewTarget!.Verdict = ReviewVerdict.RequestChanges;
        t.ReviewTarget.VerdictAt = DateTime.UtcNow;
        t.QaItems.Add(new QaItem { Id = 1, Text = "Export a plan with no participants",
            Status = QaStatus.Failed, Note = "500 from the export endpoint", VerifiedAt = DateTime.UtcNow });
        t.QaShots.Add(new QaShot { Order = 0, Caption = "The 500", ImageFile = "0.png" });

        Assert.Null(HandoffRequirements.Unmet(t, TicketPhase.PeerReviewing, TicketPhase.NeedsReview, null));
    }

    [Fact]
    public void An_untested_check_does_block_the_review_handoff()
    {
        var t = ReviewTicketAt(TicketPhase.PeerReviewing);
        t.ReviewTarget!.Verdict = ReviewVerdict.Approve;
        t.ReviewTarget.VerdictAt = DateTime.UtcNow;
        t.QaItems.Add(new QaItem { Id = 7, Text = "Never looked at this one", Status = QaStatus.Todo });
        t.QaShots.Add(new QaShot { Order = 0, Caption = "x", ImageFile = "0.png" });

        var unmet = HandoffRequirements.Unmet(t, TicketPhase.PeerReviewing, TicketPhase.NeedsReview, null);

        Assert.NotNull(unmet);
        Assert.Contains("7", unmet!);
    }

    [Fact]
    public void A_verdict_from_before_the_last_push_has_expired()
    {
        var bounced = DateTime.UtcNow.AddMinutes(-5);
        var t = ReviewTicketAt(TicketPhase.PeerReviewing);
        t.SkipQaTesting = true;
        t.ReviewTarget!.Verdict = ReviewVerdict.Approve;
        t.ReviewTarget.VerdictAt = bounced.AddMinutes(-30);

        var unmet = HandoffRequirements.Unmet(t, TicketPhase.PeerReviewing, TicketPhase.NeedsReview, bounced);

        Assert.NotNull(unmet);
        Assert.Contains("before it last went back", unmet!, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Going round again --------------------------------------------------

    [Fact]
    public async Task Sending_the_ticket_round_again_clears_the_submitted_stamp()
    {
        // Null means "waiting on us", set means "waiting on them" - and a fresh pass is ours again.
        int id;
        await using (var ctx = NewCtx())
        {
            var t = await Seed(ctx, TicketPhase.NeedsReview, TicketTrack.Review);
            t.ReviewTarget!.SubmittedAt = DateTime.UtcNow.AddHours(-2);
            await ctx.SaveChangesAsync();
            id = t.Id;
        }

        await using var svcCtx = NewCtx();
        var svc = new TicketService(svcCtx);
        await svc.TransitionAsync(id, TicketPhase.NeedsPeerReview, "they pushed a fix");

        var target = await svc.GetReviewTargetAsync(id);
        Assert.NotNull(target);
        Assert.Null(target!.SubmittedAt);
    }

    [Fact]
    public async Task Submitting_marks_the_pending_comments_and_leaves_dismissed_ones_alone()
    {
        int id, keptId, dismissedId;
        await using (var ctx = NewCtx())
        {
            var t = await Seed(ctx, TicketPhase.NeedsReview, TicketTrack.Review);
            id = t.Id;
            var kept = new ReviewComment { TicketId = id, Body = "this null-refs", Severity = ReviewSeverity.Blocking };
            var dismissed = new ReviewComment { TicketId = id, Body = "nope", Status = ReviewCommentStatus.Dismissed, Order = 1 };
            ctx.ReviewComments.AddRange(kept, dismissed);
            await ctx.SaveChangesAsync();
            keptId = kept.Id;
            dismissedId = dismissed.Id;
        }

        await using var svcCtx = NewCtx();
        var svc = new TicketService(svcCtx);
        var target = await svc.MarkReviewSubmittedAsync(id);

        Assert.NotNull(target!.SubmittedAt);
        await using var readCtx = NewCtx();
        Assert.Equal(ReviewCommentStatus.Submitted, (await readCtx.ReviewComments.FindAsync(keptId))!.Status);
        Assert.Equal(ReviewCommentStatus.Dismissed, (await readCtx.ReviewComments.FindAsync(dismissedId))!.Status);
    }

    // ---- Citing the shot that shows a finding -------------------------------

    /// <summary>
    /// The ordering this whole affordance exists for: a run stages a comment the moment a check
    /// fails, and only later attaches the shots in one call. If the slot could only be set at
    /// creation, the citation would be unreachable for every comment written the way the phase
    /// asks for - which is how ticket 57 ended up with six drive-test findings and no screenshots
    /// beside any of them.
    /// </summary>
    [Fact]
    public async Task A_comment_can_be_pointed_at_a_shot_that_did_not_exist_when_it_was_written()
    {
        int id, commentId;
        await using (var ctx = NewCtx())
        {
            var t = await Seed(ctx, TicketPhase.PeerReviewing, TicketTrack.Review);
            id = t.Id;
            var c = new ReviewComment { TicketId = id, Body = "the save 400s", Severity = ReviewSeverity.Blocking };
            ctx.ReviewComments.Add(c);
            await ctx.SaveChangesAsync();
            commentId = c.Id;
        }

        await using var svcCtx = NewCtx();
        var svc = new TicketService(svcCtx);
        Assert.Null((await svcCtx.ReviewComments.FindAsync(commentId))!.QaShotOrder);

        await svc.UpdateReviewCommentAsync(commentId, null, null, null, qaShotOrder: 2);

        await using var readCtx = NewCtx();
        Assert.Equal(2, (await readCtx.ReviewComments.FindAsync(commentId))!.QaShotOrder);
    }

    /// <summary>
    /// Slot 0 is a real citation and the commonest one - the first shot of the set. It has to
    /// survive the "did they pass anything?" check that a plain null-test on an int would fail.
    /// </summary>
    [Fact]
    public async Task Citing_the_first_slot_is_not_read_as_citing_nothing()
    {
        int commentId;
        await using (var ctx = NewCtx())
        {
            var t = await Seed(ctx, TicketPhase.PeerReviewing, TicketTrack.Review);
            var c = new ReviewComment { TicketId = t.Id, Body = "the portal never boots" };
            ctx.ReviewComments.Add(c);
            await ctx.SaveChangesAsync();
            commentId = c.Id;
        }

        await using (var svcCtx = NewCtx())
            await new TicketService(svcCtx).UpdateReviewCommentAsync(commentId, null, null, null, qaShotOrder: 0);

        await using var readCtx = NewCtx();
        Assert.Equal(0, (await readCtx.ReviewComments.FindAsync(commentId))!.QaShotOrder);
    }

    /// <summary>
    /// Editing the wording of a cited comment must not quietly drop its evidence: the everyday
    /// human action in the drawer is a reword, and it sends no slot at all.
    /// </summary>
    [Fact]
    public async Task Editing_a_cited_comment_leaves_the_citation_alone()
    {
        int commentId;
        await using (var ctx = NewCtx())
        {
            var t = await Seed(ctx, TicketPhase.NeedsReview, TicketTrack.Review);
            var c = new ReviewComment { TicketId = t.Id, Body = "phone renders twice", QaShotOrder = 3 };
            ctx.ReviewComments.Add(c);
            await ctx.SaveChangesAsync();
            commentId = c.Id;
        }

        await using (var svcCtx = NewCtx())
            await new TicketService(svcCtx).UpdateReviewCommentAsync(commentId, "the phone renders twice", null, null);

        await using var readCtx = NewCtx();
        var after = (await readCtx.ReviewComments.FindAsync(commentId))!;
        Assert.Equal(3, after.QaShotOrder);
        Assert.Equal("the phone renders twice", after.Body);
    }

    /// <summary>A citation taken back is taken back, which a null slot cannot express on its own.</summary>
    [Fact]
    public async Task A_citation_can_be_withdrawn()
    {
        int commentId;
        await using (var ctx = NewCtx())
        {
            var t = await Seed(ctx, TicketPhase.NeedsReview, TicketTrack.Review);
            var c = new ReviewComment { TicketId = t.Id, Body = "wrong frame", QaShotOrder = 1 };
            ctx.ReviewComments.Add(c);
            await ctx.SaveChangesAsync();
            commentId = c.Id;
        }

        await using (var svcCtx = NewCtx())
            await new TicketService(svcCtx).UpdateReviewCommentAsync(commentId, null, null, null, clearQaShot: true);

        await using var readCtx = NewCtx();
        Assert.Null((await readCtx.ReviewComments.FindAsync(commentId))!.QaShotOrder);
    }

    /// <summary>
    /// A sent comment is a matter of record, and that guard has to cover the citation too - the
    /// reader of the pull request cannot see a screenshot attached to it after the fact.
    /// </summary>
    [Fact]
    public async Task A_sent_comment_cannot_be_cited_after_the_fact()
    {
        int commentId;
        await using (var ctx = NewCtx())
        {
            var t = await Seed(ctx, TicketPhase.NeedsReview, TicketTrack.Review);
            var c = new ReviewComment { TicketId = t.Id, Body = "already gone", Status = ReviewCommentStatus.Submitted };
            ctx.ReviewComments.Add(c);
            await ctx.SaveChangesAsync();
            commentId = c.Id;
        }

        await using (var svcCtx = NewCtx())
            await new TicketService(svcCtx).UpdateReviewCommentAsync(commentId, null, null, null, qaShotOrder: 2);

        await using var readCtx = NewCtx();
        Assert.Null((await readCtx.ReviewComments.FindAsync(commentId))!.QaShotOrder);
    }

    // ---- Creation -----------------------------------------------------------

    [Fact]
    public async Task A_review_ticket_without_a_branch_is_refused()
    {
        await using var ctx = NewCtx();
        var svc = new TicketService(ctx);

        await Assert.ThrowsAsync<ArgumentException>(() => svc.CreateAsync(
            "alpha", "Review something", null, null, null, TicketPhase.NeedsPeerReview,
            track: TicketTrack.Review, reviewTarget: null));
    }

    [Fact]
    public async Task A_delivery_ticket_still_needs_no_review_target()
    {
        await using var ctx = NewCtx();
        var svc = new TicketService(ctx);

        var t = await svc.CreateAsync("alpha", "Ordinary work", null, null, null, TicketPhase.Backlog);

        Assert.Equal(TicketTrack.Delivery, t.Track);
        Assert.Null(t.ReviewTarget);
    }
}
