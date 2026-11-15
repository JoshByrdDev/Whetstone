namespace Whetstone.Models;

// The Discovery track's record (docs/discovery-track-plan.md). Tables of their own rather than
// columns or collections on Ticket, for the reason ReviewTarget gives: every field is null on
// nearly every ticket, and the ticket entity is the shape every worker receives over MCP.
//
// What is kept structured rather than in prose - where a run went, at which width, as whom, on
// which commit, in which area - is kept so for a later Recon tab: coverage that the runs did not
// write down cannot be read back out of a report.

/// <summary>
/// What Chart wrote from a discovery's direction: who the person is and the jobs they came to do.
/// Goals, not clicks. One per discovery ticket, kept after Scout so a later recheck can replay it.
/// </summary>
public class DiscoveryRoute
{
    /// <summary>Shared primary key with the ticket - a 1:1.</summary>
    public int TicketId { get; set; }
    public Ticket? Ticket { get; set; }

    /// <summary>Who the person is, in a sentence or two: where they are, what they want, what they know.</summary>
    public string Persona { get; set; } = "";

    /// <summary>Which of the product's own roles they use it as (signed out, a member, an admin...).</summary>
    public string? SignedInAs { get; set; }

    /// <summary>Three to five jobs, each one line: what the person came to get done.</summary>
    public List<string> Jobs { get; set; } = new();

    /// <summary>Showcase cases to start from, by the id the project's showcase index gives them.</summary>
    public List<string> StartCases { get; set; } = new();

    /// <summary>Viewport widths to use, as "1440x900" / "390x844".</summary>
    public List<string> Widths { get; set; } = new();

    /// <summary>Anything else the scout should know before it starts, in prose.</summary>
    public string? Notes { get; set; }

    /// <summary>The areas (discovery.areas) this route is expected to cover.</summary>
    public List<string> Areas { get; set; } = new();

    /// <summary>
    /// The run that wrote it: the ticket lease held when the call was made (<c>ClaimResult.RunId</c>),
    /// which is the run's <see cref="CycleRun.LeaseRunId"/> - and through it the run's model, provider,
    /// effort and skill version. Null for a write made with no live lease.
    /// </summary>
    public int? RunId { get; set; }

    public DateTime WrittenAt { get; set; } = DateTime.UtcNow;

    /// <summary>When the owner approved it at the route gate. Null when the gate was off, or it has not been approved.</summary>
    public DateTime? ApprovedAt { get; set; }

    /// <summary>True once the owner changed what Chart wrote, at the gate.</summary>
    public bool OwnerEdited { get; set; }
}

/// <summary>What kind of thing a scout noticed.</summary>
public enum ObservationKind
{
    /// <summary>It tried to do something and could not.</summary>
    Wall,
    /// <summary>It could, but judges the experience should be different.</summary>
    Judgement,
    /// <summary>Something the product does not do and would be better for doing.</summary>
    Idea,
    /// <summary>Small, real, irritating.</summary>
    RoughEdge,
    /// <summary>It is simply broken.</summary>
    Bug,
}

/// <summary>
/// One thing Scout saw. Uncapped on purpose: the pilot played safe because it was capped, so the
/// cutting is Sift's job and Scout logs everything.
/// </summary>
public class DiscoveryObservation
{
    public int Id { get; set; }
    public int TicketId { get; set; }
    public Ticket? Ticket { get; set; }

    public ObservationKind Kind { get; set; }
    public string Title { get; set; } = "";
    /// <summary>What it saw, and why it matters to the person on the route.</summary>
    public string Saw { get; set; } = "";
    /// <summary>What it was trying to do when it saw it.</summary>
    public string? Tried { get; set; }

    /// <summary>Which of the route's jobs it was on (0-based), when it was on one.</summary>
    public int? Job { get; set; }
    public string? Url { get; set; }
    public string? Width { get; set; }
    public string? SignedInAs { get; set; }
    public string? Area { get; set; }

    /// <summary>The screenshot, copied under the data root (<c>discovery/&lt;ticket&gt;/</c>), relative to it.</summary>
    public string? ShotPath { get; set; }
    public int? FocusX { get; set; }
    public int? FocusY { get; set; }
    public int? FocusW { get; set; }
    public int? FocusH { get; set; }

    /// <summary>The commit the product was built from on this run.</summary>
    public string? Commit { get; set; }

    /// <summary>
    /// The run that wrote it: the ticket lease held when the call was made (<c>ClaimResult.RunId</c>),
    /// which is the run's <see cref="CycleRun.LeaseRunId"/> - and through it the run's model, provider,
    /// effort and skill version. Null for a write made with no live lease.
    /// </summary>
    public int? RunId { get; set; }

    /// <summary>
    /// Seconds into <see cref="RecordingId"/>'s video where this was seen. Set when the run's
    /// recording is adopted (<c>DiscoveryMedia</c>), from <see cref="ObservedAt"/>; null for a run
    /// that was not recorded.
    /// </summary>
    public double? VideoAt { get; set; }

    /// <summary>The recording this was seen in, once one is adopted that spans <see cref="ObservedAt"/>.</summary>
    public int? RecordingId { get; set; }

    /// <summary>
    /// When it was seen: the screenshot's own write time when there is one (the moment the frame
    /// shows), else when it was logged - a few seconds later, after the scout had thought about it.
    /// </summary>
    public DateTime? ObservedAt { get; set; }

    /// <summary>When retention removed the screenshot. The text stays; the drawer says when it went.</summary>
    public DateTime? ShotPrunedAt { get; set; }

    /// <summary>0 for the scout; N for what the Nth recheck saw.</summary>
    public int Round { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>One page a run was on, for coverage: where, how wide, as whom, for how long, on what code.</summary>
public class DiscoveryVisit
{
    public int Id { get; set; }
    public int TicketId { get; set; }
    public Ticket? Ticket { get; set; }

    public string Url { get; set; } = "";
    /// <summary>The route pattern the URL belongs to, where the project can say (<c>/events/[slug]</c>).</summary>
    public string? RoutePattern { get; set; }
    public string? Width { get; set; }
    public string? SignedInAs { get; set; }
    public int? Seconds { get; set; }
    public string? Area { get; set; }
    /// <summary>A showcase case the page belongs to, when it does.</summary>
    public string? Case { get; set; }
    public string? Commit { get; set; }

    /// <summary>
    /// The run that wrote it: the ticket lease held when the call was made (<c>ClaimResult.RunId</c>),
    /// which is the run's <see cref="CycleRun.LeaseRunId"/> - and through it the run's model, provider,
    /// effort and skill version. Null for a write made with no live lease.
    /// </summary>
    public int? RunId { get; set; }

    public DateTime At { get; set; } = DateTime.UtcNow;
}

/// <summary>What the owner's Complete does with an approved proposal.</summary>
public enum ProposalKind
{
    /// <summary>Filed as a Delivery ticket, linked back to the discovery.</summary>
    Ticket,
    /// <summary>The same, marked as a bug.</summary>
    Bug,
    /// <summary>Big enough to be a slice of its own: an idea for the steward, or a briefing elsewhere.</summary>
    Slice,
    /// <summary>A question only the owner can settle. Answered in the verdict.</summary>
    Decide,
}

/// <summary>Where a proposal is in the owner's verdict.</summary>
public enum ProposalStatus
{
    /// <summary>Written by Sift, not yet decided.</summary>
    Proposed,
    Approved,
    Rejected,
    /// <summary>Not now: comes back to a later discovery's Sift once due.</summary>
    Snoozed,
    /// <summary>Approved and acted on by Complete.</summary>
    Filed,
    /// <summary>Cut by Sift, with a reason - the dropped list. Never shown as a proposal.</summary>
    Dropped,
}

/// <summary>Why the owner rejected a proposal. Generic and Wrong teach taste; the others do not.</summary>
public enum RejectReason
{
    Generic,
    AlreadyDecided,
    NotNow,
    Wrong,
}

/// <summary>One proposal Sift wrote (or one it dropped, with its reason).</summary>
public class DiscoveryProposal
{
    public int Id { get; set; }
    public int TicketId { get; set; }
    public Ticket? Ticket { get; set; }

    /// <summary>1 is Sift's best. Dropped items keep the order they were dropped in.</summary>
    public int Rank { get; set; }
    public ProposalKind Kind { get; set; }
    public ProposalStatus Status { get; set; } = ProposalStatus.Proposed;

    /// <summary>What would be true for the person, in a sentence.</summary>
    public string Title { get; set; } = "";
    /// <summary>Why it matters to the person on the route.</summary>
    public string? Why { get; set; }
    /// <summary>What it would look like: enough to build from. Becomes the filed ticket's description.</summary>
    public string Body { get; set; } = "";
    /// <summary>The filed ticket's work items, one line each.</summary>
    public List<string> WorkItems { get; set; } = new();
    /// <summary>ticket | a few tickets | slice.</summary>
    public string? Size { get; set; }
    /// <summary>high | medium | low, and why.</summary>
    public string? Confidence { get; set; }
    public string? Area { get; set; }
    /// <summary>The observations it stands on - their shots are its evidence.</summary>
    public List<int> ObservationIds { get; set; } = new();

    /// <summary>Why Sift dropped it: generic, already decided, already planned in slice NN...</summary>
    public string? DropReason { get; set; }

    public RejectReason? RejectReason { get; set; }
    public string? RejectNote { get; set; }
    public DateTime? SnoozeUntil { get; set; }
    /// <summary>A Decide proposal's answer, the owner's words.</summary>
    public string? Answer { get; set; }

    /// <summary>The Delivery ticket Complete filed from it.</summary>
    public int? FiledTicketId { get; set; }
    /// <summary>What Complete did, in a sentence, when it did not file a ticket (a slice briefing, a decision).</summary>
    public string? FiledNote { get; set; }

    /// <summary>True once the owner has changed what Sift wrote.</summary>
    public bool Edited { get; set; }

    /// <summary>
    /// Sift's draft as it was written, kept beside whatever the owner edits it into, so "how much
    /// did the owner rewrite" is a comparison rather than a memory. Written once, by propose or
    /// drop_idea, and never changed.
    /// </summary>
    public ProposalKind DraftKind { get; set; }
    public string DraftTitle { get; set; } = "";
    public string? DraftWhy { get; set; }
    public string DraftBody { get; set; } = "";
    public List<string> DraftWorkItems { get; set; } = new();

    /// <summary>The owner's one tap: this is something they had not already thought of. Null until they say.</summary>
    public bool? NewToMe { get; set; }

    /// <summary>The run that wrote it (a sift, later a recheck). See <see cref="DiscoveryObservation.RunId"/>.</summary>
    public int? RunId { get; set; }

    /// <summary>0 when Sift wrote it; N when the Nth recheck did. Recheck yield is the approved share of round 1 and later.</summary>
    public int Round { get; set; }

    /// <summary>For a recheck's proposal: a fix-up of an approved goal not met, or something new. Null for Sift's.</summary>
    public RecheckKind? RecheckKind { get; set; }

    /// <summary>For a fix-up: the approved proposal (the goal) it fixes up.</summary>
    public int? GoalId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DecidedAt { get; set; }

    /// <summary>
    /// The clip cut for it from its run's recording, under the discovery's folder (<c>p&lt;id&gt;.mp4</c>).
    /// Cut on demand, the first time the drawer plays it, and before its recording is pruned.
    /// </summary>
    public string? ClipFile { get; set; }

    /// <summary>
    /// When its clip went: at Complete for a rejected or snoozed proposal (whether or not one was ever
    /// cut - none will be after), or by retention later. Once set, no clip is cut again.
    /// </summary>
    public DateTime? ClipPrunedAt { get; set; }

    /// <summary>The owner's pin: its clip and shots are kept, by every rule and the budget, until unpinned.</summary>
    public bool Pinned { get; set; }
}

/// <summary>
/// One video a Scout or Recheck run recorded (browser_start_video), adopted off the run's browser
/// folder into the discovery's own (<c>rec&lt;id&gt;.webm</c>). Observations and chapters inside its
/// span are stamped against it. The file is pruned 7 days after the verdict it fed; the row stays.
/// </summary>
public class DiscoveryRecording
{
    public int Id { get; set; }
    public int TicketId { get; set; }
    public Ticket? Ticket { get; set; }

    /// <summary>0 for the scout, N for the Nth recheck - read off what was logged during it.</summary>
    public int Round { get; set; }
    /// <summary>The run that recorded it, read off what it logged. See <see cref="DiscoveryObservation.RunId"/>.</summary>
    public int? RunId { get; set; }

    /// <summary>The file under the discovery's folder.</summary>
    public string File { get; set; } = "";
    /// <summary>Where the browser wrote it, relative to the discovery's folder - so the same file is never adopted twice.</summary>
    public string Source { get; set; } = "";

    /// <summary>
    /// The wall-clock moment that is second 0 of the video. From the name the browser gives it
    /// (<c>video-&lt;start&gt;.webm</c>), else its write time less its length.
    /// </summary>
    public DateTime StartedAt { get; set; }
    public double Seconds { get; set; }
    public long Bytes { get; set; }

    /// <summary>When the verdict this recording fed was completed: its 7 days count from here.</summary>
    public DateTime? VerdictAt { get; set; }
    /// <summary>When retention removed the file.</summary>
    public DateTime? PrunedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A chapter a run marked in its recording - a job begun, a step within one. Chapter times are the
/// boundaries a proposal's clip is cut on.
/// </summary>
public class DiscoveryChapter
{
    public int Id { get; set; }
    public int TicketId { get; set; }
    public Ticket? Ticket { get; set; }

    public string Title { get; set; } = "";
    /// <summary>Which of the route's jobs it begins or belongs to (0-based), when it does.</summary>
    public int? Job { get; set; }
    public int Round { get; set; }
    public int? RunId { get; set; }

    public DateTime At { get; set; } = DateTime.UtcNow;
    /// <summary>Seconds into <see cref="RecordingId"/>'s video, once that recording is adopted.</summary>
    public double? VideoAt { get; set; }
    public int? RecordingId { get; set; }
}

/// <summary>What a verdict event records (<see cref="DiscoveryVerdictEvent.Kind"/>).</summary>
public enum VerdictEventKind
{
    Approved,
    Rejected,
    Snoozed,
    /// <summary>A verdict taken back: the proposal is undecided again.</summary>
    Undone,
    /// <summary>A line Sift dropped, promoted by the owner to a proposal.</summary>
    Restored,
    /// <summary>The owner's "new to me" set or cleared, apart from a verdict.</summary>
    NewToMe,
    /// <summary>Complete acted on an approved proposal: a ticket filed, a briefing written, an answer recorded.</summary>
    Filed,
}

/// <summary>
/// One thing the owner did to a proposal, appended and never rewritten (docs/discovery-track-plan.md,
/// "What is measured"): a changed mind is a second event, so approval rate, rejections by reason,
/// edits and restores are counts over this log rather than over the proposal's latest state.
/// The proposal's own columns are the current state the drawer shows; this is the history the
/// marts read.
/// </summary>
public class DiscoveryVerdictEvent
{
    public int Id { get; set; }
    public int TicketId { get; set; }
    public Ticket? Ticket { get; set; }

    /// <summary>Not a foreign key: the log outlives anything that removes a proposal.</summary>
    public int ProposalId { get; set; }
    public string ProjectKey { get; set; } = "";

    public VerdictEventKind Kind { get; set; }
    public RejectReason? RejectReason { get; set; }
    public string? Note { get; set; }
    public bool? NewToMe { get; set; }

    /// <summary>Whether what the owner acted on differed from Sift's draft, and by how much.</summary>
    public bool Edited { get; set; }
    public bool KindChanged { get; set; }
    /// <summary>Characters of edit distance between the draft and the proposal as acted on (title, why, body, work items).</summary>
    public int EditDistance { get; set; }
    /// <summary>The draft's own length in the same characters, so the distance can be read as a share.</summary>
    public int DraftLength { get; set; }

    /// <summary>For Filed: the ticket it became, when it became one.</summary>
    public int? FiledTicketId { get; set; }

    public DateTime At { get; set; } = DateTime.UtcNow;
}

/// <summary>What a recheck's proposal is, beside its kind.</summary>
public enum RecheckKind
{
    /// <summary>An approved goal the recheck found not met: what would still have to change.</summary>
    FixUp,
    /// <summary>Anything else the recheck found, on the code as it now is.</summary>
    New,
}

/// <summary>What a recheck found of one approved goal, judged by using the product rather than by reading the ticket.</summary>
public enum GoalOutcome
{
    Met,
    NotMet,
    /// <summary>Could not tell: the page would not show it, the data was not there, the goal was not about something visible.</summary>
    Unclear,
}

/// <summary>
/// One recheck's finding on one approved goal (a filed Ticket or Bug proposal): did it land. One row
/// per goal per round - a run that checks a goal twice replaces its own row - and never rewritten
/// after the round, so "landed the first time" is the outcome of a goal's earliest row, and a goal
/// once met is not asked about again.
/// </summary>
public class DiscoveryGoalCheck
{
    public int Id { get; set; }
    public int TicketId { get; set; }
    public Ticket? Ticket { get; set; }

    /// <summary>The approved proposal this is a check of. Not a foreign key, like the verdict log's.</summary>
    public int ProposalId { get; set; }
    /// <summary>The ticket the goal was filed as, when this was written.</summary>
    public int? FiledTicketId { get; set; }

    /// <summary>Which recheck this was: 1 for the first.</summary>
    public int Round { get; set; }
    public GoalOutcome Outcome { get; set; }
    /// <summary>What it did and saw, in a sentence or two.</summary>
    public string Note { get; set; } = "";
    /// <summary>Observations this round that show it, when it logged any.</summary>
    public List<int> ObservationIds { get; set; } = new();

    /// <summary>The run that wrote it. See <see cref="DiscoveryObservation.RunId"/>.</summary>
    public int? RunId { get; set; }
    public string? Commit { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;
}
