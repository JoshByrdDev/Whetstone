namespace Whetstone.Models;

/// <summary>
/// One thing only the owner can do, on the one list of them (docs/owner-todo-plan.md): a secret to
/// fetch, an account to open, infrastructure to stand up, a production deploy, a step by hand.
/// <para>
/// Named <c>OT-&lt;id&gt;</c> everywhere, so the owner can say "do OT-14" to a Claude Code session and
/// the session can read it off the board. Agents add and read; marking one done, dropping it or
/// waiting on it is the owner's, and has no MCP tool behind it.
/// </para>
/// </summary>
public class OwnerTask
{
    public int Id { get; set; }

    /// <summary>The project it is for, or null for one of the owner's own.</summary>
    public string? ProjectKey { get; set; }

    public OwnerTaskKind Kind { get; set; } = OwnerTaskKind.Action;

    /// <summary>One line: what to do.</summary>
    public string Title { get; set; } = "";

    /// <summary>
    /// Markdown a fresh session can carry out with nothing else: why, where, steps, done when, and
    /// where the result goes. Never a secret's value.
    /// </summary>
    public string Body { get; set; } = "";

    /// <summary>
    /// A dedupe slug an agent chose (<c>secret:STRIPE_API_KEY</c>). An add with the same key on the
    /// same project while this is open or waiting lands on this item instead of making another.
    /// </summary>
    public string? Key { get; set; }

    public OwnerTaskStatus Status { get; set; } = OwnerTaskStatus.Open;

    /// <summary>For <see cref="OwnerTaskStatus.Waiting"/>: who or what it waits on, and when to look again.</summary>
    public string? WaitingOn { get; set; }
    public DateTime? WaitingUntil { get; set; }

    /// <summary>The ticket that asked for it first. Later askers are in the log.</summary>
    public int? TicketId { get; set; }

    /// <summary>The slice it belongs to, when the asker said so. Otherwise its ticket's slice is read.</summary>
    public int? Unit { get; set; }

    /// <summary>
    /// The slice's release is refused while this is open. What a config or action release note was
    /// before the list existed.
    /// </summary>
    public bool HoldsRelease { get; set; }

    /// <summary>"owner", "session", "worker", "steward", or "release note" for one moved across.</summary>
    public string CreatedBy { get; set; } = "owner";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When the owner marked it done, and what they did: where the result lives.</summary>
    public DateTime? DoneAt { get; set; }
    public string? DoneNote { get; set; }

    /// <summary>When it was dropped, and why it is no longer needed.</summary>
    public DateTime? DroppedAt { get; set; }
    public string? DroppedBecause { get; set; }

    /// <summary>
    /// An agent's word that it looks done or no longer needed ("done" / "obsolete"), with its evidence.
    /// Only a suggestion: the owner decides, and it is cleared when they do.
    /// </summary>
    public string? Suggestion { get; set; }
    public string? SuggestionBecause { get; set; }
    public DateTime? SuggestedAt { get; set; }

    /// <summary>What became of the notification sent when it was added, or null if none was owed.</summary>
    public string? Notice { get; set; }

    public List<OwnerTaskNote> Notes { get; set; } = new();

    public string Ref => $"OT-{Id}";

    public bool IsOpen => Status is OwnerTaskStatus.Open or OwnerTaskStatus.Waiting;
}

/// <summary>One line in an item's log: who added it, who else asked, context, suggestions, the owner's marks.</summary>
public class OwnerTaskNote
{
    public int Id { get; set; }
    public int OwnerTaskId { get; set; }
    public OwnerTask? Task { get; set; }

    public OwnerTaskNoteKind Kind { get; set; }

    /// <summary>The same words as <see cref="OwnerTask.CreatedBy"/>.</summary>
    public string By { get; set; } = "";

    /// <summary>The ticket this line is about, when one is: another asker, usually.</summary>
    public int? TicketId { get; set; }

    public string Text { get; set; } = "";
    public DateTime At { get; set; } = DateTime.UtcNow;
}

/// <summary>What sort of thing an item is, which is how the list is grouped.</summary>
public enum OwnerTaskKind
{
    /// <summary>A key, token or password to get from somewhere and put somewhere. The value never goes in the item.</summary>
    Secret,
    /// <summary>A setting, a variable, a flag to set outside the code.</summary>
    Config,
    /// <summary>Infrastructure to stand up or change: a bucket, a queue, a DNS record, a server.</summary>
    Infra,
    /// <summary>An account, a subscription, an approval from a vendor.</summary>
    Account,
    /// <summary>Something to run against production or another shared environment, usually from a runbook on the branch.</summary>
    Deploy,
    /// <summary>Anything else that is the owner's: wording to read, a line for their own documents, a step by hand.</summary>
    Action,
}

public enum OwnerTaskStatus
{
    Open,
    /// <summary>Started, and waiting on someone else - a vendor's approval, a colleague.</summary>
    Waiting,
    Done,
    /// <summary>No longer needed.</summary>
    Dropped,
}

public enum OwnerTaskNoteKind
{
    /// <summary>Added: the first asker.</summary>
    Added,
    /// <summary>Another ticket asked for the same thing.</summary>
    AlsoAsked,
    /// <summary>More context: a finding, a link, a correction.</summary>
    Context,
    /// <summary>An agent suggests it is done or no longer needed.</summary>
    Suggested,
    /// <summary>The owner moved it: done, dropped, waiting, reopened, edited.</summary>
    Owner,
}
