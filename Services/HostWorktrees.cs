using Microsoft.EntityFrameworkCore;
using Whetstone.Models;
using Whetstone.Persistence;

namespace Whetstone.Services;

/// <summary>
/// One worktree a runner found under a project's worktree root on its machine, by what it holds -
/// never by where it is: a worker host's paths never reach the board (CLAUDE.md, "Worker hosts").
/// </summary>
/// <param name="Name">The folder's own name under the root - what the answer is keyed by, so the runner can match it back.</param>
/// <param name="Kind">
/// <c>branch</c>: a ticket's own branch is checked out there (<paramref name="Branch"/>).
/// <c>review</c>: a detached checkout of somebody else's branch, named <c>review-&lt;branch&gt;</c>.
/// <c>base</c>: a detached checkout of the base for a slice's walk-through or a discovery,
/// named <c>walkthrough-ticket-&lt;id&gt;</c> (<paramref name="TicketId"/>).
/// </param>
public record HostWorktreeItem(string Name, string Kind, string? Branch = null, int? TicketId = null);

/// <summary>Whether the runner may remove one worktree, and why, in words for its log.</summary>
public record HostWorktreeVerdict(string Name, bool Reap, string Why);

/// <summary>
/// The board's half of reaping a worker host's worktrees. <see cref="WorktreeReaper"/> removes this
/// machine's by <c>Ticket.WorktreePath</c>, which a worker host never reports, so nothing ever
/// removed a worktree there - and a host whose every worktree held its own package install filled
/// its disk. The runner lists what it has and asks; the board, which alone knows the tickets,
/// answers; the runner removes only what it was told it may, and only after its own checks (a run
/// holding it, uncommitted work, a stopped rebase - see Remove-HostWorktrees in data/runner.ps1).
/// <para>
/// The rule is <see cref="WorktreeReaper"/>'s, so a worktree lives as long on either machine: a
/// ticket's worktree goes once the ticket has been Done for <c>worktreeRetentionDays</c>. A
/// worktree no ticket names any more - its ticket deleted, its branch renamed on the board - goes
/// as well: nothing will ever come back for it. And a retention of 0, which keeps every worktree
/// here, keeps every one there too.
/// </para>
/// </summary>
public static class HostWorktrees
{
    /// <summary>The answer for one project's worktrees, item by item and in the same order.</summary>
    public static async Task<IReadOnlyList<HostWorktreeVerdict>> DecideAsync(DashboardDbContext db, string project,
        IReadOnlyList<HostWorktreeItem> items, TimeSpan keep, DateTime now, CancellationToken ct = default)
    {
        if (keep <= TimeSpan.Zero)
            return items.Select(i => new HostWorktreeVerdict(i.Name, false, "worktreeRetentionDays is 0: every worktree is kept")).ToList();

        var tickets = await db.Tickets.AsNoTracking()
            .Where(t => t.ProjectKey == project)
            .Select(t => new Row(t.Id, t.Phase, t.Branch, t.UpdatedAt, t.ReviewTarget != null ? t.ReviewTarget.SourceBranch : null))
            .ToListAsync(ct);
        var doneIds = tickets.Where(t => t.Phase == TicketPhase.Done).Select(t => t.Id).ToList();
        var arrivals = (await db.PhaseTransitions.AsNoTracking()
                .Where(p => p.To == TicketPhase.Done && doneIds.Contains(p.TicketId))
                .Select(p => new { p.TicketId, p.At })
                .ToListAsync(ct))
            .GroupBy(p => p.TicketId)
            .ToDictionary(g => g.Key, g => g.Max(p => p.At));
        var cutoff = now - keep;
        var days = keep.TotalDays.ToString("0.#");

        // The last time each finished ticket arrived at Done - as the board's reaper reads it.
        DateTime DoneAt(Row t) => arrivals.TryGetValue(t.Id, out var at) ? at : t.UpdatedAt;

        // Every ticket that names one worktree: kept while any of them is still going, and until the
        // last of them has been Done for the retention.
        HostWorktreeVerdict ByTickets(HostWorktreeItem item, List<Row> named, string what)
        {
            if (named.Count == 0) return new(item.Name, true, $"no ticket on the board names {what} any more");
            if (named.FirstOrDefault(t => t.Phase != TicketPhase.Done) is { } open)
                return new(item.Name, false, $"ticket #{open.Id} is {open.Phase}");
            var last = named.OrderByDescending(DoneAt).First();
            var at = DoneAt(last);
            return at > cutoff
                ? new(item.Name, false, $"ticket #{last.Id} has been Done only since {at:yyyy-MM-dd HH:mm} UTC (kept {days} day(s))")
                : new(item.Name, true, $"ticket #{last.Id} has been Done since {at:yyyy-MM-dd HH:mm} UTC, past {days} day(s)");
        }

        var result = new List<HostWorktreeVerdict>();
        foreach (var item in items)
        {
            switch (item.Kind)
            {
                case "branch" when !string.IsNullOrWhiteSpace(item.Branch):
                    result.Add(ByTickets(item, tickets.Where(t => t.Branch == item.Branch).ToList(), $"branch '{item.Branch}'"));
                    break;

                // A review worktree is reused by every review of the same branch, so a branch reviewed
                // again as its author answers pays its package install once (Resolve-Worktree). It is
                // kept, then, while any review of that branch is open, and goes once the last of them
                // has been Done for the retention - the same rule as two tickets sharing one on the
                // board (WorktreeReaper's inUse).
                case "review":
                    result.Add(ByTickets(item, tickets
                        .Where(t => t.ReviewSource is { Length: > 0 } src && BranchNaming.WorktreeDirName("review-" + src) == item.Name)
                        .ToList(), $"review worktree '{item.Name}'"));
                    break;

                // A walk-through's or a discovery's checkout holds no work of its own: it is the base,
                // detached, and every run of the ticket moves it to the base as it then stands. So it is
                // looser than a ticket's own worktree - it also goes when its ticket, though not Done,
                // has not moved for the retention (a discovery waiting on the tickets it filed can sit
                // for weeks), because rebuilding it costs only the install and loses nothing.
                case "base" when item.TicketId is { } id:
                    if (tickets.FirstOrDefault(t => t.Id == id) is not { } owner)
                        result.Add(new(item.Name, true, $"ticket #{id} is not on the board any more"));
                    else if (owner.Phase == TicketPhase.Done)
                        result.Add(ByTickets(item, new List<Row> { owner }, $"ticket #{id}"));
                    else if (owner.UpdatedAt <= cutoff)
                        result.Add(new(item.Name, true, $"ticket #{id} is {owner.Phase} but has not moved since {owner.UpdatedAt:yyyy-MM-dd HH:mm} UTC, past {days} day(s), and this checkout holds no work"));
                    else
                        result.Add(new(item.Name, false, $"ticket #{id} is {owner.Phase}"));
                    break;

                default:
                    result.Add(new(item.Name, false, "not a worktree the board can place"));
                    break;
            }
        }
        return result;
    }

    private sealed record Row(int Id, TicketPhase Phase, string? Branch, DateTime UpdatedAt, string? ReviewSource);
}
