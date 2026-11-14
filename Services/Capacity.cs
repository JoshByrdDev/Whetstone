using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Whetstone.Models;
using Whetstone.Persistence;

namespace Whetstone.Services;

// Why a berth is empty. The fleet's question is rarely "how many slots", it is "why was a slot free
// while there was work" - and the claim is the one place that knows the answer, ticket by ticket,
// at the moment it passes one over. So the claim says, the monitor remembers the last answer per
// machine, and the sampler writes down once a minute which reason each free berth was free for.
// Nothing here changes what is claimed: it is a meter.

/// <summary>Why a claim passed a workable ticket over.</summary>
public enum ClaimSkip
{
    /// <summary>Another ticket it is built on is not Done.</summary>
    Dependency,
    /// <summary>The claim does not run this phase: a worker host's skill set, or a provider paused by its usage limit.</summary>
    Skill,
    /// <summary>Refinement waiting for its Classroom analysis.</summary>
    Analysis,
    /// <summary>Already being worked by another berth. Not a ticket that is waiting.</summary>
    Running,
    /// <summary>A resource it needs (the QA stack, an emulator) is held.</summary>
    Resource,
    /// <summary>The claim blocked it there and then (no skill file, no progress).</summary>
    BlockedNow,
}

/// <summary>
/// What one claim saw: how many tickets were workable, and why each one it did not take was
/// passed over. Filled by <see cref="TicketService.ClaimNextAsync"/> when it is handed one.
/// </summary>
public class ClaimTrace
{
    public int Workable { get; set; }
    public Dictionary<ClaimSkip, int> Skips { get; } = new();

    /// <summary>For <see cref="ClaimSkip.Resource"/> and <see cref="ClaimSkip.Skill"/>: which resource, which phase.</summary>
    public Dictionary<string, int> Held { get; } = new(StringComparer.Ordinal);

    /// <summary>Set by the route when the claim never reached the tickets: nothing this machine may run.</summary>
    public bool NothingHere { get; set; }

    public void Skip(ClaimSkip why, string? what = null)
    {
        Skips[why] = Skips.GetValueOrDefault(why) + 1;
        if (what is not null) Held[what] = Held.GetValueOrDefault(what) + 1;
    }
}

/// <summary>The reason keys a sample carries, and what the page calls each one.</summary>
public static class CapacityReasons
{
    public const string Stopped = "stopped";
    public const string OffHours = "off-hours";
    public const string UsageLimit = "usage-limit";
    /// <summary>A worker host whose every Claude account is over the smart switch's limits.</summary>
    public const string AccountFull = "account-full";
    public const string HostOff = "host-off";
    public const string HostOffline = "host-offline";
    public const string HostBehind = "host-behind";
    public const string NothingReady = "nothing-ready";
    public const string Dependency = "dependency";
    public const string Resource = "resource";
    public const string Analysis = "analysis";
    public const string NotHere = "not-here";
    public const string Filling = "filling";
    public const string Unknown = "unknown";

    /// <summary>
    /// Reasons that are not a free berth with work it could have had: nobody was on duty. The page
    /// leaves them out of "busy while on duty", so a night off does not read as a wasted night.
    /// </summary>
    public static readonly string[] OffDuty = { Stopped, OffHours, HostOff };
}

/// <summary>What the sampler knows about one place at one minute.</summary>
/// <param name="Place"><c>@board</c> or a worker host's name.</param>
/// <param name="HostState">Null for the board; otherwise why the host cannot take work, or null if it can.</param>
/// <param name="LastClaim">The last claim made for this place, or null if none since the API started.</param>
public record PlaceInput(
    string Place, int Slots, int Busy,
    bool HeartbeatRunning, string OrchestratorState, string? SleepText,
    bool AnyProviderPaused, string? HostState, ClaimSeen? LastClaim);

/// <summary>A claim as the monitor remembers it.</summary>
public record ClaimSeen(DateTime At, bool Claimed, ClaimTrace? Trace);

public record PlaceVerdict(string? Reason, Dictionary<string, object>? Detail);

/// <summary>
/// Which one reason explains a place's free berths. Pure, so the order of the checks - which is
/// the whole of the judgment here - is what the tests read.
/// </summary>
public static class CapacityClassifier
{
    /// <summary>Older than this, the last empty claim no longer says why a berth is free now.</summary>
    public static readonly TimeSpan Stale = TimeSpan.FromMinutes(90);

    /// <summary>A free berth this soon after a claim that worked is the next pass's to fill.</summary>
    public static readonly TimeSpan FillWindow = TimeSpan.FromMinutes(2);

    public static PlaceVerdict Classify(PlaceInput p, DateTime now)
    {
        if (p.Busy >= p.Slots) return new(null, null);

        // Nobody on duty: these outrank anything a claim said, because no claim is being made.
        if (!p.HeartbeatRunning) return new(CapacityReasons.Stopped, null);
        if (p.OrchestratorState == "off-hours") return new(CapacityReasons.OffHours, null);
        if (p.HostState is { } hs) return new(hs, null);

        // The board's own account: a sleep the orchestrator took for the limit says so in its text.
        // A worker host reports its own limit as its host state, above.
        if (p.Place == CapacityPlaces.Board && p.OrchestratorState == "sleeping"
            && p.SleepText?.StartsWith("Usage limit", StringComparison.OrdinalIgnoreCase) == true)
            return new(CapacityReasons.UsageLimit, null);

        var c = p.LastClaim;
        if (c is null || now - c.At > Stale) return new(CapacityReasons.Unknown, null);
        if (c.Claimed) return now - c.At <= FillWindow ? new(CapacityReasons.Filling, null) : new(CapacityReasons.Unknown, null);

        var t = c.Trace;
        if (t is null) return new(CapacityReasons.Unknown, null);
        if (t.NothingHere) return new(CapacityReasons.NotHere, null);

        var detail = new Dictionary<string, object>();
        foreach (var (k, v) in t.Skips) detail[k.ToString()] = v;
        foreach (var (k, v) in t.Held) detail[k] = v;

        // A ticket another berth is working is not one this berth was kept from. What is left is
        // what was waiting, and the reason that kept the most of it waiting names the sample.
        var waiting = t.Skips.Where(kv => kv.Key is not ClaimSkip.Running and not ClaimSkip.BlockedNow)
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => Rank(kv.Key))
            .ToList();
        if (waiting.Count == 0 || waiting[0].Value == 0) return new(CapacityReasons.NothingReady, detail);

        var reason = waiting[0].Key switch
        {
            ClaimSkip.Resource => CapacityReasons.Resource,
            ClaimSkip.Dependency => CapacityReasons.Dependency,
            ClaimSkip.Analysis => CapacityReasons.Analysis,
            ClaimSkip.Skill => p.Place == CapacityPlaces.Board && p.AnyProviderPaused
                ? CapacityReasons.UsageLimit : CapacityReasons.NotHere,
            _ => CapacityReasons.Unknown,
        };
        return new(reason, detail);
    }

    // A tie goes to the one most worth acting on: a busy stack is a setting, a dependency is a plan.
    private static int Rank(ClaimSkip s) => s switch
    {
        ClaimSkip.Resource => 0,
        ClaimSkip.Dependency => 1,
        ClaimSkip.Analysis => 2,
        _ => 3,
    };
}

public static class CapacityPlaces
{
    public const string Board = "@board";
}

/// <summary>The last claim made for each place, kept in memory between samples.</summary>
public class CapacityMonitor
{
    private readonly ConcurrentDictionary<string, ClaimSeen> _last = new(StringComparer.OrdinalIgnoreCase);

    public void RecordClaim(string? host, bool claimed, ClaimTrace? trace, DateTime? at = null) =>
        _last[string.IsNullOrWhiteSpace(host) ? CapacityPlaces.Board : host.Trim()] =
            new ClaimSeen(at ?? DateTime.UtcNow, claimed, trace);

    public ClaimSeen? LastClaim(string place) => _last.TryGetValue(place, out var c) ? c : null;
}

/// <summary>
/// Gathers what each place looks like right now, from the services that already know: the event
/// store for the board's berths and the orchestrator's state, the worker host service for each
/// host, the run folder for a provider's usage-limit pause, and the monitor for the last claim.
/// </summary>
public class CapacitySnapshot
{
    private readonly EventStore _events;
    private readonly HeartbeatManager _heartbeat;
    private readonly WorkerHostService _hosts;
    private readonly ProjectRegistry _registry;
    private readonly DashboardPaths _paths;
    private readonly CapacityMonitor _monitor;

    public CapacitySnapshot(EventStore events, HeartbeatManager heartbeat, WorkerHostService hosts,
        ProjectRegistry registry, DashboardPaths paths, CapacityMonitor monitor)
        => (_events, _heartbeat, _hosts, _registry, _paths, _monitor) = (events, heartbeat, hosts, registry, paths, monitor);

    public IReadOnlyList<PlaceInput> Read()
    {
        var running = _heartbeat.IsRunning;
        var (state, sleep) = _events.Orchestrator;
        var paused = AnyProviderPaused();
        var list = new List<PlaceInput>
        {
            new(CapacityPlaces.Board, BoardSlots(), _events.BoardBusyCount, running, state, sleep, paused, null,
                _monitor.LastClaim(CapacityPlaces.Board)),
        };
        foreach (var h in _hosts.Snapshot())
        {
            var hostState = !h.RemoteOn || !h.Enabled || h.StopPending ? CapacityReasons.HostOff
                : !h.Online ? CapacityReasons.HostOffline
                : h.UsageLimitUntil is not null ? CapacityReasons.UsageLimit
                : !h.ClaudeOpen ? CapacityReasons.AccountFull
                : !h.CommitMatches ? CapacityReasons.HostBehind
                : null;
            list.Add(new(h.Name, h.Slots, h.Runs.Count(r => !r.Exited), running, state, sleep, paused, hostState,
                _monitor.LastClaim(h.Name)));
        }
        return list;
    }

    private int BoardSlots()
    {
        try
        {
            var g = _registry.GetGlobalConfig();
            if (g.TryGetValue("heartbeatWorkers", out var w) && w.ValueKind == JsonValueKind.Number)
                return Math.Clamp((int)w.GetDouble(), 1, HeartbeatManager.MaxWorkers);
        }
        catch { /* a malformed config must not stop the meter */ }
        return HeartbeatManager.DefaultWorkers;
    }

    /// <summary>Whether any provider's usage-limit pause (<c>run/usage-limit-*.json</c>) is still in force.</summary>
    private bool AnyProviderPaused()
    {
        try
        {
            if (!Directory.Exists(_paths.RunDir)) return false;
            foreach (var f in Directory.EnumerateFiles(_paths.RunDir, "usage-limit*.json"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(f));
                    if (doc.RootElement.TryGetProperty("resetsAt", out var r)
                        && DateTimeOffset.TryParse(r.GetString(), out var until) && until > DateTimeOffset.UtcNow)
                        return true;
                }
                catch { /* a half-written pause is the worker's to finish */ }
            }
        }
        catch { }
        return false;
    }
}

/// <summary>
/// Once a minute, one <see cref="CapacitySample"/> per place. When nothing was ready at all, the
/// detail also says where the work was instead - waiting for review, blocked, in the backlog, or
/// in a project the heartbeat is not working - because "nothing ready" is the answer most worth
/// breaking down.
/// </summary>
public class CapacitySampler : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan Keep = TimeSpan.FromDays(30);

    private readonly IServiceScopeFactory _scopes;
    private readonly CapacitySnapshot _snapshot;
    private readonly ProjectRegistry _registry;
    private readonly ILogger<CapacitySampler> _log;
    private DateTime _prunedAt = DateTime.MinValue;

    private readonly MachineLoadBook? _book;
    private readonly Func<string, Dictionary<string, int>>? _phases;

    /// <param name="book">Each machine's latest load reading, written beside its capacity row.</param>
    /// <param name="phases">The phases a place's busy berths are on this minute.</param>
    public CapacitySampler(IServiceScopeFactory scopes, CapacitySnapshot snapshot, ProjectRegistry registry, ILogger<CapacitySampler> log,
        MachineLoadBook? book = null, Func<string, Dictionary<string, int>>? phases = null)
        => (_scopes, _snapshot, _registry, _log, _book, _phases) = (scopes, snapshot, registry, log, book, phases);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The first sample waits a minute: right after a restart nothing has claimed yet, and every
        // place would read "unknown" for no reason but the restart.
        try { await Task.Delay(Interval, stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SampleOnceAsync(DateTime.UtcNow, stoppingToken); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _log.LogDebug(ex, "Capacity sample failed");
            }
            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    public async Task SampleOnceAsync(DateTime now, CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DashboardDbContext>();

        Dictionary<string, object>? whereWorkIs = null;
        foreach (var p in _snapshot.Read())
        {
            var v = CapacityClassifier.Classify(p, now);
            var detail = v.Detail;
            if (v.Reason == CapacityReasons.NothingReady)
            {
                whereWorkIs ??= await WhereWorkIsAsync(db, ct);
                detail = new Dictionary<string, object>(detail ?? new()) { ["where"] = whereWorkIs };
            }
            db.CapacitySamples.Add(new CapacitySample
            {
                At = now, Place = p.Place, Slots = p.Slots, Busy = Math.Min(p.Busy, p.Slots),
                Reason = v.Reason,
                DetailJson = detail is { Count: > 0 } ? JsonSerializer.Serialize(detail) : null,
            });
            // The machine's own load beside it, with what its berths were doing - only when it
            // reported one lately: a machine that went quiet has no load this minute, not zero.
            if (_book?.Get(p.Place, now) is { } load)
            {
                var phases = _phases?.Invoke(p.Place);
                db.MachineLoadSamples.Add(new MachineLoadSample
                {
                    At = now, Place = p.Place, Busy = Math.Min(p.Busy, p.Slots),
                    CpuPct = load.CpuPct, MemAvailMb = load.MemAvailMb, MemTotalMb = load.MemTotalMb,
                    SwapUsedMb = load.SwapUsedMb, Pressure = load.Pressure, DiskFreeGb = load.DiskFreeGb,
                    NetInKbps = load.NetInKbps, NetOutKbps = load.NetOutKbps,
                    DockerMemUsedMb = load.DockerMemUsedMb, DockerMemLimitMb = load.DockerMemLimitMb,
                    PhasesJson = phases is { Count: > 0 } ? JsonSerializer.Serialize(phases) : null,
                });
            }
        }

        if (now - _prunedAt > TimeSpan.FromHours(1))
        {
            var cutoff = now - Keep;
            await db.CapacitySamples.Where(s => s.At < cutoff).ExecuteDeleteAsync(ct);
            await db.MachineLoadSamples.Where(s => s.At < cutoff).ExecuteDeleteAsync(ct);
            _prunedAt = now;
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Open tickets that are not workable, by why: the shape of "nothing ready".</summary>
    private async Task<Dictionary<string, object>> WhereWorkIsAsync(DashboardDbContext db, CancellationToken ct)
    {
        var active = HeartbeatProjects();
        var rows = await db.Tickets.AsNoTracking()
            .Where(t => t.Phase != TicketPhase.Done)
            .Select(t => new { t.ProjectKey, t.Phase })
            .ToListAsync(ct);

        var result = new Dictionary<string, object>();
        void Add(string k) => result[k] = (int)result.GetValueOrDefault(k, 0) + 1;
        foreach (var r in rows)
        {
            var inScope = active.Count == 0 || active.Contains(r.ProjectKey);
            if (!inScope) { if (PhaseMachine.WorkablePhases.Contains(r.Phase)) Add("otherProjects"); continue; }
            switch (r.Phase)
            {
                case TicketPhase.NeedsReview: Add("review"); break;
                case TicketPhase.Blocked: Add("blocked"); break;
                case TicketPhase.Backlog: Add("backlog"); break;
                case TicketPhase.NeedsApproval or TicketPhase.NeedsImplementationReview: Add("approval"); break;
            }
        }
        return result;
    }

    private HashSet<string> HeartbeatProjects()
    {
        try
        {
            var g = _registry.GetGlobalConfig();
            if (g.TryGetValue("heartbeatProjects", out var hp) && hp.ValueKind == JsonValueKind.Array)
                return hp.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch { }
        return new HashSet<string>();
    }
}

/// <summary>Slot-minutes by reason over a window, per place and per day: what the System page draws.</summary>
public static class CapacitySummary
{
    public record Bucket(int SlotMinutes, int Busy, Dictionary<string, int> Idle);

    public record PlaceSummary(string Place, int Slots, Bucket Total);

    public record Day(string Date, Bucket Total);

    public record Result(DateTime From, DateTime To, int Samples, Bucket Total,
        IReadOnlyList<PlaceSummary> Places, IReadOnlyList<Day> Days);

    /// <summary>
    /// Each sample stands for one minute. <paramref name="localDay"/> turns a sample's UTC time into
    /// the calendar day it is shown under, so a day on the page is the owner's day.
    /// </summary>
    public static Result Build(IReadOnlyList<CapacitySample> samples, DateTime from, DateTime to, Func<DateTime, DateOnly> localDay)
    {
        static Bucket Sum(IEnumerable<CapacitySample> s)
        {
            int slots = 0, busy = 0;
            var idle = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var x in s)
            {
                slots += x.Slots;
                busy += x.Busy;
                var free = x.Slots - x.Busy;
                if (free > 0) idle[x.Reason ?? CapacityReasons.Unknown] = idle.GetValueOrDefault(x.Reason ?? CapacityReasons.Unknown) + free;
            }
            return new Bucket(slots, busy, idle);
        }

        var places = samples.GroupBy(s => s.Place, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key == CapacityPlaces.Board ? 0 : 1).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new PlaceSummary(g.Key, g.OrderByDescending(s => s.At).First().Slots, Sum(g)))
            .ToList();
        var days = samples.GroupBy(s => localDay(s.At)).OrderBy(g => g.Key)
            .Select(g => new Day(g.Key.ToString("yyyy-MM-dd"), Sum(g))).ToList();
        return new Result(from, to, samples.Count, Sum(samples), places, days);
    }
}
