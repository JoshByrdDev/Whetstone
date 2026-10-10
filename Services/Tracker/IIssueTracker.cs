namespace Whetstone.Services.Tracker;

/// <summary>
/// Read-only access to one project's external issue tracker.
/// <para>
/// <b>Read-only is a design rule, not an omission.</b> There is deliberately no method here that
/// edits a description, posts a comment or transitions a status. Local tickets own all state - the
/// phase, the work items, the QA items, the dependencies-with-override - and the tracker
/// contributes text and links and nothing else. That is what keeps this interface small enough to
/// have two honest implementations.
/// </para>
/// <para>
/// Everything returned is already normalised (see <see cref="Issue"/>): rich text flattened to
/// markdown, statuses reduced to <see cref="IssueStatusCategory"/>, hierarchy expressed as
/// <see cref="Issue.ParentKey"/> regardless of whether the tracker models it as a field or a link.
/// No consumer of this interface may contain the word Jira or the word Azure.
/// </para>
/// <para>
/// <b>Query languages do not appear here.</b> There is no <c>SearchAsync(string jql)</c>, because
/// JQL has no Azure DevOps equivalent - WIQL cannot even return fields in the same round-trip,
/// only ids. Every call below is an <i>intent</i> ("resolve these keys", "find my unfinished
/// work"), so a provider is free to satisfy it in one call or three.
/// </para>
/// </summary>
public interface IIssueTracker
{
    TrackerKind Kind { get; }

    /// <summary>What to call this tracker in text a human reads - "Jira", "Azure Boards".</summary>
    string DisplayName { get; }

    /// <summary>What one of its issues is called - "issue", "work item". Used in prose we generate.</summary>
    string IssueNoun { get; }

    /// <summary>
    /// False when the project has not been given credentials. Never an error on its own: a project
    /// with no tracker works perfectly well, its tickets simply carry their own titles.
    /// </summary>
    bool IsConfigured { get; }

    // ---- Identity ----------------------------------------------------------

    /// <summary>The canonical browse URL for an issue, or null when unconfigured.</summary>
    string? IssueUrl(string key);

    /// <summary>
    /// Tidy a key a human typed. Jira uppercases (keys are case-insensitive upstream but we compare
    /// ours ordinally); Azure DevOps strips the decorations people paste - <c>#1234</c>,
    /// <c>AB#1234</c> - back to the bare id. Returns null when nothing usable is left.
    /// </summary>
    string? NormalizeKey(string? candidate);

    /// <summary>
    /// The issue's identifier as it should appear in a <b>branch name</b>. Both trackers use the
    /// bare key today; it exists as a hook because the two disagree about <see cref="CommitRef"/>
    /// and splitting them later would be a silent behaviour change.
    /// </summary>
    string BranchToken(string key);

    /// <summary>
    /// The issue's identifier as it should appear in a <b>commit subject or PR title</b>.
    /// <para>
    /// This is where the two trackers genuinely differ. <c>PROJ-123: ...</c> is what a Jira user
    /// reads and what Jira's own smart-commit parser looks for. Azure DevOps links a commit to a
    /// work item only when the message contains <c>AB#1234</c> - a bare <c>1234:</c> prefix reads
    /// as noise to a human and links to nothing at all. Getting this wrong costs the traceability
    /// that is most of the reason to link a ticket to a tracker in the first place.
    /// </para>
    /// </summary>
    string CommitRef(string key);

    // ---- Reads -------------------------------------------------------------

    /// <summary>
    /// The ticket's own issue, in full: description, comments, links, attachments, parent, children.
    /// <para>
    /// Unlike every other read here this one <b>throws</b> on a transport failure rather than
    /// returning null, because its callers want to tell the difference. The MCP context tool
    /// reports the message to the agent; the resolver catches it and degrades to a cached value.
    /// Null means the tracker answered and had no such issue.
    /// </para>
    /// </summary>
    Task<Issue?> GetIssueAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// One ancestor: description, its own parent, its children, its attachments - but not its
    /// comments or its sideways links, which are the parent's business and not the child's.
    /// <para>
    /// Best-effort: returns null rather than throwing. A parent we cannot read must not fail the
    /// whole context call - it is listed as unreadable instead, so a missing description never
    /// passes for an empty one.
    /// </para>
    /// </summary>
    Task<Issue?> GetAncestorAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Summary, status, type and description for many issues at once, keyed case-insensitively by
    /// issue key. Missing keys are simply absent from the result - it is not an error to ask about
    /// an issue that has been deleted or that this token cannot see.
    /// <para>
    /// This is the board-render path: one round-trip per project rather than one per ticket. It is
    /// also how link targets are resolved, so it must stay cheap - no comments, no attachments.
    /// </para>
    /// </summary>
    Task<IReadOnlyDictionary<string, Issue>> GetBriefsAsync(
        IReadOnlyCollection<string> keys, CancellationToken ct = default);

    /// <summary>
    /// Issues assigned to the credential's owner that are not finished. Feeds the picker beside the
    /// New Ticket form's key field, so the set worth returning is exactly the set worth clicking.
    /// Filtering out the ones already on a board here is the caller's job - it spans projects.
    /// </summary>
    Task<IReadOnlyList<IssueRef>> GetMyUnfinishedAsync(int max, CancellationToken ct = default);

    /// <summary>
    /// For each of these issues, what the tracker says it is blocked BY - with the blocker's status
    /// resolved, because both providers can get it in the same round-trip.
    /// <para>
    /// Which link types count as blocking is the provider's decision, driven by project config.
    /// This is the single sharpest mismatch between the two trackers and the reason it is
    /// configurable at all: Jira's "Blocks" means impossibility, while the nearest Azure DevOps
    /// relation (Predecessor/Successor) is a scheduling hint that many teams use for ordering they
    /// have no intention of gating on.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<BlockingLink>> GetBlockingLinksAsync(
        IReadOnlyCollection<string> keys, CancellationToken ct = default);

    /// <summary>
    /// Pull an attachment's bytes down. Credentials stay on this side of the wall: the heartbeat
    /// worker never gets a tracker token, it gets local file paths.
    /// Returns null on any failure - a download we could not make is reported as not-cached, never
    /// as an exception that takes the whole context call with it.
    /// </summary>
    Task<byte[]?> DownloadAttachmentAsync(IssueAttachment attachment, CancellationToken ct = default);
}
