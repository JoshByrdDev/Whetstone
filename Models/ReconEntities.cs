namespace Whetstone.Models;

/// <summary>What a Recon set-aside event records (<see cref="ReconDismissalEvent.Kind"/>).</summary>
public enum ReconDismissalKind
{
    /// <summary>The owner pressed Not now on a suggestion.</summary>
    Dismissed,
    /// <summary>The owner took a Not now back.</summary>
    Undone,
}

/// <summary>
/// The owner setting a Recon suggestion aside, or taking that back (docs/recon-tab-plan.md, "Not
/// now"). Appended and never rewritten. The reader hides a suggestion while its latest event is
/// <see cref="ReconDismissalKind.Dismissed"/>, the area's code has not moved past
/// <see cref="AreaHeadCommit"/> and <see cref="Until"/>, if set, is still ahead - a filter on this
/// live table, so it takes effect without a refresh. An owner action: written by a REST route with
/// no MCP tool behind it.
/// </summary>
public class ReconDismissalEvent
{
    public int Id { get; set; }
    public string ProjectKey { get; set; } = "";

    /// <summary>The suggestion's key: <c>gap:&lt;area&gt;[:&lt;role&gt;:&lt;width&gt;]</c>, <c>changed:&lt;area&gt;</c> or <c>rerun:&lt;discovery id&gt;</c>.</summary>
    public string SuggestionKey { get; set; } = "";

    public ReconDismissalKind Kind { get; set; }

    /// <summary>The latest commit that touched the area's code when the owner pressed it (the suggestion's <c>area_head_commit</c>). Null for an area with no code paths.</summary>
    public string? AreaHeadCommit { get; set; }

    /// <summary>Hidden until then at most, when the owner gave a date.</summary>
    public DateTime? Until { get; set; }

    public DateTime At { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One agent draft of directions for a Recon suggestion (docs/recon-tab-plan.md, "Drafted
/// directions"): two or three candidates, each a person and the job they came to do, for the owner
/// to pick from, edit and file - or to ignore. Asked for by the owner from the Scout form, through a
/// REST route with no MCP tool behind it, and kept, so the form reopens on the last draft rather than
/// paying for another, and so a filed discovery can say which candidate it started from
/// (<see cref="Ticket.ReconDraftId"/>) - which is how drafted and written directions are compared.
/// </summary>
public class ReconDraft
{
    public int Id { get; set; }
    public string ProjectKey { get; set; } = "";

    /// <summary>The suggestion it was drafted for: <c>gap:&lt;area&gt;[:&lt;role&gt;:&lt;width&gt;]</c> or <c>changed:&lt;area&gt;</c>.</summary>
    public string SuggestionKey { get; set; } = "";

    public string Area { get; set; } = "";

    /// <summary>The candidates, as JSON: <c>[{direction, signedInAs, width, why}]</c> (<see cref="Services.ReconDrafter.Candidate"/>).</summary>
    public string CandidatesJson { get; set; } = "[]";

    /// <summary>What the drafter said it would need to draft better, aimed at the area's <c>about</c>. Null when it had enough.</summary>
    public string? Question { get; set; }

    public string? Model { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CacheCreationTokens { get; set; }
    public int DurationMs { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
