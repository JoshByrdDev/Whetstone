using System.Collections.Concurrent;

namespace Whetstone.Services.Tracker;

/// <summary>
/// Builds the right <see cref="IIssueTracker"/> for a project from that project's own config.
/// <para>
/// There is deliberately no container-wide tracker. Which system a project's issues live in - and
/// the credentials for it - are properties of the project, so every caller names one. One project can be
/// on Jira while the project beside it is on Azure Boards, and neither inherits anything from the
/// other.
/// </para>
/// <para>
/// Instances are cached per project because each owns an <see cref="HttpClient"/> and, on Jira, a
/// resolved cloudId. The cache is keyed on the settings themselves rather than just the project
/// name, so saving new credentials in Settings takes effect on the next call instead of at the next
/// restart - which matters because restarting kills every in-flight worker.
/// </para>
/// </summary>
public class IssueTrackerFactory
{
    private readonly ProjectRegistry _registry = null!;
    private readonly ConcurrentDictionary<string, (string Stamp, IIssueTracker Tracker)> _cache = new();

    public IssueTrackerFactory(ProjectRegistry registry) => _registry = registry;

    /// <summary>For a subclass that overrides <see cref="For"/> and never touches the registry.</summary>
    protected IssueTrackerFactory() { }

    /// <summary>
    /// This project's tracker. Never null: a project with no tracker gets one that is honestly
    /// unconfigured rather than a null the caller has to remember to check.
    /// <para>
    /// A tracker whose settings have changed is replaced but deliberately <b>not</b> disposed. A
    /// board render and a settings save can overlap, and disposing an instance another thread is
    /// mid-request on would turn a credential edit into an <c>ObjectDisposedException</c> on
    /// someone's screen. The cost of not disposing is one orphaned <see cref="HttpClient"/> per
    /// credential edit, collected on its own; that is the cheaper mistake by a wide margin.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Virtual so a test can stand a fake tracker behind it. The alternative was a second interface
    /// over a class with one method, which buys nothing the keyword does not.
    /// </remarks>
    public virtual IIssueTracker For(string project)
    {
        var stamp = Stamp(_registry.GetProjectConfig(project).Tracker);

        if (_cache.TryGetValue(project, out var cached) && cached.Stamp == stamp)
            return cached.Tracker;

        // Re-read inside the factory method so two racing rebuilds both see the same settings.
        return _cache.AddOrUpdate(project,
            _ => (stamp, Create(_registry.GetProjectConfig(project).Tracker)),
            (_, existing) => existing.Stamp == stamp
                ? existing
                : (stamp, Create(_registry.GetProjectConfig(project).Tracker))).Tracker;
    }

    public static IIssueTracker Create(TrackerConfig config) => config.Kind switch
    {
        TrackerKind.Jira => new JiraTracker(config),
        TrackerKind.AzureDevOps => new AzureDevOpsTracker(config),
        _ => NoTracker.Instance,
    };

    /// <summary>
    /// Everything a rebuild would depend on. The token is included by hash rather than by value:
    /// this string ends up in a dictionary key that a heap dump or a debugger watch would show, and
    /// a secret has no business being there when a fingerprint answers the same question.
    /// </summary>
    private static string Stamp(TrackerConfig c) =>
        $"{c.Kind}|{c.BaseUrl}|{c.Email}|{c.Project}|{c.Token.GetHashCode()}"
        + $"|{string.Join(",", c.EffectiveBlockingLinkTypes)}|{string.Join(",", c.EffectiveDoneStates)}";
}

/// <summary>
/// The tracker a project with no tracker has.
/// <para>
/// It exists so that "this project has no Jira" is not a null anyone can forget to check, and so an
/// unconfigured project is physically incapable of making a network call. Every read returns
/// nothing, which is the truth: there is no external system to have an opinion.
/// </para>
/// </summary>
public sealed class NoTracker : IIssueTracker
{
    public static readonly NoTracker Instance = new();
    private NoTracker() { }

    public TrackerKind Kind => TrackerKind.None;
    public string DisplayName => "no tracker";
    public string IssueNoun => "issue";
    public bool IsConfigured => false;

    public string? IssueUrl(string key) => null;
    public string? NormalizeKey(string? candidate)
        => string.IsNullOrWhiteSpace(candidate) ? null : candidate.Trim();
    public string BranchToken(string key) => key;
    public string CommitRef(string key) => key;

    public Task<Issue?> GetIssueAsync(string key, CancellationToken ct = default) => Task.FromResult<Issue?>(null);
    public Task<Issue?> GetAncestorAsync(string key, CancellationToken ct = default) => Task.FromResult<Issue?>(null);

    public Task<IReadOnlyDictionary<string, Issue>> GetBriefsAsync(
        IReadOnlyCollection<string> keys, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<string, Issue>>(
            new Dictionary<string, Issue>(StringComparer.OrdinalIgnoreCase));

    public Task<IReadOnlyList<IssueRef>> GetMyUnfinishedAsync(int max, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<IssueRef>>(Array.Empty<IssueRef>());

    public Task<IReadOnlyList<BlockingLink>> GetBlockingLinksAsync(
        IReadOnlyCollection<string> keys, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<BlockingLink>>(Array.Empty<BlockingLink>());

    public Task<byte[]?> DownloadAttachmentAsync(IssueAttachment attachment, CancellationToken ct = default)
        => Task.FromResult<byte[]?>(null);
}
