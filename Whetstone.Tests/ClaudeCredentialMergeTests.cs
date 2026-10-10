using System.Text.Json.Nodes;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// The dashboard is a second writer to a credential file the Claude CLI owns, which is the one place
/// in this app where getting it wrong signs the user out of something else. So what is pinned here is
/// the safety of the write rather than the happy path: that we only ever overwrite the pair we
/// ourselves spent, and that everything we do not understand in that file survives us.
/// </summary>
public class ClaudeCredentialMergeTests
{
    private const string Spent = "refresh-old";

    /// <summary>A credential file with the sibling data a real one carries around the OAuth block.</summary>
    private static string File(string refreshToken = Spent) => $$"""
    {
      "claudeAiOauth": {
        "accessToken": "access-old",
        "refreshToken": "{{refreshToken}}",
        "expiresAt": 1786613681656,
        "scopes": "user:inference user:profile",
        "subscriptionType": "max",
        "rateLimitTier": "default_claude_max_5x"
      },
      "mcpOAuth": { "some-server": { "accessToken": "not-ours" } }
    }
    """;

    private static JsonNode Merged(string fileText, string? refreshToken = "refresh-new")
    {
        var text = ClaudeUsageService.MergeCredentials(
            fileText, "access-new", refreshToken, DateTimeOffset.FromUnixTimeMilliseconds(1786700000000), Spent);
        Assert.NotNull(text);
        return JsonNode.Parse(text!)!;
    }

    [Fact]
    public void Writes_the_renewed_pair_and_its_expiry()
    {
        var oauth = Merged(File())["claudeAiOauth"]!;

        Assert.Equal("access-new", (string?)oauth["accessToken"]);
        Assert.Equal("refresh-new", (string?)oauth["refreshToken"]);
        Assert.Equal(1786700000000, (long?)oauth["expiresAt"]);
    }

    [Fact]
    public void Leaves_every_field_it_does_not_own_alone()
    {
        var root = Merged(File());
        var oauth = root["claudeAiOauth"]!;

        // The tier and scopes are read by the CLI and meaningless to us; the MCP block belongs to
        // entirely different logins. Dropping any of them would be a silent logout somewhere else.
        Assert.Equal("max", (string?)oauth["subscriptionType"]);
        Assert.Equal("default_claude_max_5x", (string?)oauth["rateLimitTier"]);
        Assert.Equal("user:inference user:profile", (string?)oauth["scopes"]);
        Assert.Equal("not-ours", (string?)root["mcpOAuth"]!["some-server"]!["accessToken"]);
    }

    /// <summary>
    /// The race the whole compare-and-swap exists for: a `claude` process rotated the credential
    /// while our grant was in flight. Its pair is the newer one and the CLI is already using it, so
    /// ours - valid though it is - must not land on top of it.
    /// </summary>
    [Fact]
    public void Yields_when_the_CLI_rotated_the_refresh_token_first()
    {
        var text = ClaudeUsageService.MergeCredentials(
            File(refreshToken: "refresh-someone-else"), "access-new", "refresh-new", null, Spent);

        Assert.Null(text);
    }

    [Fact]
    public void Yields_rather_than_guessing_at_a_file_it_does_not_recognise()
    {
        Assert.Null(ClaudeUsageService.MergeCredentials(
            """{ "somethingElse": {} }""", "access-new", "refresh-new", null, Spent));
    }

    /// <summary>
    /// A grant that returns no lifetime leaves the recorded expiry as it found it, which reads as
    /// already-spent and simply renews again next poll. Better a wasted request than a token we have
    /// told ourselves is good for a duration nobody promised.
    /// </summary>
    [Fact]
    public void Keeps_the_old_expiry_when_the_grant_did_not_give_one()
    {
        var text = ClaudeUsageService.MergeCredentials(File(), "access-new", "refresh-new", null, Spent);
        var oauth = JsonNode.Parse(text!)!["claudeAiOauth"]!;

        Assert.Equal(1786613681656, (long?)oauth["expiresAt"]);
        Assert.Equal("access-new", (string?)oauth["accessToken"]);
    }
}
