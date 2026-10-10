namespace Whetstone.Models;

/// <summary>Which side of the diff a comment is anchored to.</summary>
public enum DiffSide
{
    /// <summary>The branch as proposed — where nearly every review comment belongs.</summary>
    Head,
    /// <summary>The base, for a comment about something the change removed.</summary>
    Base,
}

/// <summary>
/// What a <see cref="TicketTrack.Review"/> ticket is reviewing: somebody else's branch, and how far
/// we have got with it.
/// <para>
/// A table of its own rather than columns on <see cref="Ticket"/>, because every field here is null
/// on the overwhelming majority of rows and the ticket entity is already the shape every worker
/// receives over MCP. One row per review ticket, created with it and cascading with it.
/// </para>
/// </summary>
public class ReviewTarget
{
    /// <summary>Shared primary key with the ticket — this is a 1:1, not a collection of one.</summary>
    public int TicketId { get; set; }
    public Ticket? Ticket { get; set; }

    /// <summary>
    /// The pull request's id in the host, as a string because the two hosts number differently and
    /// nothing here does arithmetic on it. Null when the branch is being reviewed before a PR exists,
    /// which is a legitimate state: the work is reviewable the moment it is pushed.
    /// </summary>
    public string? PrId { get; set; }

    /// <summary>Where a human goes to read the PR. The only field the v1 submit flow actually needs.</summary>
    public string? PrUrl { get; set; }

    /// <summary>Their branch — the one we check out, and never write to.</summary>
    public string SourceBranch { get; set; } = "";

    /// <summary>What they are merging into. Falls back to the project's base branch when unset.</summary>
    public string? TargetBranch { get; set; }

    /// <summary>Whose work this is, for display. A review addressed to nobody reads like a bug report.</summary>
    public string? PrAuthor { get; set; }

    /// <summary>
    /// The commit the branch was at when a run last looked. This is the anchor everything else dates
    /// against: a staged comment written here is a claim about these exact line numbers.
    /// </summary>
    public string? ReviewedAtSha { get; set; }

    /// <summary>
    /// The commit origin has now. Different from <see cref="ReviewedAtSha"/> means they have pushed
    /// since the last pass, which is the whole reason a review ticket loops rather than finishing.
    /// </summary>
    public string? HeadSha { get; set; }

    /// <summary>What the reviewer concluded. Proposed by a run, and only ever confirmed by a human.</summary>
    public ReviewVerdict? Verdict { get; set; }

    /// <summary>
    /// When <see cref="Verdict"/> was last written. Its own column rather than a read of
    /// <see cref="UpdatedAt"/>, which moves for reasons that have nothing to do with the conclusion.
    /// <para>
    /// Compared against the ticket's last backward move exactly as
    /// <see cref="Ticket.LearningBriefAt"/> and <see cref="Ticket.QaShotsWaiverAt"/> are: a verdict
    /// recorded before the author's last push is a judgement about a branch that has since changed,
    /// so the hand-off stops counting it rather than carrying it forward.
    /// </para>
    /// </summary>
    public DateTime? VerdictAt { get; set; }

    /// <summary>
    /// When the human sent the staged feedback to the pull request — and the reason this track needs
    /// no "awaiting dev" phase.
    /// <para>
    /// Null means the ball is with the ticket's owner (comments to curate and send); set means it is
    /// with the author. Both states are <see cref="TicketPhase.NeedsReview"/>, because that phase
    /// already means "an automated worker went as far as it can, a human acts next" — which is true
    /// either way. What differs is WHICH human, and that is a fact about the feedback rather than
    /// about the phase.
    /// </para>
    /// <para>
    /// Derived state was the alternative — scan the comments and see whether any is unsent — and this
    /// is better for two reasons. A review that found nothing still needs to say it was sent, and
    /// there are no comments to derive that from. And a stored phase could disagree with the
    /// comments underneath it, whereas a stamp written by the submit action itself cannot.
    /// </para>
    /// <para>Cleared when the ticket goes back for another pass, so it always describes the current one.</para>
    /// </summary>
    public DateTime? SubmittedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One piece of feedback staged against someone else's pull request.
/// <para>
/// <b>Nothing here is ever sent by a worker.</b> A run writes drafts and stops; the human reads,
/// edits, dismisses and submits. That is enforced structurally rather than asked for in prose —
/// there is an <c>add_review_comment</c> MCP tool and deliberately no <c>submit_review</c> — which
/// is the same guarantee <see cref="Ticket.MasterNotes"/> and <see cref="Ticket.Priority"/> already
/// rely on, and it is checkable by reading the tool list rather than by auditing a skill file.
/// </para>
/// </summary>
public class ReviewComment
{
    public int Id { get; set; }
    public int TicketId { get; set; }
    public Ticket? Ticket { get; set; }

    /// <summary>
    /// The file this is about, repo-relative, or null for a comment about the change as a whole.
    /// A PR-level comment is not a lesser one: "this needs a migration and there isn't one" belongs
    /// to no single file.
    /// </summary>
    public string? FilePath { get; set; }

    /// <summary>1-based line in <see cref="FilePath"/>, or null for a whole-file comment.</summary>
    public int? Line { get; set; }

    public DiffSide Side { get; set; } = DiffSide.Head;

    /// <summary>The comment itself, as markdown — it is rendered as markdown in the drawer and posted as it stands.</summary>
    public string Body { get; set; } = "";

    /// <summary>
    /// How much this is claiming. Load-bearing rather than decorative: a list that does not separate
    /// "this dereferences null on the empty path" from "extra blank line" is a list the human has to
    /// re-triage by hand before sending, which is most of the work this feature exists to save.
    /// </summary>
    public ReviewSeverity Severity { get; set; } = ReviewSeverity.Question;

    public ReviewCommentStatus Status { get; set; } = ReviewCommentStatus.Draft;

    /// <summary>
    /// The commit this comment's line numbers were computed against.
    /// <para>
    /// The fourth thing in this system to carry the age of its own evidence, after
    /// <see cref="QaItem.VerifiedAt"/>, <see cref="Ticket.LearningBriefAt"/> and
    /// <see cref="TicketAnalysis.HeadSha"/> — and it follows the same rule they do: staleness is
    /// DERIVED, never destructive. When the author pushes, a comment whose file the push did not
    /// touch is still exactly right; one whose file changed is flagged and kept, for the next pass to
    /// judge as still-standing, fixed, or worth rewriting. Deleting it would throw away the reasoning
    /// that produced it, which is the expensive half.
    /// </para>
    /// </summary>
    public string? AnchorSha { get; set; }

    /// <summary>Who wrote it. A human may stage their own alongside a run's, and send them together.</summary>
    public AuthorKind Author { get; set; } = AuthorKind.Claude;

    /// <summary>
    /// The QA item this came out of, when it came out of one. A failed drive-test plus the screenshot
    /// that shows it failing is the most useful comment this system can produce, and it is only
    /// possible because the review track reuses the QA entities rather than inventing its own.
    /// </summary>
    public int? QaItemId { get; set; }

    /// <summary>Slot (0..2) of the QA shot backing this comment, when one does.</summary>
    public int? QaShotOrder { get; set; }

    /// <summary>The thread id the host gave this once it was posted. Null until then, and in v1 always null.</summary>
    public string? ExternalThreadId { get; set; }

    public DateTime? SubmittedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Display order within the ticket; ties break on id.</summary>
    public int Order { get; set; }
}
