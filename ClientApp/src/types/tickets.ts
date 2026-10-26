export type Phase =
  | 'Backlog'
  | 'NeedsSpeculation'
  | 'Speculating'
  | 'NeedsApproval'
  | 'NeedsImplementation'
  | 'Implementing'
  | 'NeedsImplementationReview'
  | 'NeedsQa'
  | 'QaTesting'
  | 'NeedsRefinement'
  | 'Refining'
  | 'NeedsPeerReview'
  | 'PeerReviewing'
  | 'NeedsReview'
  | 'Done'
  | 'Blocked';

export type PhaseKind = 'backlog' | 'queue' | 'active' | 'gate' | 'terminal' | 'blocked';

/**
 * Which pipeline a ticket runs on. 'Delivery' is our own work start to finish; 'Review' is somebody
 * else's branch, which we read, drive-test and stage feedback on but never change.
 *
 * Distinct from {@link PhaseKind}, which describes a PHASE (is it a queue, a gate, an active run).
 * This describes the TICKET. The track is what a ticket is; the phase is where it is.
 */
export type Track = 'Delivery' | 'Review';

export type ReviewSeverity = 'Blocking' | 'Question' | 'Nit' | 'Praise';
export type ReviewCommentStatus = 'Draft' | 'Approved' | 'Dismissed' | 'Submitted';
export type ReviewVerdict = 'Approve' | 'ApproveWithComments' | 'RequestChanges' | 'CannotTell';

/** What a review ticket is reviewing, and how far we have got with it. Null on a delivery ticket. */
export interface ReviewTarget {
  prId?: string | null;
  prUrl?: string | null;
  sourceBranch: string;
  targetBranch?: string | null;
  prAuthor?: string | null;
  reviewedAtSha?: string | null;
  headSha?: string | null;
  verdict?: ReviewVerdict | null;
  // Null means the ball is with us (feedback to curate and send); set means it is with the author.
  // This is why the review track needs no "awaiting dev" phase - see ReviewTarget.SubmittedAt.
  submittedAt?: string | null;
  // The author has pushed since the pass that wrote the staged comments.
  stale: boolean;
}

/** One piece of staged feedback, awaiting the owner's curation. */
export interface ReviewComment {
  id: number;
  filePath?: string | null;
  line?: number | null;
  side: 'Head' | 'Base';
  body: string;
  severity: ReviewSeverity;
  status: ReviewCommentStatus;
  author: AuthorName;
  anchorSha?: string | null;
  qaItemId?: number | null;
  qaShotOrder?: number | null;
  submittedAt?: string | null;
  order: number;
  createdAt: string;
  // The file this points at may have moved under it — kept and flagged, never dropped.
  stale: boolean;
}

/**
 * One leg of a ticket's trip through the phases. Shared by the board drawer and the Classroom, which
 * draw the same chart from the same endpoint (`/api/tickets/{id}/journey`).
 */
export interface JourneyLeg {
  /** Where the move came from. Only the first leg's matters: it is where the ticket started. */
  from: string;
  phase: string;
  enteredAt: string;
  /** Null on the leg the ticket is still in. */
  minutes: number | null;
  actor: string;
  backward: boolean;
}
export type QaStatusName = 'Todo' | 'Passed' | 'Failed' | 'Blocked';
// Steward: the project's stand-in for the owner. Its notes bind a worker exactly as the owner's do.
export type AuthorName = 'Human' | 'Claude' | 'System' | 'Steward';

export interface PhaseInfo {
  name: Phase;
  kind: PhaseKind;
  nextPhases: Phase[];
}

export interface ProjectInfo {
  key: string;
  displayName: string;
  /**
   * Which pipelines this project runs, and so which lanes the board draws for it. Presentation
   * only: a ticket carries its own {@link Track} and nothing in the pipeline consults this. The
   * server guarantees at least one, and guarantees it cannot be turned off under unfinished work.
   */
  tracks: Track[];
  /**
   * How this project's tickets are delivered: "git" raises a pull request, "tfvc" is checked in by a
   * human from Team Explorer. Decides which git buttons a ticket row offers and which dialog they
   * open. Absent from an older server, which only ever meant git.
   */
  repoType?: RepoType;
  /**
   * What a new ticket's "skip classroom analysis" box starts as for this project. Only a default:
   * each ticket carries its own setting. Absent from an older server, which meant off.
   */
  skipClassroomAnalysisByDefault?: boolean;
  /**
   * A git project that delivers by merging the ticket's branch into its base rather than by pull
   * request. The PR button's dialog then ends in a merge. Absent from an older server: PRs.
   */
  mergeInsteadOfPr?: boolean;
  /**
   * A project of several repos: each one's folder, in order. Its tickets' git buttons name one repo
   * at a time and its PR dialog has a section per repo. Null or absent for a project that is one repo.
   */
  repos?: ProjectRepo[] | null;
}

/** One repo of a project of several. See {@link ProjectInfo.repos}. */
export interface ProjectRepo {
  dir: string;
  /** In every workspace for reading, never changed or delivered. */
  readOnly: boolean;
}

/** See {@link ProjectInfo.repoType}. */
export type RepoType = 'git' | 'tfvc';

/**
 * How far up the queue a ticket sits. Five bands; the heartbeat empties the highest one that has
 * anything workable in it before it looks at the next, and only inside a band does the older rule
 * apply — the ticket nearest the finish line goes first.
 */
export type Priority = 'Lowest' | 'Low' | 'Normal' | 'High' | 'Critical';

/** What an untouched ticket carries, and the one value the board declines to draw a chip for. */
export const DEFAULT_PRIORITY: Priority = 'Normal';

/** Most urgent first — the order the claim reads them in, so the order they are offered in. */
export const PRIORITIES: Priority[] = ['Critical', 'High', 'Normal', 'Low', 'Lowest'];

/** The number behind the name. P-numbers are how anyone actually says these out loud. */
export const PRIORITY_LEVEL: Record<Priority, number> = {
  Lowest: 1, Low: 2, Normal: 3, High: 4, Critical: 5,
};

/**
 * The mark that carries the level without the reader counting. Arrows rather than a colour alone,
 * because the board's colours already mean phase — a chip that said "urgent" only in red would be
 * read as a second, quieter way of saying Blocked.
 */
export const PRIORITY_MARK: Record<Priority, string> = {
  Critical: '▲▲', High: '▲', Normal: '–', Low: '▼', Lowest: '▼▼',
};

/** One line each, for the picker: what choosing this band actually does to the queue. */
export const PRIORITY_HINT: Record<Priority, string> = {
  Critical: 'Worked before everything else, whatever state the rest of the board is in.',
  High: 'Jumps the normal queue — even past work that is nearly finished.',
  Normal: 'The default. Most-progressed first, as the board has always run.',
  Low: 'Picked up only once nothing at Normal or above is workable.',
  Lowest: 'Last. Effectively worked when the board is otherwise idle.',
};

/**
 * The chip colour per band, in theme tokens. `blocked` and `active` are the palette's two hot
 * colours and the two the eye lands on first, which is exactly what the top two bands want; the
 * quiet bands borrow the text ramp so they recede instead of competing with the phase chip.
 */
export const PRIORITY_COLOR: Record<Priority, string> = {
  Critical: 'var(--w-blocked)',
  High: 'var(--w-active)',
  Normal: 'var(--w-t400)',
  Low: 'var(--w-t500)',
  Lowest: 'var(--w-t600)',
};

export interface TicketSummary {
  id: number;
  projectKey: string;
  title: string;
  phase: Phase;
  issueKey?: string | null;
  issueUrl?: string | null;
  issueUnavailable?: boolean;
  branch?: string | null;
  boardOrder: number;
  workItemsTotal: number;
  workItemsDone: number;
  qaTotal: number;
  // Passes that still describe the current branch. A pass recorded before the ticket last went
  // back for rework has expired with it, so this can fall without any item changing status.
  qaVerified: number;
  commentCount: number;
  blockedReason?: string | null;
  phaseBeforeBlock?: Phase | null;
  updatedAt: string;
  // Waiting on another ticket. Not the same as the Blocked phase: this ticket is fine and clears
  // itself when the blocker finishes, but the heartbeat passes over it until then.
  waiting?: boolean;
  waitingOn?: string[] | null;
  // The work item being implemented, or the QA item being tested — already scoped to the ticket's
  // phase by the server. Show it only for a row an agent is actually holding: it records what a run
  // said it was doing, and a run that died mid-item leaves it set behind.
  currentItem?: string | null;
  // Code review only — the board uses it to tell a deliberate 0/0 QA row from a stalled one.
  skipQaTesting?: boolean;
  // Where a blocked ticket is recommended to resume: the queue phase feeding the work it stopped in.
  // A hint, not a restriction — every phase in nextPhases stays available.
  unblockTo?: Phase | null;
  // Owner notes no run has answered. Non-zero means this ticket's next hand-off is being held — on
  // the row because from outside, a ticket waiting on a note looks exactly like one being ignored.
  unansweredNotes?: number;
  // The band this ticket is claimed from. Drawn on the row only when it is not DEFAULT_PRIORITY:
  // the rows are laid out in phase order while the claim reads them in priority order, so without
  // it a ticket being worked ahead of the one above it looks like the heartbeat choosing at random.
  priority?: Priority;
  // Which pipeline. Needed on the row because the phase does not always say: Backlog, NeedsReview,
  // Done and Blocked are shared, and a review ticket parked at the gate wants different words and a
  // different action from a delivery ticket parked at the same one.
  track?: Track;
  // Staged feedback awaiting curation, and whether it has been sent. Together these are what let
  // the shared gate read "4 comments staged" or "submitted - awaiting dev".
  reviewCommentCount?: number;
  reviewSubmitted?: boolean;
  // The branch under review. Separate from `branch`, which means the branch this ticket OWNS — a
  // review ticket owns none, and merging the two would have every reader of `branch` treat someone
  // else's work as ours.
  reviewBranch?: string | null;
  // Expansion work items holding this ticket at NeedsApproval when its own setting would have
  // skipped the gate; 0 otherwise. Said on the row, or the stop reads as the skip having broken.
  heldForExpansion?: number;
  // In NeedsRefinement while its independent Classroom analysis is written. The heartbeat passes
  // such a ticket over until it lands, so without this the row looks ignored.
  awaitingAnalysis?: boolean;
}

/** One thing a ticket is waiting on. */
export interface Blocker {
  id: number;
  label: string;
  issueKey?: string | null;
  localTicketId?: number | null;
  origin: 'Tracker' | 'Local';
  override?: boolean | null;
  note?: string | null;
  status?: string | null;
  satisfied: boolean;
  unknown: boolean;
}

export interface WorkItem {
  id: number;
  text: string;
  done: boolean;
  order: number;
  // Set on the one item the implement phase said it was working, cleared on every other. Records
  // intent, not truth — a run that died mid-item leaves it set — so only trust it while an agent is
  // actually holding the ticket.
  startedAt?: string | null;
  // How the item relates to the ask, as speculation classified it. An open 'Expansion' item holds
  // the ticket at NeedsApproval whatever its review setting says.
  scope?: WorkItemScope;
}

export type WorkItemScope = 'Required' | 'Consequence' | 'Expansion';

export interface QaItem {
  id: number;
  text: string;
  status: QaStatusName;
  note?: string | null;
  order: number;
  // Set on the one item QA said it was testing, cleared on every other. Same caveat as WorkItem's:
  // intent, not truth — only trust it while an agent is holding the ticket.
  startedAt?: string | null;
  // When this outcome was recorded, and whether it still describes the current branch. A Passed
  // from before the ticket's last trip back for rework has expired: the code changed underneath it.
  // `verified` is the server's answer to that, because only the server knows when the bounce was.
  verifiedAt?: string | null;
  verified?: boolean;
  // The pass was argued from the code rather than driven through the app — a real outcome for some
  // items, but a weaker claim than an executed test, so the drawer says which one it is.
  passedByInspection?: boolean;
}

/** One file the author attached to a note — a screenshot of what is wrong, a log dump. */
export interface NoteAttachment {
  id: number;
  fileName: string;
  /** API route streaming the stored bytes. */
  url: string;
  /** Where the file sits on this machine — what a worker opens. Empty in browser responses. */
  localPath: string;
  bytes: number;
  isImage: boolean;
}

export interface Comment {
  id: number;
  author: AuthorName;
  body: string;
  phase?: Phase | null;
  createdAt: string;
  attachments?: NoteAttachment[] | null;
  /**
   * Only meaningful on a `Human` note. False means the note is still holding the ticket's next
   * hand-off: no worker has said what it did about it. See HandoffRequirements.UnansweredOwnerNotes.
   */
  acknowledged?: boolean;
  /** What the worker said it did, rendered under the note it answers. */
  acknowledgement?: string | null;
  /** When the note was first put in front of a run — how "nobody has looked" is told from "ignored". */
  deliveredAt?: string | null;
  acknowledgedAt?: string | null;
}

/** A rectangle in an image's own pixels. */
export interface ImageRect {
  x: number;
  y: number;
  w: number;
  h: number;
}

export interface QaShot {
  order: number;
  caption: string;
  imageUrl: string;
  /** The part of the shot that proves the caption, when QA marked one. */
  focus?: ImageRect | null;
}

/** One choice offered against a blocking question. */
export interface BlockOption {
  label: string;
  /** One line on what taking this choice costs or builds. */
  detail?: string | null;
  /**
   * The choice the agent takes if nobody answers. At most one per question, and often none — a
   * question with no recommendation is one that genuinely turns on knowledge the agent lacks.
   */
  recommended?: boolean;
}

/** One question a stopped ticket is asking. */
export interface BlockQuestion {
  question: string;
  /** The evidence behind the question — markdown, and usually the long part. */
  why?: string | null;
  /** Empty or absent means an open question: answer it in free text. */
  options?: BlockOption[] | null;
  allowOther?: boolean;
}

/**
 * The structured form of a block. Purely additive: `blockedReason` is rendered from this server-side
 * and remains the authoritative account, so a ticket without this (a prose block, a hand-written one,
 * anything blocked before the structured form existed) still reads fine — it just cannot be answered
 * by clicking.
 */
export interface BlockPayload {
  /** The framing sentence above the questions. */
  preamble?: string | null;
  questions: BlockQuestion[];
  /** Things to go and find rather than decide — an epic, a sample payload. */
  needs?: string[] | null;
}

/**
 * Which external tracker a project's issues live in. `trackerKind` is the stable token to branch
 * on; `trackerName` is what to print. On the detail shape only — the board row shows the key
 * without ever naming the system it came from.
 */
export type TrackerKind = 'Jira' | 'AzureDevOps' | 'None';

export interface TicketDetail {
  id: number;
  projectKey: string;
  title: string;
  description?: string | null;
  phase: Phase;
  issueKey?: string | null;
  issueUrl?: string | null;
  issueUnavailable?: boolean;
  // "Jira" / "AzureDevOps" / "None", and the prose name beside it ("Jira", "Azure Boards"). Naming
  // the wrong system in a link is worse than naming none, so nothing here is hard-coded.
  trackerKind: TrackerKind;
  trackerName: string;
  branch?: string | null;
  // Where the branch is checked out for the agent — its own git worktree, separate from the
  // human's checkout of the same repo. Null before speculation, and again once the ticket is Done.
  worktreePath?: string | null;
  implementationSummary?: string | null;
  // Reading material the refine phase leaves for the human before sign-off: the domain this change
  // lives in, how it worked before, why it was done this way. Required: refinement cannot hand a
  // ticket to NeedsReview without one (see HandoffRequirements), so on any ticket that reached
  // review under the pipeline this is populated.
  // learningLabel is the per-project heading ("Learn <project>"), so no project name is hard-coded here.
  learningBrief?: string | null;
  learningBriefAt?: string | null;
  learningLabel?: string | null;
  // Human-authored standing instructions. Highest authority, and never written by Claude.
  masterNotes?: string | null;
  // Human review gates — off means the pipeline skips that gate for this ticket.
  requiresSpeculationReview: boolean;
  requiresImplementationReview: boolean;
  // Code review only: QA reads the diff but never runs the app, and no phase writes QA items.
  skipQaTesting: boolean;
  // Pending one-shot routing from a reviewer's change request: the next implementation comes
  // straight back to NeedsReview instead of going round through QA and refinement.
  shortLoopReview: boolean;
  blockedReason?: string | null;
  // The same block as the questions the agent actually asked, when it asked in questions. Absent for
  // a fault report, a hand-written block, or anything blocked before the structured form existed —
  // in which case blockedReason is the whole of it.
  blockedQuestions?: BlockPayload | null;
  phaseBeforeBlock?: Phase | null;
  boardOrder: number;
  createdAt: string;
  updatedAt: string;
  workItems: WorkItem[];
  qaItems: QaItem[];
  comments: Comment[];
  qaShots: QaShot[];
  // Set only when QA explicitly said this ticket has nothing to photograph (a stored-procedure
  // change, a migration). Shown in place of the shots, so an empty panel is never ambiguous
  // between "no screen to show" and "the phase skipped it".
  qaShotsWaiver?: string | null;
  nextPhases: Phase[];
  blockers?: Blocker[] | null;
  // Where a blocked ticket is recommended to resume (the queue phase feeding the work it stopped in).
  unblockTo?: Phase | null;
  // The band this ticket is claimed from — the human's call, and editable only here and on create.
  priority?: Priority;
  // Which pipeline this ticket runs on. Fixed at creation; nothing moves a ticket between tracks.
  track?: Track;
  // The branch under review and how far we have got with it. Null on a delivery ticket.
  review?: ReviewTarget | null;
  // Feedback staged against that branch. Nothing here reaches the pull request without a person.
  reviewComments?: ReviewComment[] | null;
  // Same as the summary's heldForExpansion.
  heldForExpansion?: number;
  // Refinement goes ahead without waiting for an independent Classroom analysis.
  skipClassroomAnalysis?: boolean;
  // The analysis the current refinement pass was handed, and what it ruled on each finding.
  independentReview?: IndependentReview | null;
}

/** One thing an independent Classroom analysis raised, and refinement's ruling on it. */
export interface GateFinding {
  id: number;
  kind: 'Risk' | 'Gap' | 'Partial';
  title: string;
  text: string;
  ruling?: 'Defect' | 'NotADefect' | null;
  rulingNote?: string | null;
  ruledAt?: string | null;
}

export interface IndependentReview {
  analysisId: number;
  note: string;
  findings: GateFinding[];
}

// Display metadata for phases (label + accent styling by kind).
export const PHASE_LABEL: Record<Phase, string> = {
  Backlog: 'Backlog',
  NeedsSpeculation: 'Needs Speculation',
  Speculating: 'Speculating',
  NeedsApproval: 'Needs Approval',
  NeedsImplementation: 'Needs Implementation',
  Implementing: 'Implementing',
  NeedsImplementationReview: 'Needs Implementation Review',
  NeedsQa: 'Needs QA',
  QaTesting: 'QA Testing',
  NeedsRefinement: 'Needs Refinement',
  Refining: 'Refining',
  NeedsPeerReview: 'Needs Peer Review',
  PeerReviewing: 'Peer Reviewing',
  NeedsReview: 'Needs Review',
  Done: 'Done',
  Blocked: 'Blocked',
};

/**
 * What to call a phase on a ticket of this track. Only one phase needs it: NeedsReview is shared,
 * and it means something different on each side — a sign-off on our own work, or feedback waiting to
 * be sent on someone else's.
 */
export function phaseLabel(phase: Phase, track: Track = 'Delivery'): string {
  if (track === 'Review' && phase === 'NeedsReview') return 'Feedback Ready';
  return PHASE_LABEL[phase];
}

export const KIND_ACCENT: Record<PhaseKind, string> = {
  backlog: 'text-gray-400 border-gray-600',
  queue: 'text-sky-300 border-sky-700',
  active: 'text-amber-300 border-amber-600',
  gate: 'text-violet-300 border-violet-600',
  terminal: 'text-emerald-300 border-emerald-600',
  blocked: 'text-red-300 border-red-600',
};

export const KIND_DOT: Record<PhaseKind, string> = {
  backlog: 'bg-gray-500',
  queue: 'bg-sky-500',
  active: 'bg-amber-500',
  gate: 'bg-violet-500',
  terminal: 'bg-emerald-500',
  blocked: 'bg-red-500',
};

/**
 * One of your filing tags, as it rides on a ticket. Deliberately absent from `TicketSummary` and
 * `TicketDetail`: those two shapes are also what every worker receives over MCP, and a label in a
 * phase's prompt is a label that quietly starts meaning something to the pipeline. Labels arrive on
 * their own routes instead (`api/labels.ts`), which is what keeps that true without anyone having
 * to remember it.
 */
export interface LabelRef {
  id: number;
  name: string;
}

/** A label plus how many tickets wear it — what the picker offers, most-used first. */
export interface LabelUsage extends LabelRef {
  count: number;
}
