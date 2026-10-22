using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// The Codex meter reads Codex's own report of its windows: the copy a worker saves before its run's
/// home is deleted, or the newest session log in the user's own home - whichever is newer.
/// </summary>
public class CodexUsageServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "whetstone-codexusage-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _home;
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 14, 30, 0, TimeSpan.Zero);

    public CodexUsageServiceTests()
    {
        _home = Path.Combine(_root, "codex-home");
        Directory.CreateDirectory(Path.Combine(_root, "run"));
        Roles("codex");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private void Roles(string provider) =>
        File.WriteAllText(Path.Combine(_root, "config.json"),
            "{ \"agentRoles\": {" + string.Join(",", AgentRoles.All.Select(r => $"\"{r}\": {{ \"provider\": \"{provider}\" }}")) + "} }");

    private CodexUsageService Service(bool isolated = false)
    {
        var paths = new DashboardPaths(_root);
        var registry = new ProjectRegistry(paths);
        return new CodexUsageService(paths, new AgentProviderFactory(registry), isolated ? new QaIsolation(true) : null, () => Now, _home);
    }

    private static string Limits(double primaryUsed, DateTimeOffset primaryResets) =>
        $$"""{"primary":{"used_percent":{{primaryUsed}},"window_minutes":300,"resets_at":{{primaryResets.ToUnixTimeSeconds()}}},"secondary":{"used_percent":12.5,"window_minutes":10080,"resets_at":{{Now.AddDays(3).ToUnixTimeSeconds()}}},"plan_type":"pro"}""";

    private void Capture(DateTimeOffset at, double used) =>
        File.WriteAllText(Path.Combine(_root, "run", "usage-codex.json"),
            $$"""{"capturedAt":"{{at:o}}","rate_limits":{{Limits(used, Now.AddHours(2))}}}""");

    private void Session(DateTimeOffset at, double used)
    {
        var day = Path.Combine(_home, "sessions", "2026", "10", "22");
        Directory.CreateDirectory(day);
        File.WriteAllLines(Path.Combine(day, "rollout-a.jsonl"), new[]
        {
            """{"timestamp":"2026-10-22T13:00:00.000Z","type":"session_meta","payload":{"id":"x"}}""",
            $$$"""{"timestamp":"{{{at:o}}}","type":"event_msg","payload":{"type":"token_count","info":null,"rate_limits":{{{Limits(used, Now.AddHours(2))}}}}}""",
            $$$"""{"timestamp":"{{{at:o}}}","type":"event_msg","payload":{"type":"token_count","info":null,"rate_limits":null}}""",
        });
    }

    [Fact]
    public void A_saved_report_becomes_two_labelled_windows()
    {
        Capture(Now.AddMinutes(-10), 42);
        var u = Service().Current();
        Assert.Equal("ok", u.Status);
        Assert.Equal("pro", u.Plan);
        Assert.Equal(new[] { "5-hour", "Weekly" }, u.Limits.Select(l => l.Label));
        Assert.Equal(42, u.Limits[0].Percent);
        Assert.Equal(Now.AddHours(2).ToUnixTimeSeconds(), u.Limits[0].ResetsAt!.Value.ToUnixTimeSeconds());
    }

    [Fact]
    public void The_newer_of_the_saved_report_and_the_users_own_session_wins()
    {
        Capture(Now.AddMinutes(-10), 42);
        Session(Now.AddMinutes(-2), 77);
        Assert.Equal(77, Service().Current().Limits[0].Percent);

        Capture(Now.AddMinutes(-1), 50);
        Assert.Equal(50, Service().Current().Limits[0].Percent);
    }

    [Fact]
    public void A_qa_copy_never_reads_the_users_own_home()
    {
        Session(Now.AddMinutes(-2), 77);
        Assert.Equal("none", Service(isolated: true).Current().Status);
    }

    [Fact]
    public void A_window_whose_reset_has_passed_reads_as_empty()
    {
        File.WriteAllText(Path.Combine(_root, "run", "usage-codex.json"),
            $$"""{"capturedAt":"{{Now.AddHours(-6):o}}","rate_limits":{{Limits(95, Now.AddMinutes(-30))}}}""");
        var primary = Service().Current().Limits[0];
        Assert.Equal(0, primary.Percent);
        Assert.Null(primary.ResetsAt);
    }

    [Fact]
    public void Nothing_reported_yet_says_so()
    {
        Assert.Equal("none", Service().Current().Status);
    }

    [Fact]
    public void No_role_on_codex_means_no_meter()
    {
        Capture(Now, 42);
        Roles("claude");
        Assert.Equal("unsupported", Service().Current().Status);
    }

    [Theory]
    [InlineData(300.0, "5-hour")]
    [InlineData(10080.0, "Weekly")]
    [InlineData(1440.0, "1-day")]
    [InlineData(45.0, "45-minute")]
    public void Windows_are_named_by_their_length(double minutes, string label) =>
        Assert.Equal(label, CodexUsageService.WindowLabel(minutes, "primary"));
}
