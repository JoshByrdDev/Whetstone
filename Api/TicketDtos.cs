using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Whetstone.Services.Tracker;

namespace Whetstone.Api;

// ---- Response DTOs (avoid EF navigation cycles; phases as enum-name strings) ----
// Title/Description are the EFFECTIVE values: for a linked ticket they come live from the tracker
// (LinkedIssueResolver), not the DB. IssueUnavailable = the tracker was unreachable, so Title fell back
// to the key (or a last-known cached value).
public record TicketSummaryDto(
    int Id, string ProjectKey, string Title, string Phase,
    string? IssueKey, string? IssueUrl, bool IssueUnavailable, string? Branch, double BoardOrder,
    int WorkItemsTotal, int WorkItemsDone, int QaTotal, int QaVerified,
    int CommentCount, string? BlockedReason, string? PhaseBeforeBlock, DateTime UpdatedAt,
    // Waiting on something else. Distinct from the Blocked PHASE: a blocked ticket has stopped and
    // needs a person, whereas this one is fine and will be picked up on its own once the blocker
    // finishes. The board has to show it either way, or the heartbeat looks like it is ignoring a
    // ticket that is sitting right at the front of the queue.
    bool Waiting = false, IReadOnlyList<string>? WaitingOn = null,
    // The one item being worked right now — the work item under implementation, or the QA item
    // under test — so the board row can say what the agent is on without anyone opening the drawer.
    // Null unless the ticket is in a phase that has such a thing and something is actually flagged.
    string? CurrentItem = null,
    // Code review only. On the board this exists to disambiguate a QA row sitting at 0/0: without
    // it, a ticket doing exactly what was asked looks identical to a run that has stalled.
    bool SkipQaTesting = false,
    // Where a Blocked ticket is recommended to go back to. Null unless the ticket is blocked and its
    // pre-block phase says something. See PhaseMachine.ResumeAfterBlock.
    string? UnblockTo = null,
    // Notes the owner has left that no run has answered yet. On the BOARD row rather than only in
    // the drawer because an unanswered note holds the ticket's next hand-off: from outside, a ticket
    // waiting on one is indistinguishable from a ticket the heartbeat has quietly stopped working.
    int UnansweredNotes = 0,
    // The band this ticket is claimed from (TicketPriority, by name). On the board row because the
    // rows are drawn in phase order and the claim reads them in priority order: without it, a ticket
    // being worked ahead of the one above it looks like the heartbeat picking at random. The board
    // only draws it when it is not the default — see the client's DEFAULT_PRIORITY.
    string Priority = nameof(TicketPriority.Normal),
    // Which pipeline this ticket runs on (TicketTrack, by name). On the board row because the row's
    // own phase does not always say: Backlog, NeedsReview, Done and Blocked are shared by both
    // tracks, so without this a review ticket parked at the gate is indistinguishable from a
    // delivery ticket parked at the same one — and they want different words and different actions.
    string Track = nameof(TicketTrack.Delivery),
    // Staged feedback waiting to be curated, and whether it has been sent. Together these are what
    // let the gate say "4 comments staged" or "submitted - awaiting dev" instead of a bare phase
    // name; see ReviewTarget.SubmittedAt for why that is a stamp rather than a phase of its own.
    int ReviewCommentCount = 0, bool ReviewSubmitted = false,
    // The branch under review. Kept apart from Branch above rather than folded into it: that field
    // means "the branch this ticket OWNS", and a review ticket owns none. Merging them would make
    // every existing reader of Branch quietly start treating someone else's work as ours.
    string? ReviewBranch = null,
    // How many expansion items are holding this ticket at NeedsApproval when its own setting would
    // have skipped the gate; 0 otherwise. On the row because a ticket parked at a gate its owner
    // switched off looks like the skip broke. See WorkItemScopes.HeldForExpansion.
    int HeldForExpansion = 0,
    // Sitting in NeedsRefinement while its independent analysis is written. On the row because the
    // claim passes such a ticket over, and without it that looks like the heartbeat ignoring it.
    bool AwaitingAnalysis = false);

/// <summary>One thing a ticket is waiting on, resolved as far as it can be.</summary>
public record BlockerDto(
    int Id, string Label, string? IssueKey, int? LocalTicketId, string Origin,
    bool? Override, string? Note, string? Status, bool Satisfied, bool Unknown);

public record WorkItemDto(int Id, string Text, bool Done, int Order, DateTime? StartedAt = null,
    string Scope = nameof(WorkItemScope.Required));
/// <param name="Verified">Whether this item's Passed still describes the branch as it stands — false
/// for anything not Passed, and for a Passed recorded before the ticket last went back for rework.
/// Computed rather than stored, because it is a claim about the item AND the ticket's history; a
/// caller with no history to hand (the tests, the board summary) gets the honest false.</param>
/// <param name="PassedByInspection">The pass was argued from the code rather than driven through the
/// app. A weaker claim than an executed test, and recorded separately so nobody has to infer it.</param>
public record QaItemDto(int Id, string Text, string Status, string? Note, int Order, DateTime? StartedAt = null,
    DateTime? VerifiedAt = null, bool Verified = false, bool PassedByInspection = false);
/// <summary>
/// One file attached to a progress note, addressed to both readers at once.
/// <para>
/// <paramref name="Url"/> is what the drawer renders. <paramref name="LocalPath"/> is a real file on
/// this machine that a worker opens with the Read tool — the only way an agent can look at a
/// screenshot at all, and the same shape <see cref="TicketAttachmentDto"/> uses for the tracker's.
/// </para>
/// </summary>
public record NoteAttachmentDto(int Id, string FileName, string Url, string LocalPath, long Bytes, bool IsImage);

/// <param name="Acknowledged">True once a worker has said what it did about this note. Only ever
/// true on a Human note; the other authors' notes are not addressed to anyone.</param>
/// <param name="Acknowledgement">What the worker said it did, rendered under the note itself.</param>
/// <param name="DeliveredAt">When this note was first put in front of a run, so the owner can tell
/// "nobody has looked yet" apart from "a run has seen this and not answered it".</param>
public record CommentDto(int Id, string Author, string Body, string? Phase, DateTime CreatedAt,
    IReadOnlyList<NoteAttachmentDto>? Attachments = null,
    bool Acknowledged = false, string? Acknowledgement = null,
    DateTime? DeliveredAt = null, DateTime? AcknowledgedAt = null);
// ImageUrl is the API route that streams the stored screenshot bytes, with a ?v= stamped from the
// row's CreatedAt.
//
// The stamp is what keeps a caption and its picture telling the same story. The route on its own is
// stable for the life of the ticket - slot 0 is slot 0 forever - but the bytes behind it are not: a
// QA re-run rewrites them in place. The captions travel in this JSON, which carries no cache headers
// and so is always refetched, while the image response carries Last-Modified and no ETag, which
// invites the browser to invent a freshness lifetime for it (~10% of the file's age). The older the
// shots, the longer that window - so precisely when a re-run replaces a stale set, the reviewer is
// most likely to read the new captions over the old screenshots and conclude QA photographed the
// wrong thing. Minting a new URL whenever the shots are replaced is what makes that unable to happen.
public record QaShotDto(int Order, string Caption, string ImageUrl, ImageRect? Focus = null)
{
    /// <summary>The DTO for one stored shot, with the cache-busting stamp its URL depends on.</summary>
    public static QaShotDto From(int ticketId, QaShot s) =>
        new(s.Order, s.Caption, $"/api/tickets/{ticketId}/qa-shots/{s.Order}?v={s.CreatedAt.Ticks}", s.Focus());
}

/// <summary>
/// An attachment from the linked issue, already downloaded to this machine. LocalPath is a real file a worker can
/// open with the Read tool — it holds no tracker credentials of its own, so this is its only route to one.
/// </summary>
/// <param name="SourceIssue">Null when the attachment hangs off this ticket's own linked issue;
/// otherwise the ancestor issue it was inherited from, so a parent's mockup is readable without
/// being mistaken for something attached to this ticket.</param>
public record TicketAttachmentDto(string FileName, string LocalPath, long Bytes, string? SourceIssue = null);

/// <summary>
/// What a Review ticket is reviewing. Null on every Delivery ticket, which is how a client tells
/// the two apart without reasoning about phases.
/// </summary>
/// <param name="Stale">The author has pushed since the last pass — <c>HeadSha</c> and
/// <c>ReviewedAtSha</c> disagree — so every staged comment's line numbers describe a branch that has
/// moved. Computed rather than stored: it is a comparison of two columns, and a third column saying
/// the same thing is a third column that can be wrong.</param>
public record ReviewTargetDto(
    string? PrId, string? PrUrl, string SourceBranch, string? TargetBranch, string? PrAuthor,
    string? ReviewedAtSha, string? HeadSha, string? Verdict, DateTime? SubmittedAt, bool Stale);

/// <summary>
/// One piece of staged feedback. <paramref name="Stale"/> means the file this points at changed
/// after the comment was written, so its line numbers are no longer trustworthy — the comment is
/// kept and flagged rather than dropped, because the reasoning behind it is the expensive half.
/// </summary>
public record ReviewCommentDto(
    int Id, string? FilePath, int? Line, string Side, string Body, string Severity, string Status,
    string Author, string? AnchorSha, int? QaItemId, int? QaShotOrder, DateTime? SubmittedAt,
    int Order, DateTime CreatedAt, bool Stale = false);

public record TicketDetailDto(
    int Id, string ProjectKey, string Title, string? Description, string Phase,
    string? IssueKey, string? IssueUrl, bool IssueUnavailable, string? Branch, string? WorktreePath,
    // Which tracker this project's issues live in, and what to call it. On the DETAIL shape rather
    // than the summary because the drawer is where the tracker is actually named — "View in Jira"
    // against an Azure Boards work item is an instruction to go and look in the wrong system — and
    // because the blocker input has to know whether a bare number is a local ticket or a work item.
    // TrackerKind is the stable token to branch on ("Jira" / "AzureDevOps" / "None"); TrackerName is
    // the prose one to print.
    string TrackerKind, string TrackerName,
    string? ImplementationSummary, string? MasterNotes,
    bool RequiresSpeculationReview, bool RequiresImplementationReview,
    // Code-review-only: QA reads the diff but never runs the app, and no phase writes QA items.
    // Read by the worker off the claim payload (to leave the browser out) and by the skills
    // themselves through get_ticket, which is why it rides on the detail shape rather than a flag
    // passed separately to the heartbeat.
    bool SkipQaTesting,
    // One-shot routing for a reviewer's change request: the next implementation comes straight back
    // to NeedsReview. On the detail shape so the drawer can say so while it is pending — a ticket
    // that is going to skip QA should say that on its face, not only in the transition log.
    bool ShortLoopReview,
    string? BlockedReason, string? PhaseBeforeBlock, double BoardOrder,
    DateTime CreatedAt, DateTime UpdatedAt,
    IReadOnlyList<WorkItemDto> WorkItems, IReadOnlyList<QaItemDto> QaItems,
    IReadOnlyList<CommentDto> Comments, IReadOnlyList<QaShotDto> QaShots,
    IReadOnlyList<string> NextPhases,
    // Same moves as NextPhases, each with what it is FOR. The bare names read as interchangeable to
    // an autonomous worker picking its own hand-off; the guide is what tells it which one is the
    // success path. The UI keeps using NextPhases.
    IReadOnlyList<NextPhaseDto> NextPhaseGuide,
    IReadOnlyList<BlockerDto>? Blockers = null,
    // The refine phase's reading material for the human, plus the heading to show it under — which
    // is per-project ("Learn <project>"), so it is resolved from config rather than hard-coded in the UI.
    string? LearningBrief = null, DateTime? LearningBriefAt = null, string? LearningLabel = null,
    // Linked-issue attachments cached on this machine, with a note saying what the list does and does not
    // cover. Filled in by the MCP get_ticket tool — it is the one call every phase makes, so it is
    // the only place a design attachment is guaranteed to be seen. The REST API leaves both null.
    IReadOnlyList<TicketAttachmentDto>? Attachments = null, string? AttachmentsNote = null,
    // Why this ticket has no QA shots, when QA said so explicitly. Rendered in their place, so a
    // reader can tell "nothing to photograph" from "the phase forgot" — which is the whole reason
    // QA is made to answer rather than made to produce screenshots.
    string? QaShotsWaiver = null,
    // Where this Blocked ticket is recommended to go back to (see PhaseMachine.ResumeAfterBlock).
    // Every NextPhase remains legal; this only saves the human from reconstructing which queue feeds
    // the phase the work actually stopped in.
    string? UnblockTo = null,
    // The block as the questions the worker actually asked, when it asked in questions. BlockedReason
    // above is rendered from this and stays authoritative — a client that ignores this field loses
    // nothing but the ability to answer by clicking. Null for a prose block, a hand-written one, or
    // anything blocked before the structured form existed.
    BlockPayload? BlockedQuestions = null,
    // The band this ticket is claimed from (TicketPriority, by name). Readable by a worker through
    // get_ticket — knowing it is urgent is context worth having — but there is no tool to change it.
    string Priority = nameof(TicketPriority.Normal),
    // Which pipeline this ticket runs on (TicketTrack, by name). Readable by a worker so a review
    // run can confirm what it is holding; there is no tool that changes it, for the same reason
    // there is none for Priority — a ticket that could switch tracks mid-flight would be sitting in
    // a phase its new track does not have.
    string Track = nameof(TicketTrack.Delivery),
    // The branch under review and how far we have got with it. Null for a Delivery ticket.
    ReviewTargetDto? Review = null,
    // Feedback staged against that branch, awaiting the owner's curation. A worker can read what has
    // already been said — which is what stops a re-review repeating itself — and can add to it, but
    // nothing here reaches the pull request without a human pressing a button.
    IReadOnlyList<ReviewCommentDto>? ReviewComments = null,
    // Same as the summary's: expansion items holding this ticket at a gate its setting skips.
    int HeldForExpansion = 0,
    // Refinement goes ahead without waiting for an independent Classroom analysis. The human's
    // setting, like SkipQaTesting; readable by a worker, settable by none.
    bool SkipClassroomAnalysis = false,
    // The analysis this refinement pass was handed and what refinement has ruled on each of its
    // findings. Null when the ticket's current pass was handed none.
    IndependentReviewDto? IndependentReview = null);

/// <summary>One finding of the independent analysis, and refinement's ruling on it.</summary>
public record GateFindingDto(int Id, string Kind, string Title, string Text,
    string? Ruling, string? RulingNote, DateTime? RuledAt);

/// <summary>
/// What an independent Classroom analysis raised about the commit refinement is working on. The
/// note is addressed to the refinement run, which reads this through get_ticket.
/// </summary>
public record IndependentReviewDto(int AnalysisId, string Note, IReadOnlyList<GateFindingDto> Findings);

/// <summary>
/// One legal hand-off: what it is for, and what the ticket must be carrying before it will be
/// accepted. <paramref name="Requirement"/> is published here rather than left to the refusal
/// message so a worker meets it on the way in — the same guidance arriving only as an error is
/// guidance that costs a wasted attempt every time.
/// </summary>
public record NextPhaseDto(string Phase, string? Meaning, string? Requirement = null);

/// <summary>
/// Maps entities to DTOs. Shared by the REST API and the MCP tools. The optional effective
/// title/description override the stored values so callers can inject the tracker-resolved ones.
/// </summary>
public static class TicketMapper
{
    // For a linked ticket, title/description are ALWAYS the effective (tracker-resolved) values —
    // never the stored ones (which are null by invariant, but guard against any stale/legacy row).
    // Falling back to the key keeps the title non-empty when the tracker is unreachable and unresolved.
    /// <param name="lastBackwardAt">When this ticket last went back for rework, so the QA count can
    /// report the passes that still describe the current branch rather than every green row. A card
    /// reading "3/3 passed" about evidence from three bounces ago is the exact signal this exists to
    /// stop showing.</param>
    public static TicketSummaryDto ToSummary(Ticket t, string? effectiveTitle = null, bool issueUnavailable = false,
        IReadOnlyList<BlockerDto>? blockers = null, DateTime? lastBackwardAt = null, bool awaitingAnalysis = false) => new(
        t.Id, t.ProjectKey, t.IssueKey != null ? (effectiveTitle ?? t.IssueKey) : (effectiveTitle ?? t.Title ?? ""), t.Phase.ToString(),
        t.IssueKey, t.IssueUrl, issueUnavailable, t.Branch, t.BoardOrder,
        t.WorkItems.Count, t.WorkItems.Count(w => w.Done),
        t.QaItems.Count, t.QaItems.Count(q => HandoffRequirements.IsVerified(q, lastBackwardAt)),
        t.Comments.Count, t.BlockedReason, t.PhaseBeforeBlock?.ToString(), t.UpdatedAt,
        blockers?.Any(b => !b.Satisfied && b.Override != false) == true,
        blockers?.Where(b => !b.Satisfied && b.Override != false).Select(b => b.Label).ToList(),
        CurrentItem(t), t.SkipQaTesting,
        PhaseMachine.ResumeAfterBlock(t.Phase == TicketPhase.Blocked ? t.PhaseBeforeBlock : null)?.ToString(),
        HandoffRequirements.UnansweredOwnerNotes(t).Count,
        t.Priority.ToString(),
        t.Track.ToString(),
        // Dismissed comments are excluded from the count: the owner has already decided about those,
        // and a gate reporting "6 comments staged" when four were killed is reporting work that is
        // finished. They stay in the table so a run that keeps re-raising a dead point can be seen.
        t.ReviewComments.Count(c => c.Status != ReviewCommentStatus.Dismissed),
        t.ReviewTarget?.SubmittedAt is not null,
        t.ReviewTarget?.SourceBranch,
        HeldForExpansion(t),
        awaitingAnalysis);

    private static int HeldForExpansion(Ticket t) =>
        WorkItemScopes.HeldForExpansion(t) ? WorkItemScopes.OpenExpansionCount(t) : 0;

    /// <summary>
    /// What the agent on this ticket is working on right now, or null.
    /// <para>
    /// Scoped by the ticket's own phase rather than by scanning both lists, and that is what keeps
    /// it honest: a <c>StartedAt</c> outlives the run that set it, so an implement run killed
    /// mid-item leaves one behind for good. Consulting the work items only while the ticket is
    /// Implementing means that leftover can never surface on a ticket that has since moved to QA.
    /// </para>
    /// <para>
    /// Still only a claim about intent — the caller decides whether to show it, and the board does
    /// that only for a row an agent is actually holding.
    /// </para>
    /// </summary>
    private static string? CurrentItem(Ticket t) => t.Phase switch
    {
        TicketPhase.Implementing =>
            t.WorkItems.OrderBy(w => w.Order).FirstOrDefault(w => w.StartedAt != null && !w.Done)?.Text,
        TicketPhase.QaTesting =>
            t.QaItems.OrderBy(q => q.Order).FirstOrDefault(q => q.StartedAt != null && q.Status == QaStatus.Todo)?.Text,
        _ => null,
    };

    /// <param name="files">Resolves a note attachment to its path on disk. Optional because most
    /// callers render for the browser, which wants the URL; the MCP path passes it, because a worker
    /// that cannot be told where the file IS cannot open it.</param>
    /// <param name="tracker">This project's tracker, so the shape can name it. Optional because
    /// several callers render for a context that has no registry to hand; they get the honest
    /// "None", which the client draws as an unlinked ticket.</param>
    /// <param name="lastBackwardAt">When this ticket last went back for rework, which is what dates
    /// its QA evidence. Optional for the same reason as the two above — a caller without it gets
    /// every item reported unverified, which errs toward re-testing rather than toward trusting a
    /// pass nobody can date.</param>
    public static TicketDetailDto ToDetail(Ticket t, string? effectiveTitle = null, string? effectiveDescription = null,
        bool issueUnavailable = false, IReadOnlyList<BlockerDto>? blockers = null, string? learningLabel = null,
        IReadOnlyList<TicketAttachmentDto>? attachments = null, string? attachmentsNote = null,
        TicketFileStore? files = null, IIssueTracker? tracker = null, DateTime? lastBackwardAt = null) => new(
        t.Id, t.ProjectKey,
        t.IssueKey != null ? (effectiveTitle ?? t.IssueKey) : (effectiveTitle ?? t.Title ?? ""),
        t.IssueKey != null ? effectiveDescription : (effectiveDescription ?? t.Description),
        t.Phase.ToString(),
        t.IssueKey, t.IssueUrl, issueUnavailable, t.Branch, t.WorktreePath,
        (tracker?.Kind ?? Services.Tracker.TrackerKind.None).ToString(),
        tracker?.DisplayName ?? NoTracker.Instance.DisplayName,
        t.ImplementationSummary, t.MasterNotes,
        t.RequiresSpeculationReview, t.RequiresImplementationReview, t.SkipQaTesting, t.ShortLoopReview,
        t.BlockedReason, t.PhaseBeforeBlock?.ToString(), t.BoardOrder,
        t.CreatedAt, t.UpdatedAt,
        t.WorkItems.OrderBy(w => w.Order).Select(w => new WorkItemDto(w.Id, w.Text, w.Done, w.Order, w.StartedAt, w.Scope.ToString())).ToList(),
        t.QaItems.OrderBy(q => q.Order).Select(q => new QaItemDto(q.Id, q.Text, q.Status.ToString(), q.Note, q.Order,
            q.StartedAt, q.VerifiedAt, HandoffRequirements.IsVerified(q, lastBackwardAt), q.PassedByInspection)).ToList(),
        t.Comments.OrderBy(c => c.CreatedAt).Select(c => new CommentDto(
            c.Id, c.Author.ToString(), c.Body, c.Phase?.ToString(), c.CreatedAt,
            c.Attachments.OrderBy(a => a.Id).Select(a => new NoteAttachmentDto(
                a.Id, a.FileName, $"/api/tickets/{t.Id}/files/{a.Id}",
                files is null ? "" : files.PathFor(a).Replace('\\', '/'), a.Bytes, a.IsImage)).ToList(),
            c.AcknowledgedAt is not null, c.Acknowledgement, c.DeliveredAt, c.AcknowledgedAt)).ToList(),
        t.QaShots.OrderBy(s => s.Order).Select(s => QaShotDto.From(t.Id, s)).ToList(),
        PhaseMachine.NextPhases(t.Phase, t.Track).Select(p => p.ToString()).ToList(),
        PhaseMachine.NextPhases(t.Phase, t.Track).Select(p => new NextPhaseDto(p.ToString(), PhaseMachine.Meaning(t.Phase, p),
            HandoffRequirements.GuideSummary(t, t.Phase, p))).ToList(),
        blockers,
        t.LearningBrief, t.LearningBriefAt, learningLabel,
        attachments, attachmentsNote, t.QaShotsWaiver,
        PhaseMachine.ResumeAfterBlock(t.Phase == TicketPhase.Blocked ? t.PhaseBeforeBlock : null)?.ToString(),
        BlockQuestions.Parse(t.BlockedQuestionsJson),
        t.Priority.ToString(),
        t.Track.ToString(),
        ToReviewTarget(t.ReviewTarget),
        t.ReviewTarget is null && t.ReviewComments.Count == 0
            ? null
            : t.ReviewComments.OrderBy(c => c.Order).ThenBy(c => c.Id)
                .Select(c => ToReviewComment(c, t.ReviewTarget)).ToList(),
        HeldForExpansion(t),
        t.SkipClassroomAnalysis,
        ToIndependentReview(t));

    private static IndependentReviewDto? ToIndependentReview(Ticket t)
    {
        if (t.GateAnalysisId is not { } id) return null;
        var findings = HandoffRequirements.CurrentGateFindings(t);
        var note = findings.Count == 0
            ? "An independent Classroom analysis read this commit and raised no risks or coverage gaps. Nothing to rule on."
            : "An independent Classroom analysis read this commit from the system's side rather than the plan's. "
            + "Rule on every finding below with rule_on_finding - Defect or NotADefect, with the reason. Check each "
            + "claim against the code yourself: the analysis can be wrong, and so can the plan it is checking. A "
            + "Defect goes back to NeedsImplementation; the hand-off to NeedsReview is refused while any finding is "
            + "unruled or ruled Defect.";
        return new IndependentReviewDto(id, note, findings.Select(f => new GateFindingDto(
            f.Id, f.Kind.ToString(), f.Title, f.Text, f.Ruling?.ToString(), f.RulingNote, f.RuledAt)).ToList());
    }

    private static ReviewTargetDto? ToReviewTarget(ReviewTarget? r) => r is null ? null : new(
        r.PrId, r.PrUrl, r.SourceBranch, r.TargetBranch, r.PrAuthor,
        r.ReviewedAtSha, r.HeadSha, r.Verdict?.ToString(), r.SubmittedAt,
        HasMoved(r.ReviewedAtSha, r.HeadSha));

    private static ReviewCommentDto ToReviewComment(ReviewComment c, ReviewTarget? target) => new(
        c.Id, c.FilePath, c.Line, c.Side.ToString(), c.Body, c.Severity.ToString(), c.Status.ToString(),
        c.Author.ToString(), c.AnchorSha, c.QaItemId, c.QaShotOrder, c.SubmittedAt, c.Order, c.CreatedAt,
        HasMoved(c.AnchorSha, target?.HeadSha));

    /// <summary>
    /// Whether the branch has moved under something written against <paramref name="writtenAt"/>.
    /// <para>
    /// Deliberately the COARSE answer — the two commits differ — rather than the precise one, which
    /// is whether the push touched the file this comment points at. The precise version needs a git
    /// call per comment, and this runs on every board render. Coarse over-reports, flagging a comment
    /// whose file nobody touched, and that is the safe direction to be wrong in: it asks for a second
    /// look at something still correct, where the other way round presents a line number that has
    /// since moved as though it were current.
    /// </para>
    /// <para>Unknown on either side reads as "not moved" — an absent sha is not evidence of a push.</para>
    /// </summary>
    private static bool HasMoved(string? writtenAt, string? headNow) =>
        writtenAt is { Length: > 0 } a && headNow is { Length: > 0 } b
        && !string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Map the dependency service's resolution onto the wire shape.</summary>
    public static List<BlockerDto> ToBlockers(IEnumerable<TicketDependencyService.ResolvedBlocker> blockers) =>
        blockers.Select(b => new BlockerDto(b.Id, b.Label, b.IssueKey, b.LocalTicketId, b.Origin.ToString(),
            b.Override, b.Note, b.Status, b.Satisfied, b.Unknown)).ToList();
}
