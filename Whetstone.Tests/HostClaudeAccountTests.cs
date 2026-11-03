using System.Text.Json;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// A worker host held to the smart switch's limits. A limit is the account's, not the machine's,
/// so the board's reading of an account already counts every machine spending it - and the board
/// chooses which account a host's next run spends, from the logins that host says it has. What is
/// pinned here: a host follows the switch to an account it has, sits out when it has none under
/// its limits, and is left to its own login when the switch is off; a limit a host hits on an
/// account is that account's everywhere, not a pause on the machine.
/// </summary>
public class HostClaudeAccountTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    private static readonly IReadOnlyList<ClaudeAccount> Accounts = new[]
    {
        new ClaudeAccount(ClaudeAccounts.DefaultName, null),
        new ClaudeAccount("Second", "C:/logins/second"),
        new ClaudeAccount("Stranger", "C:/logins/stranger"),
    };

    // The board's accounts, as the CLI's profile files say who each is signed in as. "Stranger" was
    // never signed in here, so the board cannot tell who it is.
    private static ClaudeIdentity? Identity(ClaudeAccount a) => a.Name switch
    {
        "Default" => new ClaudeIdentity("uuid-a", "a@example.com"),
        "Second" => new ClaudeIdentity("uuid-b", "b@example.com"),
        _ => null,
    };

    private static readonly HostClaudeLogin LoginA = new("UUID-A", "a@example.com", "Default");
    private static readonly HostClaudeLogin LoginB = new("uuid-b", "b@example.com", "Work");

    private static SmartSwitchSettings Chain(params string[] names) =>
        new(true, names.Select(n => new SmartSwitchRung(n, 80, 90)).ToList());

    private static AccountReading Reading(double weekly) =>
        new(Now.AddMinutes(-1), new[] { new UsageLimit("weekly_all", "weekly", "Weekly", weekly, "normal", Now.AddDays(3), false) });

    private static HostAccountChoice Choose(SmartSwitchSettings s, Dictionary<string, AccountReading> readings, params HostClaudeLogin[] logins) =>
        ClaudeSmartSwitch.ChooseForHost(s, Accounts, Identity, readings, logins, Now);

    [Fact]
    public void Under_its_limits_the_first_account_the_host_has_is_chosen_by_id_not_by_name()
    {
        var c = Choose(Chain("Default", "Second"), new() { ["Default"] = Reading(10) }, LoginB, LoginA);

        Assert.True(c.Gated);
        Assert.Equal("uuid-a", c.AccountId);
        Assert.Equal("Default", c.Account);
        Assert.True(c.ClaudeOpen);
    }

    [Fact]
    public void A_host_with_the_next_account_follows_the_switch_to_it()
    {
        var c = Choose(Chain("Default", "Second"), new() { ["Default"] = Reading(85), ["Second"] = Reading(5) }, LoginA, LoginB);

        Assert.Equal("Second", c.Account);
        Assert.Contains("Default weekly 85%", c.Reason);
    }

    [Fact]
    public void A_host_whose_only_account_is_full_gets_no_claude_work()
    {
        var c = Choose(Chain("Default", "Second"), new() { ["Default"] = Reading(85), ["Second"] = Reading(5) }, LoginA);

        Assert.True(c.Gated);
        Assert.Null(c.AccountId);
        Assert.False(c.ClaudeOpen);
        Assert.Contains("Default weekly 85%", c.Reason);
        Assert.Contains("no login for Second", c.Reason);
    }

    [Fact]
    public void An_account_the_board_cannot_identify_is_never_chosen()
    {
        // The host's login might be Stranger, but nothing here can read that account's usage.
        var c = Choose(Chain("Stranger"), new(), new HostClaudeLogin("uuid-s"));

        Assert.False(c.ClaudeOpen);
        Assert.Contains("cannot tell which account Stranger is", c.Reason);
    }

    [Fact]
    public void A_host_that_reports_no_login_gets_no_claude_work_while_the_switch_chooses()
    {
        var c = Choose(Chain("Default"), new());

        Assert.False(c.ClaudeOpen);
    }

    [Fact]
    public void With_the_switch_off_the_host_spends_its_own_login()
    {
        var c = Choose(new SmartSwitchSettings(false, Chain("Default").Chain), new() { ["Default"] = Reading(99) }, LoginA);

        Assert.False(c.Gated);
        Assert.True(c.ClaudeOpen);
    }

    [Fact]
    public void A_limit_a_run_hit_outranks_the_numbers()
    {
        var blocked = new Dictionary<string, AccountReading>
        {
            ["Default"] = Reading(10) with { BlockedUntil = Now.AddHours(1) },
            ["Second"] = Reading(10),
        };

        Assert.Equal("Second", Choose(Chain("Default", "Second"), blocked, LoginA, LoginB).Account);
    }

    // ---- The board's side: what a check-in stores and the host's status shows ----

    private const string Board = "1111111111111111111111111111111111111111";
    private DateTime _now = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);

    private (WorkerHostService Svc, List<(string Id, DateTimeOffset Until)> Told) Service(SmartSwitchSettings settings,
        Dictionary<string, AccountReading> readings)
    {
        var cfg = WorkerHostConfig.Read(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            """{ "workerHosts": [{ "name": "bravo-host", "slots": 2, "projects": ["alpha"] }] }""")!);
        var told = new List<(string, DateTimeOffset)>();
        var svc = new WorkerHostService(() => cfg, () => Board, () => _now, null,
            logins => ClaudeSmartSwitch.ChooseForHost(settings, Accounts, Identity, readings, logins, Now),
            (id, until) => { told.Add((id, until)); return true; });
        return (svc, told);
    }

    [Fact]
    public async Task A_checkin_reports_logins_and_the_host_shows_the_account_chosen_for_it()
    {
        var (svc, _) = Service(Chain("Default", "Second"), new() { ["Default"] = Reading(85) });
        await svc.CheckInAsync("bravo-host", Board, Array.Empty<HostRun>(), null, TimeSpan.Zero, logins: new[] { LoginA, LoginB });

        var h = svc.Snapshot().Single();
        Assert.Equal("Second", h.Claude!.Account);
        Assert.True(h.ClaudeOpen);
        Assert.True(h.Dispatchable);
        Assert.Equal(2, h.ClaudeLogins!.Count);
        Assert.Equal("uuid-b", svc.ClaudeChoice("bravo-host").AccountId);
    }

    [Fact]
    public async Task A_host_with_no_account_under_its_limits_stays_dispatchable_for_other_providers()
    {
        var (svc, _) = Service(Chain("Default", "Second"), new() { ["Default"] = Reading(85) });
        await svc.CheckInAsync("bravo-host", Board, Array.Empty<HostRun>(), null, TimeSpan.Zero, logins: new[] { LoginA });

        var h = svc.Snapshot().Single();
        Assert.True(h.Dispatchable);
        Assert.False(h.ClaudeOpen);
    }

    [Fact]
    public async Task A_claude_limit_naming_an_account_is_the_accounts_not_the_machines()
    {
        var (svc, told) = Service(Chain("Default", "Second"), new());
        var until = _now.AddHours(2);
        await svc.CheckInAsync("bravo-host", Board, Array.Empty<HostRun>(), null, TimeSpan.Zero,
            logins: new[] { LoginA, LoginB }, limits: new[] { new HostUsageLimit("claude", until, "uuid-a") });

        Assert.Equal(("uuid-a", new DateTimeOffset(until)), Assert.Single(told));
        var h = svc.Snapshot().Single();
        Assert.Null(h.UsageLimitUntil);
        Assert.True(h.Dispatchable);
    }

    [Fact]
    public async Task Any_other_limit_still_holds_the_whole_host()
    {
        var (svc, _) = Service(Chain("Default"), new());
        var until = _now.AddHours(2);
        await svc.CheckInAsync("bravo-host", Board, Array.Empty<HostRun>(), null, TimeSpan.Zero,
            logins: new[] { LoginA },
            limits: new[] { new HostUsageLimit("codex", until), new HostUsageLimit("claude", _now.AddHours(1)) });

        Assert.Equal(until, svc.Snapshot().Single().UsageLimitUntil);
        Assert.False(svc.Snapshot().Single().Dispatchable);
    }

    [Fact]
    public async Task With_the_switch_off_a_claude_limit_holds_the_host_as_it_always_did()
    {
        var (svc, _) = Service(new SmartSwitchSettings(false, Array.Empty<SmartSwitchRung>()), new());
        var until = _now.AddHours(2);
        await svc.CheckInAsync("bravo-host", Board, Array.Empty<HostRun>(), null, TimeSpan.Zero,
            logins: new[] { LoginA }, limits: new[] { new HostUsageLimit("claude", until, "uuid-a") });

        var h = svc.Snapshot().Single();
        Assert.Equal(until, h.UsageLimitUntil);
        Assert.False(h.Claude!.Gated);
    }

    [Fact]
    public void Without_a_chooser_the_board_leaves_hosts_to_their_own_login()
    {
        var cfg = WorkerHostConfig.Read(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            """{ "workerHosts": [{ "name": "bravo-host" }] }""")!);
        var svc = new WorkerHostService(() => cfg, () => Board, () => _now);

        Assert.False(svc.ClaudeChoice("bravo-host").Gated);
        Assert.True(svc.Snapshot().Single().ClaudeOpen);
    }

    // ---- Who a login is: the CLI's profile file ----

    [Fact]
    public void A_profile_names_its_account_and_one_never_signed_in_names_none()
    {
        var dir = Path.Combine(Path.GetTempPath(), "whetstone-identity-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var signedIn = Path.Combine(dir, "a.json");
            File.WriteAllText(signedIn, """{ "numStartups": 3, "oauthAccount": { "accountUuid": " uuid-a ", "emailAddress": "a@example.com" } }""");
            var never = Path.Combine(dir, "b.json");
            File.WriteAllText(never, """{ "numStartups": 1 }""");

            Assert.Equal(new ClaudeIdentity("uuid-a", "a@example.com"), ClaudeAccounts.ReadIdentity(signedIn));
            Assert.Null(ClaudeAccounts.ReadIdentity(never));
            Assert.Null(ClaudeAccounts.ReadIdentity(Path.Combine(dir, "missing.json")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_login_of_its_own_keeps_its_profile_beside_its_credentials()
    {
        Assert.Equal(Path.Combine("C:/logins/second", ".claude.json"), ClaudeAccounts.ProfileOf(Accounts[1]));
    }
}
