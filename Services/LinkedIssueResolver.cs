using System.Collections.Concurrent;
using Whetstone.Models;
using Whetstone.Services.Tracker;

namespace Whetstone.Services;

/// <summary>
/// Resolves the EFFECTIVE title/description for tickets. A tracker-linked ticket stores neither
/// locally — they are the source-of-truth in the tracker and fetched live here, cached briefly so a
/// board render is one call per project (not one per ticket) and repeated views are free. If the
/// project has no tracker, or it cannot be reached, resolution degrades gracefully to the
/// last-known cached value, or the issue key itself, flagged <see cref="Resolved.Unavailable"/>.
/// </summary>
public class LinkedIssueResolver
{
    /// <summary>Effective values for one ticket. <paramref name="FromTracker"/> = the ticket is
    /// linked to an external issue; <paramref name="Unavailable"/> = the tracker could not be
    /// reached, so Title is a fallback rather than the live value.</summary>
    public record Resolved(string Title, string? Description, bool FromTracker, bool Unavailable);

    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    private readonly IssueTrackerFactory _trackers;
    private readonly ConcurrentDictionary<string, (DateTime At, string Title, string? Desc)> _cache = new();

    public LinkedIssueResolver(IssueTrackerFactory trackers) => _trackers = trackers;

    private static string CacheKey(string project, string issueKey) => $"{project} {issueKey}";

    private static Resolved Local(Ticket t) => new(t.Title ?? "", t.Description, false, false);

    /// <summary>Resolve one ticket (used by the single-ticket endpoints + MCP get_ticket).</summary>
    public async Task<Resolved> ResolveAsync(Ticket t)
    {
        if (string.IsNullOrEmpty(t.IssueKey)) return Local(t);

        var ck = CacheKey(t.ProjectKey, t.IssueKey);
        if (_cache.TryGetValue(ck, out var fresh) && DateTime.UtcNow - fresh.At < Ttl)
            return new Resolved(fresh.Title, fresh.Desc, true, false);

        try
        {
            var tracker = _trackers.For(t.ProjectKey);
            if (tracker.IsConfigured && await tracker.GetIssueAsync(t.IssueKey) is { } issue)
            {
                var (title, desc) = Extract(issue, t.IssueKey);
                _cache[ck] = (DateTime.UtcNow, title, desc);
                return new Resolved(title, desc, true, false);
            }
            // A null is the tracker positively answering "no such issue" - a key that was never
            // valid here, or one whose project has since been pointed at a different tracker. That
            // is NOT a resolution, and caching the key as its own title would report a ticket as
            // resolved while showing nothing but its key. It falls through to the same honest
            // "unavailable" the list path already reports for a key the batch did not come back
            // with; the two must agree, or the board and the drawer describe the same ticket
            // differently depending on which one you opened.
        }
        catch { /* fall through to the graceful fallback */ }

        return Fallback(t.ProjectKey, t.IssueKey);
    }

    /// <summary>
    /// Resolve many tickets for a list view: one batched fetch per project for the linked ones whose
    /// cache is cold, then build the per-ticket map. Unlinked tickets use their local values.
    /// </summary>
    public async Task<IReadOnlyDictionary<int, Resolved>> ResolveManyAsync(IEnumerable<Ticket> tickets)
    {
        var list = tickets.ToList();

        // Which (project -> keys) need a live fetch (linked and stale/missing in cache)?
        var needed = new Dictionary<string, HashSet<string>>();
        foreach (var t in list)
        {
            if (string.IsNullOrEmpty(t.IssueKey)) continue;
            if (_cache.TryGetValue(CacheKey(t.ProjectKey, t.IssueKey), out var c) && DateTime.UtcNow - c.At < Ttl) continue;
            (needed.TryGetValue(t.ProjectKey, out var set) ? set : needed[t.ProjectKey] = new()).Add(t.IssueKey);
        }

        foreach (var (project, keys) in needed)
        {
            try
            {
                var tracker = _trackers.For(project);
                if (!tracker.IsConfigured) continue;
                foreach (var (key, issue) in await tracker.GetBriefsAsync(keys))
                {
                    var (title, desc) = Extract(issue, key);
                    _cache[CacheKey(project, key)] = (DateTime.UtcNow, title, desc);
                }
            }
            catch { /* leave those keys uncached — per-ticket fallback below */ }
        }

        var map = new Dictionary<int, Resolved>(list.Count);
        foreach (var t in list)
        {
            if (string.IsNullOrEmpty(t.IssueKey)) { map[t.Id] = Local(t); continue; }
            map[t.Id] = _cache.TryGetValue(CacheKey(t.ProjectKey, t.IssueKey), out var c)
                ? new Resolved(c.Title, c.Desc, true, false)
                : Fallback(t.ProjectKey, t.IssueKey);
        }
        return map;
    }

    // Last-known cached value if we have one; else the key itself. Either way, flagged unavailable.
    private Resolved Fallback(string project, string issueKey) =>
        _cache.TryGetValue(CacheKey(project, issueKey), out var stale)
            ? new Resolved(stale.Title, stale.Desc, true, true)
            : new Resolved(issueKey, null, true, true);

    /// <summary>
    /// The two values a reader actually needs. The description is the issue's
    /// <see cref="Issue.FullText"/> rather than its description alone, because Azure DevOps keeps
    /// acceptance criteria in a field of their own - and a drawer showing the description while
    /// silently dropping the criteria beside it would be the worse kind of partial answer.
    /// </summary>
    private static (string title, string? desc) Extract(Issue issue, string fallbackKey)
        => (string.IsNullOrWhiteSpace(issue.Summary) ? fallbackKey : issue.Summary!, issue.FullText);
}
