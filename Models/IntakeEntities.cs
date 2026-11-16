namespace Whetstone.Models;

/// <summary>
/// One problem a project's production reported, however many times it reported it
/// (docs/intake-plan.md, section 4): what makes a repeat a note on a ticket instead of a ticket.
/// Keyed by project, channel and the adapter's fingerprint - an error tracker's issue id, an
/// alarm's name.
/// </summary>
public class IntakeIssue
{
    public int Id { get; set; }
    public string ProjectKey { get; set; } = "";
    /// <summary><see cref="IntakeChannels"/>: <c>sentry</c>, <c>cloudwatch-alarm</c>.</summary>
    public string Channel { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    /// <summary>The latest report's title, without the channel prefix the ticket carries.</summary>
    public string Title { get; set; } = "";

    /// <summary>
    /// The ticket standing for it now: the first one filed, or the regression filed after that one
    /// was Done. Null while it is held back by the limits (<see cref="HeldIn"/>) or muted before a
    /// ticket existed.
    /// </summary>
    public int? TicketId { get; set; }

    public DateTime FirstSeen { get; set; } = DateTime.UtcNow;
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;

    /// <summary>Reports received for it. A tracker's own count is in the ticket's text, not here.</summary>
    public int Occurrences { get; set; }

    /// <summary>The last report's state: <c>firing</c>, <c>regressed</c>, <c>cleared</c>.</summary>
    public string LastState { get; set; } = "";

    /// <summary>The tracker's own count of events, from its latest report, when it gives one.</summary>
    public long? ReportedCount { get; set; }

    /// <summary>
    /// Reports since the last note on its ticket, and when that note was made: a repeat adds at
    /// most one comment an hour.
    /// </summary>
    public int UnnotedOccurrences { get; set; }
    public DateTime? LastNotedAt { get; set; }

    /// <summary>
    /// The owner's "never file this again", from the ticket's drawer: counted, never filed. Only a
    /// REST route sets or clears it; no tool does.
    /// </summary>
    public bool Muted { get; set; }
    public DateTime? MutedAt { get; set; }

    /// <summary>
    /// The limits window it was held back in, when one held it (<c>h:2026-11-16T14</c>,
    /// <c>d:2026-11-16</c>): listed on that window's storm ticket and filed in a later window if it
    /// is still firing. Cleared when it gets a ticket.
    /// </summary>
    public string? HeldIn { get; set; }

    /// <summary>
    /// Every ticket filed for it, oldest first, comma-separated: the original and each regression.
    /// </summary>
    public string? TicketHistory { get; set; }
}

/// <summary>
/// One message intake took off a queue: what makes a redelivery a no-op, and where an unrecognised
/// message is kept so a new kind of report is visible before anyone writes its adapter. Kept 14
/// days, the queue's own retention: a redelivery older than that cannot come.
/// </summary>
public class IntakeMessageLog
{
    public int Id { get; set; }
    public string ProjectKey { get; set; } = "";
    /// <summary>The source's name in the project's intake config.</summary>
    public string Source { get; set; } = "";
    /// <summary>The transport's own id for the message (SQS's <c>MessageId</c>).</summary>
    public string MessageId { get; set; } = "";
    public DateTime HandledAt { get; set; } = DateTime.UtcNow;
    /// <summary><see cref="IntakeOutcomes"/>.</summary>
    public string Outcome { get; set; } = "";
    /// <summary>The adapter that recognised it; null when none did.</summary>
    public string? Adapter { get; set; }
    public int? IssueId { get; set; }
    public int? TicketId { get; set; }
    /// <summary>A line on what was done, for the Intake page.</summary>
    public string? Note { get; set; }
    /// <summary>The raw body and attributes, kept only for an unrecognised message.</summary>
    public string? RawBody { get; set; }
    public string? RawAttributes { get; set; }
}

/// <summary>What became of one message.</summary>
public static class IntakeOutcomes
{
    /// <summary>A new ticket was filed (a first report, a regression, a held issue filed later).</summary>
    public const string Filed = "filed";
    /// <summary>A repeat or a clear: counted, and noted on the open ticket.</summary>
    public const string Noted = "noted";
    /// <summary>Its issue is muted: counted, nothing filed.</summary>
    public const string Muted = "muted";
    /// <summary>Over a limit: counted, and listed on the window's storm ticket.</summary>
    public const string Held = "held";
    /// <summary>A report that is noted and never filed (an alarm's INSUFFICIENT_DATA).</summary>
    public const string Ignored = "ignored";
    /// <summary>A project's end-to-end test: filed, and Done at once.</summary>
    public const string Test = "test";
    /// <summary>No adapter recognised it.</summary>
    public const string Unrecognised = "unrecognised";
}

/// <summary>
/// One limits window that overflowed, and the one storm ticket filed for it (docs/intake-plan.md,
/// section 5). Keyed by project and window, so a second overflow in the same window adds to the
/// same ticket.
/// </summary>
public class IntakeStorm
{
    public int Id { get; set; }
    public string ProjectKey { get; set; } = "";
    /// <summary><c>h:2026-11-16T14</c> for an hour, <c>d:2026-11-16</c> for a day.</summary>
    public string Window { get; set; } = "";
    public int TicketId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
