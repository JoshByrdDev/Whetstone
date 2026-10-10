namespace Whetstone.Models;

/// <summary>
/// One of the owner's filing tags — "ui", "tech-debt", "waiting-on-client". Global across projects,
/// the way the board is: a label put on one project's ticket is offered again on another's, because
/// the person doing the filing is the same person either way.
/// <para>
/// Deliberately invisible to the pipeline. There is no navigation from <see cref="Ticket"/> to here
/// and no MCP tool that reads one, which is a stronger guarantee than asking the skills not to look:
/// the ticket aggregate is what every phase receives over MCP, and a collection hanging off it is
/// one forgotten <c>Include</c> away from being in a prompt. Nothing about how a ticket is worked
/// may ever turn on these — they are housekeeping, and housekeeping the agents can read is
/// housekeeping that quietly becomes instructions.
/// </para>
/// <para>
/// A label exists only while something wears it (see TicketLabelService): taking the last one off
/// deletes the row. That is what keeps the picker's list honest without a screen for pruning it —
/// the offered set is the set actually in use, and a typo stops being offered the moment it stops
/// being worn.
/// </para>
/// </summary>
public class Label
{
    /// <summary>
    /// Longest label we store. Generous enough for a phrase, short enough that a chip stays a chip —
    /// a label needing a sentence is a note, and the ticket already has two places to put those.
    /// </summary>
    public const int MaxLength = 40;

    public int Id { get; set; }

    /// <summary>
    /// As the owner typed it, tidied (see TicketLabelService.Normalize). Unique case-insensitively,
    /// so "UI" and "ui" are the same label and the first spelling wins.
    /// </summary>
    public string Name { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<TicketLabel> Assignments { get; set; } = new();
}

/// <summary>
/// One label worn by one ticket. Its own entity rather than a skeleton join table so the pairing can
/// carry when it was made — "when did I file this" is the kind of thing the board's owner asks.
/// </summary>
public class TicketLabel
{
    public int TicketId { get; set; }
    /// <summary>
    /// The relationship is configured from this side only — the ticket has no navigation back (see
    /// <see cref="Label"/> for why). Deleting a ticket still cascades through the foreign key.
    /// </summary>
    public Ticket? Ticket { get; set; }

    public int LabelId { get; set; }
    public Label? Label { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
