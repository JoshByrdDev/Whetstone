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
