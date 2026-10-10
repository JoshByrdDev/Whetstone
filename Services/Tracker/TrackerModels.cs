namespace Whetstone.Services.Tracker;

/// <summary>Which external system a project's issues live in.</summary>
public enum TrackerKind
{
    /// <summary>No external tracker: every ticket on this project is standalone and owns its own title.</summary>
    None,
    Jira,
    AzureDevOps,
}

/// <summary>
/// A status, reduced to the only question this system ever asks of one: is the thing finished?
/// <para>
/// Jira answers it directly - every status carries a <c>statusCategory</c> of new / indeterminate /
/// done. Azure DevOps does not: <c>System.State</c> is a per-process free-text field, so the
/// provider decides from a configured list of done states. Both land here, which is what lets
/// <see cref="TicketDependencyService.IsSatisfied"/> stay one rule rather than two.
/// </para>
/// <para>
/// <see cref="Unknown"/> is a real answer and must survive: a blocker we could not read is not the
/// same as a blocker that is open, and treating it as open would let one unreachable tracker wedge
/// every ticket that has ever been linked to anything.
/// </para>
/// </summary>
public enum IssueStatusCategory
{
    Unknown,
    Todo,
    InProgress,
    Done,
}

/// <summary>
/// An issue named but not read: enough to say what it is and whether it matters, without dragging
/// in its comments and attachments too. What a sibling, a child and a link target arrive as.
/// </summary>
public record IssueRef(string Key, string? Summary, string? Status, string? Type);

/// <param name="Id">Stable per issue, and the whole basis of "which comments are new since the plan
/// was made" - see <see cref="IssueSnapshotService"/>. A string because Jira's are strings and Azure
/// DevOps' are ints, and nothing here needs to do arithmetic on one.</param>
/// <param name="Text">Already flattened to markdown by the provider. No consumer should ever meet
/// ADF or HTML.</param>
public record IssueComment(string Id, string? Author, string? Created, string? Text);

/// <summary>
/// One hop sideways, unresolved. The relationship is the provider's own human-readable phrasing
/// ("blocks", "is blocked by", "Predecessor"), kept as the provider said it rather than mapped to
/// some house vocabulary: a reader is better served by the words their own tracker uses.
/// </summary>
public record IssueLink(string Relationship, string Key);

/// <param name="Id">Unique within the issue, and used to prefix the file on disk so two attachments
/// of the same name cannot collide. Jira gives a numeric id; Azure DevOps gives a GUID.</param>
/// <param name="DownloadUrl">Opaque to everyone but the provider that produced it - it is passed
/// straight back to <see cref="IIssueTracker.DownloadAttachmentAsync"/>, which is the only thing
/// holding credentials for it.</param>
public record IssueAttachment(
    string Id, string FileName, string DownloadUrl, long Bytes,
    string? MimeType, string? Author, string? Created);

/// <summary>
/// One external issue, normalised.
/// <para>
/// This shape is the whole point of the abstraction. An earlier version of this system passed raw
/// <c>JsonElement</c> around and every consumer dug <c>fields.status.name</c> out of it by hand -
/// which abstracts the HTTP call and nothing else, because every consumer stays Jira-shaped. Here
/// the provider does the digging once, and the resolver, the context builder and the snapshot
/// differ never learn which tracker they are reading.
/// </para>
/// <para>
/// Not every field is populated at every depth - see <see cref="IIssueTracker"/> for which call
/// fills in what. A field that was never requested arrives null or empty, exactly like a field the
/// issue genuinely does not have, so nothing may read absence as a statement about the issue.
/// </para>
/// </summary>
/// <param name="Description">Markdown. Whatever the tracker's native rich text was - ADF nodes,
/// HTML - it has already been flattened, with a visible marker left behind for anything that could
/// not be rendered rather than a silent hole.</param>
/// <param name="AcceptanceCriteria">Azure DevOps keeps acceptance criteria in a field of their own
/// (<c>Microsoft.VSTS.Common.AcceptanceCriteria</c>). Jira has no such field and leaves this null -
/// there the criteria are simply part of the description, or of the parent's.</param>
/// <param name="Parent">The hierarchy, normalised. Jira reports it as <c>fields.parent</c> and
/// never as an issue link; Azure DevOps reports it only as a link
/// (<c>System.LinkTypes.Hierarchy-Reverse</c>) and has no parent field. Both arrive here.
/// <para>
/// A <see cref="IssueRef"/> rather than a bare key because Jira's inline parent object carries a
/// summary and a type, and those are what an ancestor we cannot fetch is still able to show. A
/// parent listed with its name beats a parent listed as a number, and both beat a parent silently
/// omitted. Azure DevOps supplies only the id, so there it arrives with the rest null - honestly
/// empty rather than wrongly confident.
/// </para></param>
public record Issue(
    string Key,
    string? Summary,
    string? Description,
    string? AcceptanceCriteria,
    string? Status,
    IssueStatusCategory StatusCategory,
    string? Type,
    string? Priority,
    string? Assignee,
    string? Created,
    string? Updated,
    IssueRef? Parent,
    IReadOnlyList<IssueRef> Children,
    IReadOnlyList<IssueComment> Comments,
    IReadOnlyList<IssueLink> Links,
    IReadOnlyList<IssueAttachment> Attachments)
{
    /// <summary>An issue with nothing but its key - the shape a provider returns for "I know it
    /// exists because a child named it, and I could not read it".</summary>
    public static Issue Empty(string key) => new(
        key, null, null, null, null, IssueStatusCategory.Unknown, null, null, null, null, null,
        Parent: null,
        Array.Empty<IssueRef>(), Array.Empty<IssueComment>(), Array.Empty<IssueLink>(),
        Array.Empty<IssueAttachment>());

    public string? ParentKey => Parent?.Key;

    /// <summary>
    /// Description and acceptance criteria as one block, which is what a reader actually wants.
    /// Kept as a projection rather than folded into <see cref="Description"/> at parse time so that
    /// the snapshot diff can still say "the description changed" about the description alone.
    /// </summary>
    public string? FullText => string.IsNullOrWhiteSpace(AcceptanceCriteria)
        ? Description
        : string.IsNullOrWhiteSpace(Description)
            ? $"## Acceptance criteria\n\n{AcceptanceCriteria}"
            : $"{Description}\n\n## Acceptance criteria\n\n{AcceptanceCriteria}";
}

/// <summary>
/// "This issue cannot start until that one finishes", as the tracker reports it.
/// <para>
/// The blocker's status rides along because both providers can supply it in the same round-trip
/// they discover the link in, and re-fetching it separately would be a call per blocker for a
/// number that is only ever displayed or compared against "done".
/// </para>
/// </summary>
public record BlockingLink(
    string IssueKey, string BlockerKey, string? BlockerStatus, IssueStatusCategory BlockerCategory);
