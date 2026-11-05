using Microsoft.EntityFrameworkCore;
using Whetstone.Models;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// The two review gates a steward passes in the owner's place: a plan waiting for its yes, and a
/// build waiting for a look before QA. What is pinned is where the steward may act - a gate a
/// hand-off stopped at, never one the owner put a ticket on by hand - and that an expansion item
/// is either declined out of the plan or approved with it, on the record either way.
/// </summary>
public partial class StewardServiceTests
{
    private const string WhyItHolds =
        "Every work item is one the ticket's acceptance criteria name, and the expansion is covered by the slice's outcome A1.";

    /// <summary>A ticket a worker just finished planning, with one expansion item; returns (ticket, expansion item).</summary>
    private async Task<(int Id, int Expansion)> PlannedWithExpansion()
    {
        int id, exp;
        await using (var ctx = NewCtx())
        {
            var t = new Ticket { ProjectKey = "alpha", Title = "A planned change", Phase = TicketPhase.Speculating };
            t.WorkItems.Add(new WorkItem { Text = "The change itself", Scope = WorkItemScope.Required, Order = 0 });
            t.WorkItems.Add(new WorkItem { Text = "A settings screen nobody asked for", Scope = WorkItemScope.Expansion, Order = 1 });
            ctx.Tickets.Add(t);
            await ctx.SaveChangesAsync();
            id = t.Id;
            exp = t.WorkItems.Single(w => w.Scope == WorkItemScope.Expansion).Id;
            await new TicketService(ctx).TransitionAsync(id, TicketPhase.NeedsApproval, null, TransitionActor.Worker);
        }
        return (id, exp);
    }

    private async Task<int> BuiltForALook()
    {
        await using var ctx = NewCtx();
        var t = new Ticket { ProjectKey = "alpha", Title = "A risky change", Phase = TicketPhase.Implementing, RequiresImplementationReview = true };
        ctx.Tickets.Add(t);
        await ctx.SaveChangesAsync();
        await new TicketService(ctx).TransitionAsync(t.Id, TicketPhase.NeedsImplementationReview, null, TransitionActor.Worker);
        return t.Id;
    }

    [Fact]
    public async Task A_ticket_held_at_its_plan_by_an_expansion_item_owes_the_steward_a_run()
    {
        var (id, _) = await PlannedWithExpansion();
        Assert.Equal(TicketPhase.NeedsApproval, (await Reload(id)).Phase);
        Assert.Contains($"#{id} waits at its speculation gate", (await State())?.DueReason);
    }

    [Fact]
    public async Task A_ticket_that_skips_its_gate_owes_no_run()
    {
        await using (var ctx = NewCtx())
        {
            var t = new Ticket { ProjectKey = "alpha", Title = "Nothing to look at", Phase = TicketPhase.Speculating };
            ctx.Tickets.Add(t);
            await ctx.SaveChangesAsync();
            await new TicketService(ctx).TransitionAsync(t.Id, TicketPhase.NeedsApproval, null, TransitionActor.Worker);
            Assert.Equal(TicketPhase.NeedsImplementation, (await new TicketService(ctx).GetAsync(t.Id))!.Phase);
        }
        Assert.DoesNotContain("gate", (await State())?.DueReason ?? "");
    }

    [Fact]
    public async Task Declining_an_expansion_item_takes_it_out_of_the_plan_and_builds_the_rest()
    {
        var (id, exp) = await PlannedWithExpansion();
        await Begin();
        var r = await In(s => s.ApproveAsync("alpha", id, WhyItHolds, new[] { exp }));
        Assert.Equal("approved", r.Answer);

        var t = await Reload(id);
        Assert.Equal(TicketPhase.NeedsImplementation, t.Phase);
        Assert.DoesNotContain(t.WorkItems, w => w.Scope == WorkItemScope.Expansion);
        Assert.Single(t.WorkItems);
        Assert.Contains(t.Comments, c => c.Author == AuthorKind.Steward && c.Body.Contains("declined expansion"));
        // On the record the owner reads back and can reverse.
        Assert.Contains(Entries(), e => e.Kind == StewardEntryKind.Call && e.TicketId == id && e.Text.Contains("declined expansion"));
    }

    [Fact]
    public async Task Approving_with_an_expansion_item_left_in_builds_it_too()
    {
        var (id, exp) = await PlannedWithExpansion();
        await Begin();
        Assert.Equal("approved", (await In(s => s.ApproveAsync("alpha", id, WhyItHolds))).Answer);
        var t = await Reload(id);
        Assert.Equal(TicketPhase.NeedsImplementation, t.Phase);
        Assert.Contains(t.WorkItems, w => w.Id == exp);
        Assert.Contains(Entries(), e => e.Kind == StewardEntryKind.Call && e.Text.Contains("approved expansion"));
    }

    [Fact]
    public async Task Only_an_open_expansion_item_is_declined_and_only_at_the_plan()
    {
        var (id, _) = await PlannedWithExpansion();
        var required = (await Reload(id)).WorkItems.Single(w => w.Scope == WorkItemScope.Required).Id;
        await Begin();
        Assert.Equal("bad-request", (await In(s => s.ApproveAsync("alpha", id, WhyItHolds, new[] { required }))).Answer);
        Assert.Equal("bad-request", (await In(s => s.ApproveAsync("alpha", id, "too short"))).Answer);

        var built = await BuiltForALook();
        Assert.Equal("bad-request", (await In(s => s.ApproveAsync("alpha", built, WhyItHolds, new[] { required }))).Answer);
        Assert.Equal(TicketPhase.NeedsApproval, (await Reload(id)).Phase);
    }

    [Fact]
    public async Task A_build_stopped_for_a_look_is_approved_on_to_qa_or_sent_back()
    {
        var id = await BuiltForALook();
        Assert.Contains($"#{id} waits at its implementation gate", (await State())?.DueReason);
        await Begin();
        Assert.Equal("approved", (await In(s => s.ApproveAsync("alpha", id, WhyItHolds))).Answer);
        Assert.Equal(TicketPhase.NeedsQa, (await Reload(id)).Phase);

        var other = await BuiltForALook();
        Assert.Equal("refused", (await In(s => s.SendBackAsync("alpha", other, "NeedsRefinement", WhatIsWrong))).Answer);
        Assert.Equal("sent-back", (await In(s => s.SendBackAsync("alpha", other, "NeedsImplementation", WhatIsWrong))).Answer);
        Assert.Equal(TicketPhase.NeedsImplementation, (await Reload(other)).Phase);
    }

    [Fact]
    public async Task A_plan_sent_back_from_its_gate_is_replanned_and_stops_there_again()
    {
        var (id, _) = await PlannedWithExpansion();
        await Begin();
        Assert.Equal("refused", (await In(s => s.SendBackAsync("alpha", id, "NeedsImplementation", WhatIsWrong))).Answer);
        Assert.Equal("sent-back", (await In(s => s.SendBackAsync("alpha", id, "NeedsSpeculation", WhatIsWrong))).Answer);
        var t = await Reload(id);
        Assert.Equal(TicketPhase.NeedsSpeculation, t.Phase);
        Assert.True(t.RequiresSpeculationReview);
    }

    [Fact]
    public async Task A_gate_the_owner_put_a_ticket_on_by_hand_is_theirs()
    {
        int id;
        await using (var ctx = NewCtx())
        {
            var t = new Ticket { ProjectKey = "alpha", Title = "The owner wants to look", Phase = TicketPhase.Blocked, PhaseBeforeBlock = TicketPhase.NeedsApproval };
            ctx.Tickets.Add(t);
            await ctx.SaveChangesAsync();
            id = t.Id;
            await new TicketService(ctx).TransitionAsync(id, TicketPhase.NeedsApproval, "I'll read this one", TransitionActor.Human);
        }
        await Begin();
        Assert.Equal("owner", (await In(s => s.ApproveAsync("alpha", id, WhyItHolds))).Answer);
        Assert.Equal("owner", (await In(s => s.SendBackAsync("alpha", id, "NeedsSpeculation", WhatIsWrong))).Answer);
        Assert.Equal(TicketPhase.NeedsApproval, (await Reload(id)).Phase);
    }

    [Fact]
    public async Task Only_a_ticket_at_a_gate_is_approved_and_propose_mode_only_says_so()
    {
        var atReview = await AtReview();
        await Begin();
        Assert.Equal("not-at-a-gate", (await In(s => s.ApproveAsync("alpha", atReview, WhyItHolds))).Answer);
        await In(s => s.EndAsync("alpha", "nothing", false));

        Configure("propose");
        var (id, exp) = await PlannedWithExpansion();
        await Begin();
        var r = await In(s => s.ApproveAsync("alpha", id, WhyItHolds, new[] { exp }));
        Assert.Equal("proposed", r.Answer);
        var t = await Reload(id);
        Assert.Equal(TicketPhase.NeedsApproval, t.Phase);
        Assert.Contains(t.WorkItems, w => w.Id == exp);
    }

    [Fact]
    public async Task The_steward_can_file_or_amend_a_ticket_to_stop_at_either_gate()
    {
        await Begin();
        Assert.Equal("created", (await In(s => s.CreateAsync("alpha", Draft() with { ImplementationReview = true }))).Answer);
        int id;
        await using (var ctx = NewCtx())
        {
            var t = ctx.Tickets.Single();
            id = t.Id;
            Assert.True(t.RequiresImplementationReview);
            Assert.False(t.RequiresSpeculationReview);
        }

        Assert.Equal("amended", (await In(s => s.AmendAsync("alpha", id, null, null, null, null, planReview: true, implementationReview: false))).Answer);
        var after = await Reload(id);
        Assert.True(after.RequiresSpeculationReview);
        Assert.False(after.RequiresImplementationReview);
    }

    [Fact]
    public async Task The_status_says_what_a_ticket_at_a_gate_waits_for()
    {
        var (id, _) = await PlannedWithExpansion();
        Assert.Contains("only because of 1 expansion item", StewardService.GateNote(await Reload(id)));
        Assert.Null(StewardService.GateNote(await Reload(await AtReview())));
    }
}
