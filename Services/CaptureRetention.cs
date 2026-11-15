using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Whetstone.Models;
using Whetstone.Persistence;

namespace Whetstone.Services;

/// <summary>
/// Keeps the board's two stores of QA pictures from growing without limit - the policy is
/// docs/capture-retention.md, and this is all of its enforcement on the board host.
/// <para>
/// <c>qa-captures/&lt;ticket&gt;</c> is the QA browser's own output, which nothing reads once
/// <c>set_qa_shots</c> has copied the chosen files out; <c>qa-shots/&lt;ticket&gt;</c> is what the
/// drawer, the Classroom and walkthrough decks show. Each is kept for a while after its ticket
/// reached Done, a size budget behind that prunes the longest-Done ticket first, and an open
/// ticket is never touched by either rule. A pruned shot keeps its row, marked
/// <see cref="QaShot.PrunedAt"/>, so what showed the picture says when it went instead.
/// </para>
/// <para>
/// This machine's copies only. A worker host holds the same paths under its own root and removes
/// them itself once its runs are over (<c>Remove-HostTicketCopies</c> in <c>runner.ps1</c>).
/// </para>
/// </summary>
public class CaptureRetention : BackgroundService
{
    private static readonly TimeSpan StartDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    public const string LedgerFile = "capture-retention.jsonl";

    /// <summary>What the policy keeps. A non-positive days or budget turns that rule off.</summary>
    public sealed record Policy(int QaCapturesDays = 7, int QaShotsDays = 90, long QaCapturesBudgetMb = 2048, long QaShotsBudgetMb = 1024)
    {
        /// <summary>
        /// <c>retention</c> from the global config. A key that is missing or not a number keeps its
        /// default, so an override file that names one setting leaves the others where they were.
        /// </summary>
        public static Policy From(Dictionary<string, JsonElement> global)
        {
            var p = new Policy();
            if (!global.TryGetValue("retention", out var r) || r.ValueKind != JsonValueKind.Object) return p;
            int Int(string k, int d) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : d;
            long Long(string k, long d) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var i) ? i : d;
            return new Policy(Int("qaCapturesDays", p.QaCapturesDays), Int("qaShotsDays", p.QaShotsDays),
                Long("qaCapturesBudgetMb", p.QaCapturesBudgetMb), Long("qaShotsBudgetMb", p.QaShotsBudgetMb));
        }
    }

    public enum Store { QaCaptures, QaShots }

    /// <summary>One ticket folder a pass removed, as the ledger records it.</summary>
    public sealed record Pruned(Store Store, int TicketId, string Reason, long Bytes, DateTime At);

    private readonly IServiceScopeFactory _scopes;
    private readonly ProjectRegistry _registry;
    private readonly DashboardPaths _paths;
    private readonly ILogger<CaptureRetention> _log;

    public CaptureRetention(IServiceScopeFactory scopes, ProjectRegistry registry, DashboardPaths paths, ILogger<CaptureRetention> log)
        => (_scopes, _registry, _paths, _log) = (scopes, registry, paths, log);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<DashboardDbContext>();
                var pruned = await PruneAsync(db, _paths, Policy.From(_registry.GetGlobalConfig()), DateTime.UtcNow, _log, stoppingToken);
                if (pruned.Count > 0)
                    _log.LogInformation("Capture retention removed {Count} ticket folder(s), {Mb:F1}MB",
                        pruned.Count, pruned.Sum(p => p.Bytes) / 1048576.0);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _log.LogWarning(ex, "Capture retention pass failed");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    public static string StoreDir(DashboardPaths paths, Store store) =>
        store == Store.QaShots ? paths.QaShotsDir : Path.Combine(paths.DataRoot, HostDataMirror.QaCapturesFolder);

    /// <summary>
    /// One pass over both stores. Returns every ticket folder it removed, each also appended to the
    /// ledger; what it could not remove (a file held open) is left for the next pass.
    /// </summary>
    public static async Task<IReadOnlyList<Pruned>> PruneAsync(DashboardDbContext db, DashboardPaths paths, Policy policy,
        DateTime now, ILogger log, CancellationToken ct = default)
    {
        var pruned = new List<Pruned>();
        pruned.AddRange(await PruneStoreAsync(db, paths, Store.QaCaptures, policy.QaCapturesDays, policy.QaCapturesBudgetMb, now, log, ct));
        pruned.AddRange(await PruneStoreAsync(db, paths, Store.QaShots, policy.QaShotsDays, policy.QaShotsBudgetMb, now, log, ct));
        if (pruned.Count > 0) AppendLedger(paths, pruned, log);
        return pruned;
    }

    private sealed record Folder(int TicketId, string Path, long Bytes, bool Exists, DateTime? DoneAt, bool Open);

    private static async Task<List<Pruned>> PruneStoreAsync(DashboardDbContext db, DashboardPaths paths, Store store,
        int days, long budgetMb, DateTime now, ILogger log, CancellationToken ct)
    {
        var root = StoreDir(paths, store);
        if (!Directory.Exists(root)) return new();

        // A ticket's folder is named by its id; anything else in the store is not this policy's.
        var dirs = Directory.EnumerateDirectories(root)
            .Select(d => (dir: d, id: int.TryParse(System.IO.Path.GetFileName(d), out var i) ? i : (int?)null))
            .Where(x => x.id is not null)
            .ToDictionary(x => x.id!.Value, x => x.dir);
        if (dirs.Count == 0) return new();

        var ids = dirs.Keys.ToList();
        var tickets = await db.Tickets.AsNoTracking().Where(t => ids.Contains(t.Id))
            .Select(t => new { t.Id, t.Phase, t.UpdatedAt }).ToDictionaryAsync(t => t.Id, ct);
        var arrivals = (await db.PhaseTransitions.AsNoTracking()
                .Where(p => p.To == TicketPhase.Done && ids.Contains(p.TicketId))
                .Select(p => new { p.TicketId, p.At }).ToListAsync(ct))
            .GroupBy(p => p.TicketId).ToDictionary(g => g.Key, g => g.Max(p => p.At));

        var folders = dirs.Select(kv =>
        {
            var exists = tickets.TryGetValue(kv.Key, out var t);
            var open = exists && t!.Phase != TicketPhase.Done;
            DateTime? doneAt = exists && !open ? (arrivals.TryGetValue(kv.Key, out var at) ? at : t!.UpdatedAt) : null;
            return new Folder(kv.Key, kv.Value, SizeOf(kv.Value), exists, doneAt, open);
        }).ToList();

        var result = new List<Pruned>();
        var gone = new HashSet<int>();

        // 1. Orphans, then 2. age: both decided per folder.
        foreach (var f in folders.Where(f => !f.Open))
        {
            if (ct.IsCancellationRequested) break;
            string? reason = !f.Exists ? "orphan"
                : days > 0 && f.DoneAt is { } d && now - d > TimeSpan.FromDays(days) ? "age"
                : null;
            if (reason is null) continue;
            if (await RemoveAsync(db, store, f, reason, now, log, ct) is { } p) { result.Add(p); gone.Add(f.TicketId); }
        }

        // 3. The budget: what is left, longest-Done first, never an open ticket.
        if (budgetMb > 0)
        {
            var budget = budgetMb * 1024 * 1024;
            var total = folders.Where(f => !gone.Contains(f.TicketId)).Sum(f => f.Bytes);
            foreach (var f in folders.Where(f => !f.Open && !gone.Contains(f.TicketId)).OrderBy(f => f.DoneAt ?? DateTime.MinValue).ThenBy(f => f.TicketId))
            {
                if (total <= budget || ct.IsCancellationRequested) break;
                if (await RemoveAsync(db, store, f, "budget", now, log, ct) is { } p) { result.Add(p); total -= f.Bytes; }
            }
            if (total > budget)
                log.LogWarning("{Store} is {Mb}MB, over its {Budget}MB budget, and what is left belongs to open tickets",
                    store, total / 1048576, budgetMb);
        }
        return result;
    }

    /// <summary>
    /// Delete one ticket's folder and record it. The ticket's phase is read again first: a pass
    /// takes a while, and a ticket reopened since it started is open again.
    /// </summary>
    private static async Task<Pruned?> RemoveAsync(DashboardDbContext db, Store store, Folder f, string reason,
        DateTime now, ILogger log, CancellationToken ct)
    {
        if (f.Exists)
        {
            var phase = await db.Tickets.AsNoTracking().Where(t => t.Id == f.TicketId).Select(t => (TicketPhase?)t.Phase).FirstOrDefaultAsync(ct);
            if (phase is not null && phase != TicketPhase.Done) return null;
        }
        try { Directory.Delete(f.Path, recursive: true); }
        catch (Exception ex)
        {
            log.LogInformation("Kept {Store} of ticket {Id} for now: {Reason}", store, f.TicketId, ex.Message);
            return null;
        }

        if (store == Store.QaShots && f.Exists)
        {
            var shots = await db.QaShots.Where(s => s.TicketId == f.TicketId && s.PrunedAt == null).ToListAsync(ct);
            foreach (var s in shots) s.PrunedAt = now;
            if (shots.Count > 0) await db.SaveChangesAsync(ct);
        }
        return new Pruned(store, f.TicketId, reason, f.Bytes, now);
    }

    private static long SizeOf(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } }); }
        catch { return 0; }
    }

    private static readonly JsonSerializerOptions LedgerJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static void AppendLedger(DashboardPaths paths, IEnumerable<Pruned> pruned, ILogger log)
    {
        try
        {
            Directory.CreateDirectory(paths.GlobalLogsDir);
            File.AppendAllLines(Path.Combine(paths.GlobalLogsDir, LedgerFile), pruned.Select(p => JsonSerializer.Serialize(p, LedgerJson)));
        }
        catch (Exception ex) { log.LogWarning(ex, "Could not append to the capture retention ledger"); }
    }

    /// <summary>
    /// What a pruned shot's image URL answers with: a picture that says when it went, so a deck or
    /// a cached page that only has the URL shows the notice and not a broken image.
    /// </summary>
    public static string RemovedPlaceholderSvg(DateTime prunedAt) =>
        $"""
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 640 360" width="640" height="360">
          <rect width="640" height="360" fill="#16181d"/>
          <rect x="8" y="8" width="624" height="344" fill="none" stroke="#3a3f4a" stroke-dasharray="6 6"/>
          <text x="320" y="170" text-anchor="middle" font-family="system-ui, sans-serif" font-size="22" fill="#9aa1ad">QA shot removed on {prunedAt.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture)}</text>
          <text x="320" y="204" text-anchor="middle" font-family="system-ui, sans-serif" font-size="15" fill="#6b7280">kept for a while after the ticket was done; its caption is still on the ticket</text>
        </svg>
        """;
}
