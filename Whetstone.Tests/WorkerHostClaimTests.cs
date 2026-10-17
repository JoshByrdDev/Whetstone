using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// The claim and the lease as a worker host uses them (docs/worker-hosts-plan.md, piece 2): a claim
/// narrowed to some skills, the run id it carries, and the lease checks that stop a run the
/// orchestrator gave up on from handing anything back.
/// </summary>
public class WorkerHostClaimTests : IDisposable
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(150);
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<DashboardDbContext> _options;

    public WorkerHostClaimTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _options = new DbContextOptionsBuilder<DashboardDbContext>().UseSqlite(_conn).Options;
        using var ctx = new DashboardDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _conn.Dispose();

    private DashboardDbContext NewCtx() => new(_options);

    private static bool AllSkillsExist(string project, string skill) => true;

    private async Task<int> Seed(TicketPhase phase, DateTime updated)
    {
        await using var ctx = NewCtx();
        var t = new Ticket { ProjectKey = "alpha", Title = phase.ToString(), Phase = phase, CreatedAt = updated, UpdatedAt = updated };
        ctx.Tickets.Add(t);
        await ctx.SaveChangesAsync();
        return t.Id;
    }

    [Fact]
    public async Task A_skill_filtered_claim_passes_over_what_it_will_not_run()
    {
        var old = DateTime.UtcNow.AddHours(-1);
        await Seed(TicketPhase.NeedsQa, old);                         // the most progressed - but QA
        var impl = await Seed(TicketPhase.NeedsImplementation, old);

        await using var ctx = NewCtx();
        var svc = new TicketService(ctx, leases: new ResourceLeaseService(ctx));
        var got = await svc.ClaimNextAsync(null, AllSkillsExist, 3, null, Ttl, null, new[] { "implement", "refine" });
        Assert.Equal(impl, got!.Ticket.Id);
        Assert.Equal("implement", got.Skill);
    }

    [Fact]
    public async Task A_skill_filtered_claim_does_not_block_a_ticket_it_was_never_going_to_run()
    {
        var qa = await Seed(TicketPhase.NeedsQa, DateTime.UtcNow.AddHours(-1));

        await using var ctx = NewCtx();
        var svc = new TicketService(ctx, leases: new ResourceLeaseService(ctx));
        // The project has no qa-test skill at all. A board host claim would block the ticket for
        // that; a worker host claim that was never going to run it leaves it for the board host.
        Assert.Null(await svc.ClaimNextAsync(null, (_, s) => s != "qa-test", 3, null, Ttl, null, new[] { "implement" }));
        await using var check = NewCtx();
        Assert.Equal(TicketPhase.NeedsQa, (await check.Tickets.FindAsync(qa))!.Phase);
    }

    [Fact]
    public async Task The_run_id_is_the_ticket_leases_own_and_lives_exactly_as_long()
    {
        var id = await Seed(TicketPhase.NeedsImplementation, DateTime.UtcNow.AddHours(-1));

        await using var ctx = NewCtx();
        var leases = new ResourceLeaseService(ctx);
        var svc = new TicketService(ctx, leases: leases);
        var got = await svc.ClaimNextAsync(null, AllSkillsExist, 3, null, Ttl);

        var ticketLease = (await leases.ActiveAsync()).Single(l => l.ResourceKey == ResourceLeaseService.TicketKey(id));
        Assert.Equal(ticketLease.Id, got!.RunId);
        Assert.True(await leases.IsLiveRunAsync(got.RunId, id));
        Assert.False(await leases.IsLiveRunAsync(got.RunId, id + 1));
        Assert.Equal(new[] { got.RunId }, await leases.LiveRunsAsync(new[] { got.RunId, 999 }));

        await leases.ReleaseForTicketAsync(id, "the orchestrator gave up on its host");
        Assert.False(await leases.IsLiveRunAsync(got.RunId, id));
        Assert.Empty(await leases.LiveRunsAsync(new[] { got.RunId }));
    }

    [Fact]
    public async Task A_resource_lease_is_not_a_run()
    {
        await using var ctx = NewCtx();
        var leases = new ResourceLeaseService(ctx);
        var taken = await leases.TryAcquireAllAsync(new[] { "alpha:app-stack" }, 3, "qa-test", Ttl);
        Assert.Empty(await leases.LiveRunsAsync(new[] { taken!.Single().Id }));
    }
}
