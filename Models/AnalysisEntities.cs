namespace Whetstone.Models;

/// <summary>
/// What a Classroom analysis is asked to concentrate on. The spine of sections is the same either
/// way — the lens only shifts the weighting, so the same ticket can be studied twice from different
/// angles without either run being a different kind of artefact.
/// </summary>
public enum AnalysisLens
{
    /// <summary>Everything, evenly. The default, and the right first read.</summary>
    Full,
    /// <summary>The design space: what was chosen, what was rejected, and what forced it.</summary>
    Decisions,
    /// <summary>The code: what actually changed, and the path to read it in.</summary>
    Code,
    /// <summary>The proof: what QA exercised, what it did not, and what would still slip through.</summary>
    Testing,
}

/// <summary>
/// One generated teaching analysis of a ticket — the "Classroom" artefact.
/// <para>
/// Deliberately a separate table from <see cref="Ticket.LearningBrief"/> rather than a bigger
/// version of it. The brief is written by the refine phase, once, as part of the pipeline's exit
/// gate; this is written on demand, as many times as the human wants, and is never read by any
/// phase. Nothing here feeds back into the pipeline — no worker sees it, no hand-off checks it —
/// which is exactly what makes it safe to regenerate on a whim.
/// </para>
/// <para>
/// Rows accumulate rather than being overwritten, and that is the point: reading what the system
/// looked like at QA next to what it looked like at review is the most useful thing this table can
/// offer. <see cref="HeadSha"/> and <see cref="PhaseAtGeneration"/> are what let the UI say how far
/// the ticket has moved since, instead of quietly presenting a stale analysis as current.
/// </para>
/// </summary>
public class TicketAnalysis
{
    public int Id { get; set; }

    public int TicketId { get; set; }
    public Ticket? Ticket { get; set; }

    /// <summary>Denormalized from the ticket so an analysis can be listed without loading it.</summary>
    public string ProjectKey { get; set; } = "";

    /// <summary>Phase the ticket was in when this was written — half of the staleness answer.</summary>
    public TicketPhase PhaseAtGeneration { get; set; }

    public AnalysisLens Lens { get; set; } = AnalysisLens.Full;

    /// <summary>
    /// Commit the analysis was written against — the other half of the staleness answer, and the
    /// more honest one. A ticket can sit in the same phase over four more commits.
    /// Null when the branch could not be resolved on disk at generation time.
    /// </summary>
    public string? HeadSha { get; set; }

    /// <summary>
    /// The tree at <see cref="HeadSha"/>: what the code WAS, as opposed to which commit held it.
    /// Refinement squashes the branch without changing a byte of it, which moves the head and leaves
    /// this alone - so comparing trees is what keeps an analysis written just before refinement from
    /// reading as stale the moment it reaches review. Null on analyses written before this existed.
    /// </summary>
    public string? TreeSha { get; set; }

    /// <summary>Branch the analysis read, recorded so a later rename is visible rather than silent.</summary>
    public string? Branch { get; set; }

    /// <summary>
    /// Short hash of the ticket's requirement text as it stood when this was written — the third
    /// staleness axis, and the only one that is not about us.
    /// <para>
    /// <see cref="PhaseAtGeneration"/> and <see cref="HeadSha"/> both answer "how far has the WORK
    /// moved since", and neither budges when somebody rewrites the ticket underneath a finished
    /// analysis. That case matters here in a way it did not before the coverage map existed: a map
    /// is a set of rulings against specific words, so re-worded criteria do not make it merely dated,
    /// they make it a reading of a document that no longer exists.
    /// </para>
    /// <para>
    /// Covers the ticket's OWN text only, not its ancestors'. Checking an epic's wording would mean
    /// walking the parent chain on every board render, which is several tracker calls to answer a
    /// question nobody asked yet — so an ancestor edited after the fact goes unnoticed, and that
    /// limit is why this reports a hint rather than invalidating anything. Empty on analyses written
    /// before this existed, and on tickets with no requirement text; both read as "cannot tell".
    /// </para>
    /// </summary>
    public string? DescriptionHash { get; set; }

    /// <summary>One sentence: what this ticket actually did. Denormalized out of the document for lists.</summary>
    public string Headline { get; set; } = "";

    /// <summary>
    /// The analysis itself, as the validated block document (see <c>AnalysisDocument</c>). Stored as
    /// JSON rather than markdown on purpose: the renderer owns the presentation, so a section with
    /// nothing real to say arrives as an absent block rather than as three paragraphs of filler.
    /// </summary>
    public string BlocksJson { get; set; } = "";

    // What the run cost. Tokens rather than dollars, matching the rest of the dashboard.
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CacheCreationTokens { get; set; }

    public string? Model { get; set; }
    public int DurationMs { get; set; }

    /// <summary>
    /// Tool calls the run made. Recorded for one reason: it is the denominator the queue's progress
    /// bar needs. Without a history of how much work reading a ticket under this lens actually
    /// takes, a bar can only report the clock — which keeps advancing whether or not the run does.
    /// <para>Zero on analyses written before the run counted, which is absence, not a fast run.</para>
    /// </summary>
    public int StepCount { get; set; }

    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Where a walkthrough is. Stored by name, like every enum here.</summary>
public enum WalkthroughStatus { Pending, Ready, Failed }

/// <summary>
/// A narrated slideshow of one analysis: the script Claude wrote for it, checked against the
/// analysis, the diff and the screenshots it points at, and voiced line by line.
/// <para>
/// One per analysis, written straight after it in the same queue slot. It is a separate row rather
/// than more columns on <see cref="TicketAnalysis"/> because it can fail, be retried and be rewritten
/// on its own - a failed walkthrough must never read as a failed analysis.
/// </para>
/// </summary>
public class AnalysisWalkthrough
{
    public int Id { get; set; }

    /// <summary>
    /// What it narrates: an analysis, or a unit report. Exactly one is set. One table for both,
    /// because a deck is a deck - one clip cache, one player - and only its material differs.
    /// </summary>
    public int? AnalysisId { get; set; }
    public TicketAnalysis? Analysis { get; set; }
    public int? UnitReportId { get; set; }
    public UnitReport? UnitReport { get; set; }

    public WalkthroughStatus Status { get; set; } = WalkthroughStatus.Pending;
    /// <summary>Why it failed, in a sentence. Null unless <see cref="Status"/> is Failed.</summary>
    public string? Error { get; set; }

    /// <summary>
    /// The checked deck (see <c>Services/Walkthrough/Deck.cs</c>): scenes, each beat's narration and
    /// cue, code already cut from the patch, and each beat's clip hash and length.
    /// </summary>
    public string DeckJson { get; set; } = "";

    public string Voice { get; set; } = "";
    /// <summary>References the checks threw out, after the one repair round. Zero is the goal.</summary>
    public int Dropped { get; set; }
    public double AudioSeconds { get; set; }

    // Both script runs (the first and any repair) summed, recorded like an analysis's own.
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CacheCreationTokens { get; set; }
    public string? Model { get; set; }
    public int DurationMs { get; set; }

    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A Classroom document about one unit of a steward plan: what its owner agreed, what the unit's
/// tickets delivered against it, and how the product works now - written over the tickets' own
/// analyses rather than over the code from scratch (see <c>Services/UnitReports</c>).
/// <para>
/// Its own table, not a <see cref="TicketAnalysis"/> without a ticket: every read of that table is
/// keyed by ticket, and none of those reads means anything for a unit. The document itself is the
/// same block schema, so the page renders it with the same components.
/// </para>
/// <para>
/// Rows accumulate, like analyses: the report written when the unit was built, beside the one
/// written after a late fix, is the record of what that fix changed.
/// </para>
/// </summary>
public class UnitReport
{
    public int Id { get; set; }
    public string ProjectKey { get; set; } = "";

    /// <summary>The plan's key, as on <see cref="StewardUnit.Plan"/>. A unit number names nothing without it.</summary>
    public string Plan { get; set; } = "";
    public int Unit { get; set; }

    /// <summary>The base ref read, as resolved at generation (<c>origin/main</c> or <c>main</c>).</summary>
    public string BaseRef { get; set; } = "";

    /// <summary>The base before the unit: the previous unit's release, else its earliest ticket's fork point.</summary>
    public string FromSha { get; set; } = "";

    /// <summary>The base when this was written. Code citations point here; the diff runs From..To.</summary>
    public string ToSha { get; set; } = "";

    /// <summary>Commits on the base's first-parent line in the range, and how many of them are this unit's merges.</summary>
    public int RangeCommits { get; set; }
    public int OwnCommits { get; set; }

    /// <summary>
    /// <c>[{ticketId, analysisId}]</c>: what was read. The staleness key - a ticket added to the
    /// unit, or one re-analysed after a late fix, is a report that no longer describes the unit.
    /// </summary>
    public string ReadJson { get; set; } = "[]";

    /// <summary>
    /// The screenshot pool, <c>[{n, ticketId, ticketOrder, caption, file, focus, landedAt}]</c>.
    /// The files are copied under the report's own folder when it is written: a ticket's shots are
    /// deleted on every re-QA, and a report must not lose its pictures to a later ticket.
    /// </summary>
    public string ShotsJson { get; set; } = "[]";

    /// <summary><c>[{ticketId, kind, why}]</c>: what the report was written without - an open ticket, a missing analysis.</summary>
    public string GapsJson { get; set; } = "[]";

    public string Headline { get; set; } = "";

    /// <summary>The document, as <c>AnalysisDocument</c>. Coverage rules on the unit's outcomes.</summary>
    public string BlocksJson { get; set; } = "";

    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CacheCreationTokens { get; set; }
    public string? Model { get; set; }
    public int DurationMs { get; set; }
    public int StepCount { get; set; }

    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
}
