using Whetstone.Models;
using Whetstone.Persistence;
using Microsoft.EntityFrameworkCore;
using Whetstone.Services.UnitReports;

namespace Whetstone.Services;

/// <summary>What the queue may do with an item whose turn has come.</summary>
public enum GateDecision
{
    /// <summary>Run it now.</summary>
    Go,
    /// <summary>Not yet, for a reason that clears on its own. Keep the item; look again shortly.</summary>
    Wait,
    /// <summary>Never, for this item. Fail it and move on.</summary>
    Stop,
}

/// <param name="Reason">Why, in words a person can act on. Null only for <see cref="GateDecision.Go"/>.</param>
public readonly record struct GateVerdict(GateDecision Decision, string? Reason)
{
    public static readonly GateVerdict Go = new(GateDecision.Go, null);
    public static GateVerdict Wait(string reason) => new(GateDecision.Wait, reason);
    public static GateVerdict Stop(string reason) => new(GateDecision.Stop, reason);
}

/// <summary>
/// The question the queue asks about a ticket at the moment its turn comes — never at the moment it
/// was queued. An item can sit for ten minutes, and everything worth knowing about a ticket can
/// change inside that window.
/// </summary>
public interface IAnalysisGate
{
    Task<GateVerdict> CheckAsync(int ticketId);

    /// <summary>The same question about a unit report. See <see cref="UnitReportRunner.CheckAsync"/>.</summary>
    Task<UnitGateVerdict> CheckUnitAsync(UnitSubject unit, bool manual) =>
        Task.FromResult(new UnitGateVerdict(GateVerdict.Stop("unit reports are not written here"), Array.Empty<int>()));
}

/// <summary>
/// How a run says how it is going, while it is going.
/// <para>
/// Two different facts, reported at two different times, because they answer the reader's question
/// together and neither can answer it alone. <see cref="Expect"/> is the denominator — what a run of
/// this shape has historically cost — sent once, at the start. <see cref="Step"/> is the numerator,
/// sent on every tool call. A bar drawn from the clock alone is a guess that keeps moving after the
/// work has stalled; a bar drawn from steps alone jumps and then sits still while the model thinks.
/// </para>
/// <para>
/// Every method must be safe to call from the thread reading the process's stdout, and cheap: this
/// is on the path of every line the CLI prints.
/// </para>
/// </summary>
public interface IRunProgress
{
    /// <summary>What runs like this one have usually taken. Best-effort — a first-ever run has no history.</summary>
    void Expect(double seconds, int steps);

    /// <summary>One more tool call, and what it was pointed at.</summary>
    void Step(string activity);
}

/// <summary>For callers that do not care — tests, and any future one-off use of the executor.</summary>
public sealed class NoProgress : IRunProgress
{
    public static readonly NoProgress Instance = new();
    public void Expect(double seconds, int steps) { }
    public void Step(string activity) { }
}

/// <summary>Runs exactly one analysis. The queue owns everything about when.</summary>
public interface IAnalysisExecutor
{
    Task<(int? analysisId, string? error)> RunAsync(
        int ticketId, AnalysisLens lens, IRunProgress progress, CancellationToken ct);

    /// <summary>
    /// Write (or rewrite) the walkthrough for an analysis that already exists. Its outcome lives on
    /// the walkthrough's own row, so this returns nothing: there is no analysis result to report.
    /// </summary>
    Task RunWalkthroughAsync(int analysisId, IRunProgress progress, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Write one unit report, and its walkthrough. The stored report's id, or why there isn't one.</summary>
    Task<(int? reportId, string? error)> RunUnitAsync(UnitSubject unit, IRunProgress progress, CancellationToken ct) =>
        Task.FromResult<(int?, string?)>((null, "unit reports are not written here"));

    /// <summary>Rewrite a unit report's walkthrough. Like an analysis's, its outcome lives on its own row.</summary>
    Task RunUnitWalkthroughAsync(int reportId, IRunProgress progress, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// The real gate: is this ticket still worth studying, and is anything else in the middle of
/// changing it?
/// </summary>
public class AnalysisGate : IAnalysisGate
{
    private readonly IServiceScopeFactory _scopes;

    public AnalysisGate(IServiceScopeFactory scopes) => _scopes = scopes;

    public async Task<UnitGateVerdict> CheckUnitAsync(UnitSubject unit, bool manual)
    {
        using var scope = _scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<UnitReportRunner>().CheckAsync(unit, manual);
    }

    public async Task<GateVerdict> CheckAsync(int ticketId)
    {
        using var scope = _scopes.CreateScope();
        return await CheckAsync(
            scope.ServiceProvider.GetRequiredService<DashboardDbContext>(),
            scope.ServiceProvider.GetRequiredService<ResourceLeaseService>(),
            ticketId);
    }

    /// <summary>
    /// The decision itself, over a database rather than a container, so it can be tested against
    /// real rows.
    /// <para>
    /// Contention is checked BEFORE eligibility, and the order is load-bearing. A ticket the
    /// pipeline is implementing right now is usually ineligible right now — implementation has not
    /// finished a pass — and will usually be eligible the moment that worker hands it on. Asking
    /// about eligibility first would fail exactly the item that was about to become worth running.
    /// </para>
    /// </summary>
    public static async Task<GateVerdict> CheckAsync(
        DashboardDbContext db, ResourceLeaseService leases, int ticketId)
    {
        var ticket = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == ticketId);
        if (ticket == null) return GateVerdict.Stop("that ticket no longer exists");

        // The pipeline's own pickup lock, read rather than taken. TicketService claims this before it
        // writes anything to the ticket and worker.ps1 gives it back when the cycle ends, so a live
        // one means an agent is in that worktree at this moment — which is the whole objection to
        // analysing it. Reading the branch while something commits to it produces a document about a
        // state that never existed, and stamps it with a HeadSha that is stale as it is written.
        //
        // The lease is the signal and the phase is not: an "-ing" phase is deliberately resumable and
        // outlives the worker that set it, so a ticket parked in Implementing with nobody on it is
        // both completely normal and exactly the kind of ticket worth studying.
        var held = await leases.HolderOfAsync(ResourceLeaseService.TicketKey(ticketId));
        if (held != null)
        {
            var who = string.IsNullOrWhiteSpace(held.Skill) ? "the pipeline" : held.Skill;
            return GateVerdict.Wait($"{who} has had this ticket for {Since(held.AcquiredAt)}");
        }

        var (eligible, why) = TicketAnalysisService.Eligibility(ticket);
        return eligible ? GateVerdict.Go : GateVerdict.Stop($"it cannot be studied yet: {why}");
    }

    private static string Since(DateTime acquiredAt)
    {
        var d = DateTime.UtcNow - acquiredAt;
        if (d.TotalMinutes < 1) return "under a minute";
        if (d.TotalMinutes < 60) return $"{d.TotalMinutes:0}m";
        return $"{d.TotalHours:0.#}h";
    }
}

/// <summary>
/// The waiting line for Classroom analyses.
///
/// <para>
/// Split out of <see cref="AnalysisRunner"/> so that the cap and the work are separate ideas. The
/// cap has not changed: <b>one analysis runs at a time, globally</b>, for the reasons in the
/// runner's own comment. What changed is how the cap is expressed. It used to be a refusal — ask
/// for a second analysis and you were told no — which meant the only way to study three tickets was
/// to sit and watch the first two finish. Now it is a queue: ask for three, walk away, read them
/// when they land.
/// </para>
///
/// <para>
/// <b>Everything is decided at dequeue time.</b> An item can sit here for ten minutes, and a
/// decision made at the click — is the ticket eligible, is the login still good, which commit is at
/// HEAD, what does the tracker say the title is — is a decision made about a ticket that no longer exists
/// in that form. The gate re-asks (see <see cref="IAnalysisGate"/>) and the executor re-reads
/// everything else. An item that fails its own check fails alone; the drain moves to the next one.
/// </para>
///
/// <para>
/// <b>In memory, and that is deliberate.</b> A restart drops the pending items and you queue them
/// again. The alternative is a table and a migration to persist a list whose entire lifetime is the
/// twenty minutes you spend reading.
/// </para>
/// </summary>
public class AnalysisQueue
{
    /// <summary>
    /// How many may wait. A ceiling on a queue that drains at roughly one item per five minutes:
    /// past this, the back of the line is an hour away and you have stopped queueing work and
    /// started scheduling it.
    /// </summary>
    public const int MaxPending = 10;

    /// <summary>How many finished runs stay visible. Enough to cover a walk away, not a history.</summary>
    private const int MaxRecent = 10;

    /// <summary>
    /// How long to sit still when every pending item is held by the pipeline. Short enough that the
    /// queue starts within a poll or two of the worker letting go, long enough that idling costs one
    /// cheap lease read a minute.
    /// </summary>
    private static readonly TimeSpan DefaultHoldRecheck = TimeSpan.FromSeconds(20);

    private readonly IAnalysisGate _gate;
    private readonly IAnalysisExecutor _executor;
    private readonly ILogger<AnalysisQueue> _log;
    private readonly TimeSpan _holdRecheck;

    private readonly object _lock = new();
    private readonly List<Pending> _pending = new();
    private readonly List<Outcome> _recent = new();
    private Running? _current;
    private bool _draining;
    private long _nextId = 1;

    /// <param name="holdRecheck">
    /// Only ever passed by tests, which cannot afford to sit out a twenty-second idle to prove that
    /// a held item eventually runs.
    /// </param>
    public AnalysisQueue(IAnalysisGate gate, IAnalysisExecutor executor, ILogger<AnalysisQueue> log,
        TimeSpan? holdRecheck = null)
    {
        _gate = gate;
        _executor = executor;
        _log = log;
        _holdRecheck = holdRecheck ?? DefaultHoldRecheck;
    }

    private class Pending
    {
        public long Id;
        public int TicketId;
        public AnalysisLens Lens;
        /// <summary>
        /// Set for a walkthrough-only item: the analysis whose walkthrough to rewrite - or, with
        /// <see cref="Unit"/> set too, the unit report whose walkthrough to rewrite.
        /// </summary>
        public int? WalkthroughOf;
        /// <summary>Set for a unit report, and then <see cref="TicketId"/> is zero.</summary>
        public UnitSubject? Unit;
        /// <summary>For a unit report asked for by a person rather than by the board.</summary>
        public bool Manual;
        /// <summary>
        /// For a unit report: the tickets this item has already queued an analysis of. Each is asked
        /// for once. One that comes back with nothing is reported missing rather than asked for again,
        /// or a ticket that cannot be analysed would hold the unit's report forever.
        /// </summary>
        public readonly HashSet<int> Requested = new();
        public DateTime QueuedAt;
        /// <summary>Why this item was passed over on the last drain pass, or null if it wasn't.</summary>
        public string? HoldReason;
    }

    private class Running
    {
        public long Id;
        public int TicketId;
        public AnalysisLens Lens;
        public int? WalkthroughOf;
        public UnitSubject? Unit;
        public DateTime StartedAt;
        /// <summary>
        /// Lives here rather than inside the run so that stopping is possible from outside it. The
        /// executor links its own timeout to this token, so a cancel and a timeout come out of the
        /// same kill path.
        /// </summary>
        public readonly CancellationTokenSource Cts = new();
        /// <summary>Set when a person stopped it, to tell that apart from a run that fell over.</summary>
        public bool Cancelled;

        // ---- what the run reports about itself, all written under the queue's lock ----

        /// <summary>Tool calls seen so far.</summary>
        public int Steps;
        /// <summary>The most recent one, in words. Null until the run makes its first move.</summary>
        public string? Activity;
        /// <summary>What runs like this have taken, or null when there is no history to say.</summary>
        public double? ExpectedSeconds;
        public int? ExpectedSteps;
    }

    /// <summary>
    /// The running slot's own progress sink. A nested class so that everything a run reports lands
    /// under the same lock the snapshot is taken under: a status read must never catch a step count
    /// from after the activity line it is shown beside.
    /// </summary>
    private sealed class RunReporter : IRunProgress
    {
        private readonly AnalysisQueue _queue;
        private readonly Running _run;

        public RunReporter(AnalysisQueue queue, Running run) { _queue = queue; _run = run; }

        public void Expect(double seconds, int steps)
        {
            lock (_queue._lock)
            {
                _run.ExpectedSeconds = seconds > 0 ? seconds : null;
                _run.ExpectedSteps = steps > 0 ? steps : null;
            }
        }

        public void Step(string activity)
        {
            lock (_queue._lock)
            {
                _run.Steps++;
                _run.Activity = activity;
            }
        }
    }

    /// <param name="HoldReason">Why it was stepped over last pass, or null if it wasn't.</param>
    public record QueuedAnalysis(long Id, int TicketId, AnalysisLens Lens, DateTime QueuedAt,
        string? HoldReason, int? WalkthroughOf = null, UnitSubject? Unit = null);

    /// <param name="Steps">Tool calls the run has made so far.</param>
    /// <param name="Activity">The most recent of them, in words, or null before the first one.</param>
    /// <param name="ExpectedSeconds">What runs like this have taken. Null when nothing comparable has finished.</param>
    /// <param name="ExpectedSteps">Likewise, in tool calls.</param>
    public record RunningAnalysis(long Id, int TicketId, AnalysisLens Lens, DateTime StartedAt,
        int Steps, string? Activity, double? ExpectedSeconds, int? ExpectedSteps, int? WalkthroughOf = null,
        UnitSubject? Unit = null);

    /// <param name="AnalysisId">The analysis written - or, for a unit, the report.</param>
    public record Outcome(long Id, int TicketId, AnalysisLens Lens, DateTime FinishedAt,
        int? AnalysisId, string? Error, UnitSubject? Unit = null);

    // ---- the queue ----------------------------------------------------------

    /// <summary>
    /// Join the line. Returns immediately — the caller polls <see cref="GetStatus"/>.
    /// <para>
    /// Deduped on ticket AND lens. The same ticket under a different lens is a legitimate second
    /// read, not a double-click.
    /// </para>
    /// </summary>
    public (bool ok, string message) Enqueue(int ticketId, AnalysisLens lens)
    {
        lock (_lock)
        {
            if (_current is { } c && c.WalkthroughOf == null && c.TicketId == ticketId && c.Lens == lens)
                return (false, "That analysis is already running.");

            if (_pending.Any(p => p.WalkthroughOf == null && p.TicketId == ticketId && p.Lens == lens))
                return (false, "That is already in the queue.");

            if (_pending.Count >= MaxPending)
                return (false, $"The queue is full at {MaxPending}. Let some of it drain first.");

            _pending.Add(new Pending
            {
                Id = _nextId++,
                TicketId = ticketId,
                Lens = lens,
                QueuedAt = DateTime.UtcNow,
            });
        }

        EnsureDraining();
        return (true, "Queued.");
    }

    /// <summary>
    /// Rewrite one analysis's walkthrough, in the same single slot as analyses: it is another agent
    /// run, and the reason for one slot - not competing with the pipeline's own workers for the
    /// machine - applies to it unchanged. A new analysis writes its walkthrough on its own; this is
    /// for a retry, and for analyses that predate walkthroughs.
    /// </summary>
    public (bool ok, string message) EnqueueWalkthrough(int ticketId, int analysisId)
    {
        lock (_lock)
        {
            if (_current is { } c && (c.WalkthroughOf == analysisId || (c.WalkthroughOf == null && c.TicketId == ticketId)))
                return (false, "That walkthrough is already being written.");
            if (_pending.Any(p => p.WalkthroughOf == analysisId))
                return (false, "That walkthrough is already in the queue.");
            if (_pending.Count >= MaxPending)
                return (false, $"The queue is full at {MaxPending}. Let some of it drain first.");

            _pending.Add(new Pending
            {
                Id = _nextId++,
                TicketId = ticketId,
                WalkthroughOf = analysisId,
                QueuedAt = DateTime.UtcNow,
            });
        }

        EnsureDraining();
        return (true, "Queued.");
    }

    /// <summary>
    /// Write a unit report, in the same single slot: it is one more agent run, and a longer one.
    /// Whatever analyses of its tickets it is still missing are queued behind it when its turn comes,
    /// and it steps aside for them - the line stays in order, and the report runs once they have.
    /// </summary>
    /// <param name="manual">A person asked. See <see cref="UnitReportRunner.CheckAsync"/> for what that changes.</param>
    public (bool ok, string message) EnqueueUnit(UnitSubject unit, bool manual)
    {
        lock (_lock)
        {
            if (_current is { WalkthroughOf: null } c && c.Unit == unit) return (false, "That report is already being written.");
            if (_pending.Any(p => p.Unit == unit && p.WalkthroughOf == null)) return (false, "That report is already in the queue.");
            if (_pending.Count >= MaxPending)
                return (false, $"The queue is full at {MaxPending}. Let some of it drain first.");

            _pending.Add(new Pending { Id = _nextId++, Unit = unit, Manual = manual, QueuedAt = DateTime.UtcNow });
        }

        EnsureDraining();
        return (true, "Queued.");
    }

    /// <summary>Rewrite one unit report's walkthrough: the retry, and the way a report gets one after the fact.</summary>
    public (bool ok, string message) EnqueueUnitWalkthrough(UnitSubject unit, int reportId)
    {
        lock (_lock)
        {
            if (_current is { } c && c.Unit == unit)
                return (false, "That unit's report or walkthrough is already being written.");
            if (_pending.Any(p => p.Unit == unit && p.WalkthroughOf == reportId))
                return (false, "That walkthrough is already in the queue.");
            if (_pending.Count >= MaxPending)
                return (false, $"The queue is full at {MaxPending}. Let some of it drain first.");

            _pending.Add(new Pending { Id = _nextId++, Unit = unit, WalkthroughOf = reportId, QueuedAt = DateTime.UtcNow });
        }

        EnsureDraining();
        return (true, "Queued.");
    }

    /// <summary>Drop a unit report, waiting or running.</summary>
    public bool CancelUnit(UnitSubject unit)
    {
        CancellationTokenSource? stop = null;
        lock (_lock)
        {
            if (_pending.FirstOrDefault(p => p.Unit == unit && p.WalkthroughOf == null) is { } waiting)
            {
                _pending.Remove(waiting);
                return true;
            }
            if (_current is { WalkthroughOf: null } c && c.Unit == unit)
            {
                c.Cancelled = true;
                stop = c.Cts;
            }
        }

        if (stop == null) return false;
        try { stop.Cancel(); } catch (ObjectDisposedException) { /* finished as we reached for it */ }
        return true;
    }

    /// <summary>
    /// Drop one item, whether it is waiting or already running. One entry point because a person
    /// clicking "x" on a row is not thinking about which of those two it is, and the row can change
    /// from one to the other while they reach for it.
    /// </summary>
    public bool Cancel(int ticketId, AnalysisLens lens)
    {
        CancellationTokenSource? stop = null;
        lock (_lock)
        {
            var waiting = _pending.FirstOrDefault(p => p.WalkthroughOf == null && p.TicketId == ticketId && p.Lens == lens);
            if (waiting != null)
            {
                _pending.Remove(waiting);
                return true;
            }

            if (_current is { } c && c.WalkthroughOf == null && c.TicketId == ticketId && c.Lens == lens)
            {
                c.Cancelled = true;
                stop = c.Cts;
            }
        }

        if (stop == null) return false;
        try { stop.Cancel(); } catch (ObjectDisposedException) { /* finished as we reached for it */ }
        return true;
    }

    /// <summary>
    /// Empty the waiting line and leave the running item alone. "I have changed my mind about the
    /// rest of this" is a different intention from "stop what you are doing", and the panel offers
    /// them as different buttons.
    /// </summary>
    public int ClearPending()
    {
        lock (_lock)
        {
            var n = _pending.Count;
            _pending.Clear();
            return n;
        }
    }

    /// <summary>
    /// The whole of the queue at one instant, taken under the lock so the three lists cannot
    /// disagree with each other. Pending is in turn order; recent is newest first.
    /// </summary>
    public (RunningAnalysis? Current, IReadOnlyList<QueuedAnalysis> Pending,
            IReadOnlyList<Outcome> Recent) Snapshot()
    {
        lock (_lock)
        {
            var current = _current == null
                ? null
                : new RunningAnalysis(_current.Id, _current.TicketId, _current.Lens, _current.StartedAt,
                    _current.Steps, _current.Activity, _current.ExpectedSeconds, _current.ExpectedSteps,
                    _current.WalkthroughOf, _current.Unit);

            return (
                current,
                _pending.Select(p => new QueuedAnalysis(p.Id, p.TicketId, p.Lens, p.QueuedAt, p.HoldReason, p.WalkthroughOf, p.Unit)).ToList(),
                _recent.ToList());
        }
    }

    /// <summary>Everything the Classroom polls for: what is running, what is next, what just finished.</summary>
    public object GetStatus()
    {
        var (c, pending, recent) = Snapshot();
        return new
        {
            running = c != null,
            ticketId = c?.Unit is null ? c?.TicketId : null,
            lens = c?.Lens.ToString(),
            // "walkthrough" for an item rewriting an existing analysis's walkthrough, so the panel can
            // say so instead of claiming a whole new analysis is under way.
            kind = c == null ? null : Kind(c.Unit, c.WalkthroughOf),
            unit = c?.Unit,
            walkthroughOf = c?.WalkthroughOf,
            startedAt = c?.StartedAt,
            elapsedSeconds = c == null ? (double?)null : (DateTime.UtcNow - c.StartedAt).TotalSeconds,

            // What the run is actually doing, and what it usually costs. Together these are the only
            // honest progress bar available for work whose end is not known in advance: the client
            // draws elapsed and steps against these two expectations. Expectations are null on the
            // first run of a lens — a client that has no denominator must say so rather than invent
            // one, so this reports the absence instead of a default.
            steps = c?.Steps ?? 0,
            activity = c?.Activity,
            expectedSeconds = c?.ExpectedSeconds,
            expectedSteps = c?.ExpectedSteps,

            pending = pending.Select(p => new
            {
                id = p.Id,
                ticketId = p.Unit is null ? p.TicketId : (int?)null,
                lens = p.Lens.ToString(),
                kind = Kind(p.Unit, p.WalkthroughOf),
                unit = p.Unit,
                walkthroughOf = p.WalkthroughOf,
                queuedAt = p.QueuedAt,
                holdReason = p.HoldReason,
            }).ToList(),

            // Newest first. Replaces the single "last run" this used to report: with a queue, two
            // items can fail while you are reading a third, and one slot only ever showed you one of
            // them — for a ticket you may not have been looking at.
            recent = recent.Where(r => r.Unit is null).Select(r => new
            {
                id = r.Id,
                ticketId = r.TicketId,
                lens = r.Lens.ToString(),
                finishedAt = r.FinishedAt,
                analysisId = r.AnalysisId,
                error = r.Error,
            }).ToList(),

            // Unit reports that finished, kept apart so a page listing ticket analyses never meets one.
            units = recent.Where(r => r.Unit is not null).Select(r => new
            {
                id = r.Id,
                unit = r.Unit,
                finishedAt = r.FinishedAt,
                reportId = r.AnalysisId,
                error = r.Error,
            }).ToList(),
        };
    }

    private static string Kind(UnitSubject? unit, int? walkthroughOf) =>
        (unit, walkthroughOf) switch
        {
            (null, null) => "analysis",
            (null, _) => "walkthrough",
            (_, null) => "unit",
            _ => "unit-walkthrough",
        };

    // ---- the drain ----------------------------------------------------------

    private void EnsureDraining()
    {
        lock (_lock)
        {
            if (_draining) return;
            _draining = true;
        }
        _ = Task.Run(DrainAsync);
    }

    private async Task DrainAsync()
    {
        try
        {
            while (true)
            {
                var next = await NextReadyAsync();
                if (next != null)
                {
                    await RunAsync(next);
                    continue;
                }

                lock (_lock)
                {
                    if (_pending.Count == 0)
                    {
                        // Nothing left. Stand down inside the lock so an Enqueue racing us either
                        // added its item before this check, or starts a fresh drain after it.
                        _draining = false;
                        return;
                    }
                }

                // Items remain, but every one of them is held by pipeline work. Idle rather than
                // finish: the queue is not empty, the hold clears on its own, and a queue that
                // quietly gave up would be indistinguishable from one that had run everything.
                await Task.Delay(_holdRecheck);
            }
        }
        catch (Exception ex)
        {
            // The loop's own failure, not a run's — a run that throws is caught below and recorded.
            _log.LogError(ex, "The Classroom queue's drain loop stopped unexpectedly");
            lock (_lock) { _draining = false; }
        }
    }

    /// <summary>
    /// The first item that can actually run right now, promoted to current — or null when none can.
    /// <para>
    /// Held items keep their place rather than moving to the back, so the line stays FIFO; the drain
    /// simply steps over them. Failing items are dropped here, which is what stops one bad item from
    /// standing at the front of the queue forever.
    /// </para>
    /// </summary>
    private async Task<Running?> NextReadyAsync()
    {
        List<Pending> snapshot;
        lock (_lock)
        {
            if (_current != null) return null;
            snapshot = _pending.ToList();
        }

        foreach (var p in snapshot)
        {
            GateVerdict verdict;
            try
            {
                // A walkthrough is written about an analysis that already exists, from the commit it
                // is pinned to. Whether the ticket is eligible for a NEW analysis is not its question,
                // and it takes no worktree, so the pipeline being mid-cycle on the ticket does not hold it.
                verdict = p.WalkthroughOf != null ? GateVerdict.Go
                    : p.Unit is { } unit ? UnitTurn(p, await _gate.CheckUnitAsync(unit, p.Manual))
                    : await _gate.CheckAsync(p.TicketId);
            }
            catch (Exception ex)
            {
                // A gate that cannot answer must not become a queue that cannot move. Treat it as a
                // hold: the database being unreadable for a moment is not a verdict about the ticket.
                _log.LogWarning(ex, "Could not check {Item} for the Classroom queue", p.Unit?.ToString() ?? $"ticket {p.TicketId}");
                p.HoldReason = p.Unit is null ? "could not check this ticket just now" : "could not check this unit just now";
                continue;
            }

            if (verdict.Decision == GateDecision.Wait)
            {
                p.HoldReason = verdict.Reason;
                continue;
            }

            if (verdict.Decision == GateDecision.Stop)
            {
                lock (_lock)
                {
                    if (!_pending.Remove(p)) continue;   // cancelled while we were asking
                }
                Record(new Outcome(p.Id, p.TicketId, p.Lens, DateTime.UtcNow, null, verdict.Reason, p.Unit));
                continue;
            }

            lock (_lock)
            {
                if (!_pending.Remove(p)) continue;
                var run = new Running
                {
                    Id = p.Id,
                    TicketId = p.TicketId,
                    Lens = p.Lens,
                    WalkthroughOf = p.WalkthroughOf,
                    Unit = p.Unit,
                    StartedAt = DateTime.UtcNow,
                };
                _current = run;
                return run;
            }
        }

        return null;
    }

    private async Task RunAsync(Running run)
    {
        int? analysisId = null;
        string? error = null;

        try
        {
            if (run.WalkthroughOf is int of)
                await (run.Unit is null
                    ? _executor.RunWalkthroughAsync(of, new RunReporter(this, run), run.Cts.Token)
                    : _executor.RunUnitWalkthroughAsync(of, new RunReporter(this, run), run.Cts.Token));
            else if (run.Unit is { } unit)
                (analysisId, error) = await _executor.RunUnitAsync(unit, new RunReporter(this, run), run.Cts.Token);
            else
                (analysisId, error) = await _executor.RunAsync(
                    run.TicketId, run.Lens, new RunReporter(this, run), run.Cts.Token);
        }
        catch (OperationCanceledException)
        {
            error = "Stopped before it finished.";
        }
        catch (Exception ex)
        {
            // One item's failure is one item's failure. The slot is given back in the finally below
            // and the next item runs — the whole point of queueing three and walking away.
            _log.LogError(ex, "Classroom run for {Item} failed", run.Unit?.ToString() ?? $"ticket {run.TicketId}");
            error = ex.Message;
        }
        finally
        {
            lock (_lock)
            {
                if (ReferenceEquals(_current, run)) _current = null;
            }
            run.Cts.Dispose();
        }

        // A run a person stopped needs no notice: they already know, and a red banner reporting
        // their own click back to them is noise on the one screen that is meant to be calm.
        if (run.Cancelled) return;
        // A walkthrough reports on its own row; the outcome list is for analyses.
        if (run.WalkthroughOf != null) return;

        Record(new Outcome(run.Id, run.TicketId, run.Lens, DateTime.UtcNow, analysisId, error, run.Unit));
    }

    /// <summary>
    /// A unit's verdict, once the analyses it is missing are accounted for. Each is queued once, at
    /// the back; while any of them is still waiting or running the unit steps aside for it. One that
    /// finished and left nothing usable is not asked for again - the report goes ahead and says so.
    /// </summary>
    private GateVerdict UnitTurn(Pending p, UnitGateVerdict v)
    {
        foreach (var id in v.Analyse.Where(id => !p.Requested.Contains(id)))
        {
            // Refused as already queued is still asked: it is the same analysis either way.
            Enqueue(id, AnalysisLens.Full);
            p.Requested.Add(id);
        }

        lock (_lock)
        {
            var waiting = v.Analyse.Where(id =>
                    _pending.Any(x => x.Unit is null && x.WalkthroughOf is null && x.TicketId == id)
                    || (_current is { Unit: null, WalkthroughOf: null } c && c.TicketId == id))
                .ToList();
            if (waiting.Count > 0)
                return GateVerdict.Wait($"waiting for the analysis of {string.Join(", ", waiting.Select(id => $"#{id}"))}");
        }

        return v.Verdict;
    }

    private void Record(Outcome outcome)
    {
        lock (_lock)
        {
            _recent.Insert(0, outcome);
            if (_recent.Count > MaxRecent) _recent.RemoveRange(MaxRecent, _recent.Count - MaxRecent);
        }
    }
}
