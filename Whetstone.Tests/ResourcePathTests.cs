using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// A resource a project takes only for the branches that need it (<c>resourcePaths</c>), and the
/// stack slot a run is told when it holds a machine-wide resource beside the project's own stack.
/// Together they are what lets QA runs share a machine: two stacks side by side, and the one
/// device only for the branch that changes the mobile app.
/// </summary>
public class ResourcePathTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<DashboardDbContext> _options;

    public ResourcePathTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _options = new DbContextOptionsBuilder<DashboardDbContext>().UseSqlite(_conn).Options;
        using var ctx = new DashboardDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _conn.Dispose();

    private static readonly ProjectConfig Alpha = new()
    {
        Name = "alpha",
        PhaseResources = new() { ["qa-test"] = new[] { "app-stack", "device:emulator" } },
        ResourcePaths = new() { ["device:emulator"] = new[] { "apps/mobile/" } },
    };

    private static ResourcePathGate Gate(Func<string, IReadOnlyList<string>?> files, Action? onRead = null) =>
        new((_, branch) => { onRead?.Invoke(); return Task.FromResult(files(branch)); },
            _ => Alpha, () => DateTime.UtcNow);

    private static Ticket T(string? branch) => new() { Id = 1, ProjectKey = "alpha", Branch = branch };

    // ---- Config ----

    [Fact]
    public void Paths_are_found_by_the_name_the_claim_holds()
    {
        Assert.Equal(new[] { "apps/mobile/" }, Alpha.PathsFor("device:emulator"));
        Assert.Null(Alpha.PathsFor("alpha:app-stack"));

        var unqualified = new ProjectConfig { Name = "alpha", ResourcePaths = new() { ["app-stack"] = new[] { "web/" } } };
        Assert.NotNull(unqualified.PathsFor("alpha:app-stack"));
    }

    [Fact]
    public void A_prefix_matches_with_either_slash()
    {
        Assert.True(ProjectConfig.Touches(new[] { "apps\\mobile\\App.tsx" }, new[] { "apps/mobile/" }));
        Assert.False(ProjectConfig.Touches(new[] { "apps/web/page.tsx" }, new[] { "apps/mobile/" }));
    }

    // ---- The gate ----

    [Fact]
    public async Task Needed_only_when_the_branch_changes_a_listed_path()
    {
        var gate = Gate(b => b == "mobile" ? new[] { "apps/mobile/App.tsx" } : new[] { "apps/web/page.tsx" });
        Assert.True(await gate.NeededAsync(T("mobile"), "device:emulator"));
        Assert.False(await gate.NeededAsync(T("web"), "device:emulator"));
        Assert.False(await gate.NeededAsync(T("web"), "device:emulator@bravo-host"));
    }

    [Fact]
    public async Task Every_doubt_takes_the_resource()
    {
        var gate = Gate(_ => null);
        Assert.True(await gate.NeededAsync(T("unreadable"), "device:emulator"));
        Assert.True(await gate.NeededAsync(T(null), "device:emulator"));
        Assert.True(await Gate(_ => throw new InvalidOperationException()).NeededAsync(T("x"), "device:emulator"));
    }

    [Fact]
    public async Task A_resource_with_no_paths_is_always_needed_and_reads_nothing()
    {
        var reads = 0;
        var gate = Gate(_ => Array.Empty<string>(), () => reads++);
        Assert.True(await gate.NeededAsync(T("web"), "alpha:app-stack"));
        Assert.Equal(0, reads);
    }

    [Fact]
    public async Task A_branch_is_read_once_a_minute()
    {
        var reads = 0;
        var gate = Gate(_ => new[] { "apps/web/page.tsx" }, () => reads++);
        await gate.NeededAsync(T("web"), "device:emulator");
        await gate.NeededAsync(T("web"), "device:emulator");
        Assert.Equal(1, reads);
    }

    // ---- The claim ----

    private async Task<(ClaimResultView Result, ResourceLeaseService Leases)> ClaimQa(DashboardDbContext ctx,
        string branch, ResourcePathGate gate, Func<string, string>? host = null)
    {
        ctx.Tickets.Add(new Ticket
        {
            ProjectKey = "alpha", Title = branch, Phase = TicketPhase.NeedsQa, Branch = branch,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
        var leases = new ResourceLeaseService(ctx);
        var svc = new TicketService(ctx, leases: leases);
        var r = await svc.ClaimNextAsync(null, (_, _) => true,
            resourcesFor: (_, skill) => Alpha.ResourcesFor(skill).Select(k => host?.Invoke(k) ?? k).ToArray(),
            resourceNeeded: gate.NeededAsync);
        Assert.NotNull(r);
        return (new ClaimResultView(r!.Leases, r.StackSlot), leases);
    }

    private record ClaimResultView(string[] Leases, int StackSlot);

    [Fact]
    public async Task A_branch_that_leaves_the_mobile_app_alone_does_not_take_the_emulator()
    {
        await using var ctx = new DashboardDbContext(_options);
        var gate = Gate(b => b == "mobile" ? new[] { "apps/mobile/App.tsx" } : new[] { "apps/web/page.tsx" });

        // Another run already has the emulator: a web branch is not held up behind it.
        var holder = new ResourceLeaseService(ctx);
        Assert.NotNull(await holder.TryAcquireAllAsync(new[] { "device:emulator" }, 999, "qa-test", TimeSpan.FromHours(1)));

        var (web, _) = await ClaimQa(ctx, "web", gate);
        Assert.Contains("alpha:app-stack", web.Leases);
        Assert.DoesNotContain("device:emulator", web.Leases);
    }

    [Fact]
    public async Task A_branch_that_changes_the_mobile_app_takes_both()
    {
        await using var ctx = new DashboardDbContext(_options);
        var gate = Gate(_ => new[] { "apps/mobile/App.tsx" });
        var (mobile, _) = await ClaimQa(ctx, "mobile", gate);
        Assert.Contains("alpha:app-stack", mobile.Leases);
        Assert.Contains("device:emulator", mobile.Leases);
    }

    [Fact]
    public async Task The_stack_slot_is_the_projects_own_stack_not_the_first_resource_by_name()
    {
        await using var ctx = new DashboardDbContext(_options);
        var gate = Gate(_ => new[] { "apps/mobile/App.tsx" });

        // The stack's first copy is taken, so this run gets its second. Beside it the run holds a
        // machine-wide resource whose name sorts ahead of the project's (as an emulator's can): its
        // slot 0 must not be what the run is told, or it starts on the first copy's ports.
        await ctx.ResourceLeases.AddAsync(new ResourceLease
        {
            ResourceKey = "alpha:app-stack", Slot = 0, TicketId = 998, AcquiredAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1),
        });
        await ctx.SaveChangesAsync();

        // Capacity 2 for the stack, as qaLeases with parallelStacks would give it.
        var leases = new FixedCapacityLeases(ctx, new() { ["alpha:app-stack"] = 2 });
        ctx.Tickets.Add(new Ticket
        {
            ProjectKey = "alpha", Title = "mobile", Phase = TicketPhase.NeedsQa, Branch = "mobile",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
        var r = await new TicketService(ctx, leases: leases).ClaimNextAsync(null, (_, _) => true,
            resourcesFor: (_, skill) => new[] { "aaa:device", "alpha:app-stack" }, resourceNeeded: gate.NeededAsync);

        Assert.NotNull(r);
        Assert.Contains("aaa:device", r!.Leases);
        Assert.Equal(1, r.StackSlot);
    }

    /// <summary>A lease service whose capacities are given, rather than read from a registry.</summary>
    private sealed class FixedCapacityLeases : ResourceLeaseService
    {
        private readonly Dictionary<string, int> _caps;
        public FixedCapacityLeases(DashboardDbContext db, Dictionary<string, int> caps) : base(db) => _caps = caps;
        public override int CapacityFor(string resourceKey) => _caps.TryGetValue(resourceKey, out var c) ? c : 1;
    }
}
