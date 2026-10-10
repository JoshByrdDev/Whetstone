using Whetstone.Services.Tracker;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// How each tracker names its own issues: what counts as a key, where one is browsed, and how one is
/// cited in a branch and in a commit subject.
///
/// These are pure functions with no network behind them, and they are where the two providers most
/// visibly differ. Getting <see cref="IIssueTracker.CommitRef"/> wrong is not cosmetic: Azure DevOps
/// links a commit to a work item only on the literal form <c>AB#1234</c>, so a bare "1234:" prefix
/// silently throws away most of the reason to link a ticket to a board at all.
/// </summary>
public class TrackerIdentityTests
{
    private static IIssueTracker Jira() => IssueTrackerFactory.Create(new TrackerConfig
    {
        Kind = TrackerKind.Jira,
        BaseUrl = "https://alpha.atlassian.net/",
        Email = "a@b.c",
        Token = "tok",
    });

    private static IIssueTracker Azure() => IssueTrackerFactory.Create(new TrackerConfig
    {
        Kind = TrackerKind.AzureDevOps,
        BaseUrl = "https://dev.azure.com/org",
        Project = "Your Team Project",
        Token = "pat",
    });

    // ---- Which implementation you get --------------------------------------

    [Fact]
    public void The_factory_builds_the_provider_the_config_names()
    {
        Assert.Equal(TrackerKind.Jira, Jira().Kind);
        Assert.Equal(TrackerKind.AzureDevOps, Azure().Kind);
        Assert.Equal(TrackerKind.None, IssueTrackerFactory.Create(new TrackerConfig()).Kind);
    }

    /// <summary>"This project has no tracker" must not be a null anyone can forget to check.</summary>
    [Fact]
    public async Task A_project_with_no_tracker_answers_everything_emptily()
    {
        var none = IssueTrackerFactory.Create(new TrackerConfig());

        Assert.False(none.IsConfigured);
        Assert.Null(none.IssueUrl("anything"));
        Assert.Null(await none.GetIssueAsync("anything"));
        Assert.Empty(await none.GetBriefsAsync(new[] { "a", "b" }));
        Assert.Empty(await none.GetMyUnfinishedAsync(10));
        Assert.Empty(await none.GetBlockingLinksAsync(new[] { "a" }));
    }

    /// <summary>
    /// A tracker whose credentials are half-filled must be physically incapable of a call, rather
    /// than throwing from inside a board render.
    /// </summary>
    [Fact]
    public async Task A_half_configured_tracker_makes_no_calls()
    {
        var jira = IssueTrackerFactory.Create(new TrackerConfig { Kind = TrackerKind.Jira, BaseUrl = "https://x" });

        Assert.False(jira.IsConfigured);
        Assert.Null(await jira.GetIssueAsync("ALPHA-1"));
        Assert.Null(await jira.GetAncestorAsync("ALPHA-1"));
        Assert.Empty(await jira.GetBriefsAsync(new[] { "ALPHA-1" }));
        Assert.Empty(await jira.GetMyUnfinishedAsync(10));
        Assert.Empty(await jira.GetBlockingLinksAsync(new[] { "ALPHA-1" }));
    }

    // ---- Keys --------------------------------------------------------------

    [Theory]
    [InlineData("alpha-8870", "ALPHA-8870")]
    [InlineData("  ALPHA-8870  ", "ALPHA-8870")]
    [InlineData("#ALPHA-8870", "ALPHA-8870")]
    public void Jira_keys_are_uppercased_and_tidied(string typed, string expected)
    {
        Assert.Equal(expected, Jira().NormalizeKey(typed));
    }

    [Theory]
    [InlineData("1234", "1234")]
    [InlineData("  1234 ", "1234")]
    [InlineData("#1234", "1234")]
    [InlineData("AB#1234", "1234")]
    [InlineData("ab#1234", "1234")]
    public void Azure_keys_are_stripped_back_to_the_bare_work_item_id(string typed, string expected)
    {
        Assert.Equal(expected, Azure().NormalizeKey(typed));
    }

    /// <summary>
    /// Refusing is the point. On an Azure Boards project a Jira-style key names nothing, and storing
    /// it would produce a ticket that looks linked and resolves to nothing for the rest of its life.
    /// </summary>
    [Theory]
    [InlineData("ALPHA-8870")]
    [InlineData("not a key")]
    [InlineData("12a")]
    [InlineData("")]
    [InlineData(null)]
    public void Azure_refuses_a_key_that_is_not_a_work_item_id(string? typed)
    {
        Assert.Null(Azure().NormalizeKey(typed));
    }

    // ---- Where an issue lives ----------------------------------------------

    [Fact]
    public void Jira_browses_an_issue_by_key()
    {
        Assert.Equal("https://alpha.atlassian.net/browse/ALPHA-8870", Jira().IssueUrl("ALPHA-8870"));
    }

    /// <summary>A team project's name routinely has spaces in it.</summary>
    [Fact]
    public void Azure_browses_a_work_item_under_its_escaped_team_project()
    {
        Assert.Equal("https://dev.azure.com/org/Your%20Team%20Project/_workitems/edit/1234", Azure().IssueUrl("1234"));
    }

    // ---- How an issue is cited ---------------------------------------------

    /// <summary>Both use the bare key in a branch name — "AB#1234" is not a legal one.</summary>
    [Fact]
    public void A_branch_is_named_from_the_bare_key_on_both()
    {
        Assert.Equal("ALPHA-8870", Jira().BranchToken("ALPHA-8870"));
        Assert.Equal("1234", Azure().BranchToken("1234"));
        Assert.DoesNotContain("#", Azure().BranchToken("1234"));
    }

    /// <summary>
    /// The one place the two genuinely diverge. <c>ALPHA-8870: ...</c> is what a Jira user reads and
    /// what Jira's smart-commit parser looks for; Azure DevOps links a commit to a work item only on
    /// <c>AB#1234</c>, and a bare "1234:" links to nothing at all.
    /// </summary>
    [Fact]
    public void A_commit_subject_cites_the_issue_the_way_its_own_tracker_reads_it()
    {
        Assert.Equal("ALPHA-8870", Jira().CommitRef("ALPHA-8870"));
        Assert.Equal("AB#1234", Azure().CommitRef("1234"));
    }

    // ---- Prose -------------------------------------------------------------

    /// <summary>The UI and the worker prompt both print these, so they have to be a name a human
    /// recognises rather than an enum member.</summary>
    [Fact]
    public void Each_tracker_names_itself_the_way_its_users_do()
    {
        Assert.Equal("Jira", Jira().DisplayName);
        Assert.Equal("issue", Jira().IssueNoun);
        Assert.Equal("Azure Boards", Azure().DisplayName);
        Assert.Equal("work item", Azure().IssueNoun);
    }
}
