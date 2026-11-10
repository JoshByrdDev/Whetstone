using System.Text.Json;
using Whetstone.Services;

namespace Whetstone.Tests;

/// <summary>
/// docs/slice-branches.md, piece 6: every new decision is a slice's or a convention, a slice's
/// decisions retire when it is released (derived, never written), a run is shown what is in force
/// for the slices that are open, and the decisions file holds only that.
/// </summary>
public partial class StewardServiceTests
{
    private const string WithDecisionsFile = ", \"decisionsFile\": \"docs/decisions.md\"";

    /// <summary>Each decision's status by number, as steward_decisions answers.</summary>
    private static Dictionary<int, string> Statuses(StewardReply reply)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(reply.Data));
        return doc.RootElement.GetProperty("decisions").EnumerateArray()
            .ToDictionary(d => d.GetProperty("number").GetInt32(), d => d.GetProperty("status").GetString()!);
    }

    private Task<StewardReply> Decisions(int? unit = null, bool history = false) =>
        In(s => s.DecisionsAsync("alpha", null, unit, null, history));

    [Fact]
    public async Task A_slices_decisions_retire_when_it_is_released_and_a_convention_never_does()
    {
        await ReleasableSlice(WithDecisionsFile);
        var conv = await In(s => s.OwnerRecordsDecisionAsync("alpha", new StewardNewDecision("Money is whole cents everywhere.", Convention: true)));
        Assert.Equal("recorded", conv.Answer);
        var before = Statuses(await Decisions());
        Assert.Equal("inForce", before[40]);

        Assert.Equal("released", (await In(s => s.OwnerReleasesUnitAsync("alpha", 1, true))).Answer);

        var now = Statuses(await Decisions());
        Assert.DoesNotContain(40, now.Keys);
        Assert.DoesNotContain(41, now.Keys);
        Assert.Contains("inForce", now.Values);
        var history = Statuses(await Decisions(history: true));
        Assert.Equal("retired", history[40]);

        // The file holds what is in force and nothing retired - no history at all.
        var text = _git.Committed.Last(c => c.Path == "docs/decisions.md").Content;
        Assert.Contains("## In force: conventions", text);
        Assert.Contains("Money is whole cents everywhere.", text);
        Assert.DoesNotContain("never floats", text);
        Assert.DoesNotContain("## History", text);
    }

    [Fact]
    public async Task Without_the_switch_a_released_unit_retires_nothing()
    {
        Configure("full", """ "plan": { "units": "docs/units" }, "decisionsFile": "docs/decisions.md" """);
        await OpenSlice(1, "slice/01-first", released: true);
        await In(s => s.OwnerRecordsDecisionAsync("alpha", new StewardNewDecision("A dog has a name.", Unit: 1)));
        Assert.Equal(new[] { "inForce" }, Statuses(await Decisions()).Values);
        Assert.Contains("## In force: unit 1", _git.Committed[^1].Content);
    }

    [Fact]
    public async Task A_question_about_no_slice_says_which_slice_or_that_it_is_a_convention()
    {
        SwitchToSliceBranches();
        await Begin();
        var none = await In(s => s.AskAsync("alpha", Question()));
        Assert.Equal("bad-request", none.Answer);
        Assert.Contains("slice", none.Why);

        Assert.Equal("asked", (await In(s => s.AskAsync("alpha", Question("for-slice") with { Slice = 1 }))).Answer);
        Assert.Equal("asked", (await In(s => s.AskAsync("alpha", Question("for-all") with { Convention = true }))).Answer);
        await In(s => s.OwnerAnswersAsync("alpha", "for-slice", "The one we already pay for."));
        await In(s => s.OwnerAnswersAsync("alpha", "for-all", "A new one."));

        await using var ctx = NewCtx();
        var made = ctx.StewardDecisions.Where(d => d.QuestionKey != null).ToDictionary(d => d.QuestionKey!);
        Assert.Equal(1, made["for-slice"].PlanUnit);
        Assert.False(made["for-slice"].Convention);
        Assert.Null(made["for-all"].PlanUnit);
        Assert.True(made["for-all"].Convention);
    }

    [Fact]
    public async Task A_question_cannot_decide_for_a_slice_that_is_over_nor_name_a_slice_without_the_switch()
    {
        await Begin();
        Assert.Contains("no slice branches", (await In(s => s.AskAsync("alpha", Question() with { Slice = 1 }))).Why);
        SwitchToSliceBranches();
        await OpenSlice(1, "slice/01-first", released: true);
        var over = await In(s => s.AskAsync("alpha", Question() with { Slice = 1 }));
        Assert.Equal("bad-request", over.Answer);
        Assert.Contains("is over", over.Why);
        Assert.Contains("not both", (await In(s => s.AskAsync("alpha", Question() with { Slice = 1, Convention = true }))).Why);
    }

    [Fact]
    public async Task A_question_from_a_ticket_of_a_slice_needs_no_slice_and_decides_for_that_one()
    {
        SwitchToSliceBranches();
        var ticket = await Built(1, "A1", phase: Whetstone.Models.TicketPhase.NeedsImplementation);
        await Begin();
        Assert.Equal("asked", (await In(s => s.AskAsync("alpha", Question() with { TicketId = ticket }))).Answer);
        Assert.Contains("slice 1's", (await In(s => s.AskAsync("alpha", Question("other") with { TicketId = ticket, Slice = 2 }))).Why);
    }

    [Fact]
    public async Task The_owner_records_for_a_slice_or_as_a_convention_and_their_standing_word_is_one()
    {
        Configure("full", """ "plan": { "units": "docs/units" } """);
        SwitchToSliceBranches();
        var none = await In(s => s.OwnerRecordsDecisionAsync("alpha", new StewardNewDecision("Prices never differ.")));
        Assert.Equal("bad-request", none.Answer);
        Assert.Contains("convention", none.Why);
        Assert.Contains("no unit", (await In(s => s.OwnerRecordsDecisionAsync("alpha", new StewardNewDecision("x", Unit: 1, Convention: true)))).Why);

        await In(s => s.OwnerSaysAsync("alpha", "Never text a customer after nine."));
        await using var ctx = NewCtx();
        Assert.True(ctx.StewardDecisions.Single(d => d.Rule == "Never text a customer after nine.").Convention);
    }

    [Fact]
    public async Task A_slices_view_is_its_own_its_parents_and_the_conventions()
    {
        SwitchToSliceBranches();
        await OpenSlice(1, "slice/01-first");
        await OpenSlice(2, "slice/02-second", parent: 1);
        await using (var ctx = NewCtx())
        {
            ctx.StewardDecisions.AddRange(
                new Whetstone.Models.StewardDecision { ProjectKey = "alpha", Number = 50, Rule = "Parent's.", PlanKey = "", PlanUnit = 1 },
                new Whetstone.Models.StewardDecision { ProjectKey = "alpha", Number = 51, Rule = "Own.", PlanKey = "", PlanUnit = 2 },
                new Whetstone.Models.StewardDecision { ProjectKey = "alpha", Number = 52, Rule = "Another slice's.", PlanKey = "", PlanUnit = 3 },
                new Whetstone.Models.StewardDecision { ProjectKey = "alpha", Number = 53, Rule = "Every slice's.", Convention = true });
            await ctx.SaveChangesAsync();
        }
        var seen = Statuses(await Decisions(unit: 2)).Keys.Where(n => n >= 50).Order();
        Assert.Equal(new[] { 50, 51, 53 }, seen);
        // Slice 1 is built on nothing: not its child's.
        Assert.Equal(new[] { 50, 53 }, Statuses(await Decisions(unit: 1)).Keys.Where(n => n >= 50).Order());

        // A run is handed each open slice's own, and the conventions.
        var brief = JsonSerializer.Serialize((await Begin()).Data);
        Assert.Contains("\"slices\":[{\"unit\":1,\"branch\":\"slice/01-first\",\"builtOn\":null,\"decisions\":[{\"number\":50", brief);
        Assert.Contains("{\"unit\":2,\"branch\":\"slice/02-second\",\"builtOn\":1,\"decisions\":[{\"number\":51", brief);
        Assert.Contains("\"conventions\":{\"file\":null,\"decisions\":[{\"number\":53", brief);
        Assert.DoesNotContain("Another slice's.", brief);
    }

    [Fact]
    public async Task A_retired_decision_is_touched_by_no_question_and_replaced_by_nothing()
    {
        await ReleasableSlice();
        await In(s => s.OwnerReleasesUnitAsync("alpha", 1, true));
        await Begin();
        var touch = await In(s => s.AskAsync("alpha", Question() with { Convention = true, Touches = new[] { 40 } }));
        Assert.Contains("retired with its slice", touch.Why);
        var replace = await In(s => s.OwnerRecordsDecisionAsync("alpha", new StewardNewDecision("Floats are fine.", Convention: true,
            Replaces: new[] { new StewardReplacing(40, "Changed our mind.") })));
        Assert.Contains("retired with its slice", replace.Why);
        Assert.Contains("is over", (await In(s => s.OwnerRecordsDecisionAsync("alpha", new StewardNewDecision("Late.", Unit: 1)))).Why);
    }
}
