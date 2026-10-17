using Microsoft.Extensions.Logging.Abstractions;
using Whetstone.Services;
using Whetstone.Services.Tracker;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// An isolated QA copy: the dashboard started from a worktree, on its own ports, over a throwaway
/// data root, so a ticket on the dashboard itself can be proven against a running app.
///
/// The copy is the same program as the live instance, on the same machine, with the same user's
/// login and notification channels in reach. Every test here is one of the ways it could have
/// disturbed the live board or its fleet - and the allow cases are what a QA run on the copy is
/// there to exercise, which a refusal too wide would quietly take away.
/// </summary>
public class QaIsolationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "whetstone-qa-iso-" + Guid.NewGuid().ToString("N"));
    private static readonly QaIsolation On = new(true);

    public QaIsolationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // ---- The route table ----------------------------------------------------

    [Theory]
    [InlineData("POST", "/api/heartbeat/start")]
    [InlineData("POST", "/api/heartbeat/stop")]
    [InlineData("POST", "/api/heartbeat/stop-after-cycle")]
    [InlineData("POST", "/api/heartbeat/cancel-stop")]
    [InlineData("POST", "/api/heartbeat/wake")]
    [InlineData("GET", "/api/steward/due")]
    [InlineData("POST", "/api/steward/launching")]
    [InlineData("POST", "/api/steward/abandon")]
    [InlineData("POST", "/mcp/steward")]
    [InlineData("GET", "/mcp/steward/sse")]
    [InlineData("POST", "/api/notifications/test")]
    [InlineData("POST", "/api/improvements/apply")]
    [InlineData("POST", "/api/classroom/tickets/12/generate")]
    [InlineData("POST", "/api/classroom/analyses/3/walkthrough")]
    [InlineData("POST", "/api/unit-reports/3/walkthrough")]
    [InlineData("POST", "/api/steward/plan/units/2/report")]
    [InlineData("POST", "/api/tickets/7/git/pr")]
    [InlineData("POST", "/api/tickets/7/git/force-push")]
    [InlineData("GET", "/api/tickets/7/git/checkin")]
    [InlineData("POST", "/api/tickets/7/git/checkin/record")]
    [InlineData("POST", "/api/projects/alpha/tfvc/sync")]
    [InlineData("POST", "/api/projects/alpha/stack/start")]
    [InlineData("POST", "/api/projects/alpha/stack/stop")]
    [InlineData("POST", "/api/projects/alpha/dependencies/refresh")]
    [InlineData("POST", "/api/usage/check")]
    [InlineData("POST", "/api/usage/nudge")]
    [InlineData("POST", "/api/usage/signin")]
    [InlineData("POST", "/api/usage/account")]
    [InlineData("POST", "/api/system/pick-folder")]
    [InlineData("POST", "/api/hosts/bravo-host/checkin")]
    [InlineData("POST", "/api/hosts/bravo-host/orders")]
    [InlineData("POST", "/api/tickets/7/branch/accept")]
    [InlineData("GET", "/api/hosts/project-kit/alpha")]
    // Case and a trailing slash are the same route to the router, so they are to the table.
    [InlineData("post", "/API/Heartbeat/Start/")]
    public void A_copy_refuses_every_route_that_reaches_beyond_it(string method, string path)
    {
        Assert.NotNull(QaIsolation.RefusedRoute(method, path));
    }

    [Theory]
    [InlineData("GET", "/api/heartbeat/status")]
    [InlineData("GET", "/api/state")]
    [InlineData("GET", "/api/tickets")]
    [InlineData("POST", "/api/tickets")]
    [InlineData("PUT", "/api/tickets/7")]
    [InlineData("POST", "/api/tickets/7/transition")]
    [InlineData("POST", "/api/tickets/7/comments")]
    [InlineData("POST", "/api/tickets/7/git/merge")]
    [InlineData("POST", "/api/tickets/7/git/rebase")]
    [InlineData("GET", "/api/tickets/7/git/pr")]
    [InlineData("GET", "/api/steward")]
    [InlineData("GET", "/api/steward/plan")]
    [InlineData("GET", "/api/steward/plan/units/2/report")]
    [InlineData("GET", "/api/projects/alpha/stack")]
    [InlineData("GET", "/api/usage")]
    [InlineData("GET", "/api/config")]
    [InlineData("PUT", "/api/config")]
    [InlineData("POST", "/mcp")]
    [InlineData("GET", "/api/hosts")]
    [InlineData("GET", "/")]
    public void A_copy_still_serves_what_a_QA_run_is_there_to_exercise(string method, string path)
    {
        Assert.Null(QaIsolation.RefusedRoute(method, path));
    }

    // ---- Starting at all ----------------------------------------------------

    [Fact]
    public void A_copy_will_not_start_without_a_data_root_named_for_it()
    {
        Assert.NotNull(QaIsolation.StartupProblem(null, "http://localhost:5340"));
        Assert.NotNull(QaIsolation.StartupProblem("  ", "http://localhost:5340"));
    }

    [Fact]
    public void A_copy_will_not_start_over_a_checkouts_data_folder()
    {
        var checkout = Path.Combine(_root, "checkout");
        Directory.CreateDirectory(Path.Combine(checkout, "data"));
        File.WriteAllText(Path.Combine(checkout, "Whetstone.csproj"), "<Project />");

        Assert.NotNull(QaIsolation.StartupProblem(Path.Combine(checkout, "data"), "http://localhost:5340"));
        Assert.NotNull(QaIsolation.StartupProblem(Path.Combine(checkout, "data", "deeper"), "http://localhost:5340"));
    }

    [Fact]
    public void A_copy_will_not_start_on_the_live_port_or_on_no_port_at_all()
    {
        var data = Path.Combine(_root, "qa-data");
        Assert.NotNull(QaIsolation.StartupProblem(data, null));
        Assert.NotNull(QaIsolation.StartupProblem(data, "http://localhost:5338"));
        Assert.NotNull(QaIsolation.StartupProblem(data, "http://localhost:5340;http://127.0.0.1:5338/"));
    }

    [Fact]
    public void A_copy_starts_over_a_throwaway_folder_on_a_port_of_its_own()
    {
        var data = Path.Combine(_root, "qa-data");
        Assert.Null(QaIsolation.StartupProblem(data, "http://localhost:5340"));
        // A port that merely begins with the live one's digits is a different port.
        Assert.Null(QaIsolation.StartupProblem(data, "http://localhost:53380"));
    }

    // ---- What a project may point at ---------------------------------------

    [Fact]
    public void A_project_wholly_inside_the_copys_root_is_the_copys()
    {
        var cfg = new ProjectConfig { Name = "alpha", ProjectPath = Path.Combine(_root, "repos", "alpha") };
        Assert.Null(QaIsolation.Escapes(cfg, _root));
    }

    [Fact]
    public void A_project_that_names_a_real_repository_is_not()
    {
        var outside = Path.Combine(Path.GetTempPath(), "somewhere-real");
        Assert.NotNull(QaIsolation.Escapes(new ProjectConfig { Name = "alpha", ProjectPath = outside }, _root));
        // Inside by its repo, outside by where its worktrees go - and the reaper removes worktrees.
        Assert.NotNull(QaIsolation.Escapes(new ProjectConfig
        {
            Name = "alpha", ProjectPath = Path.Combine(_root, "repos", "alpha"), WorktreeRoot = outside,
        }, _root));
        // A sibling folder whose name starts with the root's is not inside it.
        Assert.NotNull(QaIsolation.Escapes(new ProjectConfig { Name = "alpha", ProjectPath = _root + "-other" }, _root));
    }

    [Fact]
    public void The_registry_of_a_copy_hides_a_project_that_reaches_outside_it()
    {
        var data = Path.Combine(_root, "data");
        var paths = new DashboardPaths(data);
        WriteProject(paths, "alpha", Path.Combine(data, "repos", "alpha"));
        WriteProject(paths, "bravo", Path.Combine(_root, "elsewhere", "bravo"));

        var copy = new ProjectRegistry(paths, On);
        Assert.Equal(new[] { "alpha" }, copy.Projects);
        Assert.False(copy.IsEnabled("bravo"));
        Assert.Equal("", copy.GetProjectConfig("bravo").ProjectPath);
        Assert.Equal("", copy.GetProjectConfig("bravo").ResolvedWorktreeRoot);

        // The live instance is not a copy, and every project is its own business.
        Assert.Equal(new[] { "alpha", "bravo" }, new ProjectRegistry(paths).Projects);
    }

    // ---- The services behind the routes ------------------------------------

    [Fact]
    public void A_copy_refuses_to_start_a_heartbeat_even_with_the_scripts_beside_it()
    {
        var paths = new DashboardPaths(Path.Combine(_root, "data"));
        File.WriteAllText(paths.GlobalHeartbeatScript, "exit 0");
        File.WriteAllText(paths.GlobalWorkerScript, "exit 0");

        var hb = new HeartbeatManager(paths, On);
        Assert.True(hb.ScriptExists);

        var (started, message) = hb.Start();
        Assert.False(started);
        Assert.Contains(QaIsolation.EnvVar, message);
        Assert.False(hb.IsRunning);
        Assert.False(File.Exists(paths.GlobalLockFile));

        Assert.False(hb.Stop().success);
        Assert.False(hb.RequestStopAfterCycle().success);
        Assert.False(hb.Wake());
    }

    [Fact]
    public async Task A_copy_sends_no_notification()
    {
        var paths = new DashboardPaths(Path.Combine(_root, "data"));
        // A script that would leave a mark if it ever ran.
        var mark = Path.Combine(_root, "sent.txt");
        File.WriteAllText(Path.Combine(paths.DataRoot, "notify-send.ps1"), $"Set-Content -Path '{mark}' -Value sent");

        var outcome = await new NotificationSender(paths, NullLogger<NotificationSender>.Instance, On)
            .SendAsync("steward_question", "title", "message");

        Assert.False(outcome.Sent);
        Assert.Contains(QaIsolation.EnvVar, outcome.Message);
        Assert.False(File.Exists(mark));
    }

    [Fact]
    public void A_copy_has_no_tracker_whatever_its_settings_say()
    {
        var data = Path.Combine(_root, "data");
        var paths = new DashboardPaths(data);
        WriteProject(paths, "alpha", Path.Combine(data, "repos", "alpha"),
            """, "tracker": { "kind": "jira", "baseUrl": "https://alpha.atlassian.net", "email": "a@b.c", "token": "tok", "project": "PROJ" }""");

        var live = new IssueTrackerFactory(new ProjectRegistry(paths)).For("alpha");
        Assert.Equal(TrackerKind.Jira, live.Kind);
        Assert.True(live.IsConfigured);

        var copy = new IssueTrackerFactory(new ProjectRegistry(paths, On), On).For("alpha");
        Assert.Same(NoTracker.Instance, copy);
        Assert.False(copy.IsConfigured);
    }

    [Fact]
    public void A_copy_builds_no_agent_command()
    {
        var paths = new DashboardPaths(Path.Combine(_root, "data"));
        var factory = new AgentProviderFactory(new ProjectRegistry(paths, On), On);

        foreach (var provider in new[] { factory.Resolve(), factory.ResolveForAnalysis() })
        {
            Assert.IsType<RefusedAgentProvider>(provider);
            var ex = Assert.Throws<InvalidOperationException>(() => provider.BuildFreeformRun(_root, _root));
            Assert.Contains(QaIsolation.EnvVar, ex.Message);
            Assert.Throws<InvalidOperationException>(() => provider.BuildAnalysisRun(_root, "{}", _root));
        }
    }

    [Fact]
    public async Task A_copy_neither_reads_nor_renews_the_users_Claude_login()
    {
        var paths = new DashboardPaths(Path.Combine(_root, "data"));
        var registry = new ProjectRegistry(paths, On);
        var usage = new ClaudeUsageService(NullLogger<ClaudeUsageService>.Instance,
            new AgentProviderFactory(registry, On), registry, On);

        Assert.Contains(QaIsolation.EnvVar, await usage.EnsureSignedInAsync() ?? "");
        Assert.False(await usage.NudgeAsync());
        Assert.False(usage.LaunchSignIn().Ok);
        await usage.RefreshAsync(force: true);
        Assert.Equal(UsageSnapshot.Empty, usage.Current);
    }

    private static void WriteProject(DashboardPaths paths, string key, string repo, string extra = "")
    {
        Directory.CreateDirectory(paths.ProjectDir(key));
        var json = "{ \"projectPath\": " + System.Text.Json.JsonSerializer.Serialize(repo) + extra + " }";
        File.WriteAllText(paths.ProjectConfig(key), json);
    }
}
