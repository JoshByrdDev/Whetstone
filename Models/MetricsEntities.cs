namespace Whetstone.Models;

/// <summary>Where a dependency came from.</summary>
public enum DependencyOrigin
{
    /// <summary>
    /// Derived from a blocking link in the project's external tracker, and refreshed from it.
    /// <para>
    /// Stored as its ordinal (0), which is why this member kept its position when it was renamed
    /// from <c>Jira</c>: every row written before this system had a second tracker still reads back
    /// as exactly what it was.
    /// </para>
    /// </summary>
    Tracker,
    /// <summary>Added here by a person, because the block was discovered while working.</summary>
    Local,
}

/// <summary>
/// One thing a ticket is waiting on.
/// <para>
/// The tracker is the live source for the links it knows about, so a block removed there disappears
/// here on the next refresh. What it must never do is overrule a person: a dependency discovered
/// while actually doing the work is real whether or not anyone recorded it upstream, and a block the
/// human has judged irrelevant stays gone. That is what <see cref="Override"/> is - set either way
/// it pins the answer and refresh leaves the row alone.
/// </para>
/// </summary>
public class TicketDependency
{
    public int Id { get; set; }

    /// <summary>The ticket that is blocked.</summary>
    public int TicketId { get; set; }
    public Ticket? Ticket { get; set; }

    /// <summary>
    /// The blocker as an external issue key. Deliberately NOT resolved to a local ticket id at write
    /// time: the local ticket may not exist yet, and when someone creates it later the dependency
    /// should start pointing at it without anyone having to go back and fix the row.
    /// </summary>
    public string? BlockerIssueKey { get; set; }

    /// <summary>The blocker as a local ticket, for a dependency between two unlinked tickets.</summary>
    public int? BlockerTicketId { get; set; }

    public DependencyOrigin Origin { get; set; } = DependencyOrigin.Tracker;

    /// <summary>
    /// The human's verdict, which outranks the tracker. Null means "follow the tracker"; true means
    /// this blocks regardless of what the tracker says; false means it does not, regardless. A row
    /// with an override is never removed or altered by a refresh.
    /// </summary>
    public bool? Override { get; set; }

    /// <summary>Why a person added or suppressed this, when they said.</summary>
    public string? Note { get; set; }

    /// <summary>
    /// Last-known status of an external blocker - one with no local ticket of its own. The category
    /// (an <c>IssueStatusCategory</c> name) is what decides satisfaction; the name is for display.
    /// Both are resolved by the tracker as it discovers the link, so keeping them costs no extra call.
    /// <para>
    /// Rows written before the trackers were abstracted hold Jira's own category keys here
    /// ("new" / "indeterminate" / "done"). Nothing needs migrating: the only test ever applied is
    /// case-insensitive equality with "done", which both vocabularies answer identically.
    /// </para>
    /// </summary>
    public string? BlockerStatus { get; set; }
    public string? BlockerStatusCategory { get; set; }

    public DateTime? RefreshedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A claim on something only one worker at a time may use.
/// <para>
/// The thing that actually caps how many workers can run is not the process model - it is
/// contention. QA brings the stack up on fixed ports against one database, so two QA runs in a
/// project cannot both be right no matter how cleanly their worktrees are separated. A lease is
/// taken in the same transaction as the phase claim, so a ticket is never locked for work it then
/// cannot do.
/// </para>
/// <para>
/// The concurrency limiter for the whole fleet falls out of this for free: a resource named
/// <c>claude:slot</c> with a capacity of N, required by every phase, IS an N-worker cap. Nothing
/// declares it yet - it is here so that when it is wanted, it is configuration rather than code.
/// </para>
/// </summary>
public class ResourceLease
{
    public int Id { get; set; }

    /// <summary>Fully-qualified resource name - <c>myproject:app-stack</c>, or a bare global like <c>claude:slot</c>.</summary>
    public string ResourceKey { get; set; } = "";

    /// <summary>
    /// Which of the resource's capacity slots this lease holds (0-based). Exists so the database can
    /// enforce the limit with a unique index rather than trusting every caller to count correctly -
    /// the whole point of a lease is that it holds under concurrency.
    /// </summary>
    public int Slot { get; set; }

    /// <summary>The ticket the lease was taken for, which is also how it is released.</summary>
    public int TicketId { get; set; }

    /// <summary>
    /// Set when a PERSON holds this, not a ticket's run - the dashboard's start/stop control taking
    /// the QA stack so the human poking at localhost:8080 and a QA agent cannot both have it.
    /// <para>
    /// Null for every worker lease, which is what keeps the two kinds apart everywhere it matters:
    /// <see cref="ResourceLeaseService.ReleaseAllAsync"/> wipes worker leases when the orchestrator
    /// restarts, on the reasoning that a fresh orchestrator has no workers - reasoning that says
    /// nothing about a person, whose stack is still up and still theirs across that restart.
    /// <see cref="TicketId"/> is 0 for these; there is no ticket to point at.
    /// </para>
    /// </summary>
    public string? HolderLabel { get; set; }

    /// <summary>Skill the holder is running, for display - "who has the QA stack right now".</summary>
    public string? Skill { get; set; }

    public DateTime AcquiredAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this lease stops counting even if nobody released it. A worker that is killed, or an
    /// orchestrator that dies mid-cycle, must not hold the QA stack forever - so every lease has an
    /// expiry a comfortable margin past the worker timeout, and a claim sweeps the dead ones first.
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>Set when the lease was given back. Null means held.</summary>
    public DateTime? ReleasedAt { get; set; }

    /// <summary>Why it ended - released normally, or reclaimed after expiry.</summary>
    public string? ReleaseReason { get; set; }
}

/// <summary>Who caused a phase to change.</summary>
public enum TransitionActor
{
    /// <summary>A person, through the board UI or the REST API.</summary>
    Human,
    /// <summary>A heartbeat worker, through the <c>tickets</c> MCP server.</summary>
    Worker,
    /// <summary>The dashboard itself — the claim lock, a skipped review gate, the wedged-ticket breaker.</summary>
    System,
    /// <summary>
    /// The project's steward, acting for the owner within the mode the owner set. Not held to a
    /// worker's hand-off requirements, because the moves it makes are the owner's moves; what it
    /// may do at all is decided before it gets here, by the steward's own policy.
    /// </summary>
    Steward,
}

/// <summary>
/// One phase change, recorded as data rather than prose.
/// <para>
/// Every transition already leaves a System comment behind, which is the right thing for a human
/// reading a ticket's story and the wrong thing for counting: answering "how often does QA send work
/// back?" against comment text means parsing sentences that were written to be read, not queried.
/// This table is the queryable half. It is written by <c>TicketService.TransitionAsync</c>, the one
/// funnel every phase change already goes through, so it cannot drift from what actually happened —
/// including the second hop when a review gate is auto-skipped, which recurses through the same
/// method and so lands here as its own row.
/// </para>
/// </summary>
public class PhaseTransition
{
    public int Id { get; set; }

    public int TicketId { get; set; }
    public Ticket? Ticket { get; set; }

    /// <summary>
    /// Denormalised from the ticket so per-project metrics never need a join — and so a transition
    /// stays attributable to the project it happened under even if the ticket is later moved.
    /// </summary>
    public string ProjectKey { get; set; } = "";

    /// <summary>
    /// Denormalised from the ticket for the same reason <see cref="ProjectKey"/> is, and needed for
    /// a reason that only looks cosmetic: both tracks pass through
    /// <see cref="TicketPhase.NeedsReview"/>, and the Review track's re-review loop leaves it
    /// backwards. Grouped by <see cref="From"/> alone, those rows would inflate the delivery
    /// pipeline's bounce rate with a number that means "the author pushed a fix" — an interesting
    /// figure, and not the one that gauge is reporting.
    /// </summary>
    public TicketTrack Track { get; set; } = TicketTrack.Delivery;

    public TicketPhase From { get; set; }
    public TicketPhase To { get; set; }

    public TransitionActor Actor { get; set; } = TransitionActor.System;

    /// <summary>The reason given for the move, when one was.</summary>
    public string? Reason { get; set; }

    /// <summary>
    /// True when this move went <b>backwards</b> along the pipeline — QA failing, refinement finding
    /// gaps, a reviewer asking for changes. Stored rather than derived on read because it is the
    /// number the whole table exists to produce, and computing it at write time means the definition
    /// lives in exactly one place (<see cref="Persistence.PhaseMachine.IsBackward"/>). Blocked edges
    /// are not backward moves — being blocked is its own outcome, not rework.
    /// </summary>
    public bool Backward { get; set; }

    /// <summary>
    /// True for rows reconstructed from the System comments that predate this table, rather than
    /// written as the move happened. Their <see cref="Actor"/> is a placeholder — the old comments
    /// never recorded who moved the ticket — so anything counting by actor must exclude them. The
    /// phases and timings are real, which is what the bounce rate needs.
    /// </summary>
    public bool Backfilled { get; set; }

    public DateTime At { get; set; } = DateTime.UtcNow;
}

/// <summary>How a heartbeat worker run ended.</summary>
public enum CycleOutcome
{
    /// <summary>The worker returned a result — whatever it decided to do with the ticket.</summary>
    Completed,
    /// <summary>The watchdog killed it at the configured worker timeout.</summary>
    Timeout,
    /// <summary>It stopped without a result before the timeout (a crash, or a reported error).</summary>
    Error,
}

/// <summary>
/// One heartbeat worker run: what it worked, how long it took, and what it spent.
/// <para>
/// The <c>claude -p</c> stream already ends with a <c>result</c> event carrying duration, turn count
/// and token usage; until now the heartbeat read the summary text off it and dropped the rest, so
/// nothing in the system could answer what a ticket costs or which phase burns the budget. Note what
/// is deliberately NOT here: a dollar figure. This runs on a Max subscription with no per-call
/// billing, so the scarce resource is rate-limit headroom measured in tokens, and a cost column
/// would be a number nobody is charged.
/// </para>
/// </summary>
public class CycleRun
{
    public int Id { get; set; }

    public int TicketId { get; set; }
    public Ticket? Ticket { get; set; }

    public string ProjectKey { get; set; } = "";

    /// <summary>
    /// Which pipeline this run belonged to. Denormalised like <see cref="ProjectKey"/>, so the cost
    /// and duration medians can be read per track — a peer-review pass and a QA pass are different
    /// work with different shapes, and averaging them together describes neither.
    /// </summary>
    public TicketTrack Track { get; set; } = TicketTrack.Delivery;

    /// <summary>The "-ing" phase the worker ran in.</summary>
    public TicketPhase Phase { get; set; }

    /// <summary>Skill the worker executed (speculate / implement / qa-test / refine / review-pr).</summary>
    public string Skill { get; set; } = "";

    /// <summary>True when this run resumed an already in-progress ticket rather than claiming a fresh one.</summary>
    public bool Resumed { get; set; }

    /// <summary>Model the worker ran under, so a later model change is visible in the numbers.</summary>
    public string? Model { get; set; }

    public DateTime StartedAt { get; set; }
    public DateTime EndedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Wall-clock seconds for the whole cycle — including worktree preparation, which on a project
    /// with an <c>npm ci</c> setup step is minutes of real time that the model's own duration does
    /// not see. This is the number that decides how many tickets a day the loop can finish.
    /// </summary>
    public double DurationSeconds { get; set; }

    /// <summary>The model's own reported duration, when the run produced a result. Null otherwise.</summary>
    public double? ApiDurationSeconds { get; set; }

    public int? NumTurns { get; set; }

    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    /// <summary>Tokens served from the prompt cache — cheap against the rate limit, so counted apart.</summary>
    public int? CacheReadTokens { get; set; }
    public int? CacheCreationTokens { get; set; }

    /// <summary>
    /// The largest prompt any one turn of the main conversation was sent. A run's cache-read total
    /// is roughly turns times the average of this, so the two together say whether a costly run
    /// was long or fat - which are fixed in different places.
    /// </summary>
    public int? PeakContextTokens { get; set; }

    /// <summary>Characters every tool call handed back, in all. Text only; images are counted per result.</summary>
    public int? ToolResultChars { get; set; }

    /// <summary>
    /// The handful of tool results that were biggest, as JSON (<see cref="Services.MetricsService.ToolResultSize"/>).
    /// A result enters the conversation once and is re-read on every later turn, so these name
    /// what to trim. Null for a provider whose stream does not report results.
    /// </summary>
    public string? LargestToolResultsJson { get; set; }

    /// <summary>Ticket-mutating tool calls the worker made — the crude "did this run do anything" signal.</summary>
    public int Mutations { get; set; }

    public CycleOutcome Outcome { get; set; } = CycleOutcome.Completed;

    /// <summary>
    /// The phase the ticket sat in once the run was over, filled in by the dashboard rather than
    /// reported by the worker. Together with <see cref="Phase"/> it makes "did this run hand the
    /// ticket on, leave it where it was, or send it backwards?" a single-row question instead of a
    /// join against the transition log.
    /// </summary>
    public TicketPhase? PhaseAfter { get; set; }
}
