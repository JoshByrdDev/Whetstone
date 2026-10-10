using Whetstone.Api;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// The suggestions file is written by PowerShell (worker.ps1's reflection) and read by C#. Nothing
/// type-checks across that boundary, so when the reflection's vocabulary changed from
/// lesson/system to environment/codebase/process, the reader kept matching only the old pair and
/// said nothing. A filed <c>[environment]</c> item parsed as a lesson with the tag left sitting in
/// its text, which routes it to the wrong destination on apply.
///
/// These tests pin the reader to every tag the writer can emit.
/// </summary>
public class SuggestionCategoryTests
{
    // The literal strings worker.ps1 can write. Do not replace these with SuggestionCategory.All -
    // the point is to fail if the two ever disagree, and referencing All would make that impossible.
    [Theory]
    [InlineData("environment")]
    [InlineData("codebase")]
    [InlineData("process")]
    [InlineData("lesson")]
    [InlineData("system")]
    public void ParsesEveryTagTheReflectionCanWrite(string tag)
    {
        var line = $"- [ ] [{tag}] Something worth remembering.  (key: some-key, 2026-08-07 13:50)";

        var match = SuggestionCategory.CheckboxPattern.Match(line);

        Assert.True(match.Success);
        Assert.Equal(tag, match.Groups[2].Value);
        Assert.StartsWith("Something worth remembering.", match.Groups[3].Value);
        Assert.True(SuggestionCategory.IsValid(tag));
    }

    // This is the exact line worker.ps1 produced that the old regex mishandled.
    [Fact]
    public void EnvironmentTagIsNotLeftInsideTheText()
    {
        var line = "- [ ] [environment] On Windows, docker exec requires MSYS_NO_PATHCONV=1.  (key: k, 2026-08-07 13:50)";

        var match = SuggestionCategory.CheckboxPattern.Match(line);

        Assert.Equal("environment", match.Groups[2].Value);
        Assert.DoesNotContain("[environment]", match.Groups[3].Value);
    }

    [Fact]
    public void UntaggedItemFallsBackToTheDefault()
    {
        var match = SuggestionCategory.CheckboxPattern.Match("- [ ] An old item with no tag at all.");

        Assert.True(match.Success);
        Assert.Equal("", match.Groups[2].Value);
        Assert.Equal("An old item with no tag at all.", match.Groups[3].Value);
        Assert.True(SuggestionCategory.IsFact(SuggestionCategory.Default));
    }

    // An unrecognised tag must stay in the text rather than be accepted as a category, so it shows
    // up in the UI as obviously wrong instead of silently routing somewhere.
    [Fact]
    public void UnknownTagStaysPartOfTheText()
    {
        var match = SuggestionCategory.CheckboxPattern.Match("- [ ] [invented] Not a real category.");

        Assert.True(match.Success);
        Assert.Equal("", match.Groups[2].Value);
        Assert.Equal("[invented] Not a real category.", match.Groups[3].Value);
        Assert.False(SuggestionCategory.IsValid("invented"));
    }

    [Fact]
    public void ApprovedStateIsRead()
    {
        Assert.Equal("x", SuggestionCategory.CheckboxPattern.Match("- [x] [process] Approved.").Groups[1].Value);
        Assert.Equal(" ", SuggestionCategory.CheckboxPattern.Match("- [ ] [process] Pending.").Groups[1].Value);
    }

    // The five tags collapse to two destinations. Getting this wrong writes a pipeline change into
    // the repo's notes store, or a repo fact into a skill file.
    [Theory]
    [InlineData("environment", true)]
    [InlineData("codebase", true)]
    [InlineData("lesson", true)]
    [InlineData("process", false)]
    [InlineData("system", false)]
    public void TagsRouteToTheRightDestination(string tag, bool expectedFact)
        => Assert.Equal(expectedFact, SuggestionCategory.IsFact(tag));

    [Fact]
    public void RetaggingReplacesTheOldTagAndKeepsTheCheckboxState()
    {
        var retagged = SuggestionCategory.WithCategory("- [x] [lesson] Some text.", "process");
        Assert.Equal("- [x] [process] Some text.", retagged);

        var untouchedState = SuggestionCategory.WithCategory("- [ ] [environment] Some text.", "codebase");
        Assert.Equal("- [ ] [codebase] Some text.", untouchedState);
    }

    [Fact]
    public void RetaggingAnUntaggedItemInsertsTheTag()
        => Assert.Equal("- [ ] [system] Some text.",
                        SuggestionCategory.WithCategory("- [ ] Some text.", "system"));

    // The queue's only record of when an item arrived. worker.ps1 writes this stamp inline, and the
    // `### date` headings the file used to carry are gone, so a parse failure here does not surface
    // as an error - every item silently reads as undated and the list stops showing its own age.
    [Fact]
    public void ReadsTheFiledStampTheReflectionWrites()
    {
        // Exactly the shape worker.ps1 emits, taken from a real filed item.
        var text = "The advisor product_profile header endpoint returns 500; the grid endpoint works "
                 + "as a workaround.  (key: advisor-product-profile-header-500, 2026-08-26 15:16)";

        var filed = SuggestionCategory.FiledAt(text);

        Assert.Equal(new DateTime(2026, 8, 26, 15, 16, 0), filed);
    }

    [Fact]
    public void FiledStampIsNullWhenAnItemPredatesStamping()
    {
        Assert.Null(SuggestionCategory.FiledAt("Something filed before keys and dates existed."));
        // A date somewhere in the prose is not the stamp; only the trailing key clause counts.
        Assert.Null(SuggestionCategory.FiledAt("The 2026-08-26 migration needs a BOM."));
    }

    // The maintenance pass files this one itself, and it is the only item the pipeline writes with
    // no time component - so a stamp parser that insisted on HH:mm would drop it.
    [Fact]
    public void FiledStampAcceptsADateWithNoTime()
        => Assert.Equal(new DateTime(2026, 8, 26),
                        SuggestionCategory.FiledAt("Index too big.  (key: notes-index-over-limit, 2026-08-26)"));
}
