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
}
