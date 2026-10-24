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

    private async Task Answered(string key, string answer, DateTime on, int? unit = null, string? decisions = null, string question = "A question?")
    {
        await using var ctx = NewCtx();
        ctx.StewardQuestions.Add(new StewardQuestion
        {
            ProjectKey = "alpha", Key = key, Question = question, Why = "w", HeldUp = "h", AskedAt = on, Answer = answer,
            AnsweredAt = on, AnswerDeliveredAt = on, PlanUnit = unit, PlanKey = unit is null ? null : "", Decisions = decisions,
        });
        await ctx.SaveChangesAsync();
    }

    private async Task Said(string text, DateTime on)
    {
        await using var ctx = NewCtx();
        ctx.StewardEntries.Add(new StewardEntry { ProjectKey = "alpha", Kind = StewardEntryKind.OwnerSaid, Text = text, At = on, DeliveredAt = on });
        await ctx.SaveChangesAsync();
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
        Assert.Equal("recorded", (await In(s => s.OwnerRecordsDecisionAsync("alpha", new StewardNewDecision("Texts go out from one number.")))).Answer);
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
        await In(s => s.OwnerRecordsDecisionAsync("alpha", new StewardNewDecision("Everyone in the team sees the same schedule.")));
        Assert.Equal("bad-request", (await In(s => s.OwnerRecordsDecisionAsync("alpha",
            new StewardNewDecision("Technicians see only their own visits.", Replaces: new[] { new StewardReplacing(1, null) })))).Answer);   // say why
        Assert.Equal("recorded", (await In(s => s.OwnerRecordsDecisionAsync("alpha",
            new StewardNewDecision("Technicians see only their own visits.", Replaces: new[] { new StewardReplacing(1, "Technicians have a role of their own now.") })))).Answer);

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
            new StewardNewDecision("x", Replaces: new[] { new StewardReplacing(1, "again") })))).Answer);
        Assert.Equal("withdrawn", (await In(s => s.OwnerWithdrawsDecisionAsync("alpha", 2, "The roles are on hold."))).Answer);
        Assert.DoesNotContain("their own visits", Json(await In(s => s.DecisionsAsync("alpha", null))));
        Assert.Contains("\"status\":\"withdrawn\"", Json(await In(s => s.DecisionsAsync("alpha", null, history: true))));
    }

    [Fact]
    public async Task The_owners_changes_to_the_register_are_news_to_the_next_run_once()
    {
        await In(s => s.OwnerRecordsDecisionAsync("alpha", new StewardNewDecision("Invites last 30 days.")));
        var first = System.Text.Json.JsonSerializer.Serialize((await Begin()).Data);
        Assert.Contains("\"changedDecisions\":[{\"number\":1,\"rule\":\"Invites last 30 days.\"", first);
        await In(s => s.EndAsync("alpha", "applied it", true));
        Assert.Contains("\"changedDecisions\":[]", System.Text.Json.JsonSerializer.Serialize((await Begin()).Data));
    }

    [Fact]
    public async Task Decisions_can_be_narrowed_to_a_slice_or_one_of_its_outcomes()
    {
        await In(s => s.OwnerRecordsDecisionAsync("alpha", new StewardNewDecision("Project-wide: prices never differ.")));
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

    private const string PlanDoc = """
        # The plan

        ## 9. Decisions

        | Date | Decision |
        |---|---|
        | 2026-01-01 | First version: every team member sees the same schedule; a technician's own view is a later phase |
        | 2026-01-01 | Prices are never higher here than anywhere else |
        | 2026-01-02 | An invite lasts 30 days (unit 2, #4) |

        ## 10. Sources

        Elsewhere.
        """;

    private async Task ImportSetup()
    {
        Configure("full", """ "plan": { "units": "docs/units", "master": "docs/PLAN.md" } """);
        _git.Second = DecidedSpec;
        _git.InRepo["docs/PLAN.md"] = PlanDoc;
        var d = new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc);
        await Answered("invite-picks", "Unit 2: decided #4 (b), as picked; #5 (a), as picked.", d, unit: 2, decisions: "4,5");
        await Answered("sender-address", "Option A: replies go to the business.", d.AddDays(1));
        await Answered("technician-role", "Option B: technicians get their own role and see only their own schedule; other team members see everything.", d.AddDays(2),
            question: "What does a technician see?");
        await Said("Ticket 3: 'behaviour-not-asked-for' is cleared for its change as it stands (at abc1234).", d.AddDays(3));
        await Said("answer to **technician-role**: B, and a technician never sees prices.", d.AddDays(3));
        await Said("Unit 2: agreed, and decided #4 (b), as picked. Written into docs/units/02-second.md at def.", d.AddDays(3));
        await Said("Tickets are cut whole: one outcome each.", d.AddDays(4));
    }

    [Fact]
    public async Task The_import_keeps_slice_numbers_numbers_the_rest_after_them_and_counts_each_decision_once()
    {
        await ImportSetup();
        var r = await In(s => s.OwnerBuildsRegisterAsync("alpha"));
        Assert.Equal("built", r.Answer);
        var reg = Register();

        // The spec's decisions keep their numbers, with the question that asked for them.
        Assert.Equal("(b) 30 days.", reg.Single(d => d.Number == 4).Rule);
        Assert.Equal("invite-picks", reg.Single(d => d.Number == 4).QuestionKey);
        Assert.Equal("The business sends it from its own address.", reg.Single(d => d.Number == 5).Rule);
        Assert.Equal(new DateTime(2026, 3, 5), reg.Single(d => d.Number == 5).MadeAt.ToLocalTime().Date);

        // An answer the spec also keeps is one decision, in the spec's words.
        var sender = reg.Where(d => d.QuestionKey == "sender-address").ToList();
        Assert.Single(sender);
        Assert.Equal(StewardDecisionSource.Slice, sender[0].Source);
        Assert.StartsWith("Carried in from the owner's answer", sender[0].Rule);

        // The plan's rows come next, the oldest; the row that is #4 is #4, not a second decision.
        var plan = reg.Where(d => d.Source == StewardDecisionSource.Plan).ToList();
        Assert.Equal(2, plan.Count);
        Assert.All(plan, d => Assert.True(d.Number > 5));
        Assert.True(plan.Max(d => d.Number) < reg.Single(d => d.QuestionKey == "technician-role").Number);
        Assert.DoesNotContain(reg, d => d.Rule.Contains("(unit 2, #4)"));

        // The owner's words: a clearance, a settling summary and a repeated answer are not decisions of their own.
        Assert.Single(reg, d => d.Source == StewardDecisionSource.OwnerSaid);
        Assert.Contains(reg, d => d.Rule == "Tickets are cut whole: one outcome each.");
        Assert.Single(reg, d => d.QuestionKey == "technician-role");
        Assert.Equal(reg.Count, reg.Select(d => d.Number).Distinct().Count());
        Assert.Equal(7, reg.Count);   // #4, #5, the sender paragraph, two plan rows, one answer, one word

        // Again: nothing doubles.
        await In(s => s.OwnerBuildsRegisterAsync("alpha"));
        Assert.Equal(7, Register().Count);
    }

    [Fact]
    public async Task The_import_reports_what_may_disagree_and_assumes_no_override()
    {
        await ImportSetup();
        var report = Json(await In(s => s.OwnerBuildsRegisterAsync("alpha")));
        var reg = Register();
        var firstVersion = reg.Single(d => d.Rule.StartsWith("First version")).Number;
        var role = reg.Single(d => d.QuestionKey == "technician-role").Number;
        Assert.Contains($"\"Older\":{firstVersion},\"Newer\":{role}", report);

        // Reported, not assumed: both are in force until the owner says.
        Assert.Contains("First version", Json(await In(s => s.DecisionsAsync("alpha", null))));
        await using (var ctx = NewCtx())
        {
            var pair = await ctx.StewardDecisionConflicts.SingleAsync(c => c.Older == firstVersion && c.Newer == role);
            Assert.Equal("settled", (await In(s => s.OwnerSettlesConflictAsync("alpha", pair.Id, true, "Technicians have their own role."))).Answer);
            Assert.Equal("bad-request", (await In(s => s.OwnerSettlesConflictAsync("alpha", pair.Id, false, null))).Answer);
        }
        Assert.DoesNotContain("First version", Json(await In(s => s.DecisionsAsync("alpha", null))));
        Assert.Equal(firstVersion, Register().Single(d => d.Number == role).Replaces.Single().Number);

        // A second import does not put the same pair again.
        Assert.DoesNotContain($"\"Older\":{firstVersion},\"Newer\":{role}", Json(await In(s => s.OwnerBuildsRegisterAsync("alpha"))));
    }

    [Fact]
    public async Task The_decisions_file_lists_what_is_in_force_by_slice_then_provisional_then_history()
    {
        Configure("full", """ "plan": { "units": "docs/units" }, "decisionsFile": "docs/decisions.md" """);
        await In(s => s.OwnerRecordsDecisionAsync("alpha", new StewardNewDecision("Prices never differ.")));
        await In(s => s.OwnerRecordsDecisionAsync("alpha", new StewardNewDecision("A dog has a name.", Unit: 1)));
        await In(s => s.OwnerRecordsDecisionAsync("alpha", new StewardNewDecision("Texts are free for now.", Provisional: true, ProvisionalUnit: 2)));
        await In(s => s.OwnerRecordsDecisionAsync("alpha", new StewardNewDecision("Prices may differ by a dollar.",
            Replaces: new[] { new StewardReplacing(1, "Card fees.") })));

        var text = _git.Committed[^1].Content;
        var whole = text.IndexOf("## In force: the whole project");
        var unit = text.IndexOf("## In force: unit 1");
        var prov = text.IndexOf("## Provisional");
        var hist = text.IndexOf("## History");
        Assert.True(whole >= 0 && whole < unit && unit < prov && prov < hist, text);
        Assert.Contains("- **#4** Prices may differ by a dollar.", text);
        Assert.Contains("Replaces #1 (Card fees).", text);
        Assert.Contains("Until unit 2 is built.", text);
        Assert.Contains("- ~~#1~~ Prices never differ.", text);
        Assert.Contains("Replaced by #4.", text);
    }

    [Fact]
    public void A_specs_decided_section_and_a_plans_decisions_table_read_as_decisions()
    {
        var items = DecisionText.Decided(DecidedSpec);
        Assert.Equal(new int?[] { 4, 5, null }, items.Select(i => i.Number));
        Assert.Equal("How long an invite lasts", items[0].Title);
        Assert.Equal(new[] { "sender-address" }, items[2].CitedKeys);

        var rows = DecisionText.PlanRows(PlanDoc)!;
        Assert.Equal(3, rows.Count);
        Assert.Equal(new[] { 4 }, rows[2].Cites);
        Assert.DoesNotContain(rows, r => r.Text is "Decision" or "Date");
        Assert.Null(DecisionText.PlanRows("# A plan\n\n## Sources\n"));
    }
}
