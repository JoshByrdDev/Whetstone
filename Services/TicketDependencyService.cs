using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services.Tracker;
using Microsoft.EntityFrameworkCore;

namespace Whetstone.Services;

/// <summary>
/// What each ticket is waiting on, and whether it still is.
/// <para>
/// Two sources that must not fight. The tracker owns the links it knows about and is re-read live,
/// so a block cleared upstream clears here. A person owns the final say, because a dependency found
/// while doing the work is real even when nobody recorded it upstream - and a block they have judged
/// irrelevant must not come back on the next refresh. An overridden row is simply never touched by
/// <see cref="RefreshFromTrackerAsync"/>.
/// </para>
/// <para>
/// Which links count as blocking is the tracker's decision, driven by project config - see
/// <see cref="TrackerConfig.BlockingLinkTypes"/>. It has to be, because the two trackers disagree
/// about what blocking is: Jira has a "Blocks" link that means impossibility, while Azure DevOps
/// offers only Predecessor/Successor, which is the scheduling hint Jira users are told to ignore.
/// </para>
/// </summary>
public class TicketDependencyService
{
    private readonly DashboardDbContext _db;
    private readonly IssueTrackerFactory? _trackers;

    public TicketDependencyService(DashboardDbContext db, IssueTrackerFactory? trackers = null)
        => (_db, _trackers) = (db, trackers);

    /// <summary>A blocker resolved as far as we can, and whether it is out of the way.</summary>
    /// <param name="LocalTicketId">The blocker's own ticket here, when it has one.</param>
    /// <param name="Satisfied">True when this no longer blocks: the local ticket is Done, or the external issue is in a done status.</param>
    /// <param name="Unknown">True when nothing is known about the blocker's state - see <see cref="IsSatisfied"/> for why that does not block.</param>
    public record ResolvedBlocker(
        int Id, string Label, string? IssueKey, int? LocalTicketId,
        DependencyOrigin Origin, bool? Override, string? Note,
        string? Status, bool Satisfied, bool Unknown);

    /// <summary>
    /// Whether a dependency counts at all. A row exists to record something; the override is the
    /// human's verdict on it, and only an explicit "no" switches it off.
    /// </summary>
    public static bool IsEffective(TicketDependency d) => d.Override != false;

    /// <summary>
    /// Whether a blocker is out of the way.
    /// <para>
    /// The interesting case is the third one. When a blocker has no local ticket and the tracker has
    /// never been reached for it, the honest answer is "no idea" - and "no idea" must not block. An
    /// outage would otherwise silently halt every ticket that has ever been linked to anything,
    /// which is a far worse failure than starting one ticket early: today nothing gates on
    /// dependencies at all, so treating unknown as clear is never worse than the status quo, and it
    /// keeps an outage from wedging the board. Unknown is reported, not hidden.
    /// </para>
    /// <para>
    /// The comparison is case-insensitive against the single word "done", which is what lets one
    /// rule serve both trackers AND every row written before there were two of them: Jira's own
    /// category key was <c>done</c> and the normalised category is <c>Done</c>.
    /// </para>
    /// </summary>
    public static bool IsSatisfied(TicketDependency d, Ticket? blockerTicket, out bool unknown)
    {
        unknown = false;
        if (blockerTicket is not null) return blockerTicket.Phase == TicketPhase.Done;
        if (!string.IsNullOrEmpty(d.BlockerStatusCategory))
            return string.Equals(d.BlockerStatusCategory, "done", StringComparison.OrdinalIgnoreCase);
        unknown = true;
        return true;
    }

    /// <summary>
    /// Resolve every dependency on these tickets. Blocker keys are matched to local tickets HERE
    /// rather than when the row was written, so a local ticket created after the link was recorded is
    /// picked up with no repair step.
    /// </summary>
    public async Task<Dictionary<int, List<ResolvedBlocker>>> ResolveAsync(IReadOnlyCollection<int> ticketIds)
    {
        var result = new Dictionary<int, List<ResolvedBlocker>>();
        if (ticketIds.Count == 0) return result;

        var deps = await _db.TicketDependencies.Where(d => ticketIds.Contains(d.TicketId)).ToListAsync();
        if (deps.Count == 0) return result;

        // One lookup for every blocker either half of the model might name.
        var keys = deps.Where(d => d.BlockerIssueKey != null).Select(d => d.BlockerIssueKey!).Distinct().ToList();
        var ids  = deps.Where(d => d.BlockerTicketId != null).Select(d => d.BlockerTicketId!.Value).Distinct().ToList();
        var byKey = await _db.Tickets.Where(t => t.IssueKey != null && keys.Contains(t.IssueKey))
            .ToDictionaryAsync(t => t.IssueKey!, t => t);
        var byId = await _db.Tickets.Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t);

        foreach (var d in deps)
        {
            Ticket? blocker = null;
            if (d.BlockerTicketId is int bid) byId.TryGetValue(bid, out blocker);
            else if (d.BlockerIssueKey is string bk) byKey.TryGetValue(bk, out blocker);

            var satisfied = IsSatisfied(d, blocker, out var unknown);
            var label = blocker is not null
                ? $"#{blocker.Id}" + (d.BlockerIssueKey is not null ? $" ({d.BlockerIssueKey})" : "")
                : d.BlockerIssueKey ?? $"#{d.BlockerTicketId}";

            if (!result.TryGetValue(d.TicketId, out var list)) result[d.TicketId] = list = new();
            list.Add(new ResolvedBlocker(d.Id, label, d.BlockerIssueKey, blocker?.Id, d.Origin, d.Override,
                d.Note, blocker is not null ? blocker.Phase.ToString() : d.BlockerStatus, satisfied, unknown));
        }

        return result;
    }

    /// <summary>The ids of tickets that are effectively blocked right now - what the claim must skip.</summary>
    public async Task<HashSet<int>> BlockedTicketIdsAsync(IReadOnlyCollection<int> ticketIds)
    {
        var resolved = await ResolveAsync(ticketIds);
        var deps = await _db.TicketDependencies.Where(d => ticketIds.Contains(d.TicketId)).ToDictionaryAsync(d => d.Id, d => d);
        var blocked = new HashSet<int>();
        foreach (var (ticketId, blockers) in resolved)
            if (blockers.Any(b => !b.Satisfied && deps.TryGetValue(b.Id, out var d) && IsEffective(d)))
                blocked.Add(ticketId);
        return blocked;
    }

    // ---- Local edits (the half the tracker never touches) -----------------------

    /// <summary>Record a block a person found. Idempotent on the same blocker.</summary>
    public async Task<TicketDependency?> AddLocalAsync(int ticketId, string? blockerIssueKey, int? blockerTicketId, string? note)
    {
        var ticket = await _db.Tickets.FirstOrDefaultAsync(t => t.Id == ticketId);
        if (ticket is null) return null;

        // Tidied by the project's own tracker, because what counts as a key is its business: Jira
        // uppercases, Azure DevOps strips the "#" and the "AB#" people paste and rejects anything
        // that is not left as digits. A key the tracker will not recognise is refused here rather
        // than stored to sit unresolvable forever.
        blockerIssueKey = string.IsNullOrWhiteSpace(blockerIssueKey)
            ? null
            : (_trackers?.For(ticket.ProjectKey).NormalizeKey(blockerIssueKey)
               ?? blockerIssueKey.Trim().ToUpperInvariant());

        if (blockerIssueKey is null && blockerTicketId is null) return null;
        // A ticket waiting on itself would never be claimable again, and no phase move could fix it.
        if (blockerTicketId == ticketId) return null;

        var existing = await _db.TicketDependencies.FirstOrDefaultAsync(d => d.TicketId == ticketId
            && d.BlockerIssueKey == blockerIssueKey && d.BlockerTicketId == blockerTicketId);
        if (existing is not null)
        {
            // Adding something the tracker had already reported (or that was previously switched
            // off) is a person saying it counts - which is exactly an override, not a duplicate row.
            existing.Override = true;
            existing.Note = note ?? existing.Note;
            await _db.SaveChangesAsync();
            return existing;
        }

        var dep = new TicketDependency
        {
            TicketId = ticketId,
            BlockerIssueKey = blockerIssueKey,
            BlockerTicketId = blockerTicketId,
            Origin = DependencyOrigin.Local,
            Note = note,
        };
        _db.TicketDependencies.Add(dep);
        await _db.SaveChangesAsync();
        return dep;
    }

    /// <summary>
    /// Switch a dependency off (or back on) without deleting it. A tracker-derived block that a
    /// person has dismissed must stay recorded as dismissed - deleting the row would just let the
    /// next refresh put it straight back.
    /// </summary>
    public async Task<bool> SetOverrideAsync(int dependencyId, bool? value, string? note)
    {
        var dep = await _db.TicketDependencies.FirstOrDefaultAsync(d => d.Id == dependencyId);
        if (dep is null) return false;
        dep.Override = value;
        if (note is not null) dep.Note = note;
        await _db.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Delete a dependency outright. Only sensible for a locally-added one: deleting a
    /// tracker-derived row leaves nothing to stop the next refresh recreating it, so callers wanting
    /// that should override to false instead.
    /// </summary>
    public async Task<bool> DeleteAsync(int dependencyId)
    {
        var dep = await _db.TicketDependencies.FirstOrDefaultAsync(d => d.Id == dependencyId);
        if (dep is null) return false;
        _db.TicketDependencies.Remove(dep);
        await _db.SaveChangesAsync();
        return true;
    }

    // ---- Tracker refresh --------------------------------------------------------

    /// <summary>
    /// Re-read one project's blocking links and make the tracker-derived rows match, in one batched
    /// call rather than a call per ticket. Overridden rows are left exactly as they are, in both
    /// directions - that is what "local wins" means. Returns the number of rows that changed.
    /// <para>
    /// Best-effort: an unreachable or unconfigured tracker leaves every existing row untouched.
    /// Stale dependency data is worth far more than dependency data that vanishes whenever the
    /// tracker hiccups.
    /// </para>
    /// </summary>
    public async Task<int> RefreshFromTrackerAsync(string projectKey)
    {
        if (_trackers is null) return 0;
        var tracker = _trackers.For(projectKey);
        if (!tracker.IsConfigured) return 0;

        var tickets = await _db.Tickets
            .Where(t => t.ProjectKey == projectKey && t.IssueKey != null && t.Phase != TicketPhase.Done)
            .ToListAsync();
        if (tickets.Count == 0) return 0;

        IReadOnlyList<BlockingLink> links;
        try { links = await tracker.GetBlockingLinksAsync(tickets.Select(t => t.IssueKey!).Distinct().ToList()); }
        catch { return 0; }

        var byIssueKey = tickets.GroupBy(t => t.IssueKey!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var existing = await _db.TicketDependencies
            .Where(d => tickets.Select(t => t.Id).Contains(d.TicketId))
            .ToListAsync();

        var changes = 0;
        var seen = new HashSet<(int, string)>();

        foreach (var link in links)
        {
            if (!byIssueKey.TryGetValue(link.IssueKey, out var ticket)) continue;
            var category = link.BlockerCategory == IssueStatusCategory.Unknown
                ? null : link.BlockerCategory.ToString();

            seen.Add((ticket.Id, link.BlockerKey));
            var row = existing.FirstOrDefault(d => d.TicketId == ticket.Id && d.BlockerIssueKey == link.BlockerKey);
            if (row is null)
            {
                _db.TicketDependencies.Add(new TicketDependency
                {
                    TicketId = ticket.Id,
                    BlockerIssueKey = link.BlockerKey,
                    Origin = DependencyOrigin.Tracker,
                    BlockerStatus = link.BlockerStatus,
                    BlockerStatusCategory = category,
                    RefreshedAt = DateTime.UtcNow,
                });
                changes++;
            }
            else
            {
                // Status is refreshed even on an overridden row: the override decides whether the
                // block counts, not what the blocker's state actually is, and showing a stale
                // status next to a live decision helps nobody.
                if (row.BlockerStatus != link.BlockerStatus || row.BlockerStatusCategory != category) changes++;
                row.BlockerStatus = link.BlockerStatus;
                row.BlockerStatusCategory = category;
                row.RefreshedAt = DateTime.UtcNow;
            }
        }

        // Links that are gone upstream. Only tracker-derived, un-overridden rows go: a person's row
        // is theirs, and the tracker never having heard of it is the normal case, not a reason to
        // delete it.
        foreach (var row in existing.Where(d => d.Origin == DependencyOrigin.Tracker && d.Override is null
                                             && d.BlockerIssueKey is not null))
        {
            if (!seen.Contains((row.TicketId, row.BlockerIssueKey!)))
            {
                _db.TicketDependencies.Remove(row);
                changes++;
            }
        }

        if (changes > 0) await _db.SaveChangesAsync();
        return changes;
    }
}
