using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// The QA ruling log: every ruling on a QA item, kept after the item itself has moved on, credited
/// to the run that made it - so how often refinement overturns QA's weaker calls, and what the
/// re-drive then found, can be counted.
/// </summary>
public class QaRulingLogTests : IDisposable
{
    private const string Sha = "abc1234def5678900000000000000000000000ff";
    private const string Note = "git diff abc1234..HEAD changes only WizardStep3.tsx; this item is on the settings screen.";
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<DashboardDbContext> _options;

    public QaRulingLogTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _options = new DbContextOptionsBuilder<DashboardDbContext>().UseSqlite(_conn).Options;
        using var ctx = new DashboardDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _conn.Dispose();

    private DashboardDbContext NewCtx() => new(_options);

    /// <summary>A ticket in <paramref name="phase"/> with one item passed an hour ago and bounced past since.</summary>
    private async Task<(int TicketId, int ItemId)> Seed(TicketPhase phase, Action<Ticket>? setup = null)
    {
        await using var ctx = NewCtx();
        var t = new Ticket { ProjectKey = "alpha", Title = "t", Phase = phase, Branch = "ticket/1" };
        t.QaItems.Add(new QaItem { Text = "settings saves", Status = QaStatus.Passed, VerifiedAt = DateTime.UtcNow.AddHours(-1), VerifiedOn = Sha });
        setup?.Invoke(t);
        ctx.Tickets.Add(t);
        await ctx.SaveChangesAsync();
        ctx.PhaseTransitions.Add(new PhaseTransition
        {
            TicketId = t.Id, ProjectKey = "alpha", From = TicketPhase.Refining, To = TicketPhase.NeedsImplementation,
            Actor = TransitionActor.Worker, Backward = true, At = DateTime.UtcNow.AddMinutes(-30),
        });
        await ctx.SaveChangesAsync();
        return (t.Id, t.QaItems[0].Id);
    }

    /// <summary>The ticket lease a claimed run holds - its run id.</summary>
    private async Task<int> Lease(int ticketId)
    {
        await using var ctx = NewCtx();
        var l = new ResourceLease
        {
            ResourceKey = ResourceLeaseService.TicketKey(ticketId), TicketId = ticketId,
            ExpiresAt = DateTime.UtcNow.AddHours(1),
        };
        ctx.ResourceLeases.Add(l);
        await ctx.SaveChangesAsync();
        return l.Id;
    }

    private async Task<List<QaRulingEvent>> Log(int ticketId)
    {
        await using var ctx = NewCtx();
        return await new TicketService(ctx).RulingsAsync(ticketId);
    }

    [Theory]
    [InlineData(QaStatus.Passed, false, QaRulingKind.Driven)]
    [InlineData(QaStatus.Passed, true, QaRulingKind.ByInspection)]
    [InlineData(QaStatus.Failed, false, QaRulingKind.Failed)]
    [InlineData(QaStatus.Blocked, false, QaRulingKind.Blocked)]
    public async Task A_worker_s_status_writes_one_event_with_its_run(QaStatus status, bool inspection, QaRulingKind kind)
    {
        var (id, item) = await Seed(TicketPhase.QaTesting);
        var run = await Lease(id);

        await using (var ctx = NewCtx())
            await new TicketService(ctx).UpdateQaItemAsync(item, null, status, "what I saw", null,
                passedByInspection: inspection, verifiedOn: "0123456789", actor: TransitionActor.Worker);

        var e = Assert.Single(await Log(id));
        Assert.Equal(kind, e.Kind);
        Assert.Equal(run, e.RunId);
        Assert.Equal(TransitionActor.Worker, e.Actor);
        Assert.Equal(item, e.QaItemId);
        Assert.Equal("0123456789", e.VerifiedOn);
        Assert.Equal("Stale", e.PriorVerdict);
        Assert.Equal("what I saw", e.Note);
        Assert.Equal(TicketPhase.QaTesting, e.Phase);
        Assert.Null(e.OverturnsEventId);
    }

    [Fact]
    public async Task A_carry_over_writes_one_event_on_the_build_it_was_driven_on()
    {
        var (id, item) = await Seed(TicketPhase.QaTesting);
        var run = await Lease(id);
        await using (var ctx = NewCtx())
            Assert.Null((await new TicketService(ctx).CarryOverQaItemAsync(item, Note)).error);

        var e = Assert.Single(await Log(id));
        Assert.Equal(QaRulingKind.CarriedOver, e.Kind);
        Assert.Equal(run, e.RunId);
        Assert.Equal(Sha, e.VerifiedOn);
        Assert.Equal("Stale", e.PriorVerdict);
        Assert.Equal(Note, e.Note);
    }

    [Fact]
    public async Task A_refused_carry_over_writes_nothing()
    {
        var (id, item) = await Seed(TicketPhase.QaTesting);
        await using (var ctx = NewCtx())
            Assert.NotNull((await new TicketService(ctx).CarryOverQaItemAsync(item, "no build named")).error);
        Assert.Empty(await Log(id));
    }

    [Fact]
    public async Task Todo_and_starting_an_item_are_not_rulings_and_a_person_s_ruling_has_no_run()
    {
        var (id, item) = await Seed(TicketPhase.QaTesting);
        await Lease(id);
        await using (var ctx = NewCtx())
        {
            var svc = new TicketService(ctx);
            await svc.StartQaItemAsync(item);
            await svc.UpdateQaItemAsync(item, null, QaStatus.Todo, null, null, actor: TransitionActor.Worker);
            await svc.UpdateQaItemAsync(item, "renamed", null, null, null);
            await svc.UpdateQaItemAsync(item, null, QaStatus.Passed, null, null);   // the drawer: a person
        }

        var e = Assert.Single(await Log(id));
        Assert.Equal(TransitionActor.Human, e.Actor);
        Assert.Null(e.RunId);
    }

    /// <summary>
    /// The whole story the mart reads: QA carries an item, refinement overturns it, QA re-drives it
    /// and it fails. The item ends up holding only the failure; the log holds all three, linked.
    /// </summary>
    [Fact]
    public async Task An_overturn_links_to_the_call_and_survives_the_re_drive()
    {
        var (id, item) = await Seed(TicketPhase.QaTesting);
        var qaRun = await Lease(id);

        await using (var ctx = NewCtx())
        {
            var svc = new TicketService(ctx);
            await svc.CarryOverQaItemAsync(item, Note);
            var t = await ctx.Tickets.FindAsync(id);
            t!.Phase = TicketPhase.Refining;
            await ctx.SaveChangesAsync();
        }
        await using (var ctx = NewCtx())
        {
            var l = await ctx.ResourceLeases.FindAsync(qaRun);
            l!.ReleasedAt = DateTime.UtcNow;
            await ctx.SaveChangesAsync();
        }
        var refineRun = await Lease(id);

        await using (var ctx = NewCtx())
            Assert.Null((await new TicketService(ctx).ExpireQaItemAsync(item, "The diff changes the shared layout.")).error);

        await using (var ctx = NewCtx())
        {
            var t = await ctx.Tickets.FindAsync(id);
            t!.Phase = TicketPhase.QaTesting;
            await ctx.SaveChangesAsync();
            var redriven = await new TicketService(ctx).UpdateQaItemAsync(item, null, QaStatus.Failed,
                "settings no longer saves", null, verifiedOn: "0123456789", actor: TransitionActor.Worker);

            // The item moves on, as it always has ...
            Assert.Null(redriven!.CarriedOverAt);
            Assert.Null(redriven.CarryOverNote);
            Assert.Null(redriven.ExpiredAt);
            Assert.Null(redriven.ExpiryNote);
        }

        // ... and the log keeps what it moved on from.
        var log = await Log(id);
        Assert.Equal(new[] { QaRulingKind.CarriedOver, QaRulingKind.Expired, QaRulingKind.Failed }, log.Select(e => e.Kind));
        var (carry, expiry, failed) = (log[0], log[1], log[2]);
        Assert.Equal(carry.Id, expiry.OverturnsEventId);
        Assert.Equal("CarriedOver", expiry.PriorVerdict);
        Assert.Equal(refineRun, expiry.RunId);
        Assert.Equal(qaRun, carry.RunId);
        Assert.Equal(Note, carry.Note);
        Assert.Equal("The diff changes the shared layout.", expiry.Note);
        Assert.Equal("Expired", failed.PriorVerdict);
    }

    [Fact]
    public async Task An_expiry_of_a_pass_by_inspection_links_to_that_pass()
    {
        var (id, item) = await Seed(TicketPhase.QaTesting, t => t.QaItems[0].VerifiedOn = null);
        await using (var ctx = NewCtx())
        {
            var svc = new TicketService(ctx);
            await svc.UpdateQaItemAsync(item, null, QaStatus.Passed, "read the query plan", null,
                passedByInspection: true, actor: TransitionActor.Worker);
            (await ctx.Tickets.FindAsync(id))!.Phase = TicketPhase.Refining;
            await ctx.SaveChangesAsync();
            await svc.ExpireQaItemAsync(item, "The plan it read is not the one this index produces.");
        }

        var log = await Log(id);
        Assert.Equal(log[0].Id, log[1].OverturnsEventId);
        Assert.Equal("ByInspection", log[1].PriorVerdict);
    }

    [Fact]
    public async Task A_cycle_report_records_its_lease_so_rulings_join_to_the_run()
    {
        var (id, _) = await Seed(TicketPhase.QaTesting);
        await using var ctx = NewCtx();
        var run = await new MetricsService(ctx).RecordCycleAsync(new MetricsService.CycleReport(
            id, "alpha", TicketPhase.QaTesting, "qa-test", false, "m", DateTime.UtcNow.AddMinutes(-5), 300, 3,
            CycleOutcome.Completed, Detail: new MetricsService.CycleRunDetail(RunId: 41)));
        Assert.Equal(41, run!.LeaseRunId);
    }
}
