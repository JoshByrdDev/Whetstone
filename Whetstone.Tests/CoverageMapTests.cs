using System.Text.Json;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// The join between what the ticket asked for and what the run said about it.
/// <para>
/// Every rule here exists because the failure mode of a coverage map is not a wrong verdict — a
/// reader can argue with one of those. It is a map that looks complete and is not, or one that is
/// confident and cannot be checked. Both are invisible on screen unless something mechanical stops
/// them, and this is the something.
/// </para>
/// </summary>
public class CoverageMapTests
{
    private static JsonElement El(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static IReadOnlyList<DescriptionSegmenter.Segment> Segments(string markdown) =>
        DescriptionSegmenter.Compose([new DescriptionSegmenter.Source(null, null, markdown)]).Segments;

    /// <summary>One document whose single section is a coverage block carrying <paramref name="rulings"/>.</summary>
    private static JsonElement Doc(string rulings, string? summary = null) => El($$"""
    {
      "headline": "Advisor replacement",
      "sections": [
        { "title": "Requirement coverage", "blocks": [ {
          "kind": "coverage",
          {{(summary is null ? "" : $"\"text\": {JsonSerializer.Serialize(summary)},")}}
          "rulings": {{rulings}}
        } ] }
      ]
    }
    """);

    private static IReadOnlyList<AnalysisDocument.CoverageSegment> Map(
        string markdown, string rulings)
    {
        var (doc, error) = AnalysisDocument.Parse(Doc(rulings), Segments(markdown));
        Assert.Null(error);
        Assert.NotNull(doc);
        return doc!.Sections[0].Blocks[0].Segments!;
    }

    private static AnalysisDocument.CoverageSegment Only(string markdown, string ruling) =>
        Assert.Single(Map(markdown, $"[{ruling}]"), s => !DescriptionSegmenter.IsStructural(s.Kind));

    // ---- completeness -------------------------------------------------------

    /// <summary>
    /// The rule the whole feature rests on. Walking the RULINGS would produce a map of exactly the
    /// lines the run chose to mention — which can never show an omission, because an omitted line is
    /// simply not in the list. Walking the SEGMENTS means every line of the ticket is on screen, and
    /// the ones the run said nothing about say so.
    /// </summary>
    [Fact]
    public void A_line_the_run_never_ruled_on_is_reported_not_dropped()
    {
        var map = Map("- Carry over tasks\n- Notify the outgoing advisor", """
        [{ "id": "S1", "verdict": "met", "evidence": "Tasks transfer.", "refs": ["Transfer.cs:41"] }]
        """);

        Assert.Equal(2, map.Count);
        Assert.Equal("met", map[0].Verdict);
        Assert.Equal("unaddressed", map[1].Verdict);
        Assert.Equal("Notify the outgoing advisor", map[1].Text);
    }

    [Fact]
    public void A_run_that_ruled_on_nothing_still_produces_the_whole_ticket()
    {
        var map = Map("- One\n- Two\n- Three", "[]");
        Assert.Equal(3, map.Count);
        Assert.All(map, s => Assert.Equal("unaddressed", s.Verdict));
    }

    [Fact]
    public void A_ruling_for_a_line_that_does_not_exist_invents_no_row()
    {
        var map = Map("- Carry over tasks", """
        [{ "id": "S9", "verdict": "gap" }]
        """);

        var only = Assert.Single(map);
        Assert.Equal("unaddressed", only.Verdict);
    }

    [Fact]
    public void The_text_is_the_tickets_own_words_not_the_runs()
    {
        var map = Map("- Replacing must carry over open tasks", """
        [{ "id": "S1", "verdict": "gap", "evidence": "Nothing transfers tasks." }]
        """);

        Assert.Equal("Replacing must carry over open tasks", Assert.Single(map).Text);
    }

    // ---- structure ----------------------------------------------------------

    [Fact]
    public void A_heading_is_carried_but_never_ruled_on()
    {
        var map = Map("## Acceptance criteria\n\n- Audit every replacement", """
        [{ "id": "S1", "verdict": "met", "refs": ["A.cs:1"] }, { "id": "S2", "verdict": "gap" }]
        """);

        Assert.Equal("heading", map[0].Kind);
        Assert.Equal("context", map[0].Verdict);
        Assert.Null(map[0].Refs);
        Assert.Equal("gap", map[1].Verdict);
    }

    [Fact]
    public void Inheritance_survives_the_join()
    {
        var composed = DescriptionSegmenter.Compose([
            new DescriptionSegmenter.Source(null, null, "- Own ask"),
            new DescriptionSegmenter.Source("ALPHA-8700", "epic", "- Inherited ask"),
        ]);

        var (doc, _) = AnalysisDocument.Parse(Doc("""
        [{ "id": "S2", "verdict": "gap", "evidence": "The epic asked for it; nothing here does." }]
        """), composed.Segments);

        var map = doc!.Sections[0].Blocks[0].Segments!;
        Assert.Null(map[0].Origin);
        Assert.Equal("ALPHA-8700", map[1].Origin);
        Assert.Equal("epic", map[1].OriginRole);
        Assert.Equal("gap", map[1].Verdict);
    }

    // ---- the honesty gates --------------------------------------------------

    /// <summary>
    /// "Met" is the one verdict a run can produce for free, and the one a reader cannot check
    /// without something to open. Demoting an uncited one is what makes a green rail mean something:
    /// every tick on it has a file behind it.
    /// </summary>
    [Fact]
    public void Met_without_a_citation_is_demoted_to_unknown()
    {
        var s = Only("- Audit every replacement", """
        { "id": "S1", "verdict": "met", "evidence": "The audit row is written." }
        """);

        Assert.Equal("unknown", s.Verdict);
    }

    /// <summary>
    /// The demoted claim is kept, not deleted. It is still the most useful sentence anyone has about
    /// the line, and under an "unknown" chip it reads as exactly what it is — an assertion with
    /// nothing behind it — which is more use to the reader than a blank row.
    /// </summary>
    [Fact]
    public void The_demoted_claim_is_still_shown()
    {
        var s = Only("- Audit every replacement", """
        { "id": "S1", "verdict": "met", "evidence": "The audit row is written." }
        """);

        Assert.Equal("The audit row is written.", s.Evidence);
    }

    [Fact]
    public void Met_with_a_citation_stands()
    {
        var s = Only("- Audit every replacement", """
        { "id": "S1", "verdict": "met", "evidence": "Written on commit.", "refs": ["Audit.cs:88"] }
        """);

        Assert.Equal("met", s.Verdict);
        Assert.Equal(["Audit.cs:88"], s.Refs);
    }

    /// <summary>
    /// "We decided not to" and "we did not" are the same thing to a reader with nowhere to follow it,
    /// so a destination is the price of the softer word.
    /// </summary>
    [Fact]
    public void Deferred_with_nowhere_to_go_is_a_gap()
    {
        var s = Only("- Back-fill historical cases", """
        { "id": "S1", "verdict": "deferred", "evidence": "Out of scope for this ticket." }
        """);

        Assert.Equal("gap", s.Verdict);
    }

    [Fact]
    public void Deferred_with_a_destination_stands()
    {
        var s = Only("- Back-fill historical cases", """
        { "id": "S1", "verdict": "deferred", "deferredTo": "ALPHA-8752" }
        """);

        Assert.Equal("deferred", s.Verdict);
        Assert.Equal("ALPHA-8752", s.DeferredTo);
    }

    // ---- defence against the output -----------------------------------------

    [Fact]
    public void A_verdict_outside_the_vocabulary_becomes_unknown()
    {
        var s = Only("- Audit every replacement", """
        { "id": "S1", "verdict": "mostly-done" }
        """);

        Assert.Equal("unknown", s.Verdict);
    }

    /// <summary>
    /// A run that ruled twice on one line has contradicted itself. Taking the later one would
    /// silently prefer whichever it happened to write last, which is not a tie-break, it is a coin.
    /// </summary>
    [Fact]
    public void The_first_of_two_rulings_on_one_line_wins()
    {
        var s = Only("- Audit every replacement", """
        { "id": "S1", "verdict": "gap" }, { "id": "S1", "verdict": "met", "refs": ["A.cs:1"] }
        """.Replace("\n", ""));

        Assert.Equal("gap", s.Verdict);
    }

    [Fact]
    public void Citations_are_capped_at_three()
    {
        var s = Only("- Audit every replacement", """
        { "id": "S1", "verdict": "met", "refs": ["a.cs:1", "b.cs:2", "c.cs:3", "d.cs:4"] }
        """);

        Assert.Equal(3, s.Refs!.Count);
    }

    [Fact]
    public void The_qa_that_exercised_a_line_rides_along_with_it()
    {
        var s = Only("- Audit every replacement", """
        { "id": "S1", "verdict": "met", "refs": ["A.cs:1"], "qa": "Replace an advisor and check the log" }
        """);

        Assert.Equal("Replace an advisor and check the log", s.Qa);
    }

    // ---- the summary and the screenshot -------------------------------------

    /// <summary>
    /// The one line read before any row is. It rides on the block's shared <c>text</c> field rather
    /// than a kind-specific one, which is easy to drop on the floor in the parser precisely because
    /// no other coverage code touches it.
    /// </summary>
    [Fact]
    public void The_one_sentence_summary_survives()
    {
        var (doc, _) = AnalysisDocument.Parse(
            Doc("""[{ "id": "S1", "verdict": "gap" }]""", "Everything landed except the audit row."),
            Segments("- Audit every replacement"));

        Assert.Equal("Everything landed except the audit row.", doc!.Sections[0].Blocks[0].Text);
    }

    [Fact]
    public void A_map_with_no_summary_is_still_a_map()
    {
        var (doc, _) = AnalysisDocument.Parse(
            Doc("""[{ "id": "S1", "verdict": "gap" }]"""), Segments("- Audit every replacement"));

        Assert.Null(doc!.Sections[0].Blocks[0].Text);
        Assert.Single(doc.Sections[0].Blocks[0].Segments!);
    }

    [Fact]
    public void The_screenshot_that_proves_a_line_rides_with_it()
    {
        var s = Only("- Audit every replacement", """
        { "id": "S1", "verdict": "met", "refs": ["A.cs:1"], "qa": "Replace and check the log", "shot": 2 }
        """);

        Assert.Equal(2, s.Shot);
    }

    /// <summary>
    /// Zero is a real shot order, not an absent one — the first screenshot QA took. Treating it as
    /// missing would silently drop the picture from whichever requirement was captured first.
    /// </summary>
    [Fact]
    public void Shot_zero_is_a_screenshot_not_an_absence()
    {
        var s = Only("- Audit every replacement", """
        { "id": "S1", "verdict": "met", "refs": ["A.cs:1"], "shot": 0 }
        """);

        Assert.Equal(0, s.Shot);
    }

    [Fact]
    public void A_line_with_no_screenshot_carries_none()
    {
        var s = Only("- Audit every replacement", """
        { "id": "S1", "verdict": "met", "refs": ["A.cs:1"], "qa": "Replace and check the log" }
        """);

        Assert.Null(s.Shot);
        Assert.Equal("Replace and check the log", s.Qa);
    }

    // ---- snippets -----------------------------------------------------------

    [Fact]
    public void A_snippet_keeps_its_indentation_and_its_line()
    {
        var s = Only("- Only licensed advisors", """
        { "id": "S1", "verdict": "met", "refs": ["Picker.tsx:88"],
          "snippet": { "text": "if (a.LicenseState != c.State)\n    continue;", "path": "Picker.tsx", "startLine": 88, "language": "csharp" } }
        """);

        Assert.Equal("if (a.LicenseState != c.State)\n    continue;", s.Snippet!.Text);
        Assert.Equal(88, s.Snippet.StartLine);
        Assert.Equal("Picker.tsx", s.Snippet.Path);
    }

    /// <summary>
    /// The one place in this file that will not salvage what it can. A snippet is a QUOTE — its
    /// entire value is matching what is on disk, so half of one presented as the whole says
    /// something the file does not. Losing it costs one row its picture; keeping a trimmed one costs
    /// every other snippet in the document its credibility.
    /// </summary>
    [Fact]
    public void An_over_long_snippet_is_dropped_rather_than_trimmed()
    {
        var lines = string.Join("\\n", Enumerable.Range(1, 9).Select(i => $"line {i};"));
        var s = Only("- Only licensed advisors", $$"""
        { "id": "S1", "verdict": "met", "refs": ["Picker.tsx:88"],
          "snippet": { "text": "{{lines}}", "path": "Picker.tsx", "startLine": 88 } }
        """);

        Assert.Null(s.Snippet);
        // The row survives it: the verdict and the citation are untouched.
        Assert.Equal("met", s.Verdict);
        Assert.Equal(["Picker.tsx:88"], s.Refs);
    }

    [Fact]
    public void A_snippet_at_the_limit_is_kept()
    {
        var lines = string.Join("\\n", Enumerable.Range(1, 8).Select(i => $"line {i};"));
        var s = Only("- Only licensed advisors", $$"""
        { "id": "S1", "verdict": "met", "refs": ["Picker.tsx:88"],
          "snippet": { "text": "{{lines}}" } }
        """);

        Assert.NotNull(s.Snippet);
        Assert.Equal(8, s.Snippet!.Text.Split('\n').Length);
    }

    /// <summary>
    /// Quoted code that is no longer in the file arrives with no line to point at. Stamping one on
    /// would aim the reader at whatever happens to sit there now.
    /// </summary>
    [Fact]
    public void A_quote_from_history_carries_no_line_number()
    {
        var s = Only("- Only licensed advisors", """
        { "id": "S1", "verdict": "met", "refs": ["Picker.tsx:88"],
          "snippet": { "text": "if (true) return;", "path": "Picker.tsx" } }
        """);

        Assert.Null(s.Snippet!.StartLine);
        Assert.Equal("Picker.tsx", s.Snippet.Path);
    }

    [Fact]
    public void An_empty_snippet_is_no_snippet()
    {
        var s = Only("- Only licensed advisors", """
        { "id": "S1", "verdict": "met", "refs": ["Picker.tsx:88"], "snippet": { "text": "   " } }
        """);

        Assert.Null(s.Snippet);
    }

    // ---- absence ------------------------------------------------------------

    /// <summary>
    /// A ticket raised locally with a one-line title was never held to anything. An empty map on one
    /// would be a rail of grey dots implying an audit that had nothing to audit.
    /// </summary>
    [Fact]
    public void A_ticket_with_no_requirement_text_gets_no_map()
    {
        var (doc, error) = AnalysisDocument.Parse(El("""
        {
          "headline": "Advisor replacement",
          "sections": [
            { "title": "Requirement coverage", "blocks": [ { "kind": "coverage", "rulings": [] } ] },
            { "title": "The area", "blocks": [ { "kind": "prose", "text": "Cases and advisors." } ] }
          ]
        }
        """), segments: null);

        Assert.Null(error);
        // The empty section goes with it: a titled section with nothing under it is the filler the
        // whole block design exists to avoid.
        var section = Assert.Single(doc!.Sections);
        Assert.Equal("The area", section.Title);
    }

    [Fact]
    public void A_stored_map_survives_the_round_trip()
    {
        var (doc, _) = AnalysisDocument.Parse(Doc("""
        [{ "id": "S1", "verdict": "met", "evidence": "Filtered.", "refs": ["Picker.tsx:88"] }]
        """), Segments("- Only licensed advisors"));

        var back = AnalysisDocument.Deserialize(AnalysisDocument.Serialize(doc!));

        var s = Assert.Single(back!.Sections[0].Blocks[0].Segments!);
        Assert.Equal("met", s.Verdict);
        Assert.Equal("Only licensed advisors", s.Text);
        Assert.Equal(["Picker.tsx:88"], s.Refs);
    }

    [Fact]
    public void The_map_is_findable_in_a_stored_document()
    {
        var (doc, _) = AnalysisDocument.Parse(Doc("""
        [{ "id": "S1", "verdict": "gap" }]
        """), Segments("- Only licensed advisors"));

        Assert.True(AnalysisDocument.HasKind(AnalysisDocument.Serialize(doc!), "coverage"));
    }
}
