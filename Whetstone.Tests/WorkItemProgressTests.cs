using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// Pins the invariant the drawer's "working on this now" marker is read straight off:
/// a ticket has at most ONE work item carrying a <see cref="WorkItem.StartedAt"/>, and that item
/// is never <see cref="WorkItem.Done"/>. The UI does no arithmetic over the list, so if that ever
/// stops holding, the board silently reports the agent as being in two places at once, or on an
/// item it finished.
/// </summary>
public class WorkItemProgressTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<DashboardDbContext> _options;

    public WorkItemProgressTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _options = new DbContextOptionsBuilder<DashboardDbContext>().UseSqlite(_conn).Options;
        using var ctx = new DashboardDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _conn.Dispose();

    private DashboardDbContext NewCtx() => new(_options);

    /// <summary>A ticket with three work items, returned in order.</summary>
    private async Task<(int TicketId, int[] Items)> SeedTicket()
    {
        await using var ctx = NewCtx();
        var svc = new TicketService(ctx);
        var t = await svc.CreateAsync("alpha", "Big one", null, null, null, TicketPhase.Implementing);
        var ids = new List<int>();
        foreach (var text in new[] { "one", "two", "three" })
            ids.Add((await svc.AddWorkItemAsync(t.Id, text))!.Id);
        return (t.Id, ids.ToArray());
    }

    private async Task<List<WorkItem>> Items(int ticketId)
    {
        await using var ctx = NewCtx();
        return await ctx.WorkItems.Where(w => w.TicketId == ticketId).OrderBy(w => w.Order).ToListAsync();
    }

    [Fact]
    public async Task Starting_an_item_moves_the_marker_off_the_previous_one()
    {
        var (ticketId, items) = await SeedTicket();

        await using (var ctx = NewCtx()) await new TicketService(ctx).StartWorkItemAsync(items[0]);
        await using (var ctx = NewCtx()) await new TicketService(ctx).StartWorkItemAsync(items[1]);

        var after = await Items(ticketId);
        Assert.Null(after[0].StartedAt);
        Assert.NotNull(after[1].StartedAt);
        Assert.Null(after[2].StartedAt);
    }

    [Fact]
    public async Task Finishing_an_item_clears_its_marker()
    {
        var (ticketId, items) = await SeedTicket();

        await using (var ctx = NewCtx()) await new TicketService(ctx).StartWorkItemAsync(items[0]);
        await using (var ctx = NewCtx()) await new TicketService(ctx).UpdateWorkItemAsync(items[0], null, true, null);

        var after = await Items(ticketId);
        Assert.True(after[0].Done);
        Assert.Null(after[0].StartedAt);
        Assert.All(after, w => Assert.Null(w.StartedAt));
    }

    [Fact]
    public async Task Reopening_a_finished_item_does_not_resurrect_it_as_the_active_one()
    {
        // The failure this guards: if finishing left StartedAt in place, un-ticking an item later
        // would put the bot back on a row nobody is working.
        var (ticketId, items) = await SeedTicket();

        await using (var ctx = NewCtx()) await new TicketService(ctx).StartWorkItemAsync(items[0]);
        await using (var ctx = NewCtx()) await new TicketService(ctx).UpdateWorkItemAsync(items[0], null, true, null);
        await using (var ctx = NewCtx()) await new TicketService(ctx).UpdateWorkItemAsync(items[0], null, false, null);

        var after = await Items(ticketId);
        Assert.False(after[0].Done);
        Assert.Null(after[0].StartedAt);
    }

    [Fact]
    public async Task Restarting_a_finished_item_reopens_it_and_takes_the_marker_back()
    {
        // Rework: the agent walks back UP the list to redo something it had already ticked off.
        var (ticketId, items) = await SeedTicket();

        await using (var ctx = NewCtx()) await new TicketService(ctx).StartWorkItemAsync(items[0]);
        await using (var ctx = NewCtx()) await new TicketService(ctx).UpdateWorkItemAsync(items[0], null, true, null);
        await using (var ctx = NewCtx()) await new TicketService(ctx).StartWorkItemAsync(items[1]);
        await using (var ctx = NewCtx()) await new TicketService(ctx).StartWorkItemAsync(items[0]);

        var after = await Items(ticketId);
        Assert.False(after[0].Done);
        Assert.NotNull(after[0].StartedAt);
        Assert.Null(after[1].StartedAt);
    }

    [Fact]
    public async Task The_marker_never_crosses_tickets()
    {
        var a = await SeedTicket();
        var b = await SeedTicket();

        await using (var ctx = NewCtx()) await new TicketService(ctx).StartWorkItemAsync(a.Items[0]);
        await using (var ctx = NewCtx()) await new TicketService(ctx).StartWorkItemAsync(b.Items[0]);

        // Two agents work two tickets at once; starting an item on one must not clear the other's.
        Assert.NotNull((await Items(a.TicketId))[0].StartedAt);
        Assert.NotNull((await Items(b.TicketId))[0].StartedAt);
    }

    [Fact]
    public async Task Ticking_items_off_as_they_finish_registers_as_progress()
    {
        // The circuit breaker fingerprints workDone/workTotal. A run cut short after 2 of 3 items
        // must look different from the claim baseline, or it burns an attempt toward Blocked for
        // having made progress.
        var (ticketId, items) = await SeedTicket();

        await using var ctx = NewCtx();
        var svc = new TicketService(ctx);
        var before = await Mark(svc, ticketId);
        await svc.UpdateWorkItemAsync(items[0], null, true, null);
        await svc.UpdateWorkItemAsync(items[1], null, true, null);
        var after = await Mark(svc, ticketId);

        Assert.NotEqual(before, after);
    }

    // ---- QA items: the same single-slot marker, with QA's own wrinkles ----

    private async Task<(int TicketId, int[] Items)> SeedQa()
    {
        await using var ctx = NewCtx();
        var svc = new TicketService(ctx);
        var t = await svc.CreateAsync("alpha", "Needs testing", null, null, null, TicketPhase.QaTesting);
        var ids = new List<int>();
        foreach (var text in new[] { "login works", "grid filters", "export downloads" })
            ids.Add((await svc.AddQaItemAsync(t.Id, text))!.Id);
        return (t.Id, ids.ToArray());
    }

    private async Task<List<QaItem>> QaItems(int ticketId)
    {
        await using var ctx = NewCtx();
        return await ctx.QaItems.Where(q => q.TicketId == ticketId).OrderBy(q => q.Order).ToListAsync();
    }

    [Fact]
    public async Task Starting_a_qa_item_moves_the_marker_off_the_previous_one()
    {
        var (ticketId, items) = await SeedQa();

        await using (var ctx = NewCtx()) await new TicketService(ctx).StartQaItemAsync(items[0]);
        await using (var ctx = NewCtx()) await new TicketService(ctx).StartQaItemAsync(items[1]);

        var after = await QaItems(ticketId);
        Assert.Null(after[0].StartedAt);
        Assert.NotNull(after[1].StartedAt);
        Assert.Null(after[2].StartedAt);
    }

    [Theory]
    [InlineData(QaStatus.Passed)]
    [InlineData(QaStatus.Failed)]
    [InlineData(QaStatus.Blocked)]
    public async Task Recording_any_outcome_clears_the_qa_marker(QaStatus outcome)
    {
        var (ticketId, items) = await SeedQa();

        await using (var ctx = NewCtx()) await new TicketService(ctx).StartQaItemAsync(items[0]);
        await using (var ctx = NewCtx()) await new TicketService(ctx).UpdateQaItemAsync(items[0], null, outcome, null, null);

        var after = await QaItems(ticketId);
        Assert.Equal(outcome, after[0].Status);
        Assert.All(after, q => Assert.Null(q.StartedAt));
    }

    [Fact]
    public async Task Setting_a_qa_item_back_to_todo_keeps_the_marker()
    {
        // Todo is the state an item sits in WHILE it is under test, unlike a work item's Done. So
        // unlike the work-item path, writing Todo must not knock the marker off the item the phase
        // is standing on.
        var (ticketId, items) = await SeedQa();

        await using (var ctx = NewCtx()) await new TicketService(ctx).StartQaItemAsync(items[0]);
        await using (var ctx = NewCtx()) await new TicketService(ctx).UpdateQaItemAsync(items[0], null, QaStatus.Todo, null, null);

        Assert.NotNull((await QaItems(ticketId))[0].StartedAt);
    }

    [Fact]
    public async Task Retesting_resets_the_verdict_but_keeps_the_note()
    {
        // A retest that never finishes must not still read as Passed. The note survives because it
        // explains the outcome being re-examined, and a run that dies mid-retest would otherwise
        // have erased why the item failed in the first place.
        var (ticketId, items) = await SeedQa();

        await using (var ctx = NewCtx())
            await new TicketService(ctx).UpdateQaItemAsync(items[0], null, QaStatus.Failed, "grid showed 0 rows, expected 12", null);
        await using (var ctx = NewCtx()) await new TicketService(ctx).StartQaItemAsync(items[0]);

        var after = await QaItems(ticketId);
        Assert.Equal(QaStatus.Todo, after[0].Status);
        Assert.NotNull(after[0].StartedAt);
        Assert.Equal("grid showed 0 rows, expected 12", after[0].Note);
    }

    [Fact]
    public async Task The_qa_marker_never_crosses_tickets()
    {
        var a = await SeedQa();
        var b = await SeedQa();

        await using (var ctx = NewCtx()) await new TicketService(ctx).StartQaItemAsync(a.Items[0]);
        await using (var ctx = NewCtx()) await new TicketService(ctx).StartQaItemAsync(b.Items[0]);

        Assert.NotNull((await QaItems(a.TicketId))[0].StartedAt);
        Assert.NotNull((await QaItems(b.TicketId))[0].StartedAt);
    }

    [Fact]
    public async Task Work_and_qa_markers_are_independent()
    {
        // One ticket can carry both at once — the phases run at different times, but nothing in the
        // schema says so, and clearing one from the other's path would be a silent cross-wire.
        var (ticketId, work) = await SeedTicket();
        int qa;
        await using (var ctx = NewCtx()) qa = (await new TicketService(ctx).AddQaItemAsync(ticketId, "smoke test"))!.Id;

        await using (var ctx = NewCtx()) await new TicketService(ctx).StartWorkItemAsync(work[0]);
        await using (var ctx = NewCtx()) await new TicketService(ctx).StartQaItemAsync(qa);

        Assert.NotNull((await Items(ticketId))[0].StartedAt);
        Assert.NotNull((await QaItems(ticketId))[0].StartedAt);
    }

    [Fact]
    public async Task Recording_qa_outcomes_as_they_land_registers_as_progress()
    {
        // Mirror of the work-item case: the circuit breaker also fingerprints qaResolved/qaTotal, so
        // a QA run cut short after 2 of 3 must not look like it achieved nothing.
        var (ticketId, items) = await SeedQa();

        await using var ctx = NewCtx();
        var svc = new TicketService(ctx);
        var before = await Mark(svc, ticketId);
        await svc.UpdateQaItemAsync(items[0], null, QaStatus.Passed, null, null);
        await svc.UpdateQaItemAsync(items[1], null, QaStatus.Failed, "boom", null);
        var after = await Mark(svc, ticketId);

        Assert.NotEqual(before, after);
    }

    // ---- The board row's "currently working on" line ----

    private async Task<string?> Summarised(int ticketId)
    {
        await using var ctx = NewCtx();
        var t = await new TicketService(ctx).GetAsync(ticketId);
        return Whetstone.Api.TicketMapper.ToSummary(t!).CurrentItem;
    }

    private async Task Phase(int ticketId, TicketPhase phase)
    {
        await using var ctx = NewCtx();
        var t = await ctx.Tickets.FindAsync(ticketId);
        t!.Phase = phase;
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task Summary_reports_the_work_item_being_implemented()
    {
        var (ticketId, items) = await SeedTicket();
        await Phase(ticketId, TicketPhase.Implementing);
        await using (var ctx = NewCtx()) await new TicketService(ctx).StartWorkItemAsync(items[1]);

        Assert.Equal("two", await Summarised(ticketId));
    }

    [Fact]
    public async Task Summary_reports_the_qa_item_being_tested()
    {
        var (ticketId, items) = await SeedQa();
        await Phase(ticketId, TicketPhase.QaTesting);
        await using (var ctx = NewCtx()) await new TicketService(ctx).StartQaItemAsync(items[2]);

        Assert.Equal("export downloads", await Summarised(ticketId));
    }

    [Fact]
    public async Task A_stale_work_item_flag_never_surfaces_once_the_ticket_has_moved_to_qa()
    {
        // The failure this guards: an implement run killed mid-item leaves StartedAt set for good,
        // because nothing runs afterwards to clear it. Scoping by the ticket's phase is what stops
        // that leftover being reported as what the QA agent is doing.
        var (ticketId, work) = await SeedTicket();
        int qa;
        await using (var ctx = NewCtx()) qa = (await new TicketService(ctx).AddQaItemAsync(ticketId, "smoke test"))!.Id;

        await Phase(ticketId, TicketPhase.Implementing);
        await using (var ctx = NewCtx()) await new TicketService(ctx).StartWorkItemAsync(work[0]);  // run dies here

        await Phase(ticketId, TicketPhase.QaTesting);
        await using (var ctx = NewCtx()) await new TicketService(ctx).StartQaItemAsync(qa);

        Assert.Equal("smoke test", await Summarised(ticketId));
    }

    [Fact]
    public async Task Summary_reports_nothing_for_a_phase_with_no_item_being_worked()
    {
        var (ticketId, items) = await SeedTicket();
        await Phase(ticketId, TicketPhase.Implementing);
        await using (var ctx = NewCtx()) await new TicketService(ctx).StartWorkItemAsync(items[0]);

        // Handed off with the flag still on it — a ticket sitting in a review queue is not "working".
        await Phase(ticketId, TicketPhase.NeedsReview);
        Assert.Null(await Summarised(ticketId));
    }

    [Fact]
    public async Task Summary_reports_nothing_before_the_first_item_is_started()
    {
        var (ticketId, _) = await SeedTicket();
        await Phase(ticketId, TicketPhase.Implementing);

        Assert.Null(await Summarised(ticketId));
    }

    /// <summary>ProgressMarkAsync is private; reach it the way the claim path does.</summary>
    private static async Task<string> Mark(TicketService svc, int ticketId)
    {
        var m = typeof(TicketService).GetMethod("ProgressMarkAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var t = await svc.GetAsync(ticketId);
        return await (Task<string>)m.Invoke(svc, new object[] { t! })!;
    }
}
