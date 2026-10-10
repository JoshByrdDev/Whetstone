using System.Text.Json;
using Whetstone.Services.Tracker;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// What a phase actually reads when it reads a Jira description.
/// <para>
/// The stakes here are not cosmetic. A real ticket had its first acceptance criterion and all three of
/// its sub-points struck through - work already done - while the rest stands. A flattener that drops
/// marks hands the reader a description in which those four items are live requirements, and nothing
/// downstream can tell that it happened. So every test below is really the same test: does the
/// meaning that was carried by formatting still arrive?
/// </para>
/// </summary>
public class AdfTextTests
{
    private static string? Flatten(string adfJson)
        => AdfText.ToText(JsonSerializer.Deserialize<JsonElement>(adfJson));

    /// <summary>One paragraph of text runs, given as raw JSON node objects.</summary>
    private static string Doc(params string[] runs)
        => $$"""{"type":"doc","content":[{"type":"paragraph","content":[{{string.Join(",", runs)}}]}]}""";

    private static string Run(string text, params string[] markTypes)
        => markTypes.Length == 0
            ? $$"""{"type":"text","text":{{JsonSerializer.Serialize(text)}}}"""
            : $$"""
              {"type":"text","text":{{JsonSerializer.Serialize(text)}},"marks":[
                {{string.Join(",", markTypes.Select(m => $$"""{"type":"{{m}}"}"""))}}]}
              """;

    // ---- Inline marks -------------------------------------------------------

    /// <summary>The one that started this: struck-through text must not read as a live requirement.</summary>
    [Fact]
    public void Strikethrough_survives_flattening()
        => Assert.Equal("~~Create stored procedure~~", Flatten(Doc(Run("Create stored procedure", "strike"))));

    [Fact]
    public void Bold_italic_and_code_survive_flattening()
    {
        Assert.Equal("**Production only**", Flatten(Doc(Run("Production only", "strong"))));
        Assert.Equal("*maybe*", Flatten(Doc(Run("maybe", "em"))));
        Assert.Equal("`sp_GetStatements`", Flatten(Doc(Run("sp_GetStatements", "code"))));
        Assert.Equal("<u>must</u>", Flatten(Doc(Run("must", "underline"))));
    }

    /// <summary>Markdown has no colour, so both of Jira's colour marks become the one highlight.</summary>
    [Fact]
    public void Text_colour_and_highlight_both_become_a_highlight_marker()
    {
        Assert.Equal("==careful==", Flatten(Doc(Run("careful", "textColor"))));
        Assert.Equal("==careful==", Flatten(Doc(Run("careful", "backgroundColor"))));
        // Both at once is one highlight, not two - "====careful====" renders as literal equals signs.
        Assert.Equal("==careful==", Flatten(Doc(Run("careful", "textColor", "backgroundColor"))));
    }

    [Fact]
    public void Marks_stack_on_a_single_run()
        => Assert.Equal("~~**dropped**~~", Flatten(Doc(Run("dropped", "strong", "strike"))));

    /// <summary>
    /// ADF splits a formatted run at the space as often as not. Delimiters that do not hug the words
    /// render as literal tildes, which would be worse than dropping the mark - it would look like the
    /// author typed them.
    /// </summary>
    [Fact]
    public void Delimiters_hug_the_words_when_the_run_carries_surrounding_spaces()
        => Assert.Equal("~~dropped~~ but this stands",
            Flatten(Doc(Run("dropped ", "strike"), Run("but this stands"))));

    /// <summary>A whitespace-only run would otherwise emit a bare pair of delimiters.</summary>
    [Fact]
    public void A_whitespace_only_run_emits_no_delimiters()
        => Assert.Equal("a b", Flatten(Doc(Run("a"), Run(" ", "strike"), Run("b"))));

    /// <summary>A mark we do not render must pass the text through untouched, not swallow it.</summary>
    [Fact]
    public void An_unknown_mark_leaves_the_text_alone()
        => Assert.Equal("plain", Flatten(Doc(Run("plain", "someFutureMark"))));

    /// <summary>
    /// A struck-through link is struck-through words - the destination is not part of the sentence,
    /// so it sits outside the marks where a renderer will not strike it out.
    /// </summary>
    [Fact]
    public void A_links_destination_sits_outside_the_formatting()
    {
        const string adf = """
        {"type":"doc","content":[{"type":"paragraph","content":[
          {"type":"text","text":"the old spec","marks":[
            {"type":"strike"},
            {"type":"link","attrs":{"href":"https://example.test/spec"}}]}]}]}
        """;
        Assert.Equal("~~the old spec~~ (https://example.test/spec)", Flatten(adf));
    }

    // ---- Structure ----------------------------------------------------------

    [Fact]
    public void Heading_level_survives_flattening()
        => Assert.Equal("### Acceptance criteria", Flatten(
            """{"type":"doc","content":[{"type":"heading","attrs":{"level":3},"content":[{"type":"text","text":"Acceptance criteria"}]}]}"""));

    /// <summary>
    /// An ordered list flattened to bullets stops being a sequence, and its sub-points stop belonging
    /// to any particular step. On a real ticket that is the difference between "step 1 and its three
    /// sub-points are done" and "seven things, four of them crossed out, good luck".
    /// </summary>
    [Fact]
    public void An_ordered_list_keeps_its_numbering_and_its_nesting()
    {
        const string adf = """
        {"type":"doc","content":[{"type":"orderedList","content":[
          {"type":"listItem","content":[
            {"type":"paragraph","content":[{"type":"text","text":"Create stored procedure","marks":[{"type":"strike"}]}]},
            {"type":"orderedList","content":[
              {"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"Period is Q4","marks":[{"type":"strike"}]}]}]},
              {"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"Production only","marks":[{"type":"strike"}]}]}]}]}]},
          {"type":"listItem","content":[
            {"type":"paragraph","content":[{"type":"text","text":"Create export job"}]}]}]}]}
        """;

        var text = Flatten(adf)!.Replace("\r\n", "\n");
        var lines = text.Split('\n').Where(l => l.Length > 0).ToArray();

        Assert.Equal("1. ~~Create stored procedure~~", lines[0]);
        Assert.Equal("    1. ~~Period is Q4~~", lines[1]);
        Assert.Equal("    2. ~~Production only~~", lines[2]);
        Assert.Equal("2. Create export job", lines[3]);
    }

    /// <summary>A list authored to resume at 4 is renumbered from 1 only if we ignore its start.</summary>
    [Fact]
    public void An_ordered_list_starts_where_jira_says_it_starts()
    {
        const string adf = """
        {"type":"doc","content":[{"type":"orderedList","attrs":{"order":4},"content":[
          {"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"fourth"}]}]}]}]}
        """;
        Assert.StartsWith("4. fourth", Flatten(adf));
    }

    [Fact]
    public void A_bullet_list_still_uses_bullets()
    {
        const string adf = """
        {"type":"doc","content":[{"type":"bulletList","content":[
          {"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"one"}]}]},
          {"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"two"}]}]}]}]}
        """;
        var lines = Flatten(adf)!.Replace("\r\n", "\n").Split('\n').Where(l => l.Length > 0).ToArray();
        Assert.Equal(new[] { "- one", "- two" }, lines);
    }

    /// <summary>An info panel and a warning panel say different things about the same words.</summary>
    [Fact]
    public void A_panel_names_its_kind()
        => Assert.Equal("[warning] Do not ship before 12/31", Flatten(
            """
            {"type":"doc","content":[{"type":"panel","attrs":{"panelType":"warning"},"content":[
              {"type":"paragraph","content":[{"type":"text","text":"Do not ship before 12/31"}]}]}]}
            """));

    /// <summary>The earlier fix this file exists to protect: a card is a bare URL with no text child.</summary>
    [Fact]
    public void An_inline_card_still_yields_its_url()
        => Assert.Equal("https://example.test/ALPHA-5637", Flatten(
            """
            {"type":"doc","content":[{"type":"paragraph","content":[
              {"type":"inlineCard","attrs":{"url":"https://example.test/ALPHA-5637"}}]}]}
            """));
}
