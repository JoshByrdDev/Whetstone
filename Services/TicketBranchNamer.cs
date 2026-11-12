using Whetstone.Models;
using Whetstone.Services.Tracker;

namespace Whetstone.Services;

/// <summary>
/// <see cref="BranchNaming"/> applied to a ticket as the board knows it: its project's prefix and
/// style, its tracker's branch token for the linked issue, and its title as the board resolves it.
/// The claim hands the result to the worker; the new-ticket form shows it as the placeholder.
/// </summary>
public class TicketBranchNamer
{
    private readonly ProjectRegistry _registry;
    private readonly IssueTrackerFactory _trackers;
    private readonly LinkedIssueResolver _resolver;

    public TicketBranchNamer(ProjectRegistry registry, IssueTrackerFactory trackers, LinkedIssueResolver resolver)
    {
        _registry = registry;
        _trackers = trackers;
        _resolver = resolver;
    }

    /// <summary>The branch this ticket has or will get: the name recorded on it, else the default.</summary>
    public async Task<string> ForTicketAsync(Ticket t) =>
        string.IsNullOrWhiteSpace(t.Branch) ? await DefaultForAsync(t) : t.Branch.Trim();

    /// <summary>
    /// The name a run would give this ticket's branch if none were recorded. A ticket not yet filed
    /// (the form's placeholder) is passed with an id of 0, and its unlinked name is only a shape.
    /// </summary>
    public async Task<string> DefaultForAsync(Ticket t)
    {
        var cfg = _registry.GetProjectConfig(t.ProjectKey);
        var prefix = BranchNaming.PrefixFor(cfg, _registry.GetGlobalConfig());
        var tracker = _trackers.For(t.ProjectKey);
        var token = string.IsNullOrWhiteSpace(t.IssueKey) ? "" : tracker.BranchToken(t.IssueKey);

        string? title = null;
        if (cfg.BranchNameStyle == BranchNaming.KeySlugStyle)
        {
            var r = await _resolver.ResolveAsync(t);
            // A linked issue the tracker could not be asked about stands in its key for its title,
            // which is no slug at all: the name goes without one rather than repeat the key.
            if (!r.Unavailable) title = r.Title;
        }
        return BranchNaming.Default(prefix, cfg.BranchNameStyle, t.Id, token, title);
    }
}
