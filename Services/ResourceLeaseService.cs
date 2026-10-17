using System.Text.Json;
using Whetstone.Models;
using Whetstone.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Whetstone.Services;

/// <summary>
/// Hands out exclusive use of the things workers would otherwise fight over.
/// <para>
/// Worktrees separate what each ticket writes; they do nothing about what every ticket SHARES. QA
/// starts the stack on a fixed port against one database, so the first thing a second worker would
/// do is break the first one. A lease makes that contention explicit and checkable, which is what
/// lets the claim skip a ticket it cannot currently run instead of starting it and finding out.
/// </para>
/// </summary>
public class ResourceLeaseService
{
    private readonly DashboardDbContext _db;
    private readonly ProjectRegistry? _registry;

    public ResourceLeaseService(DashboardDbContext db, ProjectRegistry? registry = null)
        => (_db, _registry) = (db, registry);

    /// <summary>
    /// The ticket itself, as a resource. Every claim takes this one, which is what stops two workers
    /// being handed the same ticket: the phase lock (Needs -&gt; -ing) only guards the FIRST pickup,
    /// and an "-ing" ticket is deliberately resumable, so a second pass through the claim would
    /// otherwise hand a live ticket - and its worktree - to a second worker.
    /// </summary>
    public static string TicketKey(int ticketId) => $"ticket:{ticketId}";

    /// <summary>
    /// How many holders a resource allows at once. One unless global config says otherwise
    /// (<c>"resourceCapacity": { "claude:slot": 3 }</c>) - exclusivity is the point, and a resource
    /// that safely allows several is the exception worth writing down.
    /// </summary>
    public int CapacityFor(string resourceKey)
    {
        // One worker per ticket is an invariant, not a preference: it is what makes a worktree safe
        // to write in. Short-circuited above the config so it cannot be raised by a typo in a
        // resourceCapacity block that was only ever meant to describe shared stacks.
        if (resourceKey.StartsWith("ticket:", StringComparison.Ordinal)) return 1;

        if (_registry is null) return 1;
        try
        {
            var global = _registry.GetGlobalConfig();
            if (global.TryGetValue("resourceCapacity", out var caps) && caps.ValueKind == JsonValueKind.Object
                && caps.TryGetProperty(resourceKey, out var v) && v.ValueKind == JsonValueKind.Number)
                return Math.Max(1, v.GetInt32());
        }
        catch { /* malformed config must not make every resource unavailable */ }
        return 1;
    }

    /// <summary>
    /// Reclaim leases nobody gave back. Called before every acquisition, so a worker killed at the
    /// timeout - or an orchestrator that died mid-cycle - costs one claim cycle rather than jamming
    /// the resource until someone notices.
    /// </summary>
    public async Task<int> SweepExpiredAsync()
    {
        var now = DateTime.UtcNow;
        var dead = await _db.ResourceLeases.Where(l => l.ReleasedAt == null && l.ExpiresAt <= now).ToListAsync();
        foreach (var l in dead)
        {
            l.ReleasedAt = now;
            l.ReleaseReason = "expired - the holder never released it";
        }
        if (dead.Count > 0) await _db.SaveChangesAsync();
        return dead.Count;
    }

    /// <summary>
    /// Take every one of <paramref name="resourceKeys"/> for a ticket, or none of them.
    /// <para>
    /// All-or-nothing on purpose: a worker holding the database but not the stack has taken a
    /// resource away from someone who could have used it and still cannot run itself. Returns null
    /// when any one of them is busy, which the caller reads as "try the next ticket".
    /// </para>
    /// </summary>
    public async Task<List<ResourceLease>?> TryAcquireAllAsync(
        IReadOnlyCollection<string> resourceKeys, int ticketId, string? skill, TimeSpan ttl,
        string? holderLabel = null)
    {
        if (resourceKeys.Count == 0) return new List<ResourceLease>();

        await SweepExpiredAsync();

        var now = DateTime.UtcNow;
        var taken = new List<ResourceLease>();

        // Sorted so two callers chasing the same pair of resources always contend on the same one
        // first - deadlock needs them to disagree about the order.
        foreach (var key in resourceKeys.Distinct().OrderBy(k => k, StringComparer.Ordinal))
        {
            var held = await _db.ResourceLeases
                .Where(l => l.ResourceKey == key && l.ReleasedAt == null)
                .Select(l => l.Slot)
                .ToListAsync();

            var capacity = CapacityFor(key);
            var slot = Enumerable.Range(0, capacity).FirstOrDefault(s => !held.Contains(s), -1);
            if (slot < 0) return null;   // busy - and nothing has been written yet

            taken.Add(new ResourceLease
            {
                ResourceKey = key,
                Slot = slot,
                TicketId = ticketId,
                Skill = skill,
                HolderLabel = holderLabel,
                AcquiredAt = now,
                ExpiresAt = now.Add(ttl),
            });
        }

        _db.ResourceLeases.AddRange(taken);
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // The unique index refused it: someone else took the same slot between the read and the
            // write. Losing the race is a normal outcome, not an error - the caller moves on to the
            // next ticket and this one is picked up on a later cycle.
            foreach (var t in taken) _db.Entry(t).State = EntityState.Detached;
            return null;
        }

        return taken;
    }

    /// <summary>
    /// Give back everything a ticket holds. Keyed by ticket rather than by lease id so a worker that
    /// died without reporting its leases still gets cleaned up by the next thing that mentions the
    /// ticket, and so releasing twice is harmless.
    /// </summary>
    public async Task<int> ReleaseForTicketAsync(int ticketId, string? reason = null)
    {
        var held = await _db.ResourceLeases.Where(l => l.TicketId == ticketId && l.ReleasedAt == null).ToListAsync();
        foreach (var l in held)
        {
            l.ReleasedAt = DateTime.UtcNow;
            l.ReleaseReason = reason ?? "released";
        }
        if (held.Count > 0) await _db.SaveChangesAsync();
        return held.Count;
    }

    /// <summary>
    /// Drop every live lease except the ones named in <paramref name="keepTicketIds"/>. Called by the
    /// orchestrator as it starts, on the reasoning that a fresh orchestrator has no workers yet, so
    /// anything still held is from a run that was killed rather than finished. Without it a hard stop
    /// would park each in-flight ticket behind its own lease until the TTL ran out - hours of a queue
    /// that looks stuck.
    /// <para>
    /// That reasoning has one hole, and <paramref name="keepTicketIds"/> is the patch. Workers are
    /// their own processes, not children of the orchestrator, so an orchestrator that dies WITHOUT
    /// taking them down (a crash, a closed console, one pid killed by hand) leaves them running. The
    /// restart would then hand a live worker's ticket - and its half-written worktree - to a second
    /// one, which is the exact collision the ticket lease exists to prevent. The orchestrator finds
    /// those survivors before it calls this and names them here.
    /// </para>
    /// </summary>
    public async Task<int> ReleaseAllAsync(string reason, IReadOnlyCollection<int>? keepTicketIds = null)
    {
        var keep = keepTicketIds is { Count: > 0 } ? keepTicketIds.ToHashSet() : null;

        // Human-held leases survive this. The argument for wiping is "a fresh orchestrator has no
        // workers yet", which is an argument about workers only - a person's stack is still up and
        // still theirs across an orchestrator restart, and dropping their lease would hand the QA
        // stack to an agent while they are looking at it.
        var held = await _db.ResourceLeases.Where(l => l.ReleasedAt == null && l.HolderLabel == null).ToListAsync();
        if (keep is not null) held = held.Where(l => !keep.Contains(l.TicketId)).ToList();
        foreach (var l in held)
        {
            l.ReleasedAt = DateTime.UtcNow;
            l.ReleaseReason = reason;
        }
        if (held.Count > 0) await _db.SaveChangesAsync();
        return held.Count;
    }

    /// <summary>
    /// The live lease on <paramref name="resourceKey"/>, or null when it is free. Slot 0 only, which
    /// is exactly right for the things this is asked about: a stack binds fixed ports, so its
    /// capacity is one and "who holds it" has a single answer.
    /// </summary>
    public async Task<ResourceLease?> HolderOfAsync(string resourceKey)
    {
        await SweepExpiredAsync();
        return await _db.ResourceLeases
            .Where(l => l.ResourceKey == resourceKey && l.ReleasedAt == null)
            .OrderBy(l => l.Slot)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// Push a human-held lease's expiry out. Called every time the dashboard sees the stack still
    /// answering, which makes the running stack itself the evidence the lease should live: the
    /// person never has to renew anything, and a dashboard that dies stops renewing, so the lease
    /// lapses on its own instead of blocking the queue until someone notices.
    /// </summary>
    public async Task<bool> ExtendHumanLeaseAsync(string resourceKey, TimeSpan ttl)
    {
        var lease = await _db.ResourceLeases
            .FirstOrDefaultAsync(l => l.ResourceKey == resourceKey && l.ReleasedAt == null && l.HolderLabel != null);
        if (lease is null) return false;
        lease.ExpiresAt = DateTime.UtcNow.Add(ttl);
        await _db.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Give back a human-held lease. Released by resource rather than by ticket because there is no
    /// ticket - and it is a no-op when a WORKER holds the resource, so the stop path can never take
    /// a lease away from a running agent by mistake.
    /// </summary>
    public async Task<bool> ReleaseHumanLeaseAsync(string resourceKey, string reason)
    {
        var held = await _db.ResourceLeases
            .Where(l => l.ResourceKey == resourceKey && l.ReleasedAt == null && l.HolderLabel != null)
            .ToListAsync();
        foreach (var l in held)
        {
            l.ReleasedAt = DateTime.UtcNow;
            l.ReleaseReason = reason;
        }
        if (held.Count > 0) await _db.SaveChangesAsync();
        return held.Count > 0;
    }

    /// <summary>
    /// Whether <paramref name="runId"/> is still the live lease on <paramref name="ticketId"/> itself -
    /// i.e. the run that took it has not been given up on. A worker host's run is named by this id
    /// (see <c>TicketService.ClaimResult.RunId</c>), so a run the orchestrator released while its
    /// host was unreachable can neither hand its branch back nor keep running.
    /// </summary>
    public async Task<bool> IsLiveRunAsync(int runId, int ticketId)
    {
        await SweepExpiredAsync();
        var key = TicketKey(ticketId);
        return await _db.ResourceLeases.AnyAsync(l => l.Id == runId && l.ResourceKey == key && l.ReleasedAt == null);
    }

    /// <summary>Which of <paramref name="runIds"/> are still live ticket leases.</summary>
    public async Task<HashSet<int>> LiveRunsAsync(IReadOnlyCollection<int> runIds)
    {
        if (runIds.Count == 0) return new HashSet<int>();
        await SweepExpiredAsync();
        var ids = runIds.Distinct().ToList();
        var live = await _db.ResourceLeases
            .Where(l => ids.Contains(l.Id) && l.ReleasedAt == null && l.ResourceKey.StartsWith("ticket:"))
            .Select(l => l.Id).ToListAsync();
        return live.ToHashSet();
    }

    /// <summary>Everything currently held, newest first - "who has the QA stack right now".</summary>
    public async Task<List<ResourceLease>> ActiveAsync()
    {
        await SweepExpiredAsync();
        return await _db.ResourceLeases.Where(l => l.ReleasedAt == null)
            .OrderByDescending(l => l.AcquiredAt).ToListAsync();
    }

    /// <summary>Resource keys with no free slot right now - what a claim has to route around.</summary>
    public async Task<HashSet<string>> BusyResourcesAsync()
    {
        await SweepExpiredAsync();
        var counts = await _db.ResourceLeases.Where(l => l.ReleasedAt == null)
            .GroupBy(l => l.ResourceKey)
            .Select(g => new { Key = g.Key, Count = g.Count() })
            .ToListAsync();
        return counts.Where(c => c.Count >= CapacityFor(c.Key)).Select(c => c.Key).ToHashSet();
    }
}
