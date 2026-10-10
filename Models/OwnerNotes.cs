namespace Whetstone.Models;

/// <summary>
/// A file the ticket's owner attached to a progress note — a screenshot of the thing that is wrong,
/// a log dump, a sample payload.
/// <para>
/// Stored the same way every other binary in this system is (see QaShotStore, IssueAttachmentStore):
/// the bytes live on disk under <c>data/ticket-files/&lt;ticketId&gt;/</c> and the row keeps only the
/// name. That shape is what makes the file reachable from both sides — the drawer renders it from a
/// URL, and the worker opens it with the Read tool at a local path, which is the only way an agent
/// can look at an image at all.
/// </para>
/// </summary>
public class NoteAttachment
{
    public int Id { get; set; }

    public int CommentId { get; set; }
    public Comment? Comment { get; set; }

    /// <summary>
    /// The ticket this file belongs to, denormalised from the comment.
    /// <para>
    /// Storage is partitioned by ticket, so every path this row resolves to needs the ticket id. Read
    /// off the parent comment it would force a join on the one call that must not need one — the
    /// worker prompt renders a path per attachment while the ticket is already in hand.
    /// </para>
    /// </summary>
    public int TicketId { get; set; }

    /// <summary>The name the human's file had, for display. Never used to build a path.</summary>
    public string FileName { get; set; } = "";

    /// <summary>
    /// Filename (not a path) under <c>data/ticket-files/&lt;ticketId&gt;/</c>. Generated, never taken
    /// from <see cref="FileName"/>: an uploaded name is attacker-shaped input even when the attacker
    /// is a tired person pasting something, and it also collides the moment two notes carry a file
    /// called <c>screenshot.png</c>.
    /// </summary>
    public string StoredFile { get; set; } = "";

    public string ContentType { get; set; } = "";
    public long Bytes { get; set; }

    /// <summary>True for the types a browser can render inline, so the drawer knows to show a thumbnail.</summary>
    public bool IsImage { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
