using System.Text.Json;
using Whetstone.Api;
using Whetstone.Mcp;
using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// The per-ticket human review gates. Both flags default off, so the pipeline hands a ticket
/// straight past NeedsApproval / NeedsImplementationReview unless it explicitly asks for that
/// review. The final NeedsReview sign-off is never skipped — see the last two tests.
/// </summary>
public class TicketServiceGateTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<DashboardDbContext> _options;

    public TicketServiceGateTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _options = new DbContextOptionsBuilder<DashboardDbContext>().UseSqlite(_conn).Options;
        using var ctx = new DashboardDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _conn.Dispose();

    private DashboardDbContext NewCtx() => new(_options);

    private async Task<int> Seed(TicketPhase phase, bool spec = false, bool impl = false)
    {
        await using var ctx = NewCtx();
        var t = new Ticket
        {
            ProjectKey = "alpha",
            Title = "t",
            Phase = phase,
            RequiresSpeculationReview = spec,
            RequiresImplementationReview = impl,
        };
        ctx.Tickets.Add(t);
        await ctx.SaveChangesAsync();
        return t.Id;
    }

    // ---- Speculation gate (NeedsApproval) ----

    [Fact]
    public async Task Speculation_review_off_skips_straight_to_implementation()
    {
        var id = await Seed(TicketPhase.Speculating);

        await using var ctx = NewCtx();
        var t = await new TicketService(ctx).TransitionAsync(id, TicketPhase.NeedsApproval, null);

        Assert.Equal(TicketPhase.NeedsImplementation, t!.Phase);
    }

    [Fact]
    public async Task Speculation_review_on_stops_at_the_approval_gate()
    {
        var id = await Seed(TicketPhase.Speculating, spec: true);

        await using var ctx = NewCtx();
        var t = await new TicketService(ctx).TransitionAsync(id, TicketPhase.NeedsApproval, null);

        Assert.Equal(TicketPhase.NeedsApproval, t!.Phase);
    }

    // ---- Expansion items hold the speculation gate ----

    private async Task AddItems(int id, params (string Text, WorkItemScope Scope, bool Done)[] items)
    {
        await using var ctx = NewCtx();
        var svc = new TicketService(ctx);
        foreach (var (text, scope, done) in items)
        {
            var wi = await svc.AddWorkItemAsync(id, text, scope);
            if (done) await svc.UpdateWorkItemAsync(wi!.Id, null, true, null);
        }
    }

    [Fact]
    public async Task An_expansion_item_stops_at_approval_even_with_speculation_review_off()
    {
        var id = await Seed(TicketPhase.Speculating);
        await AddItems(id, ("fix it", WorkItemScope.Required, false), ("second cheque", WorkItemScope.Expansion, false));

        await using var ctx = NewCtx();
        var t = await new TicketService(ctx).TransitionAsync(id, TicketPhase.NeedsApproval, null);

        Assert.Equal(TicketPhase.NeedsApproval, t!.Phase);
        Assert.True(WorkItemScopes.HeldForExpansion(t));
        Assert.Equal(1, TicketMapper.ToSummary(t).HeldForExpansion);
        // The trail says why, since the ticket's own setting says it would not have stopped.
        Assert.Contains(t.Comments, c => c.Author == AuthorKind.System && c.Body.StartsWith("Held for approval") && c.Body.Contains("1 expansion item "));
    }

    [Fact]
    public async Task Consequence_items_and_built_expansion_items_do_not_hold_the_gate()
    {
        var id = await Seed(TicketPhase.Speculating);
        await AddItems(id, ("scope to live sources", WorkItemScope.Consequence, false), ("already shipped", WorkItemScope.Expansion, true));

        await using var ctx = NewCtx();
        var t = await new TicketService(ctx).TransitionAsync(id, TicketPhase.NeedsApproval, null);

        Assert.Equal(TicketPhase.NeedsImplementation, t!.Phase);
    }

    [Fact]
    public async Task A_ticket_that_wants_review_anyway_is_not_reported_as_held_for_expansion()
    {
        var id = await Seed(TicketPhase.Speculating, spec: true);
        await AddItems(id, ("second cheque", WorkItemScope.Expansion, false));

        await using var ctx = NewCtx();
        var t = await new TicketService(ctx).TransitionAsync(id, TicketPhase.NeedsApproval, null);

        Assert.Equal(TicketPhase.NeedsApproval, t!.Phase);
        Assert.False(WorkItemScopes.HeldForExpansion(t));
        Assert.DoesNotContain(t.Comments, c => c.Body.StartsWith("Held for approval"));
    }

    [Fact]
    public async Task Add_work_items_refuses_a_missing_or_unknown_scope_without_writing_anything()
    {
        var id = await Seed(TicketPhase.Speculating);

        await using (var ctx = NewCtx())
        {
            var tools = new TicketService(ctx);
            Assert.Contains("one scope per item", JsonSerializer.Serialize(await TicketTools.AddWorkItems(tools, id, new[] { "a", "b" }, new[] { "required" })));
            Assert.Contains("not one of", JsonSerializer.Serialize(await TicketTools.AddWorkItems(tools, id, new[] { "a", "b" }, new[] { "required", "nice-to-have" })));
            await TicketTools.AddWorkItems(tools, id, new[] { "c" }, new[] { "Expansion" });
        }

        await using var check = NewCtx();
        var items = check.WorkItems.Where(w => w.TicketId == id).ToList();
        Assert.Single(items);
        Assert.Equal(WorkItemScope.Expansion, items[0].Scope);
    }

    // ---- Implementation gate (NeedsImplementationReview, before QA) ----

    [Fact]
    public async Task Implementation_review_off_goes_straight_on_to_qa()
    {
        var id = await Seed(TicketPhase.Implementing);

        await using var ctx = NewCtx();
        var t = await new TicketService(ctx).TransitionAsync(id, TicketPhase.NeedsImplementationReview, null);

        Assert.Equal(TicketPhase.NeedsQa, t!.Phase);
    }

    [Fact]
    public async Task Implementation_review_on_stops_before_qa()
    {
        var id = await Seed(TicketPhase.Implementing, impl: true);

        await using var ctx = NewCtx();
        var t = await new TicketService(ctx).TransitionAsync(id, TicketPhase.NeedsImplementationReview, null);

        Assert.Equal(TicketPhase.NeedsImplementationReview, t!.Phase);
    }

    // ---- The final sign-off is always a human's ----

    [Fact]
    public async Task Refinement_always_stops_at_the_final_review_even_with_both_gates_off()
    {
        var id = await Seed(TicketPhase.Refining);

        await using var ctx = NewCtx();
        var t = await new TicketService(ctx).TransitionAsync(id, TicketPhase.NeedsReview, null);

        Assert.Equal(TicketPhase.NeedsReview, t!.Phase);
    }

    // ---- The gates are independent ----

    [Fact]
    public async Task Each_gate_is_governed_only_by_its_own_flag()
    {
        // Wants the speculation review but not the implementation one.
        var id = await Seed(TicketPhase.Implementing, spec: true, impl: false);

        await using var ctx = NewCtx();
        var t = await new TicketService(ctx).TransitionAsync(id, TicketPhase.NeedsImplementationReview, null);

        Assert.Equal(TicketPhase.NeedsQa, t!.Phase);
    }

    // ---- Only the automated hand-off skips ----

    [Fact]
    public async Task Unblocking_onto_a_gate_parks_the_ticket_there()
    {
        // A human deliberately unblocking into NeedsApproval must stay put, even with the
        // review flag off — only the Speculating -> NeedsApproval hand-off auto-skips.
        var id = await Seed(TicketPhase.Blocked);

        await using var ctx = NewCtx();
        var t = await new TicketService(ctx).TransitionAsync(id, TicketPhase.NeedsApproval, null);

        Assert.Equal(TicketPhase.NeedsApproval, t!.Phase);
    }

    // ---- The skip is recorded ----

    [Fact]
    public async Task Skipping_a_gate_leaves_both_hops_in_the_timeline()
    {
        var id = await Seed(TicketPhase.Speculating);

        await using (var ctx = NewCtx())
            await new TicketService(ctx).TransitionAsync(id, TicketPhase.NeedsApproval, null);

        await using var check = NewCtx();
        var bodies = check.Comments.Where(c => c.TicketId == id).Select(c => c.Body).ToList();

        Assert.Contains(bodies, b => b.Contains("Speculating") && b.Contains("NeedsApproval"));
        Assert.Contains(bodies, b => b.Contains("NeedsApproval") && b.Contains("NeedsImplementation")
                                     && b.Contains("speculation review not required"));
    }

    [Fact]
    public async Task Gates_default_to_off_on_a_new_ticket()
    {
        await using var ctx = NewCtx();
        var t = await new TicketService(ctx).CreateAsync("alpha", "t", null, null, null, TicketPhase.Backlog);

        Assert.False(t.RequiresSpeculationReview);
        Assert.False(t.RequiresImplementationReview);
        // Full QA is the default: a ticket nobody thought about gets tested, not waved through.
        Assert.False(t.SkipQaTesting);
    }

    // ---- Code review only: not a gate, but set the same way ----

    [Fact]
    public async Task Code_review_only_survives_a_create_and_an_update()
    {
        await using var ctx = NewCtx();
        var svc = new TicketService(ctx);

        var t = await svc.CreateAsync("alpha", "t", null, null, null, TicketPhase.Backlog, skipQaTesting: true);
        Assert.True(t.SkipQaTesting);

        var off = await svc.UpdateAsync(t.Id, null, null, null, null, null, null, skipQaTesting: false);
        Assert.False(off!.SkipQaTesting);

        // Omitting it leaves it alone, like the gates — an unrelated edit must not silently
        // re-enable a full QA pass on a ticket the human deliberately excused.
        var back = await svc.UpdateAsync(t.Id, null, null, null, null, null, null, skipQaTesting: true);
        var untouched = await svc.UpdateAsync(t.Id, "renamed", null, null, null, null, null);
        Assert.True(back!.SkipQaTesting);
        Assert.True(untouched!.SkipQaTesting);
    }

    /// <summary>
    /// The flag's actual teeth. Authoring QA items is behaviour no skill file ever asked for -
    /// speculation and QA both do it because the tool is in front of them - so a skill politely
    /// asking them to stop would be exactly as reliable as the prose that never asked them to start.
    /// </summary>
    [Fact]
    public async Task Add_qa_items_is_refused_on_a_code_review_only_ticket()
    {
        var id = await Seed(TicketPhase.Speculating);
        await using (var seed = NewCtx())
        {
            (await seed.Tickets.FindAsync(id))!.SkipQaTesting = true;
            await seed.SaveChangesAsync();
        }

        await using var ctx = NewCtx();
        var result = await TicketTools.AddQaItems(new TicketService(ctx), id, new[] { "Log in and click the thing" });

        var error = result.GetType().GetProperty("error")?.GetValue(result) as string;
        Assert.NotNull(error);
        Assert.Contains("code-review-only", error);
        Assert.Empty(ctx.QaItems.Where(q => q.TicketId == id));
    }

    /// <summary>
    /// And it is refused only there. Every ordinary ticket must keep being able to grow a checklist -
    /// this guard sits on the one path QA and speculation both use to write one.
    /// </summary>
    [Fact]
    public async Task Add_qa_items_still_works_on_an_ordinary_ticket()
    {
        var id = await Seed(TicketPhase.QaTesting);

        await using var ctx = NewCtx();
        await TicketTools.AddQaItems(new TicketService(ctx), id, new[] { "Log in and click the thing" });

        Assert.Single(ctx.QaItems.Where(q => q.TicketId == id));
    }

    /// <summary>
    /// The human's own route stays open. They set the flag; if they then type an item by hand they
    /// meant it, and the refusal above is aimed at agents improvising, not at the ticket's owner.
    /// </summary>
    [Fact]
    public async Task A_human_can_still_add_a_qa_item_to_a_code_review_only_ticket()
    {
        var id = await Seed(TicketPhase.QaTesting);
        await using (var seed = NewCtx())
        {
            (await seed.Tickets.FindAsync(id))!.SkipQaTesting = true;
            await seed.SaveChangesAsync();
        }

        await using var ctx = NewCtx();
        var added = await new TicketService(ctx).AddQaItemAsync(id, "I want this one checked anyway");

        Assert.NotNull(added);
    }

    /// <summary>
    /// It removes work inside QA; it does not move the ticket differently. The QA phase still runs
    /// (for the code review), so every hop either side of it is unchanged.
    /// </summary>
    [Fact]
    public async Task Code_review_only_does_not_change_where_the_ticket_goes()
    {
        var id = await Seed(TicketPhase.Implementing);
        await using (var seed = NewCtx())
        {
            var t = await seed.Tickets.FindAsync(id);
            t!.SkipQaTesting = true;
            await seed.SaveChangesAsync();
        }

        await using var ctx = NewCtx();
        var moved = await new TicketService(ctx).TransitionAsync(id, TicketPhase.NeedsImplementationReview, null);

        Assert.Equal(TicketPhase.NeedsQa, moved!.Phase);
    }
}
