using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// The Classroom queue: the order it drains in, what it refuses to queue twice, the two ways out of
/// it, and — the one that actually matters when you have walked away — that a single bad item
/// cannot stop the rest of the line from running.
/// <para>
/// Also the gate, against real rows, because every one of its answers is about a ticket that has
/// changed since the click. That is the whole reason the check moved to dequeue time.
/// </para>
/// </summary>
public class AnalysisQueueTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<DashboardDbContext> _options;

    public AnalysisQueueTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _options = new DbContextOptionsBuilder<DashboardDbContext>().UseSqlite(_conn).Options;
        using var ctx = new DashboardDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _conn.Dispose();

    private DashboardDbContext NewCtx() => new(_options);

    // ---- doubles ------------------------------------------------------------

    /// <summary>A gate that answers from a script, so a test can say "this ticket is held" outright.</summary>
    private class FakeGate : IAnalysisGate
    {
        public readonly Dictionary<int, GateVerdict> Verdicts = new();
        /// <summary>What any ticket the test did not name gets.</summary>
        public GateVerdict Default = GateVerdict.Go;

        public Task<GateVerdict> CheckAsync(int ticketId) =>
            Task.FromResult(Verdicts.TryGetValue(ticketId, out var v) ? v : Default);
    }

    /// <summary>
    /// A gate that never lets anything through, so a test about admission can fill the line without
    /// racing the drain for the first item.
    /// </summary>
    private static FakeGate HoldEverything() =>
        new() { Default = GateVerdict.Wait("the pipeline has this one") };

    /// <summary>
    /// An executor that records what it was asked to run, in the order it was asked, and behaves
    /// however the test needs it to.
    /// </summary>
    private class FakeExecutor : IAnalysisExecutor
    {
        private readonly Func<int, CancellationToken, Task<(int?, string?)>> _run;
        private readonly List<int> _ran = new();

        public FakeExecutor(Func<int, CancellationToken, Task<(int?, string?)>> run) => _run = run;

        /// <summary>Ticket ids in the order they actually started.</summary>
        public IReadOnlyList<int> Ran { get { lock (_ran) return _ran.ToList(); } }

        /// <summary>What the run should report about itself before it answers. Null for a silent run.</summary>
        public Action<IRunProgress>? Narrate;

        public Task<(int? analysisId, string? error)> RunAsync(
            int ticketId, AnalysisLens lens, IRunProgress progress, CancellationToken ct)
        {
            lock (_ran) _ran.Add(ticketId);
            Narrate?.Invoke(progress);
            return _run(ticketId, ct);
        }

        /// <summary>Analysis ids whose walkthrough was asked for, in order.</summary>
        public readonly List<int> Walkthroughs = new();

        /// <summary>What a walkthrough waits on before it finishes. Completed unless a test holds it open.</summary>
        public Task HoldWalkthrough = Task.CompletedTask;

        public Task RunWalkthroughAsync(int analysisId, IRunProgress progress, CancellationToken ct)
        {
            lock (Walkthroughs) Walkthroughs.Add(analysisId);
            return HoldWalkthrough;
        }
    }

    private static FakeExecutor Instant() =>
        new((id, _) => Task.FromResult<(int?, string?)>((id * 100, null)));

    /// <summary>A queue that re-checks its held items promptly, so a test need not sit out the real
    /// twenty-second idle to see one released.</summary>
    private AnalysisQueue Queue(IAnalysisExecutor executor, IAnalysisGate? gate = null) =>
        new(gate ?? new FakeGate(), executor, NullLogger<AnalysisQueue>.Instance,
            TimeSpan.FromMilliseconds(25));

    /// <summary>An executor no test expects to be called.</summary>
    private static FakeExecutor Unused() =>
        new((_, _) => throw new InvalidOperationException("nothing should have run"));

    /// <summary>
    /// The drain runs on its own task, so every assertion about it is an assertion about something
    /// that has to happen rather than something that already has.
    /// </summary>
    private static async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(15);
        }
        throw new TimeoutException($"Timed out waiting for {what}.");
    }

    // ---- order and admission ------------------------------------------------

    [Fact]
    public async Task The_queue_drains_in_the_order_it_was_asked()
    {
        var exec = Instant();
        var q = Queue(exec);

        Assert.True(q.Enqueue(1, AnalysisLens.Full).ok);
        Assert.True(q.Enqueue(2, AnalysisLens.Full).ok);
        Assert.True(q.Enqueue(3, AnalysisLens.Full).ok);

        await Until(() => q.Snapshot().Recent.Count == 3, "all three to finish");
        Assert.Equal(new[] { 1, 2, 3 }, exec.Ran);
    }

    [Fact]
    public async Task Only_one_analysis_runs_at_a_time()
    {
        var release = new TaskCompletionSource();
        var exec = new FakeExecutor(async (id, _) =>
        {
            await release.Task;
            return (id, null);
        });
        var q = Queue(exec);

        q.Enqueue(1, AnalysisLens.Full);
        q.Enqueue(2, AnalysisLens.Full);

        await Until(() => q.Snapshot().Current != null, "the first run to start");

        // The second must be waiting, not running alongside it — the single slot is the whole cap.
        var snap = q.Snapshot();
        Assert.Equal(1, snap.Current!.TicketId);
        Assert.Equal(new[] { 2 }, snap.Pending.Select(p => p.TicketId));
        Assert.Equal(new[] { 1 }, exec.Ran);

        release.SetResult();
        await Until(() => q.Snapshot().Recent.Count == 2, "both to finish");
    }

    /// <summary>
    /// A run's own account of itself reaches the snapshot, because that snapshot is the only thing
    /// the panel ever sees. Without it the bar can be driven by nothing but the clock, which is what
    /// it looked like when it sat at a third and jumped straight to done.
    /// </summary>
    [Fact]
    public async Task The_snapshot_carries_what_the_running_item_is_doing()
    {
        var release = new TaskCompletionSource();
        var exec = new FakeExecutor(async (id, _) => { await release.Task; return (id, null); })
        {
            Narrate = p =>
            {
                p.Expect(seconds: 210, steps: 24);
                p.Step("Read AnalysisQueue.cs");
                p.Step("Bash git log -20");
            },
        };
        var q = Queue(exec);

        q.Enqueue(1, AnalysisLens.Code);
        await Until(() => q.Snapshot().Current?.Steps == 2, "both steps to be reported");

        var run = q.Snapshot().Current!;
        Assert.Equal("Bash git log -20", run.Activity);   // the latest, not the first
        Assert.Equal(210, run.ExpectedSeconds);
        Assert.Equal(24, run.ExpectedSteps);

        release.SetResult();
        await Until(() => q.Snapshot().Current == null, "the run to finish");

        // Nothing is running, so there is nothing being done: the panel must not keep narrating a
        // finished run at whatever it was doing last.
        Assert.Null(q.Snapshot().Current);
    }

    /// <summary>A run that reports nothing is a queue that reports no estimate, not a queue that
    /// invents one.</summary>
    [Fact]
    public async Task A_silent_run_has_no_estimate_rather_than_a_default_one()
    {
        var release = new TaskCompletionSource();
        var q = Queue(new FakeExecutor(async (id, _) => { await release.Task; return (id, null); }));

        q.Enqueue(1, AnalysisLens.Full);
        await Until(() => q.Snapshot().Current != null, "the run to start");

        var run = q.Snapshot().Current!;
        Assert.Equal(0, run.Steps);
        Assert.Null(run.Activity);
        Assert.Null(run.ExpectedSeconds);
        Assert.Null(run.ExpectedSteps);

        release.SetResult();
    }

    [Fact]
    public void The_same_ticket_and_lens_cannot_be_queued_twice()
    {
        var q = Queue(Unused(), HoldEverything());

        Assert.True(q.Enqueue(2, AnalysisLens.Full).ok);

        var (ok, message) = q.Enqueue(2, AnalysisLens.Full);
        Assert.False(ok);
        Assert.Contains("already", message);
    }

    [Fact]
    public void The_same_ticket_under_a_different_lens_is_a_second_read_not_a_double_click()
    {
        var q = Queue(Unused(), HoldEverything());

        Assert.True(q.Enqueue(2, AnalysisLens.Full).ok);
        Assert.True(q.Enqueue(2, AnalysisLens.Testing).ok);
    }

    [Fact]
    public void The_queue_has_a_ceiling()
    {
        var q = Queue(Unused(), HoldEverything());

        for (var i = 0; i < AnalysisQueue.MaxPending; i++) Assert.True(q.Enqueue(i, AnalysisLens.Full).ok);

        var (ok, message) = q.Enqueue(999, AnalysisLens.Full);
        Assert.False(ok);
        Assert.Contains("full", message);
    }

    // ---- getting out of the queue -------------------------------------------

    [Fact]
    public async Task A_waiting_item_can_be_taken_out_and_never_runs()
    {
        var release = new TaskCompletionSource();
        var exec = new FakeExecutor(async (id, _) => { await release.Task; return (id, null); });
        var q = Queue(exec);

        q.Enqueue(1, AnalysisLens.Full);
        q.Enqueue(2, AnalysisLens.Full);
        await Until(() => q.Snapshot().Current != null, "the first run to start");

        Assert.True(q.Cancel(2, AnalysisLens.Full));
        Assert.Empty(q.Snapshot().Pending);

        release.SetResult();
        await Until(() => q.Snapshot().Current == null, "the running item to finish");
        Assert.Equal(new[] { 1 }, exec.Ran);
    }

    [Fact]
    public async Task Stopping_the_running_item_cancels_its_token_and_reports_nothing()
    {
        var exec = new FakeExecutor(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return (null, null);
        });
        var q = Queue(exec);

        q.Enqueue(1, AnalysisLens.Full);
        await Until(() => q.Snapshot().Current != null, "the run to start");

        Assert.True(q.Cancel(1, AnalysisLens.Full));
        await Until(() => q.Snapshot().Current == null, "the run to stop");

        // A person who stopped it does not need to be told about it afterwards.
        Assert.Empty(q.Snapshot().Recent);
    }

    [Fact]
    public async Task Clearing_the_queue_leaves_the_running_item_alone()
    {
        var release = new TaskCompletionSource();
        var exec = new FakeExecutor(async (id, _) => { await release.Task; return (id, null); });
        var q = Queue(exec);

        q.Enqueue(1, AnalysisLens.Full);
        await Until(() => q.Snapshot().Current != null, "the run to start");
        q.Enqueue(2, AnalysisLens.Full);
        q.Enqueue(3, AnalysisLens.Full);

        Assert.Equal(2, q.ClearPending());

        var snap = q.Snapshot();
        Assert.Empty(snap.Pending);
        Assert.Equal(1, snap.Current!.TicketId);

        release.SetResult();
        await Until(() => q.Snapshot().Recent.Count == 1, "the running item to finish");
        Assert.Equal(new[] { 1 }, exec.Ran);
    }

    // ---- the point of the whole thing ---------------------------------------

    [Fact]
    public async Task An_item_that_fails_does_not_stall_the_ones_behind_it()
    {
        var exec = new FakeExecutor((id, _) => Task.FromResult<(int?, string?)>(
            id == 2 ? (null, "the agent fell over") : (id * 100, null)));
        var q = Queue(exec);

        q.Enqueue(1, AnalysisLens.Full);
        q.Enqueue(2, AnalysisLens.Full);
        q.Enqueue(3, AnalysisLens.Full);

        await Until(() => q.Snapshot().Recent.Count == 3, "all three to finish");

        Assert.Equal(new[] { 1, 2, 3 }, exec.Ran);
        var failed = q.Snapshot().Recent.Single(r => r.TicketId == 2);
        Assert.Equal("the agent fell over", failed.Error);
        Assert.All(q.Snapshot().Recent.Where(r => r.TicketId != 2), r => Assert.NotNull(r.AnalysisId));
    }

    [Fact]
    public async Task An_item_that_throws_does_not_stall_the_ones_behind_it()
    {
        var exec = new FakeExecutor((id, _) => id == 1
            ? throw new InvalidOperationException("boom")
            : Task.FromResult<(int?, string?)>((id * 100, null)));
        var q = Queue(exec);

        q.Enqueue(1, AnalysisLens.Full);
        q.Enqueue(2, AnalysisLens.Full);

        await Until(() => q.Snapshot().Recent.Count == 2, "both to be accounted for");
        Assert.Equal(new[] { 1, 2 }, exec.Ran);
        Assert.Equal("boom", q.Snapshot().Recent.Single(r => r.TicketId == 1).Error);
    }

    [Fact]
    public async Task An_item_the_gate_refuses_fails_alone_and_the_rest_still_run()
    {
        var gate = new FakeGate();
        gate.Verdicts[2] = GateVerdict.Stop("it cannot be studied yet: no branch");
        var exec = Instant();
        var q = Queue(exec, gate);

        q.Enqueue(1, AnalysisLens.Full);
        q.Enqueue(2, AnalysisLens.Full);
        q.Enqueue(3, AnalysisLens.Full);

        await Until(() => q.Snapshot().Recent.Count == 3, "all three to be accounted for");

        // The refused one never reached the executor, and never held up the two around it.
        Assert.Equal(new[] { 1, 3 }, exec.Ran);
        Assert.Contains("no branch", q.Snapshot().Recent.Single(r => r.TicketId == 2).Error);
    }

    // ---- pipeline contention ------------------------------------------------

    [Fact]
    public async Task A_ticket_the_pipeline_is_working_is_stepped_over_not_failed()
    {
        var gate = new FakeGate();
        gate.Verdicts[1] = GateVerdict.Wait("implement has had this ticket for 4m");
        var exec = Instant();
        var q = Queue(exec, gate);

        q.Enqueue(1, AnalysisLens.Full);
        q.Enqueue(2, AnalysisLens.Full);

        await Until(() => q.Snapshot().Recent.Count == 1, "the unheld item to run");

        // The held one is still in the line, in its place, with the reason on it — not failed, and
        // not sent to the back.
        var snap = q.Snapshot();
        Assert.Equal(new[] { 1 }, snap.Pending.Select(p => p.TicketId));
        Assert.Equal("implement has had this ticket for 4m", snap.Pending[0].HoldReason);
        Assert.Equal(new[] { 2 }, exec.Ran);
    }

    [Fact]
    public async Task A_held_ticket_runs_as_soon_as_the_pipeline_lets_go()
    {
        var gate = new FakeGate();
        gate.Verdicts[1] = GateVerdict.Wait("qa-test has had this ticket for 2m");
        var exec = Instant();
        var q = Queue(exec, gate);

        q.Enqueue(1, AnalysisLens.Full);

        // The queue does not drain to empty while a held item is in it — it idles and looks again.
        await Until(() => q.Snapshot().Pending.SingleOrDefault()?.HoldReason != null, "the hold to be noticed");
        Assert.Empty(exec.Ran);

        gate.Verdicts.Remove(1);
        await Until(() => q.Snapshot().Recent.Count == 1, "the released item to run");
        Assert.Equal(new[] { 1 }, exec.Ran);
    }

    // ---- the gate, over real rows -------------------------------------------

    [Fact]
    public async Task The_gate_refuses_a_ticket_that_has_stopped_being_eligible()
    {
        using var ctx = NewCtx();
        var t = await Seed(ctx, TicketPhase.NeedsQa);

        // The ticket moved back to a phase with nothing to read — the exact thing a click-time check
        // would have missed, because it passed at the click.
        t.Phase = TicketPhase.NeedsSpeculation;
        t.ImplementationSummary = null;
        await ctx.SaveChangesAsync();

        var verdict = await AnalysisGate.CheckAsync(ctx, new ResourceLeaseService(ctx), t.Id);
        Assert.Equal(GateDecision.Stop, verdict.Decision);
        Assert.Contains("implementation", verdict.Reason);
    }

    [Fact]
    public async Task The_gate_holds_a_ticket_a_heartbeat_agent_is_in()
    {
        using var ctx = NewCtx();
        var t = await Seed(ctx, TicketPhase.NeedsQa);
        var leases = new ResourceLeaseService(ctx);

        await leases.TryAcquireAllAsync(
            new[] { ResourceLeaseService.TicketKey(t.Id) }, t.Id, "qa-test", TimeSpan.FromMinutes(150));

        var held = await AnalysisGate.CheckAsync(ctx, leases, t.Id);
        Assert.Equal(GateDecision.Wait, held.Decision);
        Assert.Contains("qa-test", held.Reason);

        // And the moment the worker gives the ticket back, it is simply the next thing to run.
        await leases.ReleaseForTicketAsync(t.Id, "cycle finished");
        Assert.Equal(GateDecision.Go, (await AnalysisGate.CheckAsync(ctx, leases, t.Id)).Decision);
    }

    [Fact]
    public async Task A_phase_that_only_looks_busy_is_not_a_hold()
    {
        using var ctx = NewCtx();
        // Parked in an "-ing" phase with nobody on it: normal, resumable, and worth studying. Gating
        // on the phase rather than the lease would refuse exactly this ticket.
        var t = await Seed(ctx, TicketPhase.QaTesting);

        var verdict = await AnalysisGate.CheckAsync(ctx, new ResourceLeaseService(ctx), t.Id);
        Assert.Equal(GateDecision.Go, verdict.Decision);
    }

    [Fact]
    public async Task A_lease_nobody_gave_back_stops_holding_the_ticket_once_it_expires()
    {
        using var ctx = NewCtx();
        var t = await Seed(ctx, TicketPhase.NeedsQa);
        var leases = new ResourceLeaseService(ctx);

        // The orchestrator was killed without releasing. The expiry sweep is the backstop under a
        // hold that would otherwise wait for something that is never coming back.
        await leases.TryAcquireAllAsync(
            new[] { ResourceLeaseService.TicketKey(t.Id) }, t.Id, "implement", TimeSpan.FromMinutes(-1));

        Assert.Equal(GateDecision.Go, (await AnalysisGate.CheckAsync(ctx, leases, t.Id)).Decision);
    }

    [Fact]
    public async Task Contention_is_asked_about_before_eligibility()
    {
        using var ctx = NewCtx();
        // Mid-implementation: ineligible right now, and about to be eligible the moment the worker
        // hands it on. Asking about eligibility first would fail the one item that was worth waiting
        // for.
        var t = await Seed(ctx, TicketPhase.Implementing);
        var leases = new ResourceLeaseService(ctx);
        await leases.TryAcquireAllAsync(
            new[] { ResourceLeaseService.TicketKey(t.Id) }, t.Id, "implement", TimeSpan.FromMinutes(150));

        Assert.Equal(GateDecision.Wait, (await AnalysisGate.CheckAsync(ctx, leases, t.Id)).Decision);
    }

    private static async Task<Ticket> Seed(DashboardDbContext ctx, TicketPhase phase)
    {
        var t = new Ticket
        {
            ProjectKey = "alpha",
            Title = "a ticket",
            Phase = phase,
            Branch = "feature/x",
            ImplementationSummary = "did the thing",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.Tickets.Add(t);
        await ctx.SaveChangesAsync();
        return t;
    }

    // ---- walkthrough-only items ---------------------------------------------

    /// <summary>
    /// A walkthrough is written about an analysis that already exists, so the gate that decides
    /// whether a ticket may have a NEW analysis is not its question - a ticket the pipeline holds
    /// still gets its walkthrough rewritten. And its result lives on its own row, so it adds
    /// nothing to the analysis outcome list the Classroom reports failures from.
    /// </summary>
    [Fact]
    public async Task A_walkthrough_item_skips_the_gate_and_reports_nowhere_but_its_own_row()
    {
        var exec = Instant();
        var q = Queue(exec, HoldEverything());

        Assert.True(q.EnqueueWalkthrough(7, 44).ok);

        await Until(() => { lock (exec.Walkthroughs) return exec.Walkthroughs.Count == 1; }, "the walkthrough to run");
        await Until(() => q.Snapshot().Current == null, "the slot to be given back");
        Assert.Equal(44, exec.Walkthroughs[0]);
        Assert.Empty(exec.Ran);
        Assert.Empty(q.Snapshot().Recent);
    }

    [Fact]
    public async Task The_same_walkthrough_cannot_be_queued_twice_and_does_not_block_an_analysis()
    {
        var hold = new TaskCompletionSource();
        var exec = Unused();
        exec.HoldWalkthrough = hold.Task;
        var q = Queue(exec, HoldEverything());

        Assert.True(q.EnqueueWalkthrough(7, 44).ok);
        await Until(() => q.Snapshot().Current?.WalkthroughOf == 44, "the walkthrough to take the slot");

        Assert.False(q.EnqueueWalkthrough(7, 44).ok);
        // An analysis of the same ticket is a different item, and waits its turn behind it.
        Assert.True(q.Enqueue(7, AnalysisLens.Full).ok);
        hold.SetResult();
    }
}
