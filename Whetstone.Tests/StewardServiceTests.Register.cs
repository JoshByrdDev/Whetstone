using Microsoft.EntityFrameworkCore;
using Whetstone.Models;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// The register of decisions: one number per decision for good, one status each, an override kept
/// only on the newer decision, and an import that takes in what the project kept before without
/// counting a decision twice or assuming that one replaces another.
/// </summary>
public partial class StewardServiceTests
{
    private List<StewardDecision> Register()
    {
        using var ctx = NewCtx();
        return ctx.StewardDecisions.Include(d => d.Replaces).AsNoTracking().OrderBy(d => d.Number).ToList();
    }

    [Fact]
    public async Task Every_decision_gets_the_next_number_and_numbers_a_spec_has_given_are_kept_free()
    {
        _git.Second = WithDecisions;   // leaves #4, #5 and #6 open
        await Begin();
        await In(s => s.AskAsync("alpha", Question()));
        await In(s => s.AskAsync("alpha", Question("map-provider")));

        await In(s => s.OwnerAnswersAsync("alpha", "sms-provider", "The one we already pay for."));
        await In(s => s.OwnerSaysAsync("alpha", "A ticket carries one whole outcome."));
        Assert.Equal("recorded", (await In(s => s.OwnerRecordsDecisionAsync("alpha", new StewardNewDecision("Texts go out from one number.", Convention: true)))).Answer);
        // #4 to #6 are the spec's, waiting to be settled under those numbers.
        Assert.Equal(new[] { 7, 8, 9 }, Register().Select(d => d.Number));

        // Settling one keeps its number, and the spec is still where its words are written.
        Assert.Equal("decided", (await In(s => s.OwnerDecidesAsync("alpha", 2, new[] { new UnitDecisionChoice(4, "a") }, false, null))).Answer);
        var four = Register().Single(d => d.Number == 4);
        Assert.Equal(StewardDecisionSource.Slice, four.Source);
        Assert.Equal(2, four.PlanUnit);
        Assert.Equal("How long an invite lasts", four.Title);
        Assert.Equal("(a) 14 days.", four.Rule);
        Assert.Contains("- **(#4) How long an invite lasts.** Decided by the owner", _git.Committed.Single(c => c.Path == "docs/units/02-second.md").Content);

        // A number is never reused: the next answer goes after the highest.
        await In(s => s.OwnerAnswersAsync("alpha", "map-provider", "A new one."));
        Assert.Equal(10, Register().Single(d => d.QuestionKey == "map-provider").Number);

        // A draft may not leave a decision open under a number the register holds.
        Assert.Equal("number-taken", (await In(s => s.DraftUnitAsync("alpha", "02-second.md",
            "> **Status: draft.**\n\n## Story\n\nOne thing happens (A1).\n\n- **A1.** One.\n\n## To decide\n\n- **(#7) Again.** Options: (a) x; (b) y. **Pick: (a)**\n"))).Answer);
        var next = Json(await In(s => s.DecisionsAsync("alpha", null)));
        Assert.Contains("\"next\":11", next);
    }

    [Fact]
    public async Task A_replaced_decision_is_history_and_comes_back_only_when_asked_for_with_what_replaced_it()
    {
        await In(s => s.OwnerRecordsDecisionAsync("alpha", new StewardNewDecision("Everyone in the team sees the same schedule.", Convention: true)));
        Assert.Equal("bad-request", (await In(s => s.OwnerRecordsDecisionAsync("alpha",
            new StewardNewDecision("Technicians see only their own visits.", Replaces: new[] { new StewardReplacing(1, null) }, Convention: true)))).Answer);   // say why
        Assert.Equal("recorded", (await In(s => s.OwnerRecordsDecisionAsync("alpha",
            new StewardNewDecision("Technicians see only their own visits.", Replaces: new[] { new StewardReplacing(1, "Technicians have a role of their own now.") }, Convention: true)))).Answer);

        // Stored once, on the newer decision; the older one's row is untouched.
        var reg = Register();
        Assert.Empty(reg.Single(d => d.Number == 1).Replaces);
        Assert.Equal(1, reg.Single(d => d.Number == 2).Replaces.Single().Number);

        var inForce = Json(await In(s => s.DecisionsAsync("alpha", null)));
        Assert.DoesNotContain("the same schedule", inForce);
        Assert.Contains("their own visits", inForce);

        var history = Json(await In(s => s.DecisionsAsync("alpha", null, history: true)));
        Assert.Contains("\"number\":1,\"rule\":\"Everyone in the team sees the same schedule.\"", history);
        Assert.Contains("\"status\":\"replaced\"", history);
        Assert.Contains("\"replacedBy\":[{\"number\":2,\"why\":\"Technicians have a role of their own now.\"}]", history);

        // What is already replaced is not replaced again; the one in force is.
        Assert.Equal("bad-request", (await In(s => s.OwnerRecordsDecisionAsync("alpha",
            new StewardNewDecision("x", Replaces: new[] { new StewardReplacing(1, "again") }, Convention: true)))).Answer);
        Assert.Equal("withdrawn", (await In(s => s.OwnerWithdrawsDecisionAsync("alpha", 2, "The roles are on hold."))).Answer);
        Assert.DoesNotContain("their own visits", Json(await In(s => s.DecisionsAsync("alpha", null))));
        Assert.Contains("\"status\":\"withdrawn\"", Json(await In(s => s.DecisionsAsync("alpha", null, history: true))));
    }

    [Fact]
    public async Task The_owners_changes_to_the_register_are_news_to_the_next_run_once()
    {
        await In(s => s.OwnerRecordsDecisionAsync("alpha", new StewardNewDecision("Invites last 30 days.", Convention: true)));
        var first = System.Text.Json.JsonSerializer.Serialize((await Begin()).Data);
        Assert.Contains("\"changedDecisions\":[{\"number\":1,\"rule\":\"Invites last 30 days.\"", first);
        await In(s => s.EndAsync("alpha", "applied it", true));
        Assert.Contains("\"changedDecisions\":[]", System.Text.Json.JsonSerializer.Serialize((await Begin()).Data));
    }

    [Fact]
    public async Task Decisions_can_be_narrowed_to_a_slice_or_one_of_its_outcomes()
    {
        await In(s => s.OwnerRecordsDecisionAsync("alpha", new StewardNewDecision("Project-wide: prices never differ.", Convention: true)));
        await In(s => s.OwnerRecordsDecisionAsync("alpha", new StewardNewDecision("Unit 1 whole: a dog has a name.", Unit: 1)));
        await In(s => s.OwnerRecordsDecisionAsync("alpha", new StewardNewDecision("Unit 1 A1 only: the form is short.", Unit: 1, Outcomes: new[] { "A1" })));
        await In(s => s.OwnerRecordsDecisionAsync("alpha", new StewardNewDecision("Unit 2: invites last a month.", Unit: 2)));
        Assert.Equal("bad-request", (await In(s => s.OwnerRecordsDecisionAsync("alpha", new StewardNewDecision("x", Unit: 1, Outcomes: new[] { "Z9" })))).Answer);

        var a2 = Json(await In(s => s.DecisionsAsync("alpha", null, unit: 1, outcome: "A2")));
        Assert.Contains("prices never differ", a2);
        Assert.Contains("a dog has a name", a2);
        Assert.DoesNotContain("the form is short", a2);
        Assert.DoesNotContain("invites last a month", a2);
        Assert.Contains("the form is short", Json(await In(s => s.DecisionsAsync("alpha", null, unit: 1, outcome: "A1"))));
        Assert.Equal("bad-request", (await In(s => s.DecisionsAsync("alpha", null, outcome: "A1"))).Answer);
        var search = Json(await In(s => s.DecisionsAsync("alpha", "month")));
        Assert.Contains("\"count\":1", search);
    }

    private const string DecidedSpec = """
        # Unit 2: the second thing

        > **Status: agreed 2026-03-01.**

        - **A1.** One.

        ## Decided

        - **(#4) How long an invite lasts.** Decided by the owner 2026-03-04: (b) 30 days.

        - **#5 Who sends it** (2026-03-05, asked by the steward). The business sends it from its own address.

        Carried in from the owner's answer `sender-address` (2026-03-06): a reply goes to the business, not to us.

        Nothing else is decided yet.

        ## Done means

        It works.
        """;

    [Fact]
    public async Task The_decisions_file_lists_what_is_in_force_conventions_then_each_slice_then_provisional()
    {
        Configure("full", """ "slices": "docs/units", "decisionsFile": "docs/decisions.md" """);
        await In(s => s.OwnerRecordsDecisionAsync("alpha", new StewardNewDecision("Prices never differ.", Convention: true)));
        await In(s => s.OwnerRecordsDecisionAsync("alpha", new StewardNewDecision("A dog has a name.", Unit: 1)));
        await In(s => s.OwnerRecordsDecisionAsync("alpha", new StewardNewDecision("Texts are free for now.", Provisional: true, ProvisionalUnit: 2, Convention: true)));
        await In(s => s.OwnerRecordsDecisionAsync("alpha", new StewardNewDecision("Prices may differ by a dollar.", Convention: true,
            Replaces: new[] { new StewardReplacing(1, "Card fees.") })));

        var text = _git.Committed[^1].Content;
        var conventions = text.IndexOf("## In force: conventions");
        var unit = text.IndexOf("## In force: slice 1");
        var prov = text.IndexOf("## Provisional");
        Assert.True(conventions >= 0 && conventions < unit && unit < prov, text);
        Assert.Contains("- **#4** Prices may differ by a dollar.", text);
        Assert.Contains("Replaces #1 (Card fees).", text);
        Assert.Contains("Until slice 2 is built.", text);
        // What is replaced is not written again: the file holds what is in force.
        Assert.DoesNotContain("Prices never differ.", text);
        Assert.DoesNotContain("## History", text);
    }

    [Fact]
    public void A_specs_decided_section_reads_as_decisions()
    {
        var items = DecisionText.Decided(DecidedSpec);
        Assert.Equal(new int?[] { 4, 5, null }, items.Select(i => i.Number));
        Assert.Equal("How long an invite lasts", items[0].Title);
        Assert.Equal(new[] { "sender-address" }, items[2].CitedKeys);
    }
}
