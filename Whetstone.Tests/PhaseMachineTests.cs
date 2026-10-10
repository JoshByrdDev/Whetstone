using Whetstone.Models;
using Whetstone.Persistence;
using Xunit;

namespace Whetstone.Tests;

public class PhaseMachineTests
{
    [Theory]
    [InlineData(TicketPhase.Backlog, TicketPhase.NeedsSpeculation)]
    [InlineData(TicketPhase.NeedsSpeculation, TicketPhase.Speculating)]
    [InlineData(TicketPhase.Speculating, TicketPhase.NeedsApproval)]
    [InlineData(TicketPhase.NeedsApproval, TicketPhase.NeedsImplementation)]
    [InlineData(TicketPhase.NeedsApproval, TicketPhase.NeedsSpeculation)] // send back
    [InlineData(TicketPhase.NeedsImplementation, TicketPhase.Implementing)]
    [InlineData(TicketPhase.Implementing, TicketPhase.NeedsImplementationReview)]
    [InlineData(TicketPhase.NeedsImplementationReview, TicketPhase.NeedsQa)]      // code reviewed
    [InlineData(TicketPhase.NeedsImplementationReview, TicketPhase.NeedsImplementation)] // send back
    [InlineData(TicketPhase.QaTesting, TicketPhase.NeedsRefinement)]      // QA pass → refinement
    [InlineData(TicketPhase.QaTesting, TicketPhase.NeedsImplementation)]  // fail → rework
    [InlineData(TicketPhase.NeedsRefinement, TicketPhase.Refining)]
    [InlineData(TicketPhase.Refining, TicketPhase.NeedsReview)]           // refinement clean
    [InlineData(TicketPhase.Refining, TicketPhase.NeedsImplementation)]   // code gaps → rework
    [InlineData(TicketPhase.Refining, TicketPhase.NeedsQa)]               // coverage gaps → re-test, nothing to write
    [InlineData(TicketPhase.NeedsReview, TicketPhase.Done)]
    [InlineData(TicketPhase.NeedsReview, TicketPhase.NeedsImplementation)] // changes requested
    [InlineData(TicketPhase.NeedsReview, TicketPhase.NeedsRefinement)]    // tidy-up, not a code change
    [InlineData(TicketPhase.Done, TicketPhase.NeedsImplementation)]       // reopen
    public void Legal_forward_transitions_are_allowed(TicketPhase from, TicketPhase to)
        => Assert.True(PhaseMachine.CanTransition(from, to));

    [Theory]
    [InlineData(TicketPhase.Backlog, TicketPhase.Implementing)]
    [InlineData(TicketPhase.NeedsSpeculation, TicketPhase.Done)]
    [InlineData(TicketPhase.Speculating, TicketPhase.Implementing)]       // must pass through approval
    [InlineData(TicketPhase.NeedsApproval, TicketPhase.Done)]
    [InlineData(TicketPhase.NeedsQa, TicketPhase.NeedsReview)]            // must go through QaTesting
    [InlineData(TicketPhase.Implementing, TicketPhase.NeedsReview)]       // QA is mandatory - no skipping
    [InlineData(TicketPhase.Implementing, TicketPhase.NeedsQa)]           // must pass the implementation-review gate
    [InlineData(TicketPhase.QaTesting, TicketPhase.NeedsReview)]          // must go through refinement
    [InlineData(TicketPhase.Refining, TicketPhase.Done)]                  // the final sign-off is never skipped
    public void Illegal_transitions_are_rejected(TicketPhase from, TicketPhase to)
        => Assert.False(PhaseMachine.CanTransition(from, to));

    [Fact]
    public void Same_phase_is_never_a_transition()
        => Assert.False(PhaseMachine.CanTransition(TicketPhase.Implementing, TicketPhase.Implementing));

    [Theory]
    [InlineData(TicketPhase.NeedsSpeculation)]
    [InlineData(TicketPhase.Implementing)]
    [InlineData(TicketPhase.NeedsReview)]
    public void Any_active_phase_can_be_blocked(TicketPhase from)
        => Assert.True(PhaseMachine.CanTransition(from, TicketPhase.Blocked));

    [Fact]
    public void Blocked_cannot_be_blocked_again()
        => Assert.False(PhaseMachine.CanTransition(TicketPhase.Blocked, TicketPhase.Blocked));

    [Fact]
    public void Unblock_only_targets_queue_states()
    {
        Assert.True(PhaseMachine.CanTransition(TicketPhase.Blocked, TicketPhase.NeedsImplementation));
        Assert.True(PhaseMachine.CanTransition(TicketPhase.Blocked, TicketPhase.NeedsSpeculation));
        // Cannot unblock directly into an in-progress ("-ing") state.
        Assert.False(PhaseMachine.CanTransition(TicketPhase.Blocked, TicketPhase.Speculating));
        Assert.False(PhaseMachine.CanTransition(TicketPhase.Blocked, TicketPhase.Done));
    }

    [Fact]
    public void InProgress_and_actionable_classifications_are_disjoint_and_correct()
    {
        Assert.True(PhaseMachine.IsInProgress(TicketPhase.Speculating));
        Assert.True(PhaseMachine.IsInProgress(TicketPhase.Implementing));
        Assert.True(PhaseMachine.IsInProgress(TicketPhase.QaTesting));
        Assert.False(PhaseMachine.IsInProgress(TicketPhase.NeedsImplementation));

        Assert.True(PhaseMachine.IsActionable(TicketPhase.NeedsSpeculation));
        Assert.True(PhaseMachine.IsActionable(TicketPhase.NeedsImplementation));
        Assert.True(PhaseMachine.IsActionable(TicketPhase.NeedsQa));
        Assert.False(PhaseMachine.IsActionable(TicketPhase.Speculating));

        // No phase is both a queue item and actively worked.
        foreach (TicketPhase p in Enum.GetValues<TicketPhase>())
            Assert.False(PhaseMachine.IsActionable(p) && PhaseMachine.IsInProgress(p));
    }

    // ---- Heartbeat pickup ordering ----

    [Fact]
    public void WorkablePhases_equal_actionable_plus_inprogress()
    {
        foreach (TicketPhase p in Enum.GetValues<TicketPhase>())
            Assert.Equal(PhaseMachine.IsActionable(p) || PhaseMachine.IsInProgress(p), PhaseMachine.IsWorkable(p));

        Assert.Equal(
            Enum.GetValues<TicketPhase>().Where(PhaseMachine.IsWorkable).ToHashSet(),
            PhaseMachine.WorkablePhases.ToHashSet());
    }

    [Fact]
    public void ProgressRank_strictly_increases_along_the_happy_path()
    {
        var pipeline = new[]
        {
            TicketPhase.Backlog, TicketPhase.NeedsSpeculation, TicketPhase.Speculating,
            TicketPhase.NeedsApproval, TicketPhase.NeedsImplementation, TicketPhase.Implementing,
            TicketPhase.NeedsImplementationReview, TicketPhase.NeedsQa, TicketPhase.QaTesting,
            TicketPhase.NeedsRefinement, TicketPhase.Refining,
            TicketPhase.NeedsReview, TicketPhase.Done,
        };
        for (int i = 1; i < pipeline.Length; i++)
            Assert.True(PhaseMachine.ProgressRank(pipeline[i]) > PhaseMachine.ProgressRank(pipeline[i - 1]),
                $"{pipeline[i]} should outrank {pipeline[i - 1]}");
    }

    [Fact]
    public void Heartbeat_prefers_more_progressed_workable_phases()
    {
        // NeedsQa (6) > NeedsImplementation (4) > NeedsSpeculation (1); a resume outranks its own queue.
        Assert.True(PhaseMachine.ProgressRank(TicketPhase.NeedsQa) > PhaseMachine.ProgressRank(TicketPhase.NeedsImplementation));
        Assert.True(PhaseMachine.ProgressRank(TicketPhase.NeedsImplementation) > PhaseMachine.ProgressRank(TicketPhase.NeedsSpeculation));
        Assert.True(PhaseMachine.ProgressRank(TicketPhase.Implementing) > PhaseMachine.ProgressRank(TicketPhase.NeedsImplementation));
    }

    [Theory]
    [InlineData(TicketPhase.NeedsSpeculation, TicketPhase.Speculating, "speculate")]
    [InlineData(TicketPhase.NeedsImplementation, TicketPhase.Implementing, "implement")]
    [InlineData(TicketPhase.NeedsQa, TicketPhase.QaTesting, "qa-test")]
    public void Claim_maps_queue_to_inprogress_with_a_legal_transition_and_skill(TicketPhase queue, TicketPhase ing, string skill)
    {
        Assert.Equal(ing, PhaseMachine.ClaimTarget(queue));
        Assert.Equal(skill, PhaseMachine.SkillFor(ing));
        Assert.True(PhaseMachine.CanTransition(queue, PhaseMachine.ClaimTarget(queue)));
    }

    [Theory]
    [InlineData(TicketPhase.Speculating)]
    [InlineData(TicketPhase.Implementing)]
    [InlineData(TicketPhase.QaTesting)]
    public void ClaimTarget_of_inprogress_is_itself_a_resume(TicketPhase ing)
        => Assert.Equal(ing, PhaseMachine.ClaimTarget(ing));

    [Theory]
    [InlineData(TicketPhase.Backlog)]
    [InlineData(TicketPhase.NeedsApproval)]
    [InlineData(TicketPhase.NeedsReview)]
    [InlineData(TicketPhase.Done)]
    [InlineData(TicketPhase.Blocked)]
    public void ClaimTarget_throws_for_non_workable_phases(TicketPhase p)
        => Assert.Throws<ArgumentException>(() => PhaseMachine.ClaimTarget(p));

    // ---- Human review gates (skipped when a ticket doesn't require them) ----

    [Theory]
    [InlineData(TicketPhase.Speculating, TicketPhase.NeedsApproval, TicketPhase.NeedsImplementation)]
    [InlineData(TicketPhase.Implementing, TicketPhase.NeedsImplementationReview, TicketPhase.NeedsQa)]
    public void Gate_skips_continue_along_a_legal_transition(TicketPhase from, TicketPhase gate, TicketPhase expected)
    {
        Assert.True(PhaseMachine.TryGetGateSkip(from, gate, out var skipTo));
        Assert.Equal(expected, skipTo);
        // The hand-off into the gate and the skip past it must both be legal moves.
        Assert.True(PhaseMachine.CanTransition(from, gate));
        Assert.True(PhaseMachine.CanTransition(gate, skipTo));
    }

    [Theory]
    [InlineData(TicketPhase.Blocked, TicketPhase.NeedsApproval)]  // a human parking it there
    [InlineData(TicketPhase.Blocked, TicketPhase.NeedsImplementationReview)]
    [InlineData(TicketPhase.QaTesting, TicketPhase.NeedsRefinement)] // not a human gate at all
    public void Only_the_pipeline_handoff_into_a_gate_is_skippable(TicketPhase from, TicketPhase to)
        => Assert.False(PhaseMachine.TryGetGateSkip(from, to, out _));

    [Fact]
    public void The_final_review_signoff_is_never_auto_skipped()
    {
        // Refinement always hands off to NeedsReview, and only a human moves it to Done.
        Assert.False(PhaseMachine.TryGetGateSkip(TicketPhase.Refining, TicketPhase.NeedsReview, out _));
        Assert.False(PhaseMachine.CanTransition(TicketPhase.Refining, TicketPhase.Done));
    }

    [Fact]
    public void NextPhases_includes_blocked_for_active_states_and_unblock_targets_for_blocked()
    {
        Assert.Contains(TicketPhase.Blocked, PhaseMachine.NextPhases(TicketPhase.NeedsImplementation));
        Assert.Equal(
            new HashSet<TicketPhase>
            {
                TicketPhase.Backlog, TicketPhase.NeedsSpeculation, TicketPhase.NeedsApproval,
                TicketPhase.NeedsImplementation, TicketPhase.NeedsImplementationReview,
                TicketPhase.NeedsQa, TicketPhase.NeedsRefinement, TicketPhase.NeedsReview,
            },
            PhaseMachine.NextPhases(TicketPhase.Blocked).ToHashSet());

        // Every listed next phase must actually be a legal transition.
        foreach (TicketPhase from in Enum.GetValues<TicketPhase>())
            foreach (var to in PhaseMachine.NextPhases(from))
                Assert.True(PhaseMachine.CanTransition(from, to), $"{from} -> {to} listed but not allowed");
    }
}
