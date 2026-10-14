namespace Whetstone.Models;

/// <summary>
/// The local ticket lifecycle. We own this state machine — the tracker is only optional
/// read-only context. "-ing" phases mean the heartbeat is actively working the
/// ticket (a lock); "Needs*" phases are the queue it pulls from.
/// </summary>
public enum TicketPhase
{
    Backlog,
    NeedsSpeculation,
    Speculating,
    NeedsApproval,
    NeedsImplementation,
    Implementing,
    NeedsImplementationReview,
    NeedsQa,
    QaTesting,
    NeedsRefinement,
    Refining,
    // The Review track's one working pair (see TicketTrack). Deliberately its own phases rather than
    // a reuse of NeedsQa/QaTesting: a phase here is not a label but a tuple of behaviours — which
    // skill runs, which rank it claims at, what its hand-off owes, which bucket it costs into — and
    // every one of those differs. Sharing the QA pair would have put the track into SkillFor,
    // ProgressRank, Meanings, HandoffRequirements and PhaseCost, all to save two enum members.
    NeedsPeerReview,
    PeerReviewing,
    NeedsReview,
    Done,
    Blocked,
}

/// <summary>
/// Which pipeline a ticket runs on. Two tracks share one board, one heartbeat and one set of
/// entities; what differs is the path through the phases and who owns the code.
/// <para>
/// <see cref="Delivery"/> is our own work, start to finish — the pipeline this system was built for,
/// and what every ticket written before this existed carries. <see cref="Review"/> is someone
/// else's branch: we read it, we drive-test it, we stage feedback for a human to submit, and we
/// never change a line of it.
/// </para>
/// <para>
/// The two converge at <see cref="TicketPhase.NeedsReview"/> and share
/// <see cref="TicketPhase.Backlog"/>, <see cref="TicketPhase.Done"/> and
/// <see cref="TicketPhase.Blocked"/>. Those are parking spots — nothing claims them, no skill runs
/// on them — so sharing costs one conditional in the drawer and one in NextPhases. The phases that
/// dispatch an agent are split, because sharing those would have cost a conditional in every rule
/// that reads a phase.
/// </para>
/// <para>
/// The human's call, like <see cref="Ticket.Priority"/> and the review gates: fixed when the ticket
/// is created and reachable by no MCP tool, so a worker can neither read its way onto the other
/// track nor move itself off this one.
/// </para>
/// </summary>
public enum TicketTrack
{
    Delivery = 0,
    Review = 1,
}

/// <summary>What a reviewer concluded about someone else's branch, once a human has decided.</summary>
public enum ReviewVerdict
{
    /// <summary>Nothing outstanding — it can merge.</summary>
    Approve,
    /// <summary>Fine to merge, but there are notes worth reading.</summary>
    ApproveWithComments,
    /// <summary>Something must change before this merges.</summary>
    RequestChanges,
    /// <summary>The branch cannot be judged — the ticket contradicts itself, or it would not run.</summary>
    CannotTell,
}

/// <summary>How much a staged review comment is claiming. Ordered most serious first.</summary>
public enum ReviewSeverity
{
    /// <summary>This is wrong and must change before merge.</summary>
    Blocking,
    /// <summary>Something the author should answer; may or may not be a defect.</summary>
    Question,
    /// <summary>Worth saying, not worth blocking on.</summary>
    Nit,
    /// <summary>Worth saying because it is good. A review of only faults reads as a review of a fault-finder.</summary>
    Praise,
}

/// <summary>Where a staged comment is in the human's curation loop.</summary>
public enum ReviewCommentStatus
{
    /// <summary>Written by a run, not yet looked at.</summary>
    Draft,
    /// <summary>The human has okayed it for sending.</summary>
    Approved,
    /// <summary>The human killed it. Kept rather than deleted — a run that keeps re-raising the same
    /// dismissed point is worth being able to see.</summary>
    Dismissed,
    /// <summary>Sent to the pull request.</summary>
    Submitted,
}

/// <summary>
/// How far up the queue a ticket sits. Five named levels, <see cref="Normal"/> the default — and
/// what every ticket written before this existed carries.
/// <para>
/// The numbers are the point of the type rather than decoration: the claim orders by them
/// descending, so higher is more urgent. They are stored as the integer rather than the name
/// (unlike <see cref="TicketPhase"/>) for the same reason — a column that exists to be ordered by
/// is worth being orderable in SQL.
/// </para>
/// </summary>
public enum TicketPriority
{
    Lowest = 1,
    Low = 2,
    Normal = 3,
    High = 4,
    Critical = 5,
}

/// <summary>
/// A work item's relation to the ask. One ticket is the reason this exists: a plan that fixed the
/// reported bug also built a second cheque preview nobody asked for, and nothing on the ticket
/// could tell the two kinds of item apart until the reviewer read the whole diff.
/// </summary>
public enum WorkItemScope
{
    /// <summary>Produces the outcome the ticket asks for.</summary>
    Required,
    /// <summary>Unasked, but without it the ask is still wrong in a reachable case or something that works today breaks.</summary>
    Consequence,
    /// <summary>Adds something a user sees or a business rule that the ticket never asked for. Needs a human yes before it is built.</summary>
    Expansion,
}

public static class WorkItemScopes
{
    /// <summary>
    /// Expansion items not yet built. Done ones do not count: a plan replanned after the work
    /// shipped is not asking permission for work that already exists.
    /// </summary>
    public static int OpenExpansionCount(Ticket t) =>
        t.WorkItems.Count(w => w.Scope == WorkItemScope.Expansion && !w.Done);

    /// <summary>
    /// Sitting at NeedsApproval only because of expansion items — the one case where the stop has
    /// to explain itself, since the ticket's own setting says it would not have stopped.
    /// </summary>
    public static bool HeldForExpansion(Ticket t) =>
        t.Phase == TicketPhase.NeedsApproval && !t.RequiresSpeculationReview && OpenExpansionCount(t) > 0;
}

public enum QaStatus
{
    Todo,
    Passed,
    Failed,
    Blocked,
}

/// <summary>Who authored a comment / progress note.</summary>
public enum AuthorKind
{
    Human,
    Claude,
    System,
    /// <summary>
    /// The project's steward: the agent that stands in for the ticket's owner between worker runs.
    /// Its own name rather than <see cref="Human"/> so the record never claims a person said
    /// something they did not, and so what it did can be counted without reading comment text.
    /// </summary>
    Steward,
}

public static class AuthorKinds
{
    /// <summary>
    /// A note a worker has to answer before it hands off: the owner's, or the steward's speaking
    /// for them. The steward's send-back is the same instruction the owner's would have been, and
    /// a worker free to ignore it would make sending work back a suggestion.
    /// </summary>
    public static bool IsDirective(AuthorKind author) => author is AuthorKind.Human or AuthorKind.Steward;
}

/// <summary>A local ticket — the unit of work on the board. Belongs to one project (by key).</summary>
public class Ticket
{
    public int Id { get; set; }

    /// <summary>Project key — maps to data/projects/&lt;key&gt;/ (file-config source of truth).</summary>
    public string ProjectKey { get; set; } = "";

    /// <summary>
    /// Local title. NULL when the ticket is linked to an external issue — its title (and
    /// description) are the source-of-truth in that tracker and resolved live on read (see
    /// LinkedIssueResolver), never stored or maintained here. Required when there is no
    /// <see cref="IssueKey"/>.
    /// </summary>
    public string? Title { get; set; }
    /// <summary>Local description. NULL when linked (resolved from the tracker on read).</summary>
    public string? Description { get; set; }

    public TicketPhase Phase { get; set; } = TicketPhase.Backlog;

    /// <summary>
    /// Which pipeline this ticket runs on — see <see cref="TicketTrack"/>. The track is what a
    /// ticket <b>is</b>; <see cref="Phase"/> is where it is.
    /// <para>
    /// Fixed at creation. There is no MCP tool that reads or writes it, and no endpoint that changes
    /// it on an existing ticket: a ticket that switched tracks mid-flight would be sitting in a
    /// phase its new track does not have, and every rule that reads the pair would have to cope.
    /// Delivery is the default, so every ticket written before this existed is correct without being
    /// touched.
    /// </para>
    /// </summary>
    public TicketTrack Track { get; set; } = TicketTrack.Delivery;

    /// <summary>
    /// What this ticket is reviewing, when it is on the <see cref="TicketTrack.Review"/> track.
    /// Null for every Delivery ticket — which is why it is a table of its own rather than eight
    /// nullable columns here that are meaningless on all but a few rows.
    /// </summary>
    public ReviewTarget? ReviewTarget { get; set; }

    /// <summary>
    /// Feedback staged against someone else's pull request, awaiting the human's curation. Empty on
    /// a Delivery ticket.
    /// </summary>
    public List<ReviewComment> ReviewComments { get; set; } = new();

    /// <summary>
    /// Optional read-only link to an issue in this project's external tracker — a Jira key like
    /// <c>PROJ-123</c>, or an Azure DevOps work item id like <c>1234</c>. Which system it names is
    /// a property of the project, not of the ticket, so nothing here records it. We never write back.
    /// </summary>
    public string? IssueKey { get; set; }
    public string? IssueUrl { get; set; }

    /// <summary>Feature branch in the target repo, once implementation starts.</summary>
    public string? Branch { get; set; }

    /// <summary>
    /// The git worktree this ticket's work happens in — a checkout of <see cref="Branch"/> that is
    /// the agent's alone, so the human's own checkout of the repo is never switched or dirtied
    /// underneath them.
    ///
    /// Written by the heartbeat when it creates the worktree, and read back for display and for
    /// cleanup when the ticket reaches Done. It is a cache, not the truth: git itself is the
    /// registry (<c>git worktree list</c>), and a path recorded here may already be gone — the user
    /// is free to remove a worktree by hand at any time.
    /// </summary>
    public string? WorktreePath { get; set; }

    /// <summary>
    /// Human-authored standing instructions for this ticket — the highest authority on how the work
    /// should be done. Where these contradict the description (or the linked issue), these win. Deliberately
    /// WRITE-ONLY-BY-HUMAN: exposed to the worker for reading (and injected into every phase's
    /// prompt) but there is no MCP tool that can modify it, so Claude can never edit its own orders.
    /// </summary>
    public string? MasterNotes { get; set; }

    /// <summary>
    /// A short, plain-language description of the delivered solution (a few sentences, mixing the
    /// business "what/why" with light technical detail). Rewritten by the implement phase every time
    /// it finishes, so it always reflects the latest state of the work. NULL until first implemented.
    /// </summary>
    public string? ImplementationSummary { get; set; }

    /// <summary>
    /// Reading material for the human, written by the refine phase once the branch is finished: what
    /// part of the target repo's domain this change lives in, how it worked before, why it was done
    /// this way, and where to start reading. Rewritten in full on every clean refine run, so it
    /// always describes the commit that is actually about to be reviewed.
    /// <para>
    /// Deliberately NOT the same artefact as <see cref="ImplementationSummary"/> (what was
    /// delivered) or the commit message (what changed, for the repo). This one exists to keep the
    /// ticket's owner learning the domain rather than drifting into trusting the pipeline blindly,
    /// so it is written for someone who knows the codebase in general but not this corner of it.
    /// It is also not a knowledge store: durable facts about the repo still go to that repo's notes
    /// store through reflection, and a brief only links to them.
    /// </para>
    /// </summary>
    public string? LearningBrief { get; set; }

    /// <summary>When <see cref="LearningBrief"/> was last written.</summary>
    public DateTime? LearningBriefAt { get; set; }

    /// <summary>
    /// Why this ticket finished QA with no screenshots — the escape hatch on the QA-shots hand-off
    /// requirement (see HandoffRequirements). Some work genuinely has nothing to photograph: a
    /// stored-procedure change, a migration, a build fix. Those tickets are not forced to invent a
    /// screenshot, but they ARE forced to say so, which is the difference between "nothing to show"
    /// and "the phase forgot".
    /// <para>
    /// Written only through set_qa_shots with an empty set, and cleared the moment real shots are
    /// attached, so the two can never both be true.
    /// </para>
    /// </summary>
    public string? QaShotsWaiver { get; set; }

    /// <summary>
    /// When <see cref="QaShotsWaiver"/> was recorded. Compared against the ticket's last backward
    /// move, so a waiver written on an earlier QA pass does not excuse a later one — the work may
    /// have grown a UI since.
    /// </summary>
    public DateTime? QaShotsWaiverAt { get; set; }

    /// <summary>
    /// What the linked issue looked like when the plan was made (captured on the way out of
    /// speculation, JSON — see IssueSnapshotService). Later phases diff the live issue against this
    /// to answer the only question about it they actually have: "has this moved under me since?".
    /// Null when the ticket has no link, or has not been speculated yet.
    /// </summary>
    public string? IssueSnapshot { get; set; }

    /// <summary>When <see cref="IssueSnapshot"/> was taken.</summary>
    public DateTime? IssueSnapshotAt { get; set; }

    /// <summary>Lower sorts first within a board column.</summary>
    public double BoardOrder { get; set; }

    /// <summary>
    /// Which band this ticket is claimed from. The heartbeat takes the highest band that has
    /// anything workable in it, and only then asks its usual question — most-progressed first — of
    /// the tickets inside that band. So a ticket one step off the starting line at
    /// <see cref="TicketPriority.High"/> is picked up ahead of one halfway to Done at
    /// <see cref="TicketPriority.Normal"/>, while the finish-what-you-started rule survives intact
    /// within each band rather than being replaced by this.
    /// <para>
    /// The human's call, like the review gates and <see cref="SkipQaTesting"/>: there is no MCP tool
    /// that sets it, so a worker can never move its own ticket up the queue.
    /// </para>
    /// </summary>
    public TicketPriority Priority { get; set; } = TicketPriority.Normal;

    /// <summary>
    /// Require a human to approve the speculation before implementation starts (the NeedsApproval
    /// gate). Off by default: the pipeline passes straight from Speculating to NeedsImplementation
    /// unless this ticket is explicitly flagged as wanting the review.
    /// </summary>
    public bool RequiresSpeculationReview { get; set; }

    /// <summary>
    /// Require a human to review the implementation before QA runs (the NeedsImplementationReview
    /// gate). Off by default: the pipeline passes straight from Implementing to NeedsQa unless set.
    /// Note this is NOT the final NeedsReview sign-off, which is never skipped.
    /// </summary>
    public bool RequiresImplementationReview { get; set; }

    /// <summary>
    /// Run QA as a code review only — no app, no browser, no QA checklist. For work small or
    /// mechanical enough that standing a stack up to click through it costs more than it proves.
    /// Off by default, so an untouched ticket behaves exactly as it always has.
    /// <para>
    /// Deliberately NOT one of the review gates above. Those bypass a whole phase (see
    /// PhaseMachine.GateSkips); this one removes the second half of a phase that still runs. QA
    /// always reads the diff — that part is never skipped, and is the reason this is safe to turn on.
    /// </para>
    /// <para>
    /// What it switches off is otherwise unwritten behaviour: no skill file asks a phase to author
    /// QA items, but speculation and QA both do it anyway (the heartbeat prompt lists the tool, and
    /// an empty checklist invites filling). Prose asking them to stop would be no more reliable than
    /// the prose that never asked them to start, so add_qa_items itself refuses on a flagged ticket.
    /// The dashboard's own endpoint does not: the human who set the flag is entitled to overrule it
    /// by hand, and the flag still wins at test time.
    /// </para>
    /// </summary>
    public bool SkipQaTesting { get; set; }

    /// <summary>
    /// Hand this ticket to refinement without waiting for an independent Classroom analysis of it.
    /// <para>
    /// By default a delivery ticket in NeedsRefinement is not claimed until an analysis of its
    /// current commit exists, and refinement then has to rule on every risk and coverage gap that
    /// analysis raised (see <see cref="GateFinding"/>). The analysis reads the change from the
    /// system's side rather than from the plan's - one ticket passed QA and refinement with a
    /// picker and a saved group filtering accounts two different ways, and only the analysis
    /// noticed. It costs one analysis run per trip through refinement, which on a small ticket is
    /// time not worth spending; this is how a ticket says so.
    /// </para>
    /// <para>
    /// The human's call, set from the project's default when the ticket is created and editable
    /// afterwards. There is no MCP tool for it: it removes a check on the pipeline's own work.
    /// </para>
    /// </summary>
    public bool SkipClassroomAnalysis { get; set; }

    /// <summary>
    /// The analysis this ticket's current refinement pass was handed, when it was handed one. Set
    /// at the claim into Refining and read by the hand-off rule: every <see cref="GateFinding"/> of
    /// this analysis must be ruled on before the ticket may go to NeedsReview. Null when the pass
    /// ran without one - skipped, or the analysis could not be written.
    /// </summary>
    public int? GateAnalysisId { get; set; }

    /// <summary>
    /// Send the next implementation straight back to <see cref="TicketPhase.NeedsReview"/> instead of
    /// round-tripping it through QA and refinement — the "short loop" a reviewer asks for when the
    /// change they want is small enough that a full cycle costs more than it proves.
    /// <para>
    /// Set only by the review gate's change-request composer, and cleared the moment the ticket lands
    /// back on NeedsReview, so it is one trip and never a standing setting. Deliberately not offered
    /// to workers: it removes the two phases that exist to check their work, which makes it exactly
    /// the decision a worker should not be making about its own output.
    /// </para>
    /// <para>
    /// It is a routing flag, not a transition. The worker still hands off to
    /// NeedsImplementationReview exactly as it always does and never learns which way it was sent —
    /// the same shape as the optional review gates above (see PhaseMachine.GateSkips), for the same
    /// reason: a hand-off whose target depends on a flag is a hand-off a worker can get wrong.
    /// </para>
    /// </summary>
    public bool ShortLoopReview { get; set; }

    /// <summary>
    /// Who filed this ticket, when it was not a person: <see cref="TransitionActor.Steward"/> for
    /// one a steward wrote from the plan. Null for everything a person filed, and for every ticket
    /// from before this was recorded.
    /// </summary>
    public TransitionActor? CreatedBy { get; set; }

    /// <summary>
    /// The unit of the project's plan this ticket delivers part of, by number, and which of its
    /// outcomes (<see cref="PlanOutcomes"/>, comma-separated ids). Set when a steward files a
    /// ticket against the plan, and what lets "how far is this unit" be counted off the board
    /// rather than read back out of each ticket's description.
    /// </summary>
    public int? PlanUnit { get; set; }
    public string? PlanOutcomes { get; set; }

    /// <summary>
    /// The key of the plan <see cref="PlanUnit"/> is a unit of. Every plan numbers its units from
    /// one, so the number names a unit only together with this. Null or empty for a plan that was
    /// never given a key.
    /// </summary>
    public string? PlanKey { get; set; }

    /// <summary>
    /// The commit that landed this ticket's branch in its base, and when, on a project that merges
    /// locally (by the board or by a steward). Null on a pull-request or TFVC project, where the
    /// merge happens somewhere this machine does not watch, and on every ticket merged before this
    /// was recorded. What lets a unit report say which commits in its range are the unit's own.
    /// </summary>
    public string? MergeSha { get; set; }
    public DateTime? MergedAt { get; set; }

    /// <summary>Set when Phase == Blocked.</summary>
    public string? BlockedReason { get; set; }

    /// <summary>
    /// The same block as a <see cref="BlockPayload"/>, when the worker asked in questions rather
    /// than in prose. Null for a hand-written block and for every ticket blocked before the
    /// structured form existed.
    /// <para>
    /// Strictly additive: <see cref="BlockedReason"/> above is rendered FROM this and remains the
    /// copy everything else reads. This one exists so the drawer can offer the answer as a choice,
    /// and losing it costs nothing but that.
    /// </para>
    /// </summary>
    public string? BlockedQuestionsJson { get; set; }

    /// <summary>Phase the ticket was in before it was blocked, to support unblock-to-prior.</summary>
    public TicketPhase? PhaseBeforeBlock { get; set; }

    /// <summary>
    /// Consecutive heartbeat work attempts that achieved nothing — the wedged-ticket circuit
    /// breaker. A fresh claim seeds it to 1; a resume increments it only when the previous run left
    /// no progress behind (see <see cref="HeartbeatProgressMark"/>), and resets it to 1 when the run
    /// did advance the ticket. Once it reaches the configured max the ticket is Blocked instead of
    /// resumed. A fresh claim into a new "-ing" phase reseeds it, so it never carries across phases.
    /// </summary>
    public int HeartbeatAttempts { get; set; }

    /// <summary>
    /// Fingerprint of the ticket's progress as of the last heartbeat claim: work items added and
    /// ticked off, QA items added and resolved, the implementation summary, the recorded branch.
    /// Comparing it on the next claim answers the only question the circuit breaker actually has —
    /// did the last run advance this ticket, or achieve nothing? Sitting in an "-ing" phase does not
    /// answer that on its own: a ticket bigger than one run ends every run still Implementing, and
    /// counting those as failures would block the ticket for making progress. NULL for a ticket that
    /// has never been claimed (and for one mid-phase when this was introduced).
    /// </summary>
    public string? HeartbeatProgressMark { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<WorkItem> WorkItems { get; set; } = new();
    public List<QaItem> QaItems { get; set; } = new();
    public List<Comment> Comments { get; set; } = new();
    public List<QaShot> QaShots { get; set; } = new();
    public List<GateFinding> GateFindings { get; set; } = new();
}

/// <summary>Where a <see cref="GateFinding"/> came from in the analysis document.</summary>
public enum GateFindingKind
{
    /// <summary>A <c>risk</c> callout: something the analysis believes is wrong or will go wrong.</summary>
    Risk,
    /// <summary>A requirement the coverage map says nothing in the change addresses.</summary>
    Gap,
    /// <summary>A requirement the coverage map says was only partly delivered.</summary>
    Partial,
}

/// <summary>What refinement concluded about one finding.</summary>
public enum GateRuling
{
    /// <summary>Real, and it goes back to implementation.</summary>
    Defect,
    /// <summary>Checked, and not a defect in this change - with the reason recorded.</summary>
    NotADefect,
}

/// <summary>
/// One thing an independent Classroom analysis raised that refinement must rule on.
/// <para>
/// Copied out of the analysis document at the claim, rather than read from it on demand, because a
/// ruling needs something stable to hang on: the document is a presentation format, and a finding
/// identified by its position in it would silently point at a different callout after a re-render.
/// </para>
/// </summary>
public class GateFinding
{
    public int Id { get; set; }
    public int TicketId { get; set; }
    public Ticket? Ticket { get; set; }

    /// <summary>The analysis this came from. A new analysis means a new set of findings.</summary>
    public int AnalysisId { get; set; }

    public int Order { get; set; }
    public GateFindingKind Kind { get; set; }

    /// <summary>The callout's title, or the requirement line in the ticket's own words.</summary>
    public string Title { get; set; } = "";

    /// <summary>The callout's text, or the coverage map's evidence for the verdict.</summary>
    public string Text { get; set; } = "";

    public GateRuling? Ruling { get; set; }
    public string? RulingNote { get; set; }
    public DateTime? RuledAt { get; set; }
}

/// <summary>
/// A "QA shot": one screenshot of the completed work plus a short caption, captured by the QA
/// phase. A ticket keeps at most 3, rewritten each QA run, so a reviewer can eyeball what was
/// verified at a glance. The image bytes live on disk (data/qa-shots/&lt;ticketId&gt;/); only the
/// filename + caption are stored here.
/// </summary>
public class QaShot
{
    public int Id { get; set; }
    public int TicketId { get; set; }
    public Ticket? Ticket { get; set; }

    /// <summary>0-based slot (0..2) — also the display order.</summary>
    public int Order { get; set; }
    /// <summary>Two sentences max describing what the screenshot shows.</summary>
    public string Caption { get; set; } = "";
    /// <summary>Filename (not a full path) under data/qa-shots/&lt;ticketId&gt;/, e.g. "0.png".</summary>
    public string ImageFile { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // The part of the screenshot that proves the caption, in the image's own pixels. Optional: a
    // full-page shot of a small change leaves the reader hunting, and QA is the one phase that knows
    // where to look. A walkthrough zooms to it; all four are set together or not at all.
    public int? FocusX { get; set; }
    public int? FocusY { get; set; }
    public int? FocusW { get; set; }
    public int? FocusH { get; set; }

    public Whetstone.Services.ImageRect? Focus() =>
        FocusX is int x && FocusY is int y && FocusW is int w && FocusH is int h ? new(x, y, w, h) : null;

    public void SetFocus(Whetstone.Services.ImageRect? r) =>
        (FocusX, FocusY, FocusW, FocusH) = ((int?)r?.X, (int?)r?.Y, (int?)r?.W, (int?)r?.H);
}

/// <summary>A unit of implementation work — produced locally by speculate/approval (replaces the tracker's own checklist field).</summary>
public class WorkItem
{
    public int Id { get; set; }
    public int TicketId { get; set; }
    public Ticket? Ticket { get; set; }

    public string Text { get; set; } = "";
    public bool Done { get; set; }
    public int Order { get; set; }

    /// <summary>
    /// How this item relates to what the ticket asked for, as speculation classified it. An open
    /// <see cref="WorkItemScope.Expansion"/> item holds the ticket at NeedsApproval whatever
    /// <see cref="Ticket.RequiresSpeculationReview"/> says — see <see cref="WorkItemScopes"/>.
    /// </summary>
    public WorkItemScope Scope { get; set; } = WorkItemScope.Required;

    /// <summary>
    /// When the implement phase said it was picking this item up — the ticket's one in-flight item.
    /// <para>
    /// Deliberately an invariant rather than a free field: starting an item clears it on every
    /// sibling, and finishing one clears its own, so a ticket has at most one item with a
    /// <c>StartedAt</c> and that item is never <see cref="Done"/>. Reading it is therefore just
    /// "which item is being worked right now", with no arithmetic over the list.
    /// </para>
    /// <para>
    /// It records intent, not truth: a run killed mid-item leaves it set forever, because nothing
    /// runs afterwards to clear it. The UI is what makes that harmless — it only draws the working
    /// marker while a live agent actually holds the ticket, so a stale flag simply stops being
    /// rendered rather than claiming work nobody is doing.
    /// </para>
    /// </summary>
    public DateTime? StartedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A QA test item — produced/verified locally (replaces the tracker's own test-case field).</summary>
public class QaItem
{
    public int Id { get; set; }
    public int TicketId { get; set; }
    public Ticket? Ticket { get; set; }

    public string Text { get; set; } = "";
    public QaStatus Status { get; set; } = QaStatus.Todo;
    /// <summary>Reason for a Failed/Blocked outcome.</summary>
    public string? Note { get; set; }
    public int Order { get; set; }

    /// <summary>
    /// When the QA phase said it was testing this item — the ticket's one in-flight test. Same
    /// single-slot invariant as <see cref="WorkItem.StartedAt"/>: starting one clears every sibling,
    /// recording an outcome clears its own, and the UI only draws it while a live agent holds the
    /// ticket so a run that died mid-test leaves nothing claiming to be under way.
    /// <para>
    /// It matters more here than on the work items. A QA item reads <see cref="QaStatus.Todo"/>
    /// both before anyone looks at it and while it is being tested, so without this a run halfway
    /// through fourteen items is indistinguishable from one that has not started.
    /// </para>
    /// </summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>
    /// When the current <see cref="Status"/> was recorded — the age of this item's evidence.
    /// <para>
    /// A status on its own says what QA concluded, but not what it concluded it ABOUT. A ticket that
    /// bounces back for rework returns with different code, and a Passed from before that bounce is
    /// a verdict on a version that no longer exists. Compared against the ticket's last backward
    /// move (see HandoffRequirements), this is what tells those apart, so a pass expires at the
    /// bounce instead of being carried forward by a run with no way to know it was stale.
    /// </para>
    /// <para>
    /// The outcome and its note are deliberately NOT cleared when that happens. Expiry is derived
    /// rather than destructive: the board keeps showing what the last pass proved and when — exactly
    /// the context a re-test wants — while the pipeline stops counting it as verification. NULL for
    /// a Todo item, and for every item recorded before this column existed; an unknown age reads as
    /// stale, which is the safe direction.
    /// </para>
    /// </summary>
    public DateTime? VerifiedAt { get; set; }

    /// <summary>
    /// This item passed by reading the code rather than by driving the app — "the file this guards
    /// is untouched since the run that already passed it", not "I ran it and watched it work".
    /// <para>
    /// Sometimes that is the honest and correct answer: an import path nothing in the diff reaches,
    /// a guard settled by the query plan. But it is a materially weaker claim than an executed test,
    /// and it is the claim that let a regression through on a real ticket — the run wrote its reasoning
    /// in the note, the note was prose, and a green item is a green item. So the two are recorded as
    /// different things rather than left for a reader to catch in the wording.
    /// </para>
    /// <para>
    /// Not refused at the hand-off, on purpose. Making it fatal would buy nothing except runs that
    /// stop admitting it; making it visible costs nothing and puts the choice in front of the human
    /// signing the ticket off. Cleared whenever the item is re-tested, like <see cref="VerifiedAt"/>.
    /// </para>
    /// </summary>
    public bool PassedByInspection { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A local progress note on a ticket (replaces tracker comments — the async channel).</summary>
public class Comment
{
    public int Id { get; set; }
    public int TicketId { get; set; }
    public Ticket? Ticket { get; set; }

    public AuthorKind Author { get; set; } = AuthorKind.Claude;
    public string Body { get; set; } = "";
    /// <summary>Phase the ticket was in when this note was written (for context in the timeline).</summary>
    public TicketPhase? Phase { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this note was first injected into a worker's prompt. Recorded for the human's benefit
    /// only — the drawer says "delivered" rather than leaving them guessing whether anyone saw it —
    /// and never used to decide what to deliver. <see cref="AcknowledgedAt"/> is what does that.
    /// </summary>
    public DateTime? DeliveredAt { get; set; }

    /// <summary>
    /// When a worker said what it did about this note, or null while it is still outstanding.
    /// <para>
    /// Only <see cref="AuthorKind.Human"/> notes ever carry this. It is the whole difference between
    /// a progress note and a directive: an unacknowledged human note is re-delivered to every run
    /// that claims the ticket and REFUSES the phase's hand-off (see HandoffRequirements), so a note
    /// left at the review gate cannot be quietly worked past. Delivery alone was never enough —
    /// a prompt a run has read is not a prompt a run has acted on, and nothing downstream could tell
    /// those apart.
    /// </para>
    /// </summary>
    public DateTime? AcknowledgedAt { get; set; }

    /// <summary>
    /// What the worker said it did about this note, written through acknowledge_note. Kept on the
    /// note rather than posted as a reply comment so the answer renders under the question it
    /// answers — a timeline where the two are eleven entries apart is how the pipeline lost track of
    /// this kind of exchange in the first place.
    /// </summary>
    public string? Acknowledgement { get; set; }

    /// <summary>Files the author attached — screenshots, log dumps, sample payloads.</summary>
    public List<NoteAttachment> Attachments { get; set; } = new();
}
