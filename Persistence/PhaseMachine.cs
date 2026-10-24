using Whetstone.Models;

namespace Whetstone.Persistence;

/// <summary>
/// The single authority on legal phase transitions. Both the ticketing API and
/// the heartbeat engine validate moves through here so they can never disagree.
/// </summary>
public static class PhaseMachine
{
    /// <summary>Allowed next phases for each phase (excluding the special Blocked edges, handled below).</summary>
    private static readonly IReadOnlyDictionary<TicketPhase, TicketPhase[]> Forward = new Dictionary<TicketPhase, TicketPhase[]>
    {
        // Two edges out of the backlog, one per track (see IsOnTrack): the filter in NextPhases /
        // CanTransition leaves exactly one of them visible to any given ticket, so this table can
        // hold every edge in the system without a Delivery ticket ever being offered a Review move.
        [TicketPhase.Backlog]             = new[] { TicketPhase.NeedsSpeculation, TicketPhase.NeedsPeerReview },
        [TicketPhase.NeedsSpeculation]    = new[] { TicketPhase.Speculating },
        [TicketPhase.Speculating]         = new[] { TicketPhase.NeedsApproval },
        // Approve → implement, or send back for another speculation pass.
        [TicketPhase.NeedsApproval]       = new[] { TicketPhase.NeedsImplementation, TicketPhase.NeedsSpeculation },
        [TicketPhase.NeedsImplementation] = new[] { TicketPhase.Implementing },
        // Implementation always hands off toward QA - it may never skip straight to review. The QA
        // phase itself handles the "nothing to test" case, so QA is never a dead end.
        [TicketPhase.Implementing]        = new[] { TicketPhase.NeedsImplementationReview },
        // Optional human look at the implementation before QA spends time on it; send it back; or
        // the short loop straight to final sign-off (see Ticket.ShortLoopReview). That last edge is
        // safe to have here and would not be safe one phase earlier: no worker is ever standing in
        // this phase - it is a gate, not a workable phase - so the only callers who can choose it are
        // the dashboard's own routing and the human whose ticket it is.
        [TicketPhase.NeedsImplementationReview] = new[] { TicketPhase.NeedsQa, TicketPhase.NeedsImplementation, TicketPhase.NeedsReview },
        [TicketPhase.NeedsQa]             = new[] { TicketPhase.QaTesting },
        // QA pass → refinement (final cleanup/validation); QA fail → back to implementation.
        [TicketPhase.QaTesting]           = new[] { TicketPhase.NeedsRefinement, TicketPhase.NeedsImplementation },
        [TicketPhase.NeedsRefinement]     = new[] { TicketPhase.Refining },
        // Refinement clean → review; gaps found → back to implementation, or back to QA when the gap
        // is only that something went untested and there is nothing to write.
        [TicketPhase.Refining]            = new[] { TicketPhase.NeedsReview, TicketPhase.NeedsQa, TicketPhase.NeedsImplementation },

        // ---- The Review track's one working pair ----------------------------
        // Someone else's branch: read it, drive-test it, stage feedback. It has no backward edge of
        // its own, and that is deliberate — a review that found ten defects has SUCCEEDED, so it
        // hands off forward exactly like a clean one. The findings are the deliverable, not a
        // failure to route around. Blocked remains available for a branch that cannot be judged.
        [TicketPhase.NeedsPeerReview]     = new[] { TicketPhase.PeerReviewing },
        [TicketPhase.PeerReviewing]       = new[] { TicketPhase.NeedsReview },

        // Review approve → done; changes requested → back to implementation, or back to speculation
        // when what was asked for is new scope rather than a correction (see
        // TicketService.RequestChangesAsync); or another refinement pass when the code is right and
        // only its write-up/validation is not. On the Review track the same gate instead loops back
        // for a re-review once the author has pushed.
        [TicketPhase.NeedsReview]         = new[] { TicketPhase.Done, TicketPhase.NeedsImplementation, TicketPhase.NeedsSpeculation, TicketPhase.NeedsRefinement, TicketPhase.NeedsPeerReview },
        // Reopen a finished ticket.
        [TicketPhase.Done]                = new[] { TicketPhase.NeedsImplementation },
        [TicketPhase.Blocked]             = Array.Empty<TicketPhase>(),
    };

    /// <summary>
    /// Phases only a <see cref="TicketTrack.Review"/> ticket ever occupies.
    /// <para>
    /// Everything not listed here or in <see cref="SharedPhases"/> belongs to Delivery. Stated as
    /// the two small sets rather than the large one so that adding a Delivery phase — much the more
    /// likely change — needs no edit here at all.
    /// </para>
    /// </summary>
    private static readonly HashSet<TicketPhase> ReviewPhases = new()
    {
        TicketPhase.NeedsPeerReview,
        TicketPhase.PeerReviewing,
    };

    /// <summary>
    /// Phases both tracks pass through. These are the parking spots — nothing claims them, no skill
    /// runs on them, no lease is taken — which is exactly why they are safe to share. The working
    /// phases are split per track instead, because a phase that dispatches an agent has to answer
    /// which skill, which rank, and what its hand-off owes, and those differ.
    /// </summary>
    private static readonly HashSet<TicketPhase> SharedPhases = new()
    {
        TicketPhase.Backlog,
        TicketPhase.NeedsReview,
        TicketPhase.Done,
        TicketPhase.Blocked,
    };

    /// <summary>Whether <paramref name="phase"/> is one a ticket on <paramref name="track"/> can be in.</summary>
    public static bool IsOnTrack(TicketPhase phase, TicketTrack track)
    {
        if (SharedPhases.Contains(phase)) return true;
        return track == TicketTrack.Review ? ReviewPhases.Contains(phase) : !ReviewPhases.Contains(phase);
    }

    /// <summary>The track a phase belongs to, for the phases that belong to exactly one.</summary>
    public static TicketTrack? TrackOf(TicketPhase phase) =>
        SharedPhases.Contains(phase) ? null
        : ReviewPhases.Contains(phase) ? TicketTrack.Review
        : TicketTrack.Delivery;

    /// <summary>
    /// What each legal move <b>means</b>, in the words of whoever has to pick one.
    /// <para>
    /// The rules above are unambiguous about what is legal and say nothing about what is intended,
    /// which is not enough for an autonomous worker choosing its own hand-off. Offered only
    /// "NeedsRefinement, NeedsImplementation" on the way out of QaTesting, a worker whose QA had
    /// passed cleanly read both as failure paths, refused both, and parked the ticket in an
    /// in-progress phase - where the heartbeat kept re-claiming it. Naming the success path fixes
    /// that; the comments that used to carry this only ever reached people reading the source.
    /// </para>
    /// </summary>
    private static readonly IReadOnlyDictionary<(TicketPhase From, TicketPhase To), string> Meanings =
        new Dictionary<(TicketPhase, TicketPhase), string>
        {
            [(TicketPhase.Backlog, TicketPhase.NeedsSpeculation)] = "Pull the ticket out of the backlog and queue it for speculation.",
            [(TicketPhase.Speculating, TicketPhase.NeedsApproval)] = "FORWARD: speculation done, hand off for approval. Auto-skips straight to NeedsImplementation when this ticket does not require a speculation review.",
            [(TicketPhase.NeedsApproval, TicketPhase.NeedsImplementation)] = "Approved - queue it for implementation.",
            [(TicketPhase.NeedsApproval, TicketPhase.NeedsSpeculation)] = "BACKWARD: the plan needs another speculation pass.",
            [(TicketPhase.Implementing, TicketPhase.NeedsImplementationReview)] = "FORWARD: implementation done, hand off toward QA. Auto-skips straight to NeedsQa when this ticket does not require an implementation review.",
            [(TicketPhase.NeedsImplementationReview, TicketPhase.NeedsQa)] = "The implementation looks right - send it to QA.",
            [(TicketPhase.NeedsImplementationReview, TicketPhase.NeedsImplementation)] = "BACKWARD: the implementation needs changes before QA is worth running.",
            [(TicketPhase.NeedsImplementationReview, TicketPhase.NeedsReview)] = "SHORT LOOP: skip QA and refinement and go straight back to the human who asked for this change. Reserved for a reviewer's own small change request (see Ticket.ShortLoopReview), where a full QA and refinement cycle costs more than it proves. Not a hand-off an automated worker chooses - it removes the two phases that exist to check the work.",
            [(TicketPhase.QaTesting, TicketPhase.NeedsRefinement)] = "FORWARD, AND THE SUCCESS PATH: QA passed - hand off to refinement. Refinement always follows a clean QA and is NOT a failure state; a ticket whose QA passed belongs here, never left parked in QaTesting.",
            [(TicketPhase.QaTesting, TicketPhase.NeedsImplementation)] = "BACKWARD: QA found failures or blockers - send it back to be fixed.",
            [(TicketPhase.Refining, TicketPhase.NeedsReview)] = "FORWARD, AND THE SUCCESS PATH: refinement found nothing outstanding - hand off for the owner's final sign-off. This is as far as a worker goes; a ticket is moved to Done by its owner, or by the project's steward acting for them, and never by the worker that built it.",
            [(TicketPhase.Refining, TicketPhase.NeedsQa)] = "BACKWARD: the code is fine but its testing is not - a requirement with no QA item against it, a QA item that passed without really exercising the thing, missing QA shots. Use this when nothing needs to be WRITTEN, only checked; it costs a QA pass instead of a full implementation cycle.",
            [(TicketPhase.Refining, TicketPhase.NeedsImplementation)] = "BACKWARD: refinement found gaps in the CODE - something unbuilt, stubbed, or wrong. Send it back to be fixed; it returns through QA and refinement automatically. This is the route for any change to what the code does, however small - refinement does not make those itself.",
            [(TicketPhase.Backlog, TicketPhase.NeedsPeerReview)] = "Pull the ticket out of the backlog and queue it for a peer review pass.",
            [(TicketPhase.PeerReviewing, TicketPhase.NeedsReview)] = "FORWARD, AND THE SUCCESS PATH: the review pass is finished - hand the staged feedback to the ticket's owner to curate and send. This is the ONLY hand-off out of this phase, and it is the right one whatever you found: a review that turned up ten defects has succeeded, because the findings ARE the deliverable. There is no route back to implementation from here - the code belongs to someone else and this pipeline does not fix it.",
            [(TicketPhase.NeedsReview, TicketPhase.NeedsPeerReview)] = "BACKWARD: the author has pushed since the last pass, so the branch needs reviewing again. Not a move an automated worker makes - the owner decides when there is something new to look at.",
            [(TicketPhase.NeedsReview, TicketPhase.Done)] = "The owner's sign-off, made by a person or by the project's steward in their place. Not a move a worker makes.",
            [(TicketPhase.NeedsReview, TicketPhase.NeedsImplementation)] = "BACKWARD: the reviewer asked for changes.",
            [(TicketPhase.NeedsReview, TicketPhase.NeedsSpeculation)] = "BACKWARD: the reviewer asked for something new enough that it needs planning before it is built - new scope, or a decision still open - so it goes back through speculation and its approval gate. Not a move an automated worker makes.",
            [(TicketPhase.NeedsReview, TicketPhase.NeedsRefinement)] = "BACKWARD: the reviewer wants another refinement pass - the code itself is fine, but the ticket is not tidy enough to sign off (documentation, notes, or validation left undone). Cheaper than a full implementation cycle; refinement routes it onward from there.",
            [(TicketPhase.Done, TicketPhase.NeedsImplementation)] = "Reopen a finished ticket.",
        };

    /// <summary>How the <paramref name="from"/> → <paramref name="to"/> move is meant to be used, if it is described.</summary>
    public static string? Meaning(TicketPhase from, TicketPhase to)
    {
        // Blocked is reachable from everywhere, so it is an edge rather than a Forward entry - but it
        // still needs describing, or it reads as a third neutral option next to the real hand-offs.
        //
        // Both halves of this are load-bearing. Described only as an environment fault, it reads as
        // "the build broke" and a worker holding a genuinely contradictory ticket goes looking for a
        // hand-off instead of asking the question - which is how a plausible wrong answer gets built.
        // Described only as "ask when unsure", it becomes the cheap way out of every hard decision.
        if (to == TicketPhase.Blocked)
            return "ESCAPE HATCH, AND THE WAY TO ASK A QUESTION: the work cannot honestly continue "
                 + "until a person does something. Two cases. (1) Something outside your control "
                 + "stopped it: the workspace is wrong, a tool is missing, the app will not build. "
                 + "(2) The TICKET DOES NOT ADD UP: it contradicts itself, or the acceptance criteria "
                 + "contradict the description, or what it asks for is too under-specified to build "
                 + "without guessing at something a wrong guess would waste the whole cycle on. Say "
                 + "exactly what you need in the reason - name the contradiction, or ask the specific "
                 + "question - because that reason is what the human answers. It is REQUIRED. "
                 + "Blocking is not a failure and does not count against the ticket. What it is not: "
                 + "a way to avoid choosing between two legitimate hand-offs, and not the route for a "
                 + "question you could answer by reading the repo, the attachments, or the linked issue.";

        if (to == TicketPhase.NeedsSpeculation && IsInProgress(from))
            return "REPLAN: the requirements changed under this ticket and the existing plan no longer matches them. Prefer the request_respeculation tool, which also turns the human speculation review on. Not for work that is merely harder than expected.";
        return Meanings.TryGetValue((from, to), out var m) ? m : null;
    }

    /// <summary>
    /// The two optional human review gates, as pipeline hand-offs mapped to the phase the ticket
    /// continues to when that review is <b>not</b> required for it (see Ticket.RequiresSpeculationReview /
    /// RequiresImplementationReview). Only these exact hand-offs auto-skip, so a human can still
    /// park a ticket on a gate deliberately and have it stay there.
    /// <para>
    /// The final <see cref="TicketPhase.NeedsReview"/> sign-off is deliberately absent: refinement
    /// always hands off to it, and only a human moves it to Done.
    /// </para>
    /// </summary>
    private static readonly IReadOnlyDictionary<(TicketPhase From, TicketPhase Gate), TicketPhase> GateSkips =
        new Dictionary<(TicketPhase, TicketPhase), TicketPhase>
        {
            [(TicketPhase.Speculating, TicketPhase.NeedsApproval)] = TicketPhase.NeedsImplementation,
            [(TicketPhase.Implementing, TicketPhase.NeedsImplementationReview)] = TicketPhase.NeedsQa,
        };

    /// <summary>
    /// True when moving <paramref name="from"/> → <paramref name="gate"/> is a skippable review
    /// hand-off, yielding the phase to continue to in <paramref name="skipTo"/>. The caller decides
    /// whether the ticket actually wants that review.
    /// </summary>
    public static bool TryGetGateSkip(TicketPhase from, TicketPhase gate, out TicketPhase skipTo)
        => GateSkips.TryGetValue((from, gate), out skipTo);

    /// <summary>Phases a Blocked ticket may be unblocked into (a queue/"Needs*" state).</summary>
    private static readonly HashSet<TicketPhase> UnblockTargets = new()
    {
        TicketPhase.Backlog,
        TicketPhase.NeedsSpeculation,
        TicketPhase.NeedsApproval,
        TicketPhase.NeedsImplementation,
        TicketPhase.NeedsImplementationReview,
        TicketPhase.NeedsQa,
        TicketPhase.NeedsRefinement,
        TicketPhase.NeedsPeerReview,
        TicketPhase.NeedsReview,
    };

    /// <summary>
    /// Where an unblocked ticket should go back to: the queue phase for the work it was blocked in
    /// (<see cref="Ticket.PhaseBeforeBlock"/>), or null when there is nothing to infer from.
    /// <para>
    /// Every unblock target is legal, which is exactly the problem - offered eight equal options, the
    /// human unblocking a ticket has to reconstruct which phase it stopped in and which queue feeds
    /// that phase. Picking wrong is quiet and expensive: NeedsSpeculation throws away a plan that was
    /// fine, NeedsReview skips the work that never finished. This is a RECOMMENDATION only; the
    /// caller still offers the rest.
    /// </para>
    /// <para>
    /// The inverse of <see cref="ClaimTarget"/> for an in-progress phase. A ticket blocked while
    /// parked on a review gate or in a queue goes back to where it was standing, and one blocked out
    /// of Done has nowhere to resume to.
    /// </para>
    /// </summary>
    public static TicketPhase? ResumeAfterBlock(TicketPhase? phaseBeforeBlock) => phaseBeforeBlock switch
    {
        TicketPhase.Speculating => TicketPhase.NeedsSpeculation,
        TicketPhase.Implementing => TicketPhase.NeedsImplementation,
        TicketPhase.QaTesting => TicketPhase.NeedsQa,
        TicketPhase.Refining => TicketPhase.NeedsRefinement,
        TicketPhase.PeerReviewing => TicketPhase.NeedsPeerReview,
        not null when UnblockTargets.Contains(phaseBeforeBlock.Value) => phaseBeforeBlock.Value,
        _ => null,
    };

    /// <summary>Phases that are actively being worked by the heartbeat (act as a lock).</summary>
    public static bool IsInProgress(TicketPhase phase) => phase is
        TicketPhase.Speculating or TicketPhase.Implementing or TicketPhase.QaTesting or TicketPhase.Refining
        or TicketPhase.PeerReviewing;

    /// <summary>Phases the heartbeat pulls from and runs the matching skill.</summary>
    public static bool IsActionable(TicketPhase phase) => phase is
        TicketPhase.NeedsSpeculation or TicketPhase.NeedsImplementation or TicketPhase.NeedsQa or TicketPhase.NeedsRefinement
        or TicketPhase.NeedsPeerReview;

    public static bool IsTerminal(TicketPhase phase) => phase == TicketPhase.Done;

    // ---- Heartbeat pickup ordering (the global, project-agnostic loop) -------

    /// <summary>
    /// Phases the one heartbeat can act on: a "Needs*" queue state to <b>claim</b>, or an
    /// "-ing" state to <b>resume</b> (a run that was interrupted mid-work).
    /// </summary>
    public static readonly TicketPhase[] WorkablePhases =
    {
        TicketPhase.NeedsSpeculation, TicketPhase.Speculating,
        TicketPhase.NeedsImplementation, TicketPhase.Implementing,
        TicketPhase.NeedsQa, TicketPhase.QaTesting,
        TicketPhase.NeedsRefinement, TicketPhase.Refining,
        TicketPhase.NeedsPeerReview, TicketPhase.PeerReviewing,
    };

    public static bool IsWorkable(TicketPhase phase) => IsActionable(phase) || IsInProgress(phase);

    /// <summary>
    /// The skills a project MUST have for the heartbeat to work its tickets — one per "-ing" phase
    /// (see <see cref="SkillFor"/>). A project missing any of these has its matching tickets Blocked,
    /// so onboarding scaffolds exactly this set. Kept in sync with the SkillFor map.
    /// </summary>
    public static readonly string[] RequiredSkills = { "speculate", "implement", "qa-test", "refine" };

    /// <summary>
    /// Skills a project may have but need not. Deliberately NOT in <see cref="RequiredSkills"/>:
    /// that list is what <c>ProjectScaffolder.IsScaffolded</c> checks, so a fifth entry there would
    /// mark every existing project unscaffolded the moment this shipped, over a skill most of them
    /// have no use for. A project without one of these simply cannot work that track — the claim's
    /// own <c>skillExists</c> guard Blocks such a ticket with a message that names the missing file,
    /// which is the correct outcome and needs no extra code.
    /// <para>
    /// They still belong in the Settings panel's per-phase model list, so they are merged into it
    /// there; see <c>ConfigEndpoints.WorkerPhases</c>.
    /// </para>
    /// </summary>
    public static readonly string[] OptionalSkills = { "review-pr" };

    /// <summary>Every skill the heartbeat might run, required or not.</summary>
    public static readonly string[] AllSkills = RequiredSkills.Concat(OptionalSkills).ToArray();

    /// <summary>
    /// Position along the pipeline — higher = closer to Done. The heartbeat claims the
    /// highest-rank workable ticket first ("finish the most-progressed work before starting new").
    /// </summary>
    public static int ProgressRank(TicketPhase phase) => phase switch
    {
        TicketPhase.Backlog => 0,
        TicketPhase.NeedsSpeculation => 1,
        TicketPhase.Speculating => 2,
        TicketPhase.NeedsApproval => 3,
        TicketPhase.NeedsImplementation => 4,
        TicketPhase.Implementing => 5,

        // TIED WITH THE TWO ABOVE, ON PURPOSE. Do not "fix" this by giving them numbers of their own.
        //
        // The rank exists to order the claim, and the two tracks' phases are not otherwise
        // comparable: a Review ticket has four stations to a Delivery ticket's nine, so any number
        // that reads as "how far along" on one scale is a lie on the other. Handed the QA pair's
        // ranks (7/8) - the shape this took before the phases were split - every review ticket in
        // the queue outranked every delivery ticket still in speculation or implementation, so
        // reviews systematically preempted the work they were meant to sit alongside.
        //
        // Tying them to the implementation pair puts a review at the same place in the queue as the
        // work it is the counterpart of, and hands the decision to the ticket's TRACK - which is
        // the next key the claim orders on, and which prefers Delivery. That is the rule stated
        // whole: highest priority band first, then most-progressed, then delivery over review.
        // Take the tie away and the track tiebreak becomes unreachable code.
        TicketPhase.NeedsPeerReview => 4,
        TicketPhase.PeerReviewing => 5,

        TicketPhase.NeedsImplementationReview => 6,
        TicketPhase.NeedsQa => 7,
        TicketPhase.QaTesting => 8,
        TicketPhase.NeedsRefinement => 9,
        TicketPhase.Refining => 10,
        TicketPhase.NeedsReview => 11,
        TicketPhase.Done => 12,
        TicketPhase.Blocked => -1,
        _ => -1,
    };

    /// <summary>
    /// True when <paramref name="from"/> → <paramref name="to"/> moves <b>backwards</b> along the
    /// pipeline: QA failing, refinement finding gaps, a reviewer asking for changes, a replan.
    /// <para>
    /// This is the definition of "rework" the metrics count, kept here rather than at the call site
    /// because a bounce rate whose definition lives in two places is a bounce rate nobody trusts.
    /// Blocked edges are excluded in both directions: being blocked means the work stopped, which is
    /// a different outcome from being sent back to be redone, and unblocking would otherwise show up
    /// as forward progress the pipeline never made.
    /// </para>
    /// </summary>
    public static bool IsBackward(TicketPhase from, TicketPhase to)
    {
        if (from == TicketPhase.Blocked || to == TicketPhase.Blocked) return false;
        return ProgressRank(to) < ProgressRank(from);
    }

    /// <summary>
    /// The "-ing" phase a workable ticket is claimed into (the lock). A ticket already in an
    /// "-ing" phase is being resumed, so its claim target is itself (no transition needed).
    /// </summary>
    public static TicketPhase ClaimTarget(TicketPhase phase) => phase switch
    {
        TicketPhase.NeedsSpeculation => TicketPhase.Speculating,
        TicketPhase.NeedsImplementation => TicketPhase.Implementing,
        TicketPhase.NeedsQa => TicketPhase.QaTesting,
        TicketPhase.NeedsRefinement => TicketPhase.Refining,
        TicketPhase.NeedsPeerReview => TicketPhase.PeerReviewing,
        _ when IsInProgress(phase) => phase,
        _ => throw new ArgumentException($"{phase} is not a workable phase"),
    };

    /// <summary>
    /// The skill folder name run for an "-ing" phase. Stays a pure function of the phase — which is
    /// the dividend of splitting the Review track's working pair off rather than sharing the QA one.
    /// The claim path therefore needs no notion of a track at all.
    /// </summary>
    public static string SkillFor(TicketPhase inProgress) => inProgress switch
    {
        TicketPhase.Speculating => "speculate",
        TicketPhase.Implementing => "implement",
        TicketPhase.QaTesting => "qa-test",
        TicketPhase.Refining => "refine",
        TicketPhase.PeerReviewing => "review-pr",
        _ => throw new ArgumentException($"{inProgress} has no associated skill"),
    };

    /// <param name="track">
    /// The moving ticket's track. Every edge in <see cref="Forward"/> is filtered through it, so a
    /// Delivery ticket can never be walked onto a Review phase or the reverse. Defaults to Delivery
    /// because that is what a ticket with no opinion is; a caller that forgets to pass a Review
    /// ticket's track gets its moves REFUSED rather than misrouted, which is the safe direction to
    /// fail in.
    /// </param>
    public static bool CanTransition(TicketPhase from, TicketPhase to, TicketTrack track = TicketTrack.Delivery)
    {
        if (from == to) return false;

        // Nothing may leave its own track. Checked first because it is the one rule no other clause
        // is allowed to override - including the unblock and replan escapes below, both of which
        // name phases that exist on only one side.
        if (!IsOnTrack(to, track)) return false;

        // Anything not already blocked can be blocked.
        if (to == TicketPhase.Blocked)
            return from != TicketPhase.Blocked;

        // Requirements can move under a phase that is mid-flight. When they do, the plan is what is
        // wrong, so returning to speculation has to be reachable from any in-progress phase -
        // otherwise the only legal moves are ones that pretend the old plan still holds.
        // request_respeculation is the intended door (it also forces the review gate on).
        if (to == TicketPhase.NeedsSpeculation && IsInProgress(from))
            return true;

        // Unblock into a queue state.
        if (from == TicketPhase.Blocked)
            return UnblockTargets.Contains(to);

        return Forward.TryGetValue(from, out var allowed) && allowed.Contains(to);
    }

    /// <summary>
    /// Legal next phases from <paramref name="from"/> for a ticket on <paramref name="track"/>,
    /// including Blocked/unblock edges. Always agrees with <see cref="CanTransition"/> — the tests
    /// assert exactly that, for every phase on every track.
    /// </summary>
    public static IEnumerable<TicketPhase> NextPhases(TicketPhase from, TicketTrack track = TicketTrack.Delivery)
    {
        if (from == TicketPhase.Blocked)
            return UnblockTargets.Where(p => IsOnTrack(p, track)).ToList();

        // Deliberately does NOT include the replan escape that CanTransition allows out of an
        // in-progress phase: request_respeculation is the intended door for that, and the list stays
        // the set of hand-offs a worker should be CHOOSING between. Unchanged from before the Review
        // track existed.
        var next = (Forward.TryGetValue(from, out var f) ? f : Array.Empty<TicketPhase>())
            .Where(p => IsOnTrack(p, track))
            .ToList();

        next.Add(TicketPhase.Blocked);
        return next;
    }

    /// <summary>
    /// The legal next phases for one ticket. Every ticket's are its track's, except a walk-through
    /// in QA: it has no code to send back or refine, so it passes to NeedsReview or blocks.
    /// </summary>
    public static IEnumerable<TicketPhase> NextPhasesFor(Ticket t) =>
        t.Walkthrough && t.Phase == TicketPhase.QaTesting
            ? new[] { TicketPhase.NeedsReview, TicketPhase.Blocked }
            : NextPhases(t.Phase, t.Track);

    /// <summary><see cref="CanTransition"/> for one ticket, with a walk-through's QA exits.</summary>
    public static bool CanTransitionFor(Ticket t, TicketPhase to) =>
        t.Walkthrough && t.Phase == TicketPhase.QaTesting
            ? to is TicketPhase.NeedsReview or TicketPhase.Blocked
            : CanTransition(t.Phase, to, t.Track);

    /// <summary>Every phase either track can reach — what the board's own metadata endpoint lists.</summary>
    public static IEnumerable<TicketPhase> AllNextPhases(TicketPhase from) =>
        NextPhases(from, TicketTrack.Delivery)
            .Concat(NextPhases(from, TicketTrack.Review))
            .Distinct();
}
