using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// Dependencies: what counts as a blocker, when it stops counting, and the rule that a person's
/// verdict outranks Jira in both directions. The Jira refresh itself is not covered here - it needs
/// a live instance - but everything that decides whether a ticket is claimable is.
/// </summary>
public class TicketDependencyTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<DashboardDbContext> _options;

    public TicketDependencyTests()
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

    private static async Task<Ticket> Seed(DashboardDbContext ctx, TicketPhase phase,
        string? issueKey = null, string project = "alpha")
    {
        var t = new Ticket
        {
            ProjectKey = project,
            Title = issueKey is null ? "local ticket" : null,
            IssueKey = issueKey,
            Phase = phase,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.Tickets.Add(t);
        await ctx.SaveChangesAsync();
        return t;
    }

    private static async Task<TicketDependency> Dep(DashboardDbContext ctx, int ticketId,
        string? issueKey = null, int? blockerTicketId = null,
        DependencyOrigin origin = DependencyOrigin.Tracker, bool? over = null, string? category = null)
    {
        var d = new TicketDependency
        {
            TicketId = ticketId,
            BlockerIssueKey = issueKey,
            BlockerTicketId = blockerTicketId,
            Origin = origin,
            Override = over,
            BlockerStatusCategory = category,
        };
        ctx.TicketDependencies.Add(d);
        await ctx.SaveChangesAsync();
        return d;
    }

    // ---- What blocks --------------------------------------------------------------

    [Fact]
    public async Task A_ticket_waiting_on_an_unfinished_local_ticket_is_not_claimed()
    {
        int blocked;
        await using (var ctx = NewCtx())
        {
            var blocker = await Seed(ctx, TicketPhase.NeedsImplementation, "ALPHA-1");
            var t = await Seed(ctx, TicketPhase.NeedsImplementation, "ALPHA-2");
            blocked = t.Id;
            await Dep(ctx, t.Id, issueKey: "ALPHA-1");
            _ = blocker;
        }

        await using var ctx2 = NewCtx();
        var deps = new TicketDependencyService(ctx2);
        var claim = await new TicketService(ctx2, dependencies: deps).ClaimNextAsync(null, AllSkillsExist);

        // The blocker itself is claimable and ranks equally, so it is what comes back.
        Assert.NotNull(claim);
        Assert.NotEqual(blocked, claim!.Ticket.Id);
    }

    [Fact]
    public async Task The_wait_clears_when_the_blocker_reaches_Done()
    {
        int blockerId, waitingId;
        await using (var ctx = NewCtx())
        {
            blockerId = (await Seed(ctx, TicketPhase.NeedsReview, "ALPHA-1")).Id;
            waitingId = (await Seed(ctx, TicketPhase.NeedsImplementation, "ALPHA-2")).Id;
            await Dep(ctx, waitingId, issueKey: "ALPHA-1");
        }

        await using (var ctx = NewCtx())
            Assert.Contains(waitingId, await new TicketDependencyService(ctx).BlockedTicketIdsAsync(new[] { waitingId }));

        await using (var ctx = NewCtx())
            await new TicketService(ctx).TransitionAsync(blockerId, TicketPhase.Done, "signed off", TransitionActor.Human);

        await using (var ctx = NewCtx())
            Assert.Empty(await new TicketDependencyService(ctx).BlockedTicketIdsAsync(new[] { waitingId }));
    }

    [Fact]
    public async Task An_external_blocker_is_judged_on_its_Jira_status_category()
    {
        int stillOpen, alreadyDone;
        await using (var ctx = NewCtx())
        {
            stillOpen = (await Seed(ctx, TicketPhase.NeedsImplementation, "ALPHA-2")).Id;
            alreadyDone = (await Seed(ctx, TicketPhase.NeedsImplementation, "ALPHA-3")).Id;
            // Neither blocker has a local ticket - this is the "linked to something we do not track" case.
            await Dep(ctx, stillOpen, issueKey: "ALPHA-900", category: "indeterminate");
            await Dep(ctx, alreadyDone, issueKey: "ALPHA-901", category: "done");
        }

        await using var ctx2 = NewCtx();
        var blocked = await new TicketDependencyService(ctx2).BlockedTicketIdsAsync(new[] { stillOpen, alreadyDone });
        Assert.Contains(stillOpen, blocked);
        Assert.DoesNotContain(alreadyDone, blocked);
    }

    [Fact]
    public async Task An_unknown_blocker_does_not_wedge_the_board()
    {
        // No local ticket and Jira never reached. Blocking here would let a Jira outage silently
        // stop every ticket that has ever been linked to anything.
        int id;
        await using (var ctx = NewCtx())
        {
            id = (await Seed(ctx, TicketPhase.NeedsImplementation, "ALPHA-2")).Id;
            await Dep(ctx, id, issueKey: "ALPHA-900", category: null);
        }

        await using var ctx2 = NewCtx();
        Assert.Empty(await new TicketDependencyService(ctx2).BlockedTicketIdsAsync(new[] { id }));

        var resolved = await new TicketDependencyService(ctx2).ResolveAsync(new[] { id });
        Assert.True(resolved[id][0].Unknown);   // reported, not hidden
    }

    // ---- The human outranks Jira ----------------------------------------------------

    [Fact]
    public async Task A_dismissed_Jira_block_stops_counting()
    {
        int id;
        await using (var ctx = NewCtx())
        {
            id = (await Seed(ctx, TicketPhase.NeedsImplementation, "ALPHA-2")).Id;
            await Dep(ctx, id, issueKey: "ALPHA-900", category: "new", over: false);
        }

        await using var ctx2 = NewCtx();
        Assert.Empty(await new TicketDependencyService(ctx2).BlockedTicketIdsAsync(new[] { id }));
    }

    [Fact]
    public async Task A_block_found_while_working_counts_even_though_Jira_never_heard_of_it()
    {
        int waitingId, blockerId;
        await using (var ctx = NewCtx())
        {
            blockerId = (await Seed(ctx, TicketPhase.Implementing)).Id;
            waitingId = (await Seed(ctx, TicketPhase.NeedsImplementation)).Id;
        }

        await using (var ctx = NewCtx())
        {
            var added = await new TicketDependencyService(ctx)
                .AddLocalAsync(waitingId, null, blockerId, "found while implementing - needs the new column first");
            Assert.NotNull(added);
            Assert.Equal(DependencyOrigin.Local, added!.Origin);
        }

        await using var ctx2 = NewCtx();
        Assert.Contains(waitingId, await new TicketDependencyService(ctx2).BlockedTicketIdsAsync(new[] { waitingId }));
    }

    [Fact]
    public async Task Re_adding_a_dismissed_block_turns_it_back_on_rather_than_duplicating_it()
    {
        int id; int depId;
        await using (var ctx = NewCtx())
        {
            id = (await Seed(ctx, TicketPhase.NeedsImplementation, "ALPHA-2")).Id;
            depId = (await Dep(ctx, id, issueKey: "ALPHA-900", category: "new", over: false)).Id;
        }

        await using (var ctx = NewCtx())
            await new TicketDependencyService(ctx).AddLocalAsync(id, "alpha-900", null, "actually this does block us");

        await using var read = NewCtx();
        var rows = await read.TicketDependencies.Where(d => d.TicketId == id).ToListAsync();
        Assert.Single(rows);                       // no second row for the same blocker
        Assert.Equal(depId, rows[0].Id);
        Assert.True(rows[0].Override);             // switched back on
        Assert.Contains(id, await new TicketDependencyService(read).BlockedTicketIdsAsync(new[] { id }));
    }

    [Fact]
    public async Task A_ticket_cannot_wait_on_itself()
    {
        int id;
        await using (var ctx = NewCtx()) id = (await Seed(ctx, TicketPhase.NeedsImplementation)).Id;
        await using var ctx2 = NewCtx();
        Assert.Null(await new TicketDependencyService(ctx2).AddLocalAsync(id, null, id, "oops"));
    }

    // ---- Resolution ------------------------------------------------------------------

    [Fact]
    public async Task A_blocker_key_starts_pointing_at_its_local_ticket_as_soon_as_one_exists()
    {
        // The dependency is recorded before anyone has made a local ticket for the blocker. Creating
        // it later must be enough on its own - there is no repair step to remember.
        int waitingId;
        await using (var ctx = NewCtx())
        {
            waitingId = (await Seed(ctx, TicketPhase.NeedsImplementation, "ALPHA-2")).Id;
            await Dep(ctx, waitingId, issueKey: "ALPHA-900", category: "new");
        }

        await using (var ctx = NewCtx())
        {
            var r = await new TicketDependencyService(ctx).ResolveAsync(new[] { waitingId });
            Assert.Null(r[waitingId][0].LocalTicketId);
        }

        int blockerId;
        await using (var ctx = NewCtx()) blockerId = (await Seed(ctx, TicketPhase.NeedsQa, "ALPHA-900")).Id;

        await using var ctx2 = NewCtx();
        var resolved = await new TicketDependencyService(ctx2).ResolveAsync(new[] { waitingId });
        Assert.Equal(blockerId, resolved[waitingId][0].LocalTicketId);
        Assert.Contains($"#{blockerId}", resolved[waitingId][0].Label);
        // And the local phase now decides, not the stale Jira category.
        Assert.False(resolved[waitingId][0].Satisfied);
    }

    [Fact]
    public async Task Claim_still_works_when_nothing_is_blocked()
    {
        await using (var ctx = NewCtx()) await Seed(ctx, TicketPhase.NeedsImplementation);
        await using var ctx2 = NewCtx();
        var claim = await new TicketService(ctx2, dependencies: new TicketDependencyService(ctx2))
            .ClaimNextAsync(null, AllSkillsExist);
        Assert.NotNull(claim);
        Assert.Equal(TicketPhase.Implementing, claim!.Ticket.Phase);
    }
}
