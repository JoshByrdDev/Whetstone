using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// Where the coverage map decides its lines are.
/// <para>
/// This runs before the agent does, and everything downstream inherits its mistakes silently: a
/// bullet split in half becomes two requirements, each owed its own verdict and each reading as a
/// fragment; two requirements merged into one become a single row that can only be ruled once, so
/// one of them can never be reported as a gap. Neither shows up as an error anywhere — they show up
/// as a map that quietly disagrees with the ticket.
/// </para>
/// </summary>
public class DescriptionSegmenterTests
{
    private static IReadOnlyList<DescriptionSegmenter.Segment> Split(string text) =>
        DescriptionSegmenter.Compose([new DescriptionSegmenter.Source(null, null, text)]).Segments;

    private static string[] Texts(string text) => Split(text).Select(s => s.Text).ToArray();

    // ---- the split ----------------------------------------------------------

    [Fact]
    public void A_blank_line_ends_a_paragraph()
    {
        var texts = Texts("The advisor is replaced.\n\nThe case stays open.");
        Assert.Equal(["The advisor is replaced.", "The case stays open."], texts);
    }

    [Fact]
    public void A_paragraph_wrapped_over_several_lines_is_one_segment()
    {
        var texts = Texts("The replacement dialog must list\nonly advisors licensed\nin the case's state.");
        Assert.Single(texts);
        Assert.Equal("The replacement dialog must list only advisors licensed in the case's state.", texts[0]);
    }

    [Fact]
    public void Each_bullet_is_its_own_segment()
    {
        var texts = Texts("- Carry over open tasks\n- Notify the outgoing advisor\n- Write an audit row");
        Assert.Equal(3, texts.Length);
        Assert.Equal("Notify the outgoing advisor", texts[1]);
    }

    /// <summary>
    /// The case that decides whether the rail is readable. A tracker wraps a long bullet at whatever
    /// width it likes, and treating the continuation as a new line would put half a requirement on
    /// its own row — owed its own verdict, and reading as a sentence fragment next to a red cross.
    /// </summary>
    [Fact]
    public void A_wrapped_bullet_stays_one_segment()
    {
        var texts = Texts("- Replacing must carry over open tasks,\n  and notify the outgoing advisor\n- Audit every replacement");
        Assert.Equal(2, texts.Length);
        Assert.Equal("Replacing must carry over open tasks, and notify the outgoing advisor", texts[0]);
    }

    [Fact]
    public void Nesting_is_carried_as_depth()
    {
        var segments = Split("- Replacement\n  - Only licensed advisors\n    - In the case's state");
        Assert.Equal([0, 1, 2], segments.Select(s => s.Depth).ToArray());
    }

    [Fact]
    public void Numbered_lists_are_items_too()
    {
        var segments = Split("1. Open the case\n2. Press replace\n3) Pick an advisor");
        Assert.Equal(3, segments.Count);
        Assert.All(segments, s => Assert.Equal("item", s.Kind));
        Assert.Equal("Open the case", segments[0].Text);
    }

    [Fact]
    public void Headings_are_structural()
    {
        var segments = Split("## Acceptance criteria\n\n- Must audit");
        Assert.Equal("heading", segments[0].Kind);
        Assert.Equal("Acceptance criteria", segments[0].Text);
        Assert.True(DescriptionSegmenter.IsStructural(segments[0].Kind));
        Assert.False(DescriptionSegmenter.IsStructural(segments[1].Kind));
    }

    [Fact]
    public void A_horizontal_rule_is_dropped()
    {
        Assert.Equal(["Before", "After"], Texts("Before\n\n---\n\nAfter"));
    }

    [Fact]
    public void A_quote_marker_is_stripped_but_the_words_are_kept()
    {
        Assert.Equal(["Ops asked for this in March"], Texts("> Ops asked for this in March"));
    }

    // ---- tables -------------------------------------------------------------

    [Fact]
    public void A_table_row_is_a_segment_and_the_rule_row_is_not()
    {
        var segments = Split("| Field | Rule |\n|---|---|\n| State | Must match the case |");
        Assert.Equal(2, segments.Count);
        Assert.Equal("row", segments[1].Kind);
        Assert.Equal("State  |  Must match the case", segments[1].Text);
    }

    /// <summary>
    /// The `|---|` rule is not a row, but it does say the row above it was the header — and a header
    /// promoted to a requirement puts a verdict beside the word "Field".
    /// </summary>
    [Fact]
    public void The_row_above_the_rule_becomes_the_header()
    {
        var segments = Split("| Field | Rule |\n|---|---|\n| State | Must match |");
        Assert.Equal("heading", segments[0].Kind);
    }

    // ---- code ---------------------------------------------------------------

    [Fact]
    public void A_fenced_block_keeps_its_indentation()
    {
        var segments = Split("Repro:\n\n```\nif (x) {\n    go();\n}\n```");
        var code = Assert.Single(segments, s => s.Kind == "code");
        Assert.Contains("    go();", code.Text);
    }

    [Fact]
    public void An_unterminated_fence_still_yields_its_block()
    {
        var segments = Split("Repro:\n\n```\nnot closed");
        Assert.Contains(segments, s => s.Kind == "code" && s.Text.Contains("not closed"));
    }

    // ---- composition --------------------------------------------------------

    /// <summary>
    /// Ids run in one flat sequence across every source. The run has to reproduce one exactly for a
    /// ruling to land, so there is nothing in them to get subtly wrong.
    /// </summary>
    [Fact]
    public void Ids_are_flat_across_sources()
    {
        var result = DescriptionSegmenter.Compose([
            new DescriptionSegmenter.Source(null, null, "Own ask"),
            new DescriptionSegmenter.Source("ALPHA-1", "epic", "Inherited ask\n\nSecond inherited"),
        ]);

        Assert.Equal(["S1", "S2", "S3"], result.Segments.Select(s => s.Id).ToArray());
        Assert.Null(result.Segments[0].Origin);
        Assert.Equal("ALPHA-1", result.Segments[2].Origin);
        Assert.Equal("epic", result.Segments[2].OriginRole);
    }

    [Fact]
    public void An_empty_source_contributes_nothing_and_leaves_no_gap()
    {
        var result = DescriptionSegmenter.Compose([
            new DescriptionSegmenter.Source(null, null, "First"),
            new DescriptionSegmenter.Source("ALPHA-1", "epic", "   "),
            new DescriptionSegmenter.Source("ALPHA-2", "epic", "Third"),
        ]);

        Assert.Equal(["S1", "S2"], result.Segments.Select(s => s.Id).ToArray());
        Assert.Equal("ALPHA-2", result.Segments[1].Origin);
    }

    [Fact]
    public void A_runaway_description_is_capped_and_says_how_much_it_lost()
    {
        var huge = string.Join("\n", Enumerable.Range(1, 200).Select(i => $"- Requirement {i}"));
        var result = DescriptionSegmenter.Compose([new DescriptionSegmenter.Source(null, null, huge)]);

        Assert.Equal(120, result.Segments.Count);
        Assert.Equal(80, result.Dropped);
    }

    [Fact]
    public void The_prompt_listing_carries_the_id_and_the_inheritance()
    {
        var result = DescriptionSegmenter.Compose([
            new DescriptionSegmenter.Source(null, null, "- Own ask"),
            new DescriptionSegmenter.Source("ALPHA-1", "epic", "- Inherited ask"),
        ]);

        var listing = DescriptionSegmenter.ForPrompt(result.Segments);
        Assert.Contains("[S1] (item) Own ask", listing);
        Assert.Contains("[S2] (item, inherited from ALPHA-1, the epic) Inherited ask", listing);
    }

    // ---- fingerprint --------------------------------------------------------

    [Fact]
    public void Line_endings_and_surrounding_space_are_not_an_edit()
    {
        Assert.Equal(
            DescriptionSegmenter.Fingerprint("Replace the advisor.\nAudit it."),
            DescriptionSegmenter.Fingerprint("  Replace the advisor.\r\nAudit it.  "));
    }

    [Fact]
    public void Rewording_changes_the_fingerprint()
    {
        Assert.NotEqual(
            DescriptionSegmenter.Fingerprint("Audit every replacement."),
            DescriptionSegmenter.Fingerprint("Audit every successful replacement."));
    }

    /// <summary>
    /// Empty is "cannot tell", not a value. It has to compare equal to nothing, including to another
    /// empty — otherwise a ticket with no description would report every analysis of it as unchanged
    /// on a technicality, and a missing hash would read as an answer.
    /// </summary>
    [Fact]
    public void No_text_has_no_fingerprint()
    {
        Assert.Equal("", DescriptionSegmenter.Fingerprint(null));
        Assert.Equal("", DescriptionSegmenter.Fingerprint("   \n  "));
    }
}
