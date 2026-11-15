using Microsoft.EntityFrameworkCore;
using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// Step 2 of the Discovery track, the long tail (docs/discovery-track-plan.md): the route gate, the
/// wait while what a verdict filed is built, the recheck that drives the product again, and the
/// rounds that end. The project here gives one recheck a verdict (<c>recheckRounds: 1</c>).
/// </summary>
public partial class DiscoveryTrackTests
{
    // ---- the path ---------------------------------------------------------------------------

    [Theory]
    [InlineData(TicketPhase.Charting, TicketPhase.NeedsRouteApproval)]
    [InlineData(TicketPhase.NeedsRouteApproval, TicketPhase.NeedsScouting)]
    [InlineData(TicketPhase.NeedsRouteApproval, TicketPhase.NeedsCharting)]
    [InlineData(TicketPhase.NeedsVerdict, TicketPhase.AwaitingWork)]
    [InlineData(TicketPhase.AwaitingWork, TicketPhase.NeedsRecheck)]
    [InlineData(TicketPhase.AwaitingWork, TicketPhase.NeedsReview)]
    [InlineData(TicketPhase.NeedsRecheck, TicketPhase.Rechecking)]
    [InlineData(TicketPhase.Rechecking, TicketPhase.NeedsVerdict)]
    [InlineData(TicketPhase.Rechecking, TicketPhase.NeedsReview)]
    [InlineData(TicketPhase.NeedsReview, TicketPhase.NeedsRecheck)]
    [InlineData(TicketPhase.Blocked, TicketPhase.AwaitingWork)]
    [InlineData(TicketPhase.Blocked, TicketPhase.NeedsRecheck)]
    public void The_long_tail_is_legal(TicketPhase from, TicketPhase to)
        => Assert.True(PhaseMachine.CanTransition(from, to, TicketTrack.Discovery));

    [Theory]
    [InlineData(TicketPhase.AwaitingWork)]
    [InlineData(TicketPhase.NeedsRecheck)]
    [InlineData(TicketPhase.Rechecking)]
    [InlineData(TicketPhase.NeedsRouteApproval)]
    public void The_long_tail_is_the_discoverys_alone(TicketPhase phase)
    {
        Assert.True(PhaseMachine.IsOnTrack(phase, TicketTrack.Discovery));
        Assert.False(PhaseMachine.IsOnTrack(phase, TicketTrack.Delivery));
        Assert.False(PhaseMachine.IsOnTrack(phase, TicketTrack.Review));
    }

    [Fact]
    public void A_recheck_is_claimed_and_runs_its_own_skill_and_the_gates_are_not()
    {
        Assert.True(PhaseMachine.IsActionable(TicketPhase.NeedsRecheck));
        Assert.Equal(TicketPhase.Rechecking, PhaseMachine.ClaimTarget(TicketPhase.NeedsRecheck));
        Assert.Equal("recheck", PhaseMachine.SkillFor(TicketPhase.Rechecking));
        Assert.Contains("recheck", PhaseMachine.AllSkills);
        Assert.DoesNotContain("recheck", PhaseMachine.RequiredSkills);
        Assert.Equal(TicketPhase.NeedsRecheck, PhaseMachine.ResumeAfterBlock(TicketPhase.Rechecking));
        Assert.False(PhaseMachine.IsWorkable(TicketPhase.AwaitingWork));
        Assert.False(PhaseMachine.IsWorkable(TicketPhase.NeedsRouteApproval));
        Assert.Equal(new[] { "alpha:app-stack" }, _registry.GetProjectConfig("alpha").ResourcesFor("recheck"));
    }

    [Fact]
    public void A_round_is_not_rework()
    {
        // verdict -> awaiting work -> recheck -> verdict is a loop by design; none of it counts as a send-back.
        var loop = new[] { TicketPhase.NeedsVerdict, TicketPhase.AwaitingWork, TicketPhase.NeedsRecheck, TicketPhase.Rechecking, TicketPhase.NeedsVerdict };
        for (var i = 1; i < loop.Length; i++)
            Assert.False(PhaseMachine.IsBackward(loop[i - 1], loop[i]), $"{loop[i - 1]} -> {loop[i]}");
        Assert.False(PhaseMachine.IsBackward(TicketPhase.Rechecking, TicketPhase.NeedsReview));
        Assert.True(PhaseMachine.IsBackward(TicketPhase.NeedsReview, TicketPhase.NeedsRecheck));
        Assert.True(PhaseMachine.ProgressRank(TicketPhase.Charting) < PhaseMachine.ProgressRank(TicketPhase.NeedsRouteApproval));
        Assert.True(PhaseMachine.ProgressRank(TicketPhase.NeedsRouteApproval) < PhaseMachine.ProgressRank(TicketPhase.NeedsScouting));
    }

    // ---- the route gate ---------------------------------------------------------------------

    private async Task<int> Charted(bool reviewRoute)
    {
        var (_, tickets, d) = New();
        var t = await tickets.CreateAsync("alpha", "Plan a month", "Plan your month as someone in a nearby town.",
            null, null, TicketPhase.Charting, track: TicketTrack.Discovery, requiresRouteReview: reviewRoute);
        await d.SetRouteAsync(t.Id, "A parent in a nearby town", new[] { "plan the month" }, null, new[] { "390x844" }, null, null, null);
        return t.Id;
    }

    [Fact]
    public async Task With_route_review_off_the_gate_is_skipped()
    {
        var id = await Charted(reviewRoute: false);
        var (_, tickets, _) = New();
        Assert.Equal(TicketPhase.NeedsScouting, (await tickets.TransitionAsync(id, TicketPhase.NeedsScouting, "done", TransitionActor.Worker))!.Phase);
    }

    [Fact]
    public async Task With_route_review_on_charts_hand_off_is_held_for_the_owner()
    {
        var id = await Charted(reviewRoute: true);
        var (_, tickets, d) = New();
        var held = await tickets.TransitionAsync(id, TicketPhase.NeedsScouting, "done", TransitionActor.Worker);
        Assert.Equal(TicketPhase.NeedsRouteApproval, held!.Phase);

        var (_, _, owner) = New();
        var r = await owner.EditRouteAsync(id, new DiscoveryService.RouteEdit(Jobs: new[] { "plan the month", "follow a place" }));
        Assert.True(r.OwnerEdited);
        Assert.Equal(TicketPhase.NeedsScouting, await owner.ApproveRouteAsync(id));

        await using var db = new DashboardDbContext(_options);
        var route = await db.DiscoveryRoutes.SingleAsync(x => x.TicketId == id);
        Assert.Equal(2, route.Jobs.Count);
        Assert.NotNull(route.ApprovedAt);
        Assert.Contains(await db.PhaseTransitions.Where(p => p.TicketId == id).ToListAsync(),
            p => p.From == TicketPhase.NeedsRouteApproval && p.To == TicketPhase.NeedsScouting && p.Actor == TransitionActor.Human);
    }

    [Fact]
    public async Task The_route_is_edited_and_approved_only_at_its_gate()
    {
        var id = await Charted(reviewRoute: true);
        var (_, _, d) = New();
        await Assert.ThrowsAsync<DiscoveryService.Refused>(() => d.ApproveRouteAsync(id));
        await Assert.ThrowsAsync<DiscoveryService.Refused>(() => d.EditRouteAsync(id, new(Persona: "Someone else")));
    }

    [Fact]
    public async Task Only_a_discovery_has_a_route_gate()
    {
        var (_, tickets, _) = New();
        var t = await tickets.CreateAsync("alpha", "Ours", "x", null, null, TicketPhase.Backlog, requiresRouteReview: true);
        Assert.False(t.RequiresRouteReview);
    }

    // ---- awaiting work ----------------------------------------------------------------------

    /// <summary>A discovery with two approved proposals completed: two tickets filed, waiting on them.</summary>
    private async Task<(int Id, int Obs, int Goal1, int Goal2, int Ticket1, int Ticket2)> Awaiting()
    {
        var (id, obs) = await Sifting();
        var (_, _, d) = New();
        var a = await d.ProposeAsync(id, new("Ticket", "A month view", null, "A calendar on /events", null, null, null, null, new[] { obs }));
        var b = await d.ProposeAsync(id, new("Bug", "Detail page keeps the place", null, "Keep it", null, null, null, null, new[] { obs }));
        await SetPhase(id, TicketPhase.NeedsVerdict);
        var (_, _, v) = New();
        await v.DecideAsync(a.Id, new(Status: "Approved"));
        await v.DecideAsync(b.Id, new(Status: "Approved"));
        var r = await v.CompleteAsync(id);
        Assert.Equal("AwaitingWork", r.Phase);
        return (id, obs, a.Id, b.Id, r.TicketIds[0], r.TicketIds[1]);
    }

    private async Task<TicketPhase> PhaseOf(int id)
    {
        await using var db = new DashboardDbContext(_options);
        return await db.Tickets.Where(t => t.Id == id).Select(t => t.Phase).SingleAsync();
    }

    /// <summary>A filed ticket finished by its owner, through the real move so the settle hook runs.</summary>
    private async Task Finish(int ticketId)
    {
        await SetPhase(ticketId, TicketPhase.NeedsReview);
        var (_, tickets, _) = New();
        await tickets.TransitionAsync(ticketId, TicketPhase.Done, "signed off", TransitionActor.Human);
    }

    [Fact]
    public async Task A_verdict_with_nothing_to_build_goes_straight_to_sign_off()
    {
        var (id, obs) = await Sifting();
        var (_, _, d) = New();
        var p = await d.ProposeAsync(id, new("Ticket", "A month view", null, "b", null, null, null, null, new[] { obs }));
        await SetPhase(id, TicketPhase.NeedsVerdict);
        var (_, _, v) = New();
        await v.DecideAsync(p.Id, new(Status: "Rejected", RejectReason: "Generic"));
        Assert.Equal("NeedsReview", (await v.CompleteAsync(id)).Phase);
    }

    [Fact]
    public async Task Awaiting_work_moves_on_only_when_every_filed_ticket_is_settled()
    {
        var w = await Awaiting();
        await SetPhase(w.Ticket2, TicketPhase.Blocked);
        await Finish(w.Ticket1);
        Assert.Equal(TicketPhase.AwaitingWork, await PhaseOf(w.Id));

        // The drawer says which ticket holds it.
        var (_, _, d) = New();
        var view = (await d.GetAsync(w.Id))!;
        Assert.Contains(view.Filed, f => f.Id == w.Ticket2 && f.Phase == "Blocked");

        // Dropped counts as settled: the owner deleting the blocked one lets the discovery go.
        var (_, tickets, _) = New();
        await tickets.DeleteAsync(w.Ticket2);
        Assert.Equal(TicketPhase.NeedsRecheck, await PhaseOf(w.Id));
    }

    [Fact]
    public async Task A_missed_settle_is_caught_up_by_the_next_look()
    {
        var w = await Awaiting();
        // Done behind the board's back: no transition, so no hook.
        await SetPhase(w.Ticket1, TicketPhase.Done);
        await SetPhase(w.Ticket2, TicketPhase.Done);
        Assert.Equal(TicketPhase.AwaitingWork, await PhaseOf(w.Id));
        var (_, _, d) = New();
        Assert.True(await d.SettleAsync(w.Id));
        Assert.Equal(TicketPhase.NeedsRecheck, await PhaseOf(w.Id));
    }

    // ---- recheck ----------------------------------------------------------------------------

    /// <summary>Drives a discovery through its filed work and claims its recheck.</summary>
    private async Task<(int Id, int Goal1, int Goal2)> Rechecking()
    {
        var w = await Awaiting();
        await Finish(w.Ticket1);
        await Finish(w.Ticket2);
        Assert.Equal(TicketPhase.NeedsRecheck, await PhaseOf(w.Id));
        var (_, tickets, _) = New();
        await tickets.TransitionAsync(w.Id, TicketPhase.Rechecking, "claimed");
        return (w.Id, w.Goal1, w.Goal2);
    }

    private static DiscoveryService.ObservationInput Seen(string title) =>
        new("Judgement", title, "What the person sees now", null, 0, null, null, null, null, null, null, null, null, null, null);

    [Fact]
    public async Task A_recheck_asks_about_every_filed_goal_and_writes_its_round()
    {
        var (id, g1, g2) = await Rechecking();
        var (_, tickets, d) = New();
        Assert.Equal(1, await tickets.RecheckRoundAsync(id));
        Assert.Equal(new[] { g1, g2 }, await tickets.RecheckGoalIdsAsync(id, 1));

        var o = await d.LogObservationAsync(id, Seen("The month view is there"));
        Assert.Equal(1, o.Round);
        var c = await d.CheckGoalAsync(id, g1, "Met", "Planned the month in one view", new[] { o.Id }, "abc");
        Assert.Equal(1, c.Round);
        // Again in the same round replaces the run's own answer.
        await d.CheckGoalAsync(id, g1, "not-met", "On a phone the grid overflows", null, null);
        await using var db = new DashboardDbContext(_options);
        var rows = await db.DiscoveryGoalChecks.Where(x => x.TicketId == id && x.ProposalId == g1).ToListAsync();
        Assert.Equal(GoalOutcome.NotMet, Assert.Single(rows).Outcome);
    }

    [Fact]
    public async Task Check_goal_is_a_rechecks_alone_and_only_for_its_goals()
    {
        var w = await Awaiting();
        var (_, _, d) = New();
        await Assert.ThrowsAsync<DiscoveryService.Refused>(() => d.CheckGoalAsync(w.Id, w.Goal1, "Met", "x", null, null));
        var (id, _, _) = await Rechecking();
        var (_, _, d2) = New();
        await Assert.ThrowsAsync<DiscoveryService.Refused>(() => d2.CheckGoalAsync(id, 9999, "Met", "x", null, null));
        var goal = (await d2.GetAsync(id))!.Goals[0];
        await Assert.ThrowsAsync<DiscoveryService.Refused>(() => d2.CheckGoalAsync(id, goal, "Maybe", "x", null, null));
    }

    [Fact]
    public async Task A_recheck_hands_off_only_with_every_goal_checked()
    {
        var (id, g1, g2) = await Rechecking();
        var (_, tickets, d) = New();
        await d.CheckGoalAsync(id, g1, "Met", "Works", null, null);
        var e = await Assert.ThrowsAsync<HandoffRequirementException>(() =>
            tickets.TransitionAsync(id, TicketPhase.NeedsReview, "done", TransitionActor.Worker));
        Assert.Contains($"#{g2}", e.Message);
        Assert.Null(e.RouteInstead);
        await d.CheckGoalAsync(id, g2, "Unclear", "No event had a place to keep", null, null);
        var (_, t2, _) = New();
        // Nothing proposed: no verdict to give, so the sign-off - and the refusal says so.
        var wrong = await Assert.ThrowsAsync<HandoffRequirementException>(() => t2.TransitionAsync(id, TicketPhase.NeedsVerdict, "done", TransitionActor.Worker));
        Assert.Equal(TicketPhase.NeedsReview, wrong.RouteInstead);
        Assert.Equal(TicketPhase.NeedsReview, (await t2.TransitionAsync(id, TicketPhase.NeedsReview, "done", TransitionActor.Worker))!.Phase);
    }

    [Fact]
    public async Task A_recheck_proposal_says_whether_it_is_a_fix_up_and_of_which_goal()
    {
        var (id, g1, _) = await Rechecking();
        var (_, _, d) = New();
        var o = await d.LogObservationAsync(id, Seen("Grid overflows on a phone"));
        await Assert.ThrowsAsync<DiscoveryService.Refused>(() =>
            d.ProposeAsync(id, new("Ticket", "Fit the grid", null, "b", null, null, null, null, new[] { o.Id })));
        await Assert.ThrowsAsync<DiscoveryService.Refused>(() =>
            d.ProposeAsync(id, new("Ticket", "Fit the grid", null, "b", null, null, null, null, new[] { o.Id }, "FixUp", null)));
        var fix = await d.ProposeAsync(id, new("Ticket", "Fit the grid", null, "b", null, null, null, null, new[] { o.Id }, "fix-up", g1));
        var fresh = await d.ProposeAsync(id, new("Ticket", "Share a month", null, "b", null, null, null, null, new[] { o.Id }, "New"));
        Assert.Equal((1, RecheckKind.FixUp, (int?)g1), (fix.Round, fix.RecheckKind!.Value, fix.GoalId));
        Assert.Equal((1, RecheckKind.New, (int?)null), (fresh.Round, fresh.RecheckKind!.Value, fresh.GoalId));
        // Its own cap, per round: the sift's two do not count against it.
        await Assert.ThrowsAsync<DiscoveryService.Refused>(() =>
            d.ProposeAsync(id, new("Ticket", "Third", null, "b", null, null, null, null, new[] { o.Id }, "New")));
    }

    [Fact]
    public async Task Proposals_inside_the_rounds_go_to_a_verdict_and_complete_waits_on_them_again()
    {
        var (id, g1, g2) = await Rechecking();
        var (_, tickets, d) = New();
        var o = await d.LogObservationAsync(id, Seen("Grid overflows on a phone"));
        await d.CheckGoalAsync(id, g1, "NotMet", "Overflows at 390", new[] { o.Id }, null);
        await d.CheckGoalAsync(id, g2, "Met", "Kept", null, null);
        var fix = await d.ProposeAsync(id, new("Ticket", "Fit the grid", null, "b", null, null, null, null, new[] { o.Id }, "FixUp", g1));
        var wrong = await Assert.ThrowsAsync<HandoffRequirementException>(() =>
            tickets.TransitionAsync(id, TicketPhase.NeedsReview, "done", TransitionActor.Worker));
        Assert.Equal(TicketPhase.NeedsVerdict, wrong.RouteInstead);
        var (_, t2, _) = New();
        Assert.Equal(TicketPhase.NeedsVerdict, (await t2.TransitionAsync(id, TicketPhase.NeedsVerdict, "done", TransitionActor.Worker))!.Phase);

        var (_, _, v) = New();
        await v.DecideAsync(fix.Id, new(Status: "Approved"));
        var r = await v.CompleteAsync(id);
        Assert.Equal(1, r.Filed);
        Assert.Equal("AwaitingWork", r.Phase);

        // The next recheck asks only about what is not yet met: the first goal again, and the fix-up.
        var (_, t3, _) = New();
        Assert.Equal(new[] { g1, fix.Id }, await t3.RecheckGoalIdsAsync(id, 2));
    }

    [Fact]
    public async Task Past_the_rounds_a_recheck_proposes_to_the_sign_off_where_the_owner_files_it()
    {
        var (id, g1, g2) = await Rechecking();
        // Round 1, inside the cap: a fix-up, approved and filed, built.
        var (_, _, d) = New();
        var o = await d.LogObservationAsync(id, Seen("Grid overflows"));
        await d.CheckGoalAsync(id, g1, "NotMet", "Overflows", null, null);
        await d.CheckGoalAsync(id, g2, "Met", "Kept", null, null);
        var fix = await d.ProposeAsync(id, new("Ticket", "Fit the grid", null, "b", null, null, null, null, new[] { o.Id }, "FixUp", g1));
        var (_, tickets, _) = New();
        await tickets.TransitionAsync(id, TicketPhase.NeedsVerdict, "done", TransitionActor.Worker);
        var (_, _, v) = New();
        await v.DecideAsync(fix.Id, new(Status: "Approved"));
        var filed = (await v.CompleteAsync(id)).TicketIds.Single();
        await Finish(filed);
        Assert.Equal(TicketPhase.NeedsRecheck, await PhaseOf(id));
        var (_, t2, _) = New();
        await t2.TransitionAsync(id, TicketPhase.Rechecking, "claimed");

        // Round 2, past the project's one round of verdict.
        var (_, _, d2) = New();
        var o2 = await d2.LogObservationAsync(id, Seen("Still overflows"));
        await d2.CheckGoalAsync(id, g1, "NotMet", "Still", null, null);
        await d2.CheckGoalAsync(id, fix.Id, "NotMet", "Still", null, null);
        var late = await d2.ProposeAsync(id, new("Ticket", "Scroll the grid", null, "b", null, null, null, null, new[] { o2.Id }, "FixUp", g1));
        var (_, t3, _) = New();
        var e = await Assert.ThrowsAsync<HandoffRequirementException>(() =>
            t3.TransitionAsync(id, TicketPhase.NeedsVerdict, "done", TransitionActor.Worker));
        Assert.Contains("past", e.Message);
        Assert.Equal(TicketPhase.NeedsReview, e.RouteInstead);
        Assert.Equal(TicketPhase.NeedsReview, (await t3.TransitionAsync(id, TicketPhase.NeedsReview, "done", TransitionActor.Worker))!.Phase);

        // At the sign-off the owner can still decide it, and Complete files it and stays there.
        var (_, _, owner) = New();
        await owner.DecideAsync(late.Id, new(Status: "Approved"));
        var r = await owner.CompleteAsync(id);
        Assert.Equal(1, r.Filed);
        Assert.Equal("NeedsReview", r.Phase);
        await Assert.ThrowsAsync<DiscoveryService.Refused>(() => owner.CompleteAsync(id));
    }

    [Fact]
    public async Task Landed_and_recheck_yield_can_be_read_off_the_record()
    {
        var (id, g1, g2) = await Rechecking();
        var (_, _, d) = New();
        var o = await d.LogObservationAsync(id, Seen("Grid overflows"));
        await d.CheckGoalAsync(id, g1, "Met", "Works", null, null);
        await d.CheckGoalAsync(id, g2, "NotMet", "Reset again", new[] { o.Id }, null);
        var fix = await d.ProposeAsync(id, new("Bug", "Keep the place", null, "b", null, null, null, null, new[] { o.Id }, "FixUp", g2));
        var (_, tickets, _) = New();
        await tickets.TransitionAsync(id, TicketPhase.NeedsVerdict, "done", TransitionActor.Worker);
        var (_, _, v) = New();
        await v.DecideAsync(fix.Id, new(Status: "Approved"));

        await using var db = new DashboardDbContext(_options);
        // Landed: a goal's earliest check, met.
        var firsts = (await db.DiscoveryGoalChecks.Where(g => g.TicketId == id).ToListAsync())
            .GroupBy(g => g.ProposalId).Select(g => g.OrderBy(x => x.Round).First()).ToList();
        Assert.Equal(1, firsts.Count(g => g.Outcome == GoalOutcome.Met));
        // Recheck yield: approvals of proposals a recheck wrote.
        var approvedFromRecheck = await db.DiscoveryVerdictEvents
            .Where(e => e.TicketId == id && e.Kind == VerdictEventKind.Approved)
            .Join(db.DiscoveryProposals.Where(p => p.Round > 0), e => e.ProposalId, p => p.Id, (e, p) => p.RecheckKind)
            .ToListAsync();
        Assert.Equal(new RecheckKind?[] { RecheckKind.FixUp }, approvedFromRecheck);
    }
}
