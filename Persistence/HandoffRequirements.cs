using Whetstone.Models;

namespace Whetstone.Persistence;

/// <summary>
/// What a ticket must CARRY before a hand-off is allowed, as opposed to <see cref="PhaseMachine"/>,
/// which says only which hand-offs exist.
/// <para>
/// This exists because of a gap between what the skills ask for and what the pipeline enforces.
/// Everything else a phase must leave behind has a mechanical net: a dirty tree is committed, a
/// multi-commit branch is squashed, a stale branch is rebased. The two artefacts written FOR THE
/// HUMAN - the refine phase's learning brief and QA's captioned screenshots - had nothing but
/// prose in a skill file asking for them, and on one ticket both were silently skipped: QA took
/// screenshots into its capture directory and never called set_qa_shots, and refinement handed off
/// to NeedsReview without ever loading set_learning_brief. Nothing noticed, because nothing was
/// looking. These are the things that make the pipeline's output legible to the person signing it
/// off, so they are the last things that should depend on a worker remembering a step 5.
/// </para>
/// <para>
/// Neither requirement can be satisfied by the machine on the worker's behalf - only the model that
/// did the work can write the brief or judge which screenshot is worth keeping - so the enforcement
/// is a refused transition rather than a silent fix-up. The refusal names the tool to call, and the
/// same text is published on the ticket's own nextPhases guide so a worker reads it BEFORE choosing
/// a hand-off rather than after being turned away.
/// </para>
/// </summary>
public static class HandoffRequirements
{
    /// <summary>
    /// One hand-off's requirement. <paramref name="Summary"/> rides along on the ticket's
    /// nextPhases guide (read before the move); <paramref name="HowToSatisfy"/> is what the caller
    /// is told when the move is refused.
    /// </summary>
    public record Requirement(string Summary, string HowToSatisfy);

    /// <summary>
    /// Notes the ticket's owner has left that no run has answered yet, oldest first.
    /// <para>
    /// The set is defined by the absence of an acknowledgement rather than by the presence of a
    /// delivery, and that asymmetry is the point: a note is outstanding until a worker has said what
    /// it did about it, so a run that read one and then died leaves it outstanding for the next run
    /// rather than burning it on a run that achieved nothing.
    /// </para>
    /// <para>
    /// Human-authored only. Claude's own notes are its scratchpad for the next run and the System's
    /// are the phase log; neither is addressed to anyone, and gating a hand-off on them would make
    /// every ticket unmovable.
    /// </para>
    /// </summary>
    public static IReadOnlyList<Comment> UnansweredOwnerNotes(Ticket t) => t.Comments
        .Where(c => AuthorKinds.IsDirective(c.Author) && c.AcknowledgedAt is null)
        .OrderBy(c => c.CreatedAt)
        .ToList();

    /// <summary>
    /// Why an unanswered owner note stands in the way of <paramref name="from"/> →
    /// <paramref name="to"/>, or null when it does not.
    /// <para>
    /// Kept apart from <see cref="For"/> rather than folded in as another arm of its switch, because
    /// it is not the same kind of rule. Those are per-hand-off and mutually exclusive — a ticket
    /// leaving QA owes screenshots, a ticket leaving refinement owes a brief. This one applies to
    /// EVERY hand-off out of a working phase and can be outstanding at the same time as one of them,
    /// so a single-arm switch would have had to pick which of the two to mention and hide the other.
    /// </para>
    /// <para>
    /// Exempt: moves to Blocked, which are never gated on anything (a run that cannot continue must
    /// always be able to say so — and a note it does not understand is itself a reason to block), and
    /// moves made from a phase nobody is working, which are the human's own.
    /// </para>
    /// </summary>
    public static Requirement? OwnerNoteRequirement(Ticket t, TicketPhase from, TicketPhase to)
    {
        if (to == TicketPhase.Blocked || !PhaseMachine.IsInProgress(from)) return null;

        var outstanding = UnansweredOwnerNotes(t);
        if (outstanding.Count == 0) return null;

        var n = outstanding.Count;
        var subject = n == 1 ? "a note" : $"{n} notes";
        return new Requirement(
            $"REQUIRES answering {subject} the ticket's owner left. Call acknowledge_note on each "
          + "before you hand off — the owner is the highest authority on this ticket and their note "
          + "may change what this phase should have done.",
            $"This ticket carries {subject} from its owner that no run has answered. Read "
          + $"{(n == 1 ? "it" : "them")} in get_ticket's comments (author \"Human\", acknowledged "
          + "false) along with any files attached, DO what the note asks — it outranks the ticket "
          + "description and your skill's defaults — and then call acknowledge_note for each one "
          + "with what you actually did about it. If a note asks for something you cannot or should "
          + "not do, acknowledge_note is still how you say so, and then set_phase to Blocked with "
          + "the question. What you must not do is hand off around it: the note is the owner's most "
          + "recent statement of what this ticket is for, and a hand-off that ignored it is built on "
          + "a stale reading of the ticket.");
    }

    /// <summary>
    /// The findings of the independent analysis this refinement pass was handed, in order. Empty
    /// when the pass was handed none.
    /// </summary>
    public static IReadOnlyList<GateFinding> CurrentGateFindings(Ticket t) => t.GateAnalysisId is not { } id
        ? Array.Empty<GateFinding>()
        : t.GateFindings.Where(f => f.AnalysisId == id).OrderBy(f => f.Order).ToList();

    /// <summary>
    /// Why the independent analysis's findings stand in the way of <paramref name="from"/> →
    /// <paramref name="to"/>, or null when they do not.
    /// <para>
    /// Only the clean hand-off is gated. Refinement taking a finding back to implementation is the
    /// finding doing its job, and a block is never gated on anything. Every finding must be RULED -
    /// not fixed, not agreed with - because the analysis is a second reader, not an authority: it
    /// raises things that turn out to be fine, and the ruling with its reason is what the person
    /// signing the ticket off reads to tell the two apart. What it may not be is ruled a defect and
    /// then handed off anyway.
    /// </para>
    /// </summary>
    public static Requirement? GateFindingRequirement(Ticket t, TicketPhase from, TicketPhase to)
    {
        if (from != TicketPhase.Refining || to != TicketPhase.NeedsReview) return null;

        var findings = CurrentGateFindings(t);
        if (findings.Count == 0) return null;

        var unruled = findings.Where(f => f.Ruling is null).ToList();
        var defects = findings.Where(f => f.Ruling == GateRuling.Defect).ToList();
        if (unruled.Count == 0 && defects.Count == 0) return null;

        static string Names(IEnumerable<GateFinding> fs) =>
            string.Join("; ", fs.Select(f => $"#{f.Id} \"{f.Title}\""));

        var summary = $"REQUIRES a ruling on each of the {findings.Count} finding(s) the independent Classroom "
            + "analysis raised (rule_on_finding), and none ruled Defect - a Defect goes back to "
            + "NeedsImplementation instead of forward.";

        var how = defects.Count > 0
            ? $"You ruled {Names(defects)} a Defect, so this ticket is not ready for review. Send it to "
            + "NeedsImplementation with an add_comment saying what is wrong, where, and what to change - and, "
            + "where the defect is something a QA item should have caught, add that QA item. If a ruling was "
            + "wrong, call rule_on_finding again with NotADefect and the reason."
            : $"Not yet ruled: {Names(unruled)}. For each, read what the finding claims, check it against the "
            + "code yourself, then call rule_on_finding with Defect or NotADefect and the reason. NotADefect "
            + "needs the evidence that settles it, not the plan's intent: the plan is what the finding is "
            + "checking.";

        return new Requirement(summary, how);
    }

    /// <summary>
    /// Everything the ticket's own nextPhases guide should say about this hand-off — the per-hand-off
    /// artefact requirement, the outstanding-notes requirement, or both. This is what a worker reads
    /// BEFORE choosing a move, so it has to carry every reason the move could be refused; being
    /// turned away by a rule the guide did not mention is what teaches a worker to stop reading it.
    /// </summary>
    public static string? GuideSummary(Ticket t, TicketPhase from, TicketPhase to)
    {
        var parts = new[] { OwnerNoteRequirement(t, from, to)?.Summary, GateFindingRequirement(t, from, to)?.Summary,
                For(from, to, t)?.Summary }
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToArray();
        return parts.Length == 0 ? null : string.Join(" ALSO: ", parts);
    }

    /// <summary>
    /// What <paramref name="from"/> → <paramref name="to"/> demands of the ticket's own record, if
    /// anything. <paramref name="t"/> is optional and only narrows the answer: a code-review-only
    /// ticket is asked for no screenshots, because it never started the app. Passing it matters for
    /// the published nextPhases guide — a requirement advertised there but never enforced trains a
    /// worker to discount the guide, which is the one thing that makes it worth publishing.
    /// </summary>
    public static Requirement? For(TicketPhase from, TicketPhase to, Ticket? t = null) => (from, to) switch
    {
        // A walk-through's pass is held to the same proof as any QA pass: every step, on this pass, with shots.
        (TicketPhase.QaTesting, TicketPhase.NeedsReview) when t is { Walkthrough: true } => For(TicketPhase.QaTesting, TicketPhase.NeedsRefinement),
        (TicketPhase.QaTesting, TicketPhase.NeedsRefinement) when t is { SkipQaTesting: true } => null,

        // The one requirement not about something already written on the ticket, so it is the one
        // checked in TicketService.TransitionAsync rather than in Unmet below (which is handed the
        // ticket, not the move's reason). It is declared here anyway because this is what the
        // nextPhases guide publishes, and a blocked ticket with no reason on it is the single most
        // expensive omission in the pipeline: it stops the work AND withholds the thing a person
        // needs in order to restart it.
        (_, TicketPhase.Blocked) => new Requirement(
            "REQUIRES either 'questions' the owner can answer by picking, or a 'reason' saying what a "
          + "person has to do about it. Name the contradiction, or ask the specific question. "
          + "'Blocked' is not a reason.",
            "Call set_phase to Blocked again, carrying one of these two. If you are asking the owner "
          + "to DECIDE something - a contradiction, a missing spec, anything with more than one "
          + "defensible reading - pass 'questions': each one line of 200 characters or less (at least "
          + "3 words), its evidence in that question's 'why', and the choices you would accept in its "
          + "'options'. Mark one option 'recommended' wherever you have a defensible default, because "
          + "an unanswered question falls back to it. If you are only REPORTING a fault - a dirty "
          + "tree, a build that will not run - pass 'reason' instead: one full sentence that stands on "
          + "its own to someone who has not read your run (20 characters and 4 words is the floor, and "
          + "a bare 'stuck' or 'unclear' is rejected whatever its length). Either way the human answers "
          + "what you send and it is quoted back to the run that picks this ticket up, so a vague block "
          + "costs a whole cycle. Use add_comment as well when the detail runs long."),

        // ---- The Review track's one hand-off ---------------------------------
        // Note what is NOT asked for: that the QA items PASSED. A peer review that drove the app and
        // found three defects has done its job perfectly, and the findings are the deliverable. The
        // delivery track's rule is the opposite because there the failures are ours to go and fix;
        // here the code belongs to someone else and there is no route back to implementation at all.
        // So the bar is that every item was taken to AN OUTCOME - tested and judged, either way.
        (TicketPhase.PeerReviewing, TicketPhase.NeedsReview) when t is { SkipQaTesting: true } => new Requirement(
            "REQUIRES a verdict recorded on this pass (set_review_verdict). This ticket is marked "
          + "code-review-only, so no checklist and no screenshots are expected of it.",
            "Call set_review_verdict with what you concluded about this branch - Approve, "
          + "ApproveWithComments, RequestChanges, or CannotTell - and a one-line summary, then "
          + "set_phase again. The verdict is the whole point of the pass: staged comments say what is "
          + "wrong with individual lines, and the verdict is the only place the run says what it "
          + "thinks of the change as a whole."),

        (TicketPhase.PeerReviewing, TicketPhase.NeedsReview) => new Requirement(
            "REQUIRES a verdict recorded on this pass, every QA item taken to an outcome (Passed, "
          + "Failed or Blocked - a Failed item is a RESULT here, not a blocker), and either captioned "
          + "QA shots or a recorded reason there are none.",
            "Work the checklist to a conclusion - start_qa_item, drive it, then set_qa_status with "
          + "whatever you actually observed - and write the checklist first if this ticket has none, "
          + "deriving it from the linked issue's acceptance criteria and the diff. Attach 3-6 "
          + "captioned screenshots with set_qa_shots (or an empty list with a noShotsReason if this "
          + "change shows nowhere on screen). Then call set_review_verdict and set_phase again. Do "
          + "NOT send the ticket back or block it because you found defects: staging them and handing "
          + "off IS the successful outcome of this phase."),

        (TicketPhase.Refining, TicketPhase.NeedsReview) => new Requirement(
            "REQUIRES a learning brief written on this pass. "
          + "The human signing this off did not write any of it - the brief is the only part of the "
          + "hand-off addressed to them. Call set_learning_brief before set_phase.",
            "Call set_learning_brief with 350-600 words in the five sections your skill describes "
          + "(the area / how it worked before / what changed and why / why this way / where to look), "
          + "then set_phase to NeedsReview again. There is no way past this one: nothing else in the "
          + "pipeline can write it, because nothing else read the diff. If the ticket is going back "
          + "for rework instead, take that route - the brief is only required on the clean hand-off."),

        (TicketPhase.QaTesting, TicketPhase.NeedsRefinement) => new Requirement(
            "REQUIRES every QA item to carry a Passed recorded on THIS pass — a pass from before the "
          + "ticket last went back for rework has expired, because it is a verdict on code that has "
          + "since changed — and either captioned QA shots or a recorded reason there are none. "
          + "Screenshots are strongly preferred - attach them whenever the work is visible anywhere "
          + "in the app. The empty-set escape hatch is for work with genuinely nothing to show "
          + "(a stored-procedure or migration change, a build fix), not for a run that ran out of time.",
            "Re-test each item this message names - start_qa_item, exercise it against the branch as "
          + "it stands now, then set_qa_status - and write the checklist first if this ticket has "
          + "none. Then either call set_qa_shots with 3-6 captioned screenshots of the finished "
          + "feature - three when there is more than one thing worth showing, and up to six when "
          + "there is more than that - or, if this ticket has nothing to photograph, call "
          + "set_qa_shots with an empty shots list and a "
          + "noShotsReason saying what kind of change it is and why no screen shows it. Then "
          + "set_phase again. 'I did not take any' is not a reason; 'stored-procedure change with no "
          + "UI surface' is."),

        _ => null,
    };

    /// <summary>
    /// Why <paramref name="from"/> → <paramref name="to"/> must be refused for this ticket, or null
    /// when it may proceed.
    /// <para>
    /// <paramref name="lastBackwardAt"/> is when this ticket last moved backwards along the pipeline
    /// (null if it never has). Both artefacts describe work that a bounce can change underneath
    /// them, so one written before the last bounce counts as absent - the alternative is a brief
    /// confidently describing a solution that no longer exists.
    /// </para>
    /// <para>
    /// Returns null for a move to Blocked even though <see cref="For"/> declares a requirement on it:
    /// that one is about the reason accompanying the move rather than the ticket's own record, so it
    /// is enforced where the reason is (TicketService.TransitionAsync). Nothing here can see it.
    /// </para>
    /// </summary>
    public static string? Unmet(Ticket t, TicketPhase from, TicketPhase to, DateTime? lastBackwardAt)
    {
        // Checked before the artefact requirements, because it can invalidate them. A note asking for
        // a change to the code makes the learning brief describing the old code wrong; refusing on
        // the brief first would send the run off to rewrite an artefact it is about to invalidate.
        var ownerNotes = OwnerNoteRequirement(t, from, to);
        if (ownerNotes is not null) return ownerNotes.HowToSatisfy;

        // Before the learning brief for the same reason as the notes: a finding ruled a defect sends
        // the work back, and a brief written first would describe code that is about to change.
        var findings = GateFindingRequirement(t, from, to);
        if (findings is not null) return findings.HowToSatisfy;

        var req = For(from, to, t);
        if (req is null) return null;
        // A walk-through passing straight to review is checked as the QA pass it is.
        if (t.Walkthrough && from == TicketPhase.QaTesting && to == TicketPhase.NeedsReview) to = TicketPhase.NeedsRefinement;

        if (from == TicketPhase.Refining && to == TicketPhase.NeedsReview)
        {
            if (string.IsNullOrWhiteSpace(t.LearningBrief))
                return $"This ticket has no learning brief, so it cannot go to NeedsReview. {req.HowToSatisfy}";

            if (IsStale(t.LearningBriefAt, lastBackwardAt))
                return "This ticket's learning brief was written before it last went back for rework, "
                     + "so it describes a version of the work that has since changed. Rewrite it for the "
                     + $"branch as it stands now. {req.HowToSatisfy}";

            return null;
        }

        if (from == TicketPhase.PeerReviewing && to == TicketPhase.NeedsReview)
        {
            // The conclusion first, because it is the one artefact required on every review pass -
            // including the code-review-only ones, which are excused the checklist and the shots but
            // not the judgement. A pass that read a branch and recorded nothing about it is
            // indistinguishable from a pass that never ran.
            if (t.ReviewTarget?.Verdict is null)
                return "This review pass has recorded no verdict, so nothing says what it concluded "
                     + $"about the branch. {req.HowToSatisfy}";

            if (IsStale(t.ReviewTarget.VerdictAt, lastBackwardAt))
                return "This ticket's verdict was recorded before it last went back for another pass, "
                     + "so it judges a version of the branch the author has since pushed over. "
                     + $"Record what you conclude about the branch as it stands now. {req.HowToSatisfy}";

            // A code-review-only review owes nothing further: it never started the app, so it has no
            // checklist to finish and no screen to photograph.
            if (t.SkipQaTesting) return null;

            var unresolved = UnresolvedForReview(t, lastBackwardAt);
            if (unresolved is not null) return unresolved + req.HowToSatisfy;

            if (t.QaShots.Count > 0) return null;
            if (!string.IsNullOrWhiteSpace(t.QaShotsWaiver) && !IsStale(t.QaShotsWaiverAt, lastBackwardAt))
                return null;

            return "This review is about to hand off carrying no screenshots at all, which leaves the "
                 + "person sending the feedback nothing to attach to it. "
                 + req.HowToSatisfy;
        }

        if (from == TicketPhase.QaTesting && to == TicketPhase.NeedsRefinement)
        {
            // Checked before the screenshots, for the same reason the owner's notes are checked
            // before the learning brief: an untested item means this run is not finished testing,
            // and refusing on the shots first would send it off to photograph work it has not yet
            // exercised.
            var unverified = Unverified(t, lastBackwardAt);
            if (unverified is not null) return unverified + req.HowToSatisfy;

            // Real shots win outright, at any age: a ticket that carries screenshots is not the
            // failure this guards against, and re-photographing an unchanged screen on every bounce
            // would be busywork the skill already asks for by other means.
            if (t.QaShots.Count > 0) return null;

            if (!string.IsNullOrWhiteSpace(t.QaShotsWaiver) && !IsStale(t.QaShotsWaiverAt, lastBackwardAt))
                return null;

            var stale = !string.IsNullOrWhiteSpace(t.QaShotsWaiver);
            return (stale
                    ? "The recorded reason for having no QA shots predates this ticket's last trip back "
                    + "for rework, so it says nothing about what QA just tested. "
                    : "This ticket is about to leave QA carrying no screenshots at all, which leaves the "
                    + "reviewer nothing to look at. ")
                 + req.HowToSatisfy;
        }

        return null;
    }

    /// <summary>An artefact written before the ticket last went backwards describes work that has since moved.</summary>
    private static bool IsStale(DateTime? writtenAt, DateTime? lastBackwardAt) =>
        lastBackwardAt is not null && (writtenAt is null || writtenAt < lastBackwardAt);

    /// <summary>
    /// Whether this QA item currently counts as verified — Passed, on evidence recorded since the
    /// ticket last went back for rework.
    /// <para>
    /// The staleness half is the same rule the learning brief and the shots waiver already live
    /// under, applied to the artefact that was missing it. A rework bounce means the branch changed;
    /// a Passed from before it is a verdict about code that no longer exists, and the run that
    /// inherits it has no way to tell it apart from one recorded five minutes ago.
    /// </para>
    /// </summary>
    public static bool IsVerified(QaItem q, DateTime? lastBackwardAt) =>
        q.Status == QaStatus.Passed && !IsStale(q.VerifiedAt, lastBackwardAt);

    /// <summary>
    /// Why this ticket's QA checklist does not yet support leaving QA, or null when every item on it
    /// carries a current Passed.
    /// <para>
    /// Three distinct failures, told apart because the fix differs: no checklist at all, items never
    /// taken to an outcome, and outcomes that have expired. The last is the one that motivated all
    /// of this — a checklist can be entirely green and still describe a build from four days and
    /// three rework bounces ago, which is how a regression reaches a reviewer looking tested.
    /// </para>
    /// <para>
    /// Failed and Blocked items count as unresolved rather than getting their own message: this
    /// transition is the clean hand-off, and either of those routes to NeedsImplementation instead.
    /// A run holding one is not being asked to re-test it, it is being told it took the wrong exit.
    /// </para>
    /// </summary>
    private static string? Unverified(Ticket t, DateTime? lastBackwardAt)
    {
        if (t.QaItems.Count == 0)
            return "This ticket has no QA checklist, so nothing about it has been tested and there is "
                 + "no record for a reviewer to read. Write one from the description, the acceptance "
                 + "criteria, the work items and the diff, then run it. ";

        var items = t.QaItems.OrderBy(q => q.Order).ToList();
        var stale = items.Where(q => q.Status == QaStatus.Passed && IsStale(q.VerifiedAt, lastBackwardAt)).ToList();
        var open = items.Where(q => q.Status != QaStatus.Passed).ToList();

        if (stale.Count == 0 && open.Count == 0) return null;

        var parts = new List<string>();
        if (open.Count > 0)
            parts.Add($"{Describe(open)} not been taken to a Passed outcome on this run");
        if (stale.Count > 0)
            parts.Add($"{Describe(stale)} last passed before this ticket went back for rework, so "
                    + "that verdict describes a version of the branch that has since changed");

        return $"This ticket cannot leave QA yet: {string.Join("; and ", parts)}. "
             + $"The items to deal with are {Ids(stale.Concat(open))}. ";
    }

    /// <summary>
    /// Why this review's checklist does not yet support handing off, or null when every item on it
    /// has been taken to an outcome recorded on this pass.
    /// <para>
    /// The counterpart to <see cref="Unverified"/>, and deliberately a weaker bar. That one demands
    /// a <see cref="QaStatus.Passed"/>, because on the delivery track anything else means our own
    /// work is not finished. Here a <see cref="QaStatus.Failed"/> is the finished product: it is a
    /// defect found in someone else's branch, which is what the whole pass was for. What is refused
    /// is an item nobody took a view on — and an outcome recorded before the author's last push,
    /// which describes a branch that no longer exists, exactly as it does on the other track.
    /// </para>
    /// </summary>
    private static string? UnresolvedForReview(Ticket t, DateTime? lastBackwardAt)
    {
        if (t.QaItems.Count == 0)
            return "This review has no checklist, so nothing about the branch was actually exercised "
                 + "and there is no record of what was tried. Write one from the linked issue's "
                 + "acceptance criteria and the diff, then run it. ";

        var items = t.QaItems.OrderBy(q => q.Order).ToList();
        var untested = items.Where(q => q.Status == QaStatus.Todo).ToList();
        var stale = items.Where(q => q.Status != QaStatus.Todo && IsStale(q.VerifiedAt, lastBackwardAt)).ToList();

        if (untested.Count == 0 && stale.Count == 0) return null;

        var parts = new List<string>();
        if (untested.Count > 0)
            parts.Add($"{Describe(untested)} not been taken to an outcome on this pass");
        if (stale.Count > 0)
            parts.Add($"{Describe(stale)} last recorded an outcome before the branch was pushed over, "
                    + "so it describes code that has since changed");

        return $"This review cannot hand off yet: {string.Join("; and ", parts)}. "
             + $"The items to deal with are {Ids(stale.Concat(untested))}. ";
    }

    /// <summary>"QA item 281 has" / "3 QA items have" — so the sentence around it reads either way.</summary>
    private static string Describe(IReadOnlyCollection<QaItem> items) =>
        items.Count == 1 ? $"QA item {items.First().Id} has" : $"{items.Count} QA items have";

    private static string Ids(IEnumerable<QaItem> items) =>
        string.Join(", ", items.Select(q => q.Id).Distinct().OrderBy(id => id));
}
