using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Whetstone.Models;
using Whetstone.Persistence;

namespace Whetstone.Services;

/// <summary>
/// Gives back the disk a finished ticket's worktree is holding, once the ticket has been Done long
/// enough that nobody is still looking at it in place.
///
/// A worktree is kept at Done on purpose - the finished work stays inspectable where it was built -
/// and for a while that cost nothing worth counting. It stopped being true the day a project's
/// worktree carried a gigabyte of installed packages and the fleet finished thirty tickets a day:
/// kept forever, that is a full disk inside a week, and a full disk stops every worker at once.
///
/// What goes is the directory. The branch stays, so the work itself is never what is reclaimed, and
/// a ticket that is reopened has its worktree rebuilt by the heartbeat the way a new one is.
/// </summary>
public class WorktreeReaper : BackgroundService
{
    /// <summary>
    /// Long enough after start that migrations and the first heartbeat pass are out of the way:
    /// a removal holds the hub lock, and nothing is waiting on this.
    /// </summary>
    private static readonly TimeSpan StartDelay = TimeSpan.FromMinutes(1);

    /// <summary>The app stays up for days at a time, so starting is not often enough on its own.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    public const int DefaultRetentionDays = 7;

    private readonly IServiceScopeFactory _scopes;
    private readonly ProjectRegistry _registry;
    private readonly GitWorktreeService _worktrees;
    private readonly ILogger<WorktreeReaper> _log;

    public WorktreeReaper(IServiceScopeFactory scopes, ProjectRegistry registry,
        GitWorktreeService worktrees, ILogger<WorktreeReaper> log)
        => (_scopes, _registry, _worktrees, _log) = (scopes, registry, worktrees, log);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var days = RetentionDays(_registry.GetGlobalConfig());
                if (days > 0)
                {
                    using var scope = _scopes.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<DashboardDbContext>();
                    var removed = await ReapAsync(db, _worktrees,
                        project => _registry.GetProjectConfig(project).ProjectPath,
                        TimeSpan.FromDays(days), DateTime.UtcNow, _log, stoppingToken);
                    if (removed > 0)
                        _log.LogInformation("Reaped {Count} worktree(s) of tickets Done for over {Days} day(s)", removed, days);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _log.LogWarning(ex, "Worktree reaping pass failed");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary><c>worktreeRetentionDays</c> from the global config. Zero or less keeps every worktree.</summary>
    public static int RetentionDays(Dictionary<string, JsonElement> global) =>
        global.TryGetValue("worktreeRetentionDays", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var days)
            ? days
            : DefaultRetentionDays;

    /// <summary>
    /// One pass: remove the worktree of every ticket that has been Done for longer than
    /// <paramref name="keep"/>. Returns how many came out.
    ///
    /// Everything it declines to do, it declines quietly and leaves for a person: a worktree with
    /// uncommitted or untracked files (git refuses, and nothing here forces it), a path git does not
    /// know as a worktree of that repo, and a worktree a ticket still in flight also names.
    /// </summary>
    public static async Task<int> ReapAsync(DashboardDbContext db, GitWorktreeService worktrees,
        Func<string, string?> repoFor, TimeSpan keep, DateTime now, ILogger log, CancellationToken ct = default)
    {
        var cutoff = now - keep;

        var done = await db.Tickets
            .Where(t => t.Phase == TicketPhase.Done && t.WorktreePath != null && t.WorktreePath != "")
            .ToListAsync(ct);
        if (done.Count == 0) return 0;

        // When each last arrived at Done. A ticket can be reopened and finished again, and it is the
        // last time that says how recently somebody cared about it.
        var ids = done.Select(t => t.Id).ToList();
        var arrivals = (await db.PhaseTransitions
                .Where(p => p.To == TicketPhase.Done && ids.Contains(p.TicketId))
                .Select(p => new { p.TicketId, p.At })
                .ToListAsync(ct))
            .GroupBy(p => p.TicketId)
            .ToDictionary(g => g.Key, g => g.Max(p => p.At));

        // Two tickets can name one worktree - a branch reviewed twice shares its review worktree
        // between the passes - and one of them being finished says nothing about the other.
        var inUse = (await db.Tickets
                .Where(t => t.Phase != TicketPhase.Done && t.WorktreePath != null && t.WorktreePath != "")
                .Select(t => t.WorktreePath!)
                .ToListAsync(ct))
            .Select(Normalize)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var removed = 0;
        foreach (var t in done)
        {
            if (ct.IsCancellationRequested) break;

            // No transition row means a ticket older than the table; its last edit is the best there is.
            var doneAt = arrivals.TryGetValue(t.Id, out var at) ? at : t.UpdatedAt;
            if (doneAt > cutoff) continue;
            if (inUse.Contains(Normalize(t.WorktreePath!))) continue;

            var repo = repoFor(t.ProjectKey);
            if (string.IsNullOrWhiteSpace(repo)) continue;

            var path = t.WorktreePath!;
            // Off the request threads' pool budget: this is a process wait measured in seconds.
            var result = await Task.Run(() => worktrees.Remove(repo, path), ct);

            if (result.Removed) removed++;
            else if (result.Error is not null)
            {
                log.LogInformation("Kept the worktree of Done ticket {Id} at {Path}: {Reason}", t.Id, path, result.Error);
                continue;
            }

            // Removed now, or already gone by hand: either way the record points at nothing. Not a
            // change to the ticket, so UpdatedAt is left alone.
            if (!Directory.Exists(path))
            {
                t.WorktreePath = null;
                await db.SaveChangesAsync(ct);
            }
        }
        return removed;
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar)));
}
