using Whetstone.Api;
using Whetstone.Models;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// The PR's draft title. It shipped reading <see cref="Ticket.Title"/>, which a Jira-linked ticket
/// deliberately never stores — so every real PR came out titled "ALPHA-8870 Ticket #28", pairing
/// the one identifier that travels with the one that must not.
/// </summary>
public class PrTitleTests
{
    private static Ticket Linked(Action<Ticket>? setup = null)
    {
        // Exactly as a Jira-linked ticket is stored: key only, no local title or description.
        var t = new Ticket { Id = 28, ProjectKey = "alpha", IssueKey = "ALPHA-8870", Branch = "feature/ALPHA-8870" };
        setup?.Invoke(t);
        return t;
    }

    private static LinkedIssueResolver.Resolved FromJira(string title) => new(title, null, true, false);

    [Fact]
    public void Is_the_jira_key_then_the_issue_summary()
    {
        Assert.Equal(
            "ALPHA-8870: Stop checkout running over uncommitted work",
            GitEndpoints.SuggestTitle(Linked(), FromJira("Stop checkout running over uncommitted work")));
    }

    /// <summary>The whole point: our row id is not something to publish on someone else's repo.</summary>
    [Fact]
    public void Never_falls_back_to_our_own_ticket_id()
    {
        foreach (var resolved in new[] { FromJira(""), FromJira("   ") })
            Assert.DoesNotContain("28", GitEndpoints.SuggestTitle(Linked(), resolved));
    }

    /// <summary>
    /// When Jira is unreachable the resolver hands the key back as the title. Better a bare key
    /// than "ALPHA-8870: ALPHA-8870".
    /// </summary>
    [Fact]
    public void Does_not_stutter_when_jira_could_not_be_reached()
    {
        var unavailable = new LinkedIssueResolver.Resolved("ALPHA-8870", null, true, true);
        Assert.Equal("ALPHA-8870", GitEndpoints.SuggestTitle(Linked(), unavailable));
    }

    [Fact]
    public void A_local_ticket_uses_its_own_title()
    {
        var t = new Ticket { Id = 28, ProjectKey = "alpha", Title = "Tidy the board", Branch = "feature/tidy" };
        Assert.Equal("Tidy the board", GitEndpoints.SuggestTitle(t, new LinkedIssueResolver.Resolved("Tidy the board", null, false, false)));
    }

    /// <summary>A local ticket should always have a title, but the branch is a truer last resort.</summary>
    [Fact]
    public void A_titleless_local_ticket_falls_back_to_its_branch()
    {
        var t = new Ticket { Id = 28, ProjectKey = "alpha", Branch = "feature/tidy" };
        Assert.Equal("feature/tidy", GitEndpoints.SuggestTitle(t, new LinkedIssueResolver.Resolved("", null, false, false)));
    }
}
