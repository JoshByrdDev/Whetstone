using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// The dashboard's own runs on a plan's monthly API credits. What is pinned: it is off unless
/// switched on, it only ever reaches the roles it names, a run on a key carries the key in its
/// environment and nowhere else, and a key that refuses a run hands it on - to the next key, then to
/// plan usage - rather than failing it.
/// </summary>
public class ApiCreditsTests
{
    private const string Key1 = "sk-ant-api03-first-0000000000000000AAAA";
    private const string Key2 = "sk-ant-api03-second-000000000000000BBBB";

    private sealed record Fixture(string Root, AgentProviderFactory Factory, ApiCreditsState State);

    /// <summary>A data root with this config, and - unless told otherwise - the runner installed.</summary>
    private static Fixture With(JsonObject? credits, bool installed = true, bool isolated = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "whetstone-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        var config = new JsonObject();
        if (credits is not null) config[ClaudeApiCredits.ConfigKey] = credits;
        File.WriteAllText(Path.Combine(root, "config.json"), config.ToJsonString());
        if (installed)
        {
            Directory.CreateDirectory(Path.Combine(root, "agent-sdk", "node_modules", "@anthropic-ai", "claude-agent-sdk"));
            File.WriteAllText(Path.Combine(root, "agent-sdk", "run.mjs"), "// test");
        }
        var state = new ApiCreditsState();
        var iso = isolated ? new QaIsolation(true) : QaIsolation.Off;
        var paths = new DashboardPaths(root);
        return new Fixture(root, new AgentProviderFactory(new ProjectRegistry(paths, iso), iso, state), state);
    }

    private static JsonObject On(params string[] roles) => new()
    {
        ["enabled"] = true,
        ["roles"] = new JsonArray(roles.Select(r => (JsonNode)r).ToArray()),
        ["keys"] = new JsonArray(
            new JsonObject { ["name"] = "Default org", ["key"] = Key1 },
            new JsonObject { ["name"] = "Second org", ["key"] = Key2 }),
    };

    private static string Scratch() => Directory.CreateTempSubdirectory("cdh-credits-").FullName;

    [Fact]
    public void OffByDefaultEveryRoleRunsOnTheCli()
    {
        var f = With(credits: null);
        foreach (var role in ClaudeApiCredits.EligibleRoles)
        {
            var p = f.Factory.For(role);
            Assert.Null(p.ApiCreditName);
            Assert.Equal("claude", p.BuildAnalysisRun("C:/repo", "{}", Scratch()).FileName);
        }
    }

    [Fact]
    public void SavedButSwitchedOffStillRunsOnTheCli()
    {
        var off = On(AgentRoles.Analysis);
        off["enabled"] = false;
        Assert.Null(With(off).Factory.For(AgentRoles.Analysis).ApiCreditName);
    }

    [Fact]
    public void AnAnalysisRunGoesThroughTheRunnerWithTheKeyInItsEnvironmentOnly()
    {
        var f = With(On(AgentRoles.Analysis));
        var p = f.Factory.For(AgentRoles.Analysis);
        Assert.Equal("Default org", p.ApiCreditName);

        var run = p.BuildAnalysisRun("C:/repo", """{"type":"object"}""", Scratch());
        Assert.Equal("node", run.FileName);
        Assert.Equal(Path.Combine(f.Root, "agent-sdk", "run.mjs"), run.Arguments[0]);
        // The CLI's own flags follow, unchanged, because the runner reads them the same way.
        Assert.Equal(
            new[] { "-p", "--model", "claude-opus-5-5", "--effort", "medium", "--output-format", "stream-json", "--verbose",
                    "--json-schema", """{"type":"object"}""",
                    "--tools", "Read,Grep,Glob,Bash", "--permission-mode", "bypassPermissions" },
            run.Arguments.Skip(1));
        Assert.Equal(Key1, run.Environment["ANTHROPIC_API_KEY"]);
        Assert.DoesNotContain(run.Arguments, a => a.Contains(Key1));
        // No account login rides along: a run that could see one could spend the plan instead.
        Assert.False(run.Environment.ContainsKey("CLAUDE_CONFIG_DIR"));
    }

    [Fact]
    public void OnlyTheRolesTickedSpendCredits()
    {
        var f = With(On(AgentRoles.Analysis));
        Assert.Null(f.Factory.For(AgentRoles.Applier).ApiCreditName);
        Assert.Equal("Default org", f.Factory.For(AgentRoles.Analysis).ApiCreditName);
    }

    [Fact]
    public void APipelineRoleCanNeverBeTicked()
    {
        var cfg = On("implement", "qa-test", AgentRoles.Applier);
        var f = With(cfg);
        Assert.Equal(new[] { AgentRoles.Applier }, ClaudeApiCredits.Read(Config(cfg)).Roles);
        Assert.Null(f.Factory.For("implement").ApiCreditName);
    }

    [Fact]
    public void WithoutTheRunnerInstalledRunsStayOnPlanUsage()
    {
        var f = With(On(AgentRoles.Analysis), installed: false);
        Assert.Null(f.Factory.For(AgentRoles.Analysis).ApiCreditName);
        Assert.NotNull(ClaudeApiCredits.RunnerUnavailable(new DashboardPaths(f.Root)));
    }

    [Fact]
    public void AQaCopyRunsNothingOnCreditsEither()
    {
        var f = With(On(AgentRoles.Analysis), isolated: true);
        var p = f.Factory.For(AgentRoles.Analysis);
        Assert.Null(p.ApiCreditName);
        Assert.Throws<InvalidOperationException>(() => p.BuildAnalysisRun("C:/repo", "{}", Scratch()));
    }

    [Fact]
    public void ARefusedKeyHandsTheRunToTheNextKeyThenToPlanUsage()
    {
        var f = With(On(AgentRoles.Analysis));
        var first = f.Factory.For(AgentRoles.Analysis);

        var second = first.WhenKeyRefuses!("Your credit balance is too low to access the Anthropic API.");
        Assert.Equal("Second org", second.ApiCreditName);
        Assert.Equal("credits used up for this cycle", f.State.RefusalOf("Default org")!.Why);

        var plan = second.WhenKeyRefuses!("Invalid API key · Fix external API key");
        Assert.Null(plan.ApiCreditName);
        Assert.Null(plan.WhenKeyRefuses);
        Assert.Equal("claude", plan.BuildAnalysisRun("C:/repo", "{}", Scratch()).FileName);

        // And the next run does not ask a refused key first.
        Assert.Null(f.Factory.For(AgentRoles.Analysis).ApiCreditName);
    }

    [Fact]
    public void ARefusalIsForgottenAfterAFewHours()
    {
        var now = DateTimeOffset.Parse("2026-11-05T12:00:00Z");
        var state = new ApiCreditsState(() => now);
        state.Refused("Default org", "credits used up for this cycle");
        Assert.NotNull(state.RefusalOf("Default org"));
        now += ApiCreditsState.RetryAfter;
        Assert.Null(state.RefusalOf("Default org"));
    }

    [Theory]
    [InlineData("Your credit balance is too low to access the Anthropic API. Please go to Plans & Billing to upgrade or purchase credits.", true)]
    [InlineData("Credit balance too low · Add funds: https://platform.claude.com/settings/billing", true)]
    [InlineData("the Agent SDK runner exited with code 1. Invalid API key · Fix external API key", true)]
    [InlineData("the run ended as 'error_max_structured_output_retries'", false)]
    [InlineData("the analysis timed out after 20 minutes", false)]
    [InlineData(null, false)]
    public void OnlyARefusalByTheKeyCountsAsOne(string? error, bool refused) =>
        Assert.Equal(refused, ClaudeApiCredits.IsRefusedByKey(error));

    [Fact]
    public void SavingKeepsAKeyThePageNeverHad()
    {
        var stored = JsonNode.Parse($$"""{"enabled":true,"roles":["analysis"],"keys":[{"name":"Default org","key":"{{Key1}}"},{"name":"Second org","key":"{{Key2}}"}]}""");
        // What the page sends back: names and flags, no keys - except the one being replaced.
        var incoming = JsonNode.Parse("""{"enabled":true,"roles":["analysis","applier"],"keys":[{"name":"Default org","keySet":true,"hint":"…AAAA"},{"name":"New org","key":"sk-ant-new"}]}""");

        var saved = ClaudeApiCredits.Normalize(incoming, stored);
        var keys = saved["keys"]!.AsArray().Select(k => (k!["name"]!.GetValue<string>(), k["key"]!.GetValue<string>())).ToList();
        Assert.Equal(new[] { ("Default org", Key1), ("New org", "sk-ant-new") }, keys);
        Assert.Equal(new[] { "analysis", "applier" }, saved["roles"]!.AsArray().Select(r => r!.GetValue<string>()));
        Assert.DoesNotContain("keySet", saved.ToJsonString());
    }

    [Fact]
    public void ARowWithNoKeyAndNothingSavedIsDropped()
    {
        var saved = ClaudeApiCredits.Normalize(JsonNode.Parse("""{"enabled":true,"keys":[{"name":"Empty"}]}"""), null);
        Assert.Empty(saved["keys"]!.AsArray());
        // No roles sent at all reads as the default: the Classroom.
        Assert.Equal(ClaudeApiCredits.DefaultRoles, saved["roles"]!.AsArray().Select(r => r!.GetValue<string>()));
    }

    // ---- the retry itself, through a real process ---------------------------------------------

    /// <summary>
    /// A provider whose "CLI" is a one-line node script printing a closing envelope, so the retry in
    /// <see cref="AgentJsonRunner"/> is exercised against a real exit code and a real stream.
    /// </summary>
    private sealed class ScriptedProvider(string? credit, string envelope, int exitCode, Func<string, AgentProvider>? _ = null)
        : AgentProvider
    {
        public override AgentProviderKind Kind => AgentProviderKind.Claude;
        public override string Executable => "node";
        public override string DisplayName => "scripted";
        public override string? ApiCreditName => credit;
        public int Runs;

        public override AgentRun BuildAnalysisRun(string workDir, string jsonSchema, string scratchDir)
        {
            Runs++;
            var run = new AgentRun { FileName = "node" };
            run.Arguments.AddRange(new[] { "-e", $"process.stdin.resume();process.stdin.on('end',()=>{{console.log({JsonSerializer.Serialize(envelope)});process.exit({exitCode});}})" });
            return run;
        }

        public override AgentRun BuildFreeformRun(string workDir, string scratchDir) => throw new NotSupportedException();

        public override AgentOutcome ReadAnalysis(AgentRun run, string stdout)
        {
            var env = JsonDocument.Parse(stdout.Trim().Split('\n')[^1]).RootElement.Clone();
            return env.TryGetProperty("structured_output", out var s)
                ? new AgentOutcome(s, null, 0, 0, 0, 0, null)
                : new AgentOutcome(null, env.GetProperty("result").GetString(), 0, 0, 0, 0, null);
        }

        public ScriptedProvider WithFallback(Func<string, AgentProvider> f) =>
            new(credit, envelope, exitCode, f) { WhenKeyRefuses = f };
    }

    private sealed class Steps : IRunProgress
    {
        public readonly List<string> Said = new();
        public void Expect(double seconds, int steps) { }
        public void Step(string activity) => Said.Add(activity);
    }

    [Fact]
    public async Task ARunRefusedOnCreditsIsRunAgainOnPlanUsage()
    {
        var plan = new ScriptedProvider(null, """{"type":"result","structured_output":{"ok":true}}""", 0, null);
        var refused = new ScriptedProvider("Default org",
            """{"type":"result","is_error":true,"result":"Credit balance is too low"}""", 1)
            .WithFallback(_ => plan);
        var steps = new Steps();

        var result = await AgentJsonRunner.RunAsync(refused, Path.GetTempPath(), Scratch(), "prompt", "{}",
            steps, TimeSpan.FromMinutes(1), "analysis", NullLogger.Instance, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.True(result.Outcome!.Structured!.Value.GetProperty("ok").GetBoolean());
        Assert.Equal(1, plan.Runs);
        Assert.Contains(steps.Said, s => s.Contains("running on plan usage instead"));
    }

    [Fact]
    public async Task AFailureThatIsNotTheKeysIsReportedAsItIs()
    {
        var plan = new ScriptedProvider(null, """{"type":"result","structured_output":{"ok":true}}""", 0, null);
        var broken = new ScriptedProvider("Default org",
            """{"type":"result","is_error":true,"result":"the run ended as 'error_max_structured_output_retries'"}""", 1, null)
            .WithFallback(_ => plan);

        var result = await AgentJsonRunner.RunAsync(broken, Path.GetTempPath(), Scratch(), "prompt", "{}",
            new Steps(), TimeSpan.FromMinutes(1), "analysis", NullLogger.Instance, CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Equal(0, plan.Runs);
    }

    private static IReadOnlyDictionary<string, JsonElement> Config(JsonObject credits) =>
        JsonDocument.Parse(new JsonObject { [ClaudeApiCredits.ConfigKey] = credits.DeepClone() }.ToJsonString())
            .RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
}
