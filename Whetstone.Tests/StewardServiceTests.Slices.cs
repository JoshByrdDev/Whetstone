using Microsoft.EntityFrameworkCore;
using Whetstone.Models;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// The rest of docs/plans-as-slices.md: overrides declared when they are asked about, what is not
/// a decision, the charter, ideas, and a slice's story checked, kept true and driven.
/// </summary>
public partial class StewardServiceTests
{
    // ---- piece 2: overrides declared --------------------------------------------------------

    [Fact]
    public async Task A_question_says_what_it_touches_and_an_answer_replaces_only_what_the_owner_ticked()
    {
        await In(s => s.OwnerRecordsDecisionAsync("alpha", new StewardNewDecision("Texts come from one shared number.")));
        await In(s => s.OwnerRecordsDecisionAsync("alpha", new StewardNewDecision("A text names the business.")));
        await Begin();

        // Touches is required; an empty list is a claim too.
        Assert.Equal("bad-request", (await In(s => s.AskAsync("alpha", Question() with { Touches = null }))).Answer);
        Assert.Equal("bad-request", (await In(s => s.AskAsync("alpha", Question() with { Touches = new[] { 9 } }))).Answer);
        // An option can only change what the question touches.
        Assert.Equal("bad-request", (await In(s => s.AskAsync("alpha", Question() with
        {
            Touches = new[] { 1 },
            Changes = new Dictionary<string, IReadOnlyList<int>> { ["A new one"] = new[] { 2 } },
        }))).Answer);

        Assert.Equal("asked", (await In(s => s.AskAsync("alpha", Question() with
        {
            Touches = new[] { 1, 2 },
            Changes = new Dictionary<string, IReadOnlyList<int>> { ["A new one"] = new[] { 1 } },
        }))).Answer);

        // An answer cannot replace what the question did not touch.
        Assert.Equal("bad-request", (await In(s => s.OwnerAnswersAsync("alpha", "sms-provider", "A new one.", new[] { 7 }))).Answer);
        // Left to the option: picking "A new one" replaces #1 and leaves #2.
        Assert.Equal("recorded", (await In(s => s.OwnerAnswersAsync("alpha", "sms-provider", "A new one (Costs money).", null))).Answer);

        var answer = Register().Single(d => d.QuestionKey == "sms-provider");
        Assert.Equal(new[] { 1 }, answer.Replaces.Select(r => r.Number));
        var inForce = Json(await In(s => s.DecisionsAsync("alpha", null)));
        Assert.DoesNotContain("one shared number", inForce);
        Assert.Contains("names the business", inForce);
        // And the next run is told #1 stopped.
        await In(s => s.EndAsync("alpha", "asked", false));
        Assert.Contains($"\"changedDecisions\":[{{\"number\":{answer.Number}", Json(await Begin()));
    }

    [Fact]
    public async Task An_owner_can_answer_and_leave_every_touched_decision_standing()
    {
        await In(s => s.OwnerRecordsDecisionAsync("alpha", new StewardNewDecision("Texts come from one shared number.")));
        await Begin();
        await In(s => s.AskAsync("alpha", Question() with
        {
            Touches = new[] { 1 },
            Changes = new Dictionary<string, IReadOnlyList<int>> { ["A new one"] = new[] { 1 } },
        }));
        await In(s => s.OwnerAnswersAsync("alpha", "sms-provider", "A new one.", Array.Empty<int>()));
        Assert.Empty(Register().Single(d => d.QuestionKey == "sms-provider").Replaces);
        Assert.Contains("one shared number", Json(await In(s => s.DecisionsAsync("alpha", null))));
    }

    // ---- piece 3: not decisions -----------------------------------------------------------------

    [Fact]
    public async Task An_instruction_has_an_end_is_never_a_decision_and_is_closed_by_saying_how_it_ended()
    {
        Assert.Equal("bad-request", (await In(s => s.OwnerInstructsAsync("alpha", "Hold merges.", null))).Answer);
        Assert.Equal("recorded", (await In(s => s.OwnerInstructsAsync("alpha", "Hold merges.", "the flaky test is fixed"))).Answer);
        Assert.Empty(Register());

        var first = Json(await Begin());
        Assert.Contains("Hold merges. - until the flaky test is fixed", first);
        Assert.Contains("\"instructions\":[{\"Id\":1,\"Text\":\"Hold merges.\"", first);
        Assert.Equal("bad-request", (await In(s => s.CloseInstructionAsync("alpha", 1, ""))).Answer);
        Assert.Equal("closed", (await In(s => s.CloseInstructionAsync("alpha", 1, "Ticket 12 fixed it, merged at abc."))).Answer);
        await In(s => s.EndAsync("alpha", "closed it", true));

        var second = Json(await Begin());
        Assert.DoesNotContain("Hold merges", second);
        Assert.Equal("bad-request", (await In(s => s.OwnerClosesInstructionAsync("alpha", 1, null))).Answer);
    }

    [Fact]
    public async Task A_follow_up_waits_until_a_question_takes_it_up()
    {
        await In(s => s.OwnerRecordsDecisionAsync("alpha", new StewardNewDecision("A technician has a role of their own.")));
        await Begin();
        Assert.Equal("bad-request", (await In(s => s.FollowUpAsync("alpha", 9, "x"))).Answer);
        Assert.Equal("noted", (await In(s => s.FollowUpAsync("alpha", 1, "Let's talk about a dynamic role."))).Answer);
        Assert.Contains("dynamic role", Json(await In(s => s.StatusAsync("alpha"))) + Json(await In(s => s.RegisterViewAsync("alpha"))));

        Assert.Equal("asked", (await In(s => s.AskAsync("alpha", Question("roles") with { FollowUps = new[] { 1 } }))).Answer);
        await using var ctx = NewCtx();
        Assert.Equal("question:roles", (await ctx.StewardFollowUps.SingleAsync()).CitedBy);
        Assert.Equal("bad-request", (await In(s => s.AskAsync("alpha", Question("roles-again") with { FollowUps = new[] { 1 } }))).Answer);
    }

    [Fact]
    public async Task A_ruling_that_reads_a_decision_which_no_longer_holds_is_flagged()
    {
        await In(s => s.OwnerRecordsDecisionAsync("alpha", new StewardNewDecision("Everyone in the team sees the same schedule.")));
        await Begin();
        Assert.Equal("bad-request", (await In(s => s.RuleAsync("alpha", "x", "y", null, new[] { 5 }))).Answer);
        Assert.Equal("ruled", (await In(s => s.RuleAsync("alpha", "A screen ticket shows the whole team's day.", "#1", null, new[] { 1 }))).Answer);
        Assert.DoesNotContain("was replaced by", Json(await In(s => s.StatusAsync("alpha"))));

        await In(s => s.OwnerRecordsDecisionAsync("alpha", new StewardNewDecision("Technicians see only their own visits.",
            Replaces: new[] { new StewardReplacing(1, "Roles.") })));
        Assert.Contains("\"flagged\":\"#1 was replaced by #2\"", Json(await In(s => s.StatusAsync("alpha"))));
        await In(s => s.EndAsync("alpha", "x", false));
        Assert.Contains("\"flaggedRulings\":[{\"Id\":", Json(await Begin()));

        // Kept by the owner: it now reads what replaced the dead decision, and the flag is gone.
        var id = Entries().Single(e => e.Kind == StewardEntryKind.Ruling).Id;
        Assert.Equal("kept", (await In(s => s.OwnerKeepsRulingAsync("alpha", id))).Answer);
        Assert.Equal("2", Entries().Single(e => e.Id == id).Reads);
        Assert.DoesNotContain("was replaced by", Json(await In(s => s.StatusAsync("alpha"))));
    }

    [Fact]
    public async Task A_provisional_decision_comes_due_when_its_unit_is_built_and_the_owner_confirms_it()
    {
        await In(s => s.OwnerRecordsDecisionAsync("alpha", new StewardNewDecision("Texts are free for now.", Provisional: true, ProvisionalUnit: 1)));
        Assert.Contains("\"due\":[]", Json(await In(s => s.RegisterViewAsync("alpha"))));
        await using (var ctx = NewCtx())
        {
            ctx.StewardOutcomes.AddRange(
                new StewardOutcome { ProjectKey = "alpha", Plan = "", Unit = 1, Outcome = "A1", State = StewardOutcomeState.Done },
                new StewardOutcome { ProjectKey = "alpha", Plan = "", Unit = 1, Outcome = "A2", State = StewardOutcomeState.Done });
            await ctx.SaveChangesAsync();
        }
        Assert.Contains("\"due\":[1]", Json(await In(s => s.RegisterViewAsync("alpha"))));
        Assert.Contains("\"provisionalDue\":[{\"number\":1", Json(await Begin()));
        Assert.Equal("confirmed", (await In(s => s.OwnerConfirmsDecisionAsync("alpha", 1))).Answer);
        Assert.Contains("\"status\":\"inForce\"", Json(await In(s => s.DecisionsAsync("alpha", null))));
        Assert.Equal("bad-request", (await In(s => s.OwnerConfirmsDecisionAsync("alpha", 1))).Answer);
    }
}
