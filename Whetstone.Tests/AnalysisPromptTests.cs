using Whetstone.Models;
using Whetstone.Services;
using Whetstone.Services.Tracker;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// What the Classroom run is actually told.
/// <para>
/// Three parts of this prompt now appear only under conditions — there is requirement text, the
/// issue has siblings, the description overflowed — and a conditional built out of raw string
/// literals is the kind of thing that compiles perfectly while emitting an instruction with its
/// indentation shredded or its section missing. Neither shows up as a failure: it shows up as
/// analyses that are quietly a bit worse, four minutes at a time.
/// </para>
/// </summary>
public class AnalysisPromptTests
{
    private static readonly ProjectConfig Cfg = new()
    {
        ProjectPath = @"C:\repos\alpha",
        DisplayName = "Alpha",
        BaseBranch = "main",
    };

    private static Ticket Ticket() => new()
    {
        Id = 7,
        ProjectKey = "alpha",
        Title = "Replace an advisor",
        Phase = TicketPhase.NeedsQa,
        Branch = "feature/replace-advisor",
        IssueKey = "ALPHA-8749",
    };

    private static DescriptionSegmenter.Result Map(params DescriptionSegmenter.Source[] sources) =>
        DescriptionSegmenter.Compose(sources);

    private static string Prompt(
        DescriptionSegmenter.Result map, IReadOnlyList<IssueRef>? siblings = null) =>
        AnalysisRunner.BuildPrompt(Ticket(), Cfg, AnalysisLens.Full, @"C:\repos\alpha",
            "Replace an advisor", "the description", map, siblings ?? Array.Empty<IssueRef>());

    private static readonly DescriptionSegmenter.Result Empty =
        Map(new DescriptionSegmenter.Source(null, null, null));

    // ---- the requirement listing --------------------------------------------

    [Fact]
    public void The_numbered_lines_reach_the_run_with_their_ids()
    {
        var prompt = Prompt(Map(new DescriptionSegmenter.Source(null, null,
            "## Acceptance criteria\n\n- Only licensed advisors\n- Audit every replacement")));

        Assert.Contains("## The requirement text", prompt);
        Assert.Contains("[S2] (item) Only licensed advisors", prompt);
        Assert.Contains("[S3] (item) Audit every replacement", prompt);
    }

    /// <summary>
    /// Headings in this prompt sit at column zero. A section indented by the interpolation reads to
    /// the model as a code block or a continuation of the section above it, which is the failure
    /// this test exists to catch — the words would all be present either way.
    /// </summary>
    [Fact]
    public void The_interpolated_sections_are_not_indented()
    {
        var prompt = Prompt(Map(new DescriptionSegmenter.Source(null, null, "- Only licensed advisors")));

        Assert.Contains("\n## The requirement text\n", prompt);
        Assert.Contains("\n10. **Requirement coverage**", prompt);
        Assert.DoesNotContain("\n        ## The requirement text", prompt);
        Assert.DoesNotContain("\n        10. **Requirement coverage**", prompt);
    }

    [Fact]
    public void Inherited_lines_are_flagged_and_explained()
    {
        var prompt = Prompt(Map(
            new DescriptionSegmenter.Source(null, null, "- Only licensed advisors"),
            new DescriptionSegmenter.Source("ALPHA-8700", "epic", "- Audit every replacement")));

        Assert.Contains("[S2] (item, inherited from ALPHA-8700, the epic) Audit every replacement", prompt);
        Assert.Contains("written on an ANCESTOR", prompt);
    }

    [Fact]
    public void Nothing_is_said_about_inheritance_when_nothing_was_inherited()
    {
        var prompt = Prompt(Map(new DescriptionSegmenter.Source(null, null, "- Only licensed advisors")));
        Assert.DoesNotContain("ANCESTOR", prompt);
    }

    [Fact]
    public void A_truncated_description_says_so_rather_than_pretending_to_be_whole()
    {
        var huge = string.Join("\n", Enumerable.Range(1, 200).Select(i => $"- Requirement {i}"));
        var prompt = Prompt(Map(new DescriptionSegmenter.Source(null, null, huge)));

        Assert.Contains("80 further line(s) were cut", prompt);
    }

    // ---- siblings -----------------------------------------------------------

    /// <summary>
    /// Siblings are named so a `deferred` ruling has somewhere real to point, and named ONLY — a
    /// sibling's criteria are explicitly not this ticket's to deliver, and listing them as
    /// requirements would hold the ticket to the whole epic.
    /// </summary>
    [Fact]
    public void Siblings_arrive_as_destinations_never_as_requirements()
    {
        var prompt = Prompt(
            Map(new DescriptionSegmenter.Source(null, null, "- Only licensed advisors")),
            [new IssueRef("ALPHA-8752", "Back-fill historical cases", "In Progress", "Story")]);

        Assert.Contains("- ALPHA-8752 - Back-fill historical cases [In Progress]", prompt);
        Assert.Contains("NOT yours to deliver", prompt);
        // The sibling's summary must not be numbered as something to rule on.
        Assert.DoesNotContain("] (item) Back-fill historical cases", prompt);
    }

    /// <summary>
    /// A flow stop's `asks` sits inside the userFlow field list, so it has to land at that list's
    /// indentation - flush left it would read as a block kind of its own.
    /// </summary>
    [Fact]
    public void A_stop_is_asked_which_line_called_for_it_only_when_there_are_lines()
    {
        var prompt = Prompt(Map(new DescriptionSegmenter.Source(null, null, "- Only licensed advisors")));

        Assert.Contains("\n  - `asks` - the ids of the requirement lines", prompt);
        Assert.Contains("\n  - **6 to 12 nodes.**", prompt);
        Assert.DoesNotContain("`asks`", Prompt(Empty));
        Assert.Contains("\n  - **6 to 12 nodes.**", Prompt(Empty));
    }

    [Fact]
    public void No_siblings_means_no_empty_list()
    {
        var prompt = Prompt(Map(new DescriptionSegmenter.Source(null, null, "- Only licensed advisors")));
        Assert.DoesNotContain("NOT yours to deliver", prompt);
    }

    // ---- absence ------------------------------------------------------------

    /// <summary>
    /// A locally-raised ticket with a one-line title was never held to anything written down. Asking
    /// for the section anyway invites a coverage map of the ticket's own title.
    /// </summary>
    [Fact]
    public void A_ticket_with_no_requirement_text_is_not_asked_for_a_map()
    {
        var prompt = Prompt(Empty);

        Assert.DoesNotContain("## The requirement text", prompt);
        Assert.DoesNotContain("Requirement coverage", prompt);
        Assert.DoesNotContain("`coverage`", prompt);
    }

    [Fact]
    public void The_teaching_spine_survives_either_way()
    {
        foreach (var prompt in new[] { Prompt(Empty), Prompt(Map(new DescriptionSegmenter.Source(null, null, "- An ask"))) })
        {
            Assert.Contains("1. **The area**", prompt);
            Assert.Contains("9. **Worth sitting with**", prompt);
            Assert.Contains("## Using the blocks", prompt);
        }
    }

    /// <summary>
    /// `code` was the only block kind no section asked for — it lived once, at the bottom, as a
    /// style note among a page of rules about how to quote wrongly. Six of seven runs then produced
    /// one or two against a request for two to four, and one produced none at all. Anchoring it to
    /// the sections that owe one is the fix, so the anchors are worth pinning.
    /// </summary>
    [Fact]
    public void The_sections_that_owe_a_snippet_say_so()
    {
        var prompt = Prompt(Empty);

        Assert.Contains("**quote it**", prompt);                    // section 2, the old rule
        Assert.Contains("**One `code` block is required here**", prompt);  // section 3, the decision
        Assert.Contains("**Two snippets is the floor and four is the", prompt);
    }

    /// <summary>
    /// The ids exist so a ruling can name a line; they are not vocabulary. Four of five real runs
    /// wrote them into reader-facing prose anyway — "the same gate as S5", and one summary that was
    /// seven ids long — because the run reaches for them when it wants to cross-reference. Banning
    /// the symptom is not enough on its own, so the instruction has to supply the alternative.
    /// </summary>
    [Fact]
    public void The_run_is_told_the_ids_are_not_for_the_reader()
    {
        var prompt = Prompt(Map(new DescriptionSegmenter.Source(null, null, "- An ask")));

        Assert.Contains("the reader never sees them", prompt);
        Assert.Contains("quote three or four of ITS OWN WORDS", prompt);
    }

    /// <summary>
    /// The budget exemption is the reason a long ticket does not cost the analysis its prose. A run
    /// counting rulings against 1600 words would shorten the map to protect the essay — which means
    /// dropping requirements to save space. It is stated only where there is a map to exempt.
    /// </summary>
    [Fact]
    public void The_coverage_block_is_exempt_from_the_word_budget()
    {
        Assert.Contains("the `coverage` and `example` blocks",
            Prompt(Map(new DescriptionSegmenter.Source(null, null, "- An ask"))));
    }

    [Fact]
    public void The_word_budget_still_applies_when_there_is_no_map()
    {
        var prompt = Prompt(Empty);
        Assert.Contains("**Length: 900-1600 words across all blocks, `example` blocks excepted.**", prompt);
        Assert.DoesNotContain("`coverage` and `example`", prompt);
    }

    /// <summary>
    /// An example is asked for after each decision and documented as a block, with or without a
    /// map - and its continuation lines sit under its bullet, not flush left where they would read
    /// as a new paragraph of the block list.
    /// </summary>
    [Fact]
    public void Examples_are_asked_for_and_documented_on_every_ticket()
    {
        foreach (var prompt in new[] { Prompt(Empty), Prompt(Map(new DescriptionSegmenter.Source(null, null, "- An ask"))) })
        {
            Assert.Contains("**Follow each decision with an `example`**", prompt);
            Assert.Contains("\n- `example` - a made-up situation", prompt);
            Assert.Contains("\n  - **Invent the names, never the behaviour.**", prompt);
            Assert.Contains("\n- **Length: 900-1600 words", prompt);
            Assert.DoesNotContain("\nprotect the prose", prompt);
            Assert.DoesNotContain("\nexplains; their own limit", prompt);
        }
    }

    /// <summary>
    /// Both live inside the conditional section 10, so a ticket with no requirement text must not be
    /// told about either — a field described but never asked for is one the run may produce anyway.
    /// <para>
    /// Each half asserts the same sentence present and absent, rather than a short substring of it.
    /// `shot` is not one field but two: a ruling carries the screenshot that shows its requirement
    /// met, and a flow node carries the one that is a picture of that stop. The second is
    /// unconditional and rightly so — a ticket with no requirement text still has screenshots and
    /// still gets a flow — so a bare `` `shot` `` here would fail on a sibling field it was never
    /// about.
    /// </para>
    /// </summary>
    [Fact]
    public void The_summary_and_the_screenshot_are_asked_for_only_where_there_is_a_map()
    {
        var withMap = Prompt(Map(new DescriptionSegmenter.Source(null, null, "- An ask")));
        Assert.Contains("the whole map in one sentence", withMap);
        Assert.Contains("`shot` is the `order` of the QA screenshot", withMap);

        var without = Prompt(Empty);
        Assert.DoesNotContain("the whole map in one sentence", without);
        Assert.DoesNotContain("`shot` is the `order` of the QA screenshot", without);
    }
}
