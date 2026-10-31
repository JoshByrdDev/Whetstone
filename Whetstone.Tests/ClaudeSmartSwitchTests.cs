using System.Text.Json;
using System.Text.Json.Nodes;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// The smart switch's one rule: the first account on the list under both of its limits. What is
/// pinned here is everything that rule has to survive - a window that has reset since it was read,
/// a limit a run actually hit, an account with no login, and every account being full at once.
/// </summary>
public class ClaudeSmartSwitchTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    private static readonly IReadOnlyList<ClaudeAccount> Accounts = new[]
    {
        new ClaudeAccount(ClaudeAccounts.DefaultName, null),
        new ClaudeAccount("Second", "C:/logins/second"),
        new ClaudeAccount("Third", "C:/logins/third"),
    };

    private static SmartSwitchSettings Chain(params string[] names) =>
        new(true, names.Select(n => new SmartSwitchRung(n, 95, 90)).ToList());

    private static AccountReading Reading(double session, double weekly, DateTimeOffset? sessionReset = null, DateTimeOffset? blocked = null) =>
        new(Now.AddMinutes(-1), new[]
        {
            new UsageLimit("session", "session", "Session", session, "normal", sessionReset ?? Now.AddHours(2), true),
            new UsageLimit("weekly_all", "weekly", "Weekly", weekly, "normal", Now.AddDays(3), false),
        }, blocked);

    private static SmartSwitchDecision Decide(
        SmartSwitchSettings s, string current, Dictionary<string, AccountReading> readings, Func<ClaudeAccount, bool>? login = null) =>
        ClaudeSmartSwitch.Decide(s, Accounts, login ?? (_ => true), readings, current, Now);

    [Fact]
    public void Under_both_limits_the_first_account_stays()
    {
        var d = Decide(Chain("Default", "Second"), "Default", new() { ["Default"] = Reading(89, 94) });
        Assert.Null(d.SwitchTo);
    }

    [Fact]
    public void A_session_at_its_limit_moves_to_the_next()
    {
        var d = Decide(Chain("Default", "Second"), "Default", new() { ["Default"] = Reading(90, 10) });
        Assert.Equal("Second", d.SwitchTo);
        Assert.Contains("session", d.Reason);
    }

    [Fact]
    public void A_week_at_its_limit_moves_to_the_next()
    {
        var d = Decide(Chain("Default", "Second"), "Default", new() { ["Default"] = Reading(5, 96) });
        Assert.Equal("Second", d.SwitchTo);
    }

    [Fact]
    public void Each_account_is_held_to_its_own_limits()
    {
        var s = new SmartSwitchSettings(true, new[]
        {
            new SmartSwitchRung("Default", 95, 90),
            new SmartSwitchRung("Second", 80, 50),
        });
        var d = Decide(s, "Second", new()
        {
            ["Default"] = Reading(95, 10),
            ["Second"] = Reading(60, 10),
        });
        // Second is past its own 50% session limit, and Default is still full: nowhere better to go.
        Assert.Null(d.SwitchTo);
        Assert.Contains("Every account", d.Reason);
    }

    [Fact]
    public void A_window_that_has_reset_since_it_was_read_brings_the_first_account_back()
    {
        // Read at 95% before the switch away; its session window rolled ten minutes ago.
        var d = Decide(Chain("Default", "Second"), "Second", new()
        {
            ["Default"] = Reading(95, 50, sessionReset: Now.AddMinutes(-10)),
            ["Second"] = Reading(20, 20),
        });
        Assert.Equal("Default", d.SwitchTo);
    }

    [Fact]
    public void A_limit_a_run_hit_outranks_the_numbers()
    {
        var d = Decide(Chain("Default", "Second"), "Default", new()
        {
            ["Default"] = Reading(40, 40, blocked: Now.AddHours(1)),
        });
        Assert.Equal("Second", d.SwitchTo);
        Assert.Contains("usage limit", d.Reason);
    }

    [Fact]
    public void A_hit_limit_lapses_at_its_reset()
    {
        var d = Decide(Chain("Default", "Second"), "Second", new()
        {
            ["Default"] = Reading(40, 40, blocked: Now.AddMinutes(-1)),
        });
        Assert.Equal("Default", d.SwitchTo);
    }

    [Fact]
    public void An_account_never_signed_in_to_is_passed_over()
    {
        var d = Decide(Chain("Default", "Second", "Third"), "Default",
            new() { ["Default"] = Reading(95, 10) },
            login: a => a.Name != "Second");
        Assert.Equal("Third", d.SwitchTo);
    }

    [Fact]
    public void An_account_with_no_reading_yet_is_taken_at_its_word()
    {
        var d = Decide(Chain("Default", "Second"), "Default", new() { ["Default"] = Reading(91, 10) });
        Assert.Equal("Second", d.SwitchTo);
    }

    [Fact]
    public void A_name_the_list_has_that_no_account_answers_to_is_skipped()
    {
        var d = Decide(Chain("Gone", "Second"), "Default", new());
        Assert.Equal("Second", d.SwitchTo);
    }

    [Fact]
    public void Off_never_switches()
    {
        var s = Chain("Default", "Second") with { Enabled = false };
        var d = Decide(s, "Default", new() { ["Default"] = Reading(100, 100) });
        Assert.Null(d.SwitchTo);
    }

    [Fact]
    public void Scoped_weekly_windows_count_as_weekly()
    {
        var r = new AccountReading(Now, new[]
        {
            new UsageLimit("weekly_scoped", "weekly_opus", "Weekly · Opus", 97, "normal", Now.AddDays(2), false),
        });
        Assert.Equal((0, 97), ClaudeSmartSwitch.Load(r, Now));
    }

    [Fact]
    public void Stored_settings_are_clamped_and_deduplicated()
    {
        var stored = ClaudeSmartSwitch.Normalize(JsonNode.Parse("""
            { "enabled": true, "chain": [
                { "account": " Default ", "weekly": 150, "session": 0 },
                { "account": "default", "weekly": 50, "session": 50 },
                { "account": "", "weekly": 50 },
                { "account": "Second" } ] }
            """));

        var chain = stored["chain"]!.AsArray();
        Assert.Equal(2, chain.Count);
        Assert.Equal("Default", chain[0]!["account"]!.GetValue<string>());
        Assert.Equal(100, chain[0]!["weekly"]!.GetValue<double>());
        Assert.Equal(1, chain[0]!["session"]!.GetValue<double>());
        Assert.Equal(ClaudeSmartSwitch.DefaultWeekly, chain[1]!["weekly"]!.GetValue<double>());
        Assert.Equal(ClaudeSmartSwitch.DefaultSession, chain[1]!["session"]!.GetValue<double>());

        var read = ClaudeSmartSwitch.Read(
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                new JsonObject { [ClaudeSmartSwitch.ConfigKey] = stored }.ToJsonString())!);
        Assert.True(read.Enabled);
        Assert.Equal(new[] { "Default", "Second" }, read.Chain.Select(r => r.Account));
    }
}
