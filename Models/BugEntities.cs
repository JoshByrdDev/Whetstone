namespace Whetstone.Models;

/// <summary>Where a <see cref="BugLink"/> stands in the owner's review of it.</summary>
public enum BugLinkStatus
{
    /// <summary>Written by a trace; nobody has looked.</summary>
    Suggested,
    /// <summary>The owner says this is where the bug came from.</summary>
    Confirmed,
    /// <summary>The owner says it is not. Kept, so a re-trace does not offer it again as new.</summary>
    Rejected,
}

/// <summary>How a link came to exist.</summary>
public enum BugLinkOrigin
{
    /// <summary>Suggested by blaming the fix (docs/bugs-and-experiments-plan.md, 1.3).</summary>
    Blame,
    /// <summary>The owner named a source the trace did not.</summary>
    Manual,
}

/// <summary>Who noticed the bug. Set by the owner when confirming.</summary>
public enum BugFoundBy
{
    Owner,
    /// <summary>QA on a later ticket ran into it.</summary>
    LaterTicketQa,
    Production,
    Steward,
}

/// <summary>
/// One answer to "which ticket wrote this bug": a bug ticket, the ticket its fix's changed lines
/// were blamed to, and the owner's verdict on that.
/// <para>
/// The board suggests and the owner decides. Confirming, rejecting and naming a source are REST
/// routes with no MCP tool behind them, the same separation by absence the steward's limits use: a
/// worker or a steward whose own ticket a bug is traced to must never be the one to clear it.
/// </para>
/// </summary>
public class BugLink
{
    public int Id { get; set; }

    public int BugTicketId { get; set; }
    public Ticket? BugTicket { get; set; }

    /// <summary>
    /// The ticket whose commits wrote the blamed lines. Null for the lines blamed to commits the board
    /// did not deliver (written by hand, or before the board existed): shown, so a trace that found
    /// only those does not look like one that found nothing.
    /// </summary>
    public int? SourceTicketId { get; set; }

    /// <summary>Denormalised from the bug ticket so per-project counts need no join.</summary>
    public string ProjectKey { get; set; } = "";

    public BugLinkStatus Status { get; set; } = BugLinkStatus.Suggested;
    public BugLinkOrigin Origin { get; set; } = BugLinkOrigin.Blame;

    /// <summary>Blamed lines attributed to this source. A neighbouring line (an add-only fix) counts half.</summary>
    public double Lines { get; set; }

    /// <summary><see cref="Lines"/> over the trace's total, 0..1.</summary>
    public double Share { get; set; }

    /// <summary>The introducing commits, as JSON: <c>[{repo, sha, subject, lines, files}]</c>.</summary>
    public string? CommitsJson { get; set; }

    /// <summary>Every line came from around an insertion rather than from a line the fix changed.</summary>
    public bool Neighbours { get; set; }

    /// <summary>
    /// The source ticket was already merged or Done when the bug ticket was filed - an escaped bug,
    /// whoever found it. Worked out when the link is written (<c>BugTraceService.IsEscaped</c>).
    /// </summary>
    public bool Escaped { get; set; }

    public BugFoundBy? FoundBy { get; set; }

    /// <summary>
    /// The pipeline stage that should have caught it: <c>speculate</c>, <c>implement</c>,
    /// <c>qa-test</c>, <c>refine</c> or <c>review</c>. A stage, not a board column, so it is the
    /// skill's name, which is what runs and experiments are counted by.
    /// </summary>
    public string? MissedBy { get; set; }

    public string? Note { get; set; }

    public DateTime TracedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DecidedAt { get; set; }
}

/// <summary>The last trace of one bug ticket: what was blamed and, when nothing was, why.</summary>
public class BugTrace
{
    /// <summary>The bug ticket. One row per ticket, overwritten on a re-trace.</summary>
    public int TicketId { get; set; }

    public DateTime TracedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Lines blamed in all, neighbours at half weight.</summary>
    public double LinesBlamed { get; set; }

    /// <summary>The fix's range per repo, as JSON: <c>[{repo, parent, tip}]</c>.</summary>
    public string? RangesJson { get; set; }

    /// <summary>Why a trace found nothing, or anything else worth saying about how it went.</summary>
    public string? Note { get; set; }
}

/// <summary>
/// A commit a ticket delivered to its base: the map from what <c>git blame</c> says to which ticket
/// wrote it. Written when the board merges a ticket (the merge's own commits) and when a ticket
/// reaches Done (its branch's commits, with patch ids, for a pull request merged elsewhere).
/// </summary>
public class TicketCommit
{
    public int Id { get; set; }

    public int TicketId { get; set; }
    public Ticket? Ticket { get; set; }

    public string ProjectKey { get; set; } = "";

    /// <summary>The repo's folder on a project of several; empty on a project of one.</summary>
    public string Repo { get; set; } = "";

    public string Sha { get; set; } = "";

    /// <summary><c>git patch-id --stable</c>, so a rebased or squashed-from-one commit still matches.</summary>
    public string? PatchId { get; set; }

    public string? Subject { get; set; }

    /// <summary><c>merge</c> (read from a board merge) or <c>branch</c> (the branch's commits at Done).</summary>
    public string Source { get; set; } = "branch";

    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;
}
