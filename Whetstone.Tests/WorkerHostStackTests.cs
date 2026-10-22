using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// A worker host's own stack (docs/worker-hosts-plan.md, phase 2). A QA run on a worker host takes
/// that host's copy of the stack lease, which neither waits on the board host's nor blocks it; and
/// it may run more than one at once only where the host allows it AND the project says its stack
/// can run twice on one machine. Every way that could start one stack twice on the same ports is a
/// test here.
/// </summary>
public class WorkerHostStackTests : IDisposable
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(150);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "whetstone-hoststack-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<DashboardDbContext> _options;
    private readonly ProjectRegistry _registry;

    public WorkerHostStackTests()
    {
        foreach (var (key, parallel) in new[] { ("alpha", true), ("bravo", false) })
        {
            var dir = Path.Combine(_root, "projects", key);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "project.defaults.json"),
                $$"""{ "displayName": "{{key}}", "projectPath": "C:\\nowhere\\{{key}}", "parallelStacks": {{(parallel ? "true" : "false")}} }""");
        }
        File.WriteAllText(Path.Combine(_root, "config.json"), """
            { "workerHosts": [ { "name": "bravo-host", "slots": 4, "qaLeases": 3, "projects": ["alpha", "bravo"],
                                 "skills": ["implement", "qa-test"] } ] }
            """);
        _registry = new ProjectRegistry(new DashboardPaths(_root));

        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _options = new DbContextOptionsBuilder<DashboardDbContext>().UseSqlite(_conn).Options;
        using var ctx = new DashboardDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _conn.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private DashboardDbContext NewCtx() => new(_options);

    [Fact]
    public void A_worker_hosts_stack_is_its_own_resource()
    {
        Assert.Equal("alpha:app-stack@bravo-host", WorkerHostConfig.HostResource("alpha:app-stack", "bravo-host"));
        Assert.Equal(("alpha:app-stack", "bravo-host"), WorkerHostConfig.SplitHostResource("alpha:app-stack@bravo-host"));
        Assert.Null(WorkerHostConfig.SplitHostResource("alpha:app-stack"));
        Assert.Null(WorkerHostConfig.SplitHostResource("@x"));
        Assert.Null(WorkerHostConfig.SplitHostResource("x@"));
    }

    [Fact]
    public void Qa_leases_apply_only_to_a_project_whose_stack_can_run_twice()
    {
        using var ctx = NewCtx();
        var leases = new ResourceLeaseService(ctx, _registry);
        Assert.Equal(3, leases.CapacityFor("alpha:app-stack@bravo-host"));
        Assert.Equal(1, leases.CapacityFor("bravo:app-stack@bravo-host"));
        Assert.Equal(1, leases.CapacityFor("alpha:app-stack@charlie-host"));
        // The board host's own stack is untouched by a worker host's numbers.
        Assert.Equal(1, leases.CapacityFor("alpha:app-stack"));
    }

    /// <summary>
    /// The board host's own qaLeases follows the same rule as a worker host's: more than one copy
    /// only of a stack that can run twice, and never past the ceiling.
    /// </summary>
    [Fact]
    public void The_board_hosts_qa_leases_apply_only_to_a_parallel_stacks_project()
    {
        File.WriteAllText(Path.Combine(_root, "config.json"), """{ "qaLeases": 4 }""");
        using var ctx = NewCtx();
        var leases = new ResourceLeaseService(ctx, _registry);
        Assert.Equal(4, leases.CapacityFor("alpha:app-stack"));
        Assert.Equal(1, leases.CapacityFor("bravo:app-stack"));
        Assert.Equal(1, leases.CapacityFor("nobody:app-stack"));
        Assert.Equal(1, leases.CapacityFor("ticket:7"));

        File.WriteAllText(Path.Combine(_root, "config.json"), """{ "qaLeases": 40 }""");
        Assert.Equal(ResourceLeaseService.MaxQaLeases, new ResourceLeaseService(ctx, _registry).CapacityFor("alpha:app-stack"));

        File.WriteAllText(Path.Combine(_root, "config.json"), "{}");
        Assert.Equal(1, new ResourceLeaseService(ctx, _registry).CapacityFor("alpha:app-stack"));
    }

    /// <summary>An explicit resourceCapacity entry is the more specific setting, so it wins.</summary>
    [Fact]
    public void A_resource_capacity_entry_outranks_qa_leases()
    {
        File.WriteAllText(Path.Combine(_root, "config.json"),
            """{ "qaLeases": 4, "resourceCapacity": { "alpha:app-stack": 2 } }""");
        using var ctx = NewCtx();
        Assert.Equal(2, new ResourceLeaseService(ctx, _registry).CapacityFor("alpha:app-stack"));
    }

    [Fact]
    public void A_host_entry_that_names_no_qa_leases_has_one()
    {
        File.WriteAllText(Path.Combine(_root, "config.json"), """{ "workerHosts": [ { "name": "bravo-host" } ] }""");
        using var ctx = NewCtx();
        Assert.Equal(1, new ResourceLeaseService(ctx, _registry).CapacityFor("alpha:app-stack@bravo-host"));
    }

    private async Task<int> Seed(string project, TicketPhase phase, DateTime updated)
    {
        await using var ctx = NewCtx();
        var t = new Ticket { ProjectKey = project, Title = $"{project} {phase}", Phase = phase, CreatedAt = updated, UpdatedAt = updated };
        ctx.Tickets.Add(t);
        await ctx.SaveChangesAsync();
        return t.Id;
    }

    private static Func<string, string, string[]> Qa(string? host) => (project, skill) =>
        skill == "qa-test"
            ? new[] { host is null ? $"{project}:app-stack" : WorkerHostConfig.HostResource($"{project}:app-stack", host) }
            : Array.Empty<string>();

    [Fact]
    public async Task The_board_hosts_qa_and_a_worker_hosts_qa_run_side_by_side()
    {
        var old = DateTime.UtcNow.AddHours(-1);
        var first = await Seed("bravo", TicketPhase.NeedsQa, old);
        var second = await Seed("bravo", TicketPhase.NeedsQa, old.AddMinutes(1));

        await using var ctx = NewCtx();
        var svc = new TicketService(ctx, leases: new ResourceLeaseService(ctx, _registry));
        var board = await svc.ClaimNextAsync(null, (_, _) => true, 3, Qa(null), Ttl);
        Assert.Equal(first, board!.Ticket.Id);
        // The board host's stack is taken - and a worker host's QA is not held up by it.
        Assert.Null(await svc.ClaimNextAsync(null, (_, _) => true, 3, Qa(null), Ttl));
        var remote = await svc.ClaimNextAsync(null, (_, _) => true, 3, Qa("bravo-host"), Ttl);
        Assert.Equal(second, remote!.Ticket.Id);
        Assert.Contains("bravo:app-stack@bravo-host", remote.Leases);
    }

    [Fact]
    public async Task A_project_that_cannot_run_two_stacks_gets_one_qa_at_a_time_on_a_host()
    {
        var old = DateTime.UtcNow.AddHours(-1);
        await Seed("bravo", TicketPhase.NeedsQa, old);
        await Seed("bravo", TicketPhase.NeedsQa, old.AddMinutes(1));

        await using var ctx = NewCtx();
        var svc = new TicketService(ctx, leases: new ResourceLeaseService(ctx, _registry));
        var a = await svc.ClaimNextAsync(null, (_, _) => true, 3, Qa("bravo-host"), Ttl);
        Assert.Equal(0, a!.StackSlot);
        Assert.Null(await svc.ClaimNextAsync(null, (_, _) => true, 3, Qa("bravo-host"), Ttl));
    }

    [Fact]
    public async Task Parallel_qa_runs_on_a_host_each_get_their_own_stack_slot()
    {
        var old = DateTime.UtcNow.AddHours(-1);
        for (var i = 0; i < 4; i++) await Seed("alpha", TicketPhase.NeedsQa, old.AddMinutes(i));

        await using var ctx = NewCtx();
        var svc = new TicketService(ctx, leases: new ResourceLeaseService(ctx, _registry));
        var slots = new List<int>();
        for (var i = 0; i < 3; i++) slots.Add((await svc.ClaimNextAsync(null, (_, _) => true, 3, Qa("bravo-host"), Ttl))!.StackSlot);
        Assert.Equal(new[] { 0, 1, 2 }, slots);
        // qaLeases is 3: the fourth waits.
        Assert.Null(await svc.ClaimNextAsync(null, (_, _) => true, 3, Qa("bravo-host"), Ttl));
    }

    [Fact]
    public async Task A_run_that_holds_no_stack_is_on_slot_zero()
    {
        await Seed("alpha", TicketPhase.NeedsImplementation, DateTime.UtcNow.AddHours(-1));
        await using var ctx = NewCtx();
        var svc = new TicketService(ctx, leases: new ResourceLeaseService(ctx, _registry));
        Assert.Equal(0, (await svc.ClaimNextAsync(null, (_, _) => true, 3, Qa("bravo-host"), Ttl))!.StackSlot);
    }
}
