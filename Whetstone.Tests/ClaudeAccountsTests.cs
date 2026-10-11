using System.Text.Json;
using System.Text.Json.Nodes;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// Which Claude login a run spends is read by the dashboard and, separately, by the heartbeat. What
/// is pinned here is the dashboard's half of that agreement: the fallbacks that keep a mistyped or
/// half-written config on the CLI's own login rather than on a guess the other half would not make.
/// </summary>
public class ClaudeAccountsTests
{
    private static Dictionary<string, JsonElement> Config(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    private const string Two = """
        "claudeAccounts": [ { "name": "Second", "configDir": "C:/logins/second" } ]
        """;

    [Fact]
    public void A_config_that_has_never_heard_of_accounts_is_the_default_alone()
    {
        var cfg = Config("{}");

        Assert.Equal(new[] { ClaudeAccounts.DefaultName }, ClaudeAccounts.All(cfg).Select(a => a.Name));
        Assert.Null(ClaudeAccounts.Active(cfg).ConfigDir);
    }

    [Fact]
    public void The_named_account_is_in_force_whatever_its_case()
    {
        var active = ClaudeAccounts.Active(Config($$"""{ {{Two}}, "claudeAccount": "second" }"""));

        Assert.Equal("Second", active.Name);
        Assert.Equal("C:/logins/second", active.ConfigDir);
    }

    [Fact]
    public void A_name_nothing_answers_to_falls_back_to_the_default()
    {
        var active = ClaudeAccounts.Active(Config($$"""{ {{Two}}, "claudeAccount": "Third" }"""));

        Assert.Equal(ClaudeAccounts.DefaultName, active.Name);
    }

    [Fact]
    public void An_entry_without_a_directory_is_not_an_account()
    {
        // The heartbeat skips it too. Filling one in here would put the meter on an account the
        // fleet is not running as.
        var cfg = Config("""{ "claudeAccounts": [ { "name": "Second" } ], "claudeAccount": "Second" }""");

        Assert.Single(ClaudeAccounts.All(cfg));
        Assert.Null(ClaudeAccounts.Active(cfg).ConfigDir);
    }

    [Fact]
    public void Nothing_stored_can_take_the_defaults_name_or_repeat_another()
    {
        var cfg = Config("""
            { "claudeAccounts": [
                { "name": "default", "configDir": "C:/a" },
                { "name": "Second", "configDir": "C:/b" },
                { "name": "SECOND", "configDir": "C:/c" } ] }
            """);

        var all = ClaudeAccounts.All(cfg);

        Assert.Equal(new[] { ClaudeAccounts.DefaultName, "Second" }, all.Select(a => a.Name));
        Assert.Equal("C:/b", all[1].ConfigDir);
    }

    [Fact]
    public void Saving_gives_a_new_account_a_folder_and_keeps_one_already_chosen()
    {
        var stored = ClaudeAccounts.Normalize(JsonNode.Parse("""
            [ { "name": " Work Max " }, { "name": "Kept", "configDir": "D:/kept" }, { "name": "" } ]
            """));

        Assert.Equal(2, stored.Count);
        Assert.Equal("Work Max", stored[0]!["name"]!.GetValue<string>());
        Assert.EndsWith(".claude-work-max", stored[0]!["configDir"]!.GetValue<string>());
        Assert.Equal("D:/kept", stored[1]!["configDir"]!.GetValue<string>());
    }

    [Fact]
    public void A_dashboard_run_is_pointed_at_the_account_and_left_alone_on_the_default()
    {
        var switched = new ClaudeAgentProvider(configDir: "C:/logins/second").BuildFreeformRun("C:/work", "C:/scratch");
        var analysis = new ClaudeAgentProvider(configDir: "C:/logins/second").BuildAnalysisRun("C:/work", "{}", "C:/scratch");
        var own = new ClaudeAgentProvider().BuildFreeformRun("C:/work", "C:/scratch");

        Assert.Equal("C:/logins/second", switched.Environment["CLAUDE_CONFIG_DIR"]);
        Assert.Equal("C:/logins/second", analysis.Environment["CLAUDE_CONFIG_DIR"]);
        Assert.False(own.Environment.ContainsKey("CLAUDE_CONFIG_DIR"));
    }
}
