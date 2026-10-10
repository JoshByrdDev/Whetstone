using System.Text.Json;
using Whetstone.Services.Tracker;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// Which tracker a project is on, read off its config.
///
/// The load-bearing case is the legacy one. Every project that existed before this system had a
/// second tracker carries a top-level <c>jira</c> block, and reading it wrong does not fail loudly:
/// it produces a project whose tickets quietly stop resolving their titles, on a board that still
/// draws them.
/// </summary>
public class TrackerConfigTests
{
    private static TrackerConfig Load(string json)
        => TrackerConfig.From(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!);

    // ---- Backwards compatibility -------------------------------------------

    [Fact]
    public void The_legacy_jira_block_still_reads_as_a_configured_jira_tracker()
    {
        var c = Load("""
            { "jira": { "siteUrl": "https://alpha.atlassian.net", "email": "a@b.c", "apiToken": "tok" } }
            """);

        Assert.Equal(TrackerKind.Jira, c.Kind);
        Assert.Equal("https://alpha.atlassian.net", c.BaseUrl);
        Assert.Equal("a@b.c", c.Email);
        Assert.Equal("tok", c.Token);
        Assert.True(c.IsConfigured);
    }

    /// <summary>Both present is a half-finished migration. One of them has to win, and it is the
    /// one written deliberately.</summary>
    [Fact]
    public void A_tracker_block_wins_over_a_legacy_jira_block()
    {
        var c = Load("""
            {
              "jira": { "siteUrl": "https://old.atlassian.net", "email": "a@b.c", "apiToken": "old" },
              "tracker": { "kind": "azureDevOps", "baseUrl": "https://dev.azure.com/org",
                           "project": "Thing", "token": "new" }
            }
            """);

        Assert.Equal(TrackerKind.AzureDevOps, c.Kind);
        Assert.Equal("https://dev.azure.com/org", c.BaseUrl);
    }

    // ---- Reading the tracker block -----------------------------------------

    [Fact]
    public void An_azure_devops_block_reads_its_own_fields()
    {
        var c = Load("""
            { "tracker": { "kind": "azureDevOps", "baseUrl": "https://dev.azure.com/org",
                           "project": "YourTeamProject", "token": "pat" } }
            """);

        Assert.Equal(TrackerKind.AzureDevOps, c.Kind);
        Assert.Equal("YourTeamProject", c.Project);
        Assert.Equal("pat", c.Token);
        Assert.True(c.IsConfigured);
    }

    /// <summary>The two vendors' own words for the same field differ, and people write what they
    /// know. Refusing a config over "orgUrl" would be a poor use of anyone's afternoon.</summary>
    [Theory]
    [InlineData("\"siteUrl\": \"https://x\"")]
    [InlineData("\"orgUrl\": \"https://x\"")]
    [InlineData("\"url\": \"https://x\"")]
    [InlineData("\"baseUrl\": \"https://x\"")]
    public void The_base_url_is_read_under_any_of_its_common_names(string field)
    {
        Assert.Equal("https://x", Load($$"""{ "tracker": { "kind": "jira", {{field}} } }""").BaseUrl);
    }

    [Theory]
    [InlineData("\"token\": \"t\"")]
    [InlineData("\"apiToken\": \"t\"")]
    [InlineData("\"pat\": \"t\"")]
    public void The_secret_is_read_under_any_of_its_common_names(string field)
    {
        Assert.Equal("t", Load($$"""{ "tracker": { "kind": "jira", {{field}} } }""").Token);
    }

    /// <summary>The vendor has renamed Azure DevOps three times and people write it every way there
    /// is.</summary>
    [Theory]
    [InlineData("azureDevOps")]
    [InlineData("azure-devops")]
    [InlineData("AzureDevOps")]
    [InlineData("ado")]
    [InlineData("vsts")]
    [InlineData("tfs")]
    public void Azure_devops_is_recognised_however_it_is_spelled(string kind)
    {
        Assert.Equal(TrackerKind.AzureDevOps, TrackerConfig.ParseKind(kind));
    }

    /// <summary>A block that names no kind but carries Jira's fields is still a Jira block.</summary>
    [Fact]
    public void A_kindless_block_carrying_an_email_is_read_as_jira()
    {
        var c = Load("""{ "tracker": { "baseUrl": "https://x.atlassian.net", "email": "a@b.c", "token": "t" } }""");
        Assert.Equal(TrackerKind.Jira, c.Kind);
        Assert.True(c.IsConfigured);
    }

    // ---- Not configured is not an error ------------------------------------

    [Fact]
    public void A_project_with_no_tracker_block_has_no_tracker()
    {
        var c = Load("""{ "projectPath": "C:/Repos/Alpha" }""");
        Assert.Equal(TrackerKind.None, c.Kind);
        Assert.False(c.IsConfigured);
    }

    /// <summary>
    /// Half-filled is not configured. An Azure DevOps organisation URL with no team project would
    /// 404 on every read, and nothing on the board would say why.
    /// </summary>
    [Theory]
    [InlineData("""{ "tracker": { "kind": "azureDevOps", "baseUrl": "https://dev.azure.com/org", "token": "t" } }""")]
    [InlineData("""{ "tracker": { "kind": "azureDevOps", "project": "Thing", "token": "t" } }""")]
    [InlineData("""{ "tracker": { "kind": "azureDevOps", "baseUrl": "https://dev.azure.com/org", "project": "Thing" } }""")]
    [InlineData("""{ "tracker": { "kind": "jira", "baseUrl": "https://x", "token": "t" } }""")]
    [InlineData("""{ "tracker": { "kind": "jira", "email": "a@b.c", "token": "t" } }""")]
    public void An_incomplete_block_reports_itself_unconfigured(string json)
    {
        Assert.False(Load(json).IsConfigured);
    }

    // ---- Blocking links: absent and empty mean different things -------------

    [Fact]
    public void An_absent_blocking_list_falls_back_to_the_providers_default()
    {
        Assert.Equal(new[] { "Blocks" },
            Load("""{ "tracker": { "kind": "jira" } }""").EffectiveBlockingLinkTypes);
        Assert.Equal(new[] { "System.LinkTypes.Dependency-Reverse" },
            Load("""{ "tracker": { "kind": "azureDevOps" } }""").EffectiveBlockingLinkTypes);
    }

    /// <summary>
    /// The escape hatch for Azure DevOps' weaker semantics: Predecessor is a scheduling hint many
    /// teams set for ordering they never meant to gate work on, and "nothing blocks" has to be
    /// sayable. It is only distinguishable from "not configured" because the key is present.
    /// </summary>
    [Fact]
    public void An_empty_but_present_blocking_list_means_nothing_blocks()
    {
        var c = Load("""{ "tracker": { "kind": "azureDevOps", "blockingLinkTypes": [] } }""");
        Assert.Empty(c.EffectiveBlockingLinkTypes);
    }

    [Fact]
    public void A_named_blocking_list_replaces_the_default()
    {
        var c = Load("""{ "tracker": { "kind": "azureDevOps", "blockingLinkTypes": ["Custom.Blocks"] } }""");
        Assert.Equal(new[] { "Custom.Blocks" }, c.EffectiveBlockingLinkTypes);
    }

    // ---- Done states -------------------------------------------------------

    /// <summary>The default covers Agile, Scrum, Basic and CMMI.</summary>
    [Fact]
    public void Absent_done_states_fall_back_to_the_stock_templates()
    {
        Assert.Equal(new[] { "Closed", "Done", "Removed" },
            Load("""{ "tracker": { "kind": "azureDevOps" } }""").EffectiveDoneStates);
    }

    /// <summary>
    /// Unlike blocking links, empty is NOT a distinct answer here: "no state means finished" would
    /// say every blocker blocks forever, which nobody wants and which reads as a bug from the board.
    /// </summary>
    [Fact]
    public void An_empty_done_state_list_falls_back_rather_than_meaning_nothing_is_ever_done()
    {
        Assert.Equal(new[] { "Closed", "Done", "Removed" },
            Load("""{ "tracker": { "kind": "azureDevOps", "doneStates": [] } }""").EffectiveDoneStates);
    }

    [Fact]
    public void A_named_done_state_list_replaces_the_default()
    {
        var c = Load("""{ "tracker": { "kind": "azureDevOps", "doneStates": ["Shipped"] } }""");
        Assert.Equal(new[] { "Shipped" }, c.EffectiveDoneStates);
    }

    // ---- Round-tripping ----------------------------------------------------

    [Fact]
    public void The_kind_written_out_is_the_kind_read_back()
    {
        foreach (var kind in new[] { TrackerKind.Jira, TrackerKind.AzureDevOps, TrackerKind.None })
            Assert.Equal(kind, TrackerConfig.ParseKind(TrackerConfig.KindToString(kind)));
    }

    /// <summary>A malformed block is a project with no tracker, not a crash on startup.</summary>
    [Theory]
    [InlineData("""{ "tracker": "not an object" }""")]
    [InlineData("""{ "tracker": 42 }""")]
    [InlineData("""{ "tracker": null }""")]
    public void A_malformed_block_degrades_to_no_tracker(string json)
    {
        Assert.Equal(TrackerKind.None, Load(json).Kind);
    }
}
