using Whetstone.Services.Tracker;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// Flattening the HTML that Azure DevOps returns for a work item's long text fields.
///
/// This is the counterpart to <see cref="AdfText"/> and it answers to the same rule: never drop
/// something silently. A stripped-tags implementation would lose a link, a table and an embedded
/// screenshot without leaving any sign it had — and a phase reading the result could not tell it was
/// planning from a partial description. Formatting counts for the same reason: struck-through
/// acceptance criteria arriving as live requirements is worse than no criteria at all.
/// </summary>
public class HtmlTextTests
{
    private static string? Text(string? html, IReadOnlyDictionary<string, string>? names = null)
        => HtmlText.ToText(html, names);

    // ---- Nothing worth saying ----------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<p></p>")]
    [InlineData("<div><br></div>")]
    public void An_empty_field_reads_as_nothing_rather_than_as_blank_text(string? html)
    {
        Assert.Null(Text(html));
    }

    // ---- The plain case ----------------------------------------------------

    /// <summary>
    /// A newer organisation can store these fields as markdown. It needs no special case, but it
    /// does need to survive: re-rendering markdown as if it were HTML would mangle it.
    /// </summary>
    [Fact]
    public void Markdown_with_no_tags_passes_through_untouched()
    {
        Assert.Equal("## Criteria\n\n- one\n- two", Text("## Criteria\n\n- one\n- two"));
    }

    [Fact]
    public void Entities_are_decoded()
    {
        Assert.Equal("Fees & Billing < 10% \"done\"", Text("<p>Fees &amp; Billing &lt; 10% &quot;done&quot;</p>"));
    }

    /// <summary>
    /// A rich-text editor pretty-prints its markup. Keeping that whitespace would indent the
    /// document, and four leading spaces read as a markdown code block — a change of meaning, not of
    /// appearance.
    /// </summary>
    [Fact]
    public void Layout_whitespace_between_blocks_does_not_become_indentation()
    {
        var text = Text("<div>\n    <p>First line.</p>\n    <p>Second line.</p>\n</div>")!;
        Assert.DoesNotContain("\n ", text);
        Assert.Equal("First line.\nSecond line.", text);
    }

    /// <summary>The space between two adjacent inline elements is a real word boundary.</summary>
    [Fact]
    public void The_space_between_two_inline_elements_survives()
    {
        Assert.Equal("**foo** *bar*", Text("<p><b>foo</b> <i>bar</i></p>"));
    }

    // ---- Formatting --------------------------------------------------------

    [Theory]
    [InlineData("<b>bold</b>", "**bold**")]
    [InlineData("<strong>bold</strong>", "**bold**")]
    [InlineData("<i>it</i>", "*it*")]
    [InlineData("<em>it</em>", "*it*")]
    [InlineData("<s>gone</s>", "~~gone~~")]
    [InlineData("<del>gone</del>", "~~gone~~")]
    [InlineData("<code>x</code>", "`x`")]
    [InlineData("<u>under</u>", "<u>under</u>")]
    public void Inline_formatting_survives_as_markdown(string html, string expected)
    {
        Assert.Equal(expected, Text(html));
    }

    /// <summary>
    /// The failure this prevents: a real ticket had half its acceptance criteria struck through, and a
    /// flattening that dropped the mark handed every reader a description in which those items read
    /// as live requirements.
    /// </summary>
    [Fact]
    public void A_struck_through_requirement_is_still_visibly_struck_through()
    {
        Assert.Contains("~~Export to CSV~~", Text("<ul><li><s>Export to CSV</s></li><li>Export to XLSX</li></ul>")!);
    }

    /// <summary>
    /// Delimiters have to hug the words — <c>~~ foo~~</c> renders as literal tildes everywhere — and
    /// an editor routinely leaves the trailing space inside the formatted element.
    /// </summary>
    [Fact]
    public void Delimiters_hug_the_words_when_the_editor_leaves_a_space_inside()
    {
        Assert.Equal("**bold** and plain", Text("<p><b>bold </b>and plain</p>"));
    }

    [Fact]
    public void Headings_keep_their_level()
    {
        Assert.Equal("## Acceptance criteria", Text("<h2>Acceptance criteria</h2>"));
    }

    // ---- Structure ---------------------------------------------------------

    /// <summary>
    /// Only the list knows an item's position, so flattening everything to "- " would turn an
    /// ordered sequence into an unordered set and lose which sub-point belonged to which step.
    /// </summary>
    [Fact]
    public void An_ordered_list_keeps_its_numbering()
    {
        Assert.Equal("1. first\n2. second\n3. third",
            Text("<ol><li>first</li><li>second</li><li>third</li></ol>"));
    }

    /// <summary>A list can be authored to resume at 4.</summary>
    [Fact]
    public void An_ordered_list_starts_where_it_says_it_does()
    {
        Assert.StartsWith("4. fourth", Text("<ol start=\"4\"><li>fourth</li></ol>")!);
    }

    [Fact]
    public void Nested_lists_are_indented_by_their_depth()
    {
        var text = Text("<ul><li>outer<ul><li>inner</li></ul></li></ul>")!;
        Assert.Contains("- outer", text);
        Assert.Contains("inner", text);
    }

    /// <summary>Cells read as a row when separated; without it the structure that made the table
    /// worth writing is lost.</summary>
    [Fact]
    public void Table_rows_read_as_rows()
    {
        var text = Text("<table><tr><td>Fee</td><td>Amount</td></tr><tr><td>Late</td><td>25</td></tr></table>")!;
        Assert.Contains("Fee | Amount", text);
        Assert.Contains("Late | 25", text);
    }

    /// <summary>A cell's own paragraph must not break the row holding it.</summary>
    [Fact]
    public void A_paragraph_inside_a_cell_does_not_break_the_row()
    {
        Assert.Contains("Fee | Amount", Text("<table><tr><td><p>Fee</p></td><td><p>Amount</p></td></tr></table>")!);
    }

    // ---- Never drop something silently -------------------------------------

    /// <summary>The destination is usually the point, and it only survives if we append it.</summary>
    [Fact]
    public void A_links_destination_survives_alongside_its_text()
    {
        Assert.Equal("the spec (https://example.com/spec)", Text("<a href=\"https://example.com/spec\">the spec</a>"));
    }

    /// <summary>A link whose text IS the URL should not say it twice.</summary>
    [Fact]
    public void A_bare_url_link_is_not_repeated()
    {
        Assert.Equal("https://example.com/spec", Text("<a href=\"https://example.com/spec\">https://example.com/spec</a>"));
    }

    /// <summary>
    /// We cannot show the image, but saying it exists is the whole point — the caller can then go
    /// and open the file we downloaded for it.
    /// </summary>
    [Fact]
    public void An_embedded_image_is_named_from_the_attachment_map()
    {
        const string guid = "3f2504e0-4f89-41d3-9a0c-0305e82c3301";
        var names = new Dictionary<string, string> { [guid] = "fee-subtypes.png" };

        Assert.Equal("[attachment: fee-subtypes.png]",
            Text($"<img src=\"https://dev.azure.com/org/_apis/wit/attachments/{guid}?fileName=x.png\">", names));
    }

    /// <summary>With no map, the filename Azure DevOps puts in the query string is the next best name.</summary>
    [Fact]
    public void An_embedded_image_falls_back_to_the_filename_in_its_url()
    {
        Assert.Equal("[attachment: mockup.png]",
            Text("<img src=\"https://dev.azure.com/org/_apis/wit/attachments/3f2504e0-4f89-41d3-9a0c-0305e82c3301?fileName=mockup.png\">"));
    }

    [Fact]
    public void An_embedded_image_falls_back_to_its_alt_text()
    {
        Assert.Equal("[attachment: The login screen]", Text("<img src=\"https://x/y.png\" alt=\"The login screen\">"));
    }

    // ---- Robustness --------------------------------------------------------

    /// <summary>A stylesheet has nothing in it for a reader — this is the one case where dropping
    /// something silently is right.</summary>
    [Fact]
    public void Style_and_script_content_is_dropped_entirely()
    {
        Assert.Equal("Real text.", Text("<style>p { color: red }</style><p>Real text.</p><script>alert(1)</script>"));
    }

    /// <summary>
    /// The input is one field written by an editor, not a document. Losing content is the failure
    /// that matters; getting the tree subtly wrong is not — so text survives even when the structure
    /// around it does not.
    /// </summary>
    [Theory]
    [InlineData("<p>Unclosed paragraph", "Unclosed paragraph")]
    [InlineData("</div>Stray close tag", "Stray close tag")]
    [InlineData("<p>Text <b>with an unclosed bold", "Text **with an unclosed bold**")]
    [InlineData("Text with a < that is not a tag", "Text with a < that is not a tag")]
    public void Malformed_markup_still_yields_its_text(string html, string expected)
    {
        Assert.Equal(expected, Text(html));
    }

    /// <summary>A quote inside an attribute must not truncate the tag half-way through.</summary>
    [Fact]
    public void A_quote_inside_an_attribute_does_not_end_the_tag_early()
    {
        Assert.Equal("link (https://x/y)", Text("<a title=\"the &gt; sign\" href=\"https://x/y\">link</a>"));
    }

    [Fact]
    public void Comments_and_doctypes_carry_nothing_through()
    {
        Assert.Equal("Kept.", Text("<!DOCTYPE html><!-- dropped --><p>Kept.</p>"));
    }

    /// <summary>A non-breaking space is an ordinary space by the time anyone reads this.</summary>
    [Fact]
    public void Non_breaking_spaces_become_ordinary_ones()
    {
        Assert.Equal("one two", Text("<p>one&nbsp;two</p>"));
    }
}
