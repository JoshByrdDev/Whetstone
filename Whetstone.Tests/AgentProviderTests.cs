using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// The provider layer is the only thing standing between the rest of the dashboard and a CLI's
/// private conventions, so what is pinned here is the CONTRACT rather than the wiring: the argv
/// Claude has always been given (which must not drift while a second provider is added), and the
/// normalisation that turns each CLI's own answer into one shape the callers can read.
/// </summary>
public class AgentProviderTests
{
    private static string Scratch()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cdh-agent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ---- Claude: the incumbent must not move --------------------------------

    /// <summary>
    /// The exact flags the Classroom analysis runs with. This test exists to fail loudly if a
    /// refactor quietly changes what the read-only run is allowed to do — dropping <c>--tools</c>
    /// here would silently hand a Write tool to a run the docs promise cannot edit.
    /// <para>
    /// <c>stream-json</c> is pinned for a different reason: the queue panel's progress comes from
    /// reading those events as they arrive, so going back to <c>json</c> would not fail anything —
    /// the document would still be produced — it would just silently return the panel to a bar that
    /// cannot move. And <c>--verbose</c> is not a logging preference: the CLI refuses to stream
    /// under <c>-p</c> without it.
    /// </para>
    /// </summary>
    [Fact]
    public void ClaudeAnalysisRunKeepsItsExactArgv()
    {
        var run = new ClaudeAgentProvider().BuildAnalysisRun("C:/repo", """{"type":"object"}""", Scratch());

        Assert.Equal("claude", run.FileName);
        Assert.Equal(
            new[] { "-p", "--model", "claude-opus-5-5", "--output-format", "stream-json", "--verbose",
                    "--json-schema", """{"type":"object"}""",
                    "--tools", "Read,Grep,Glob,Bash", "--permission-mode", "bypassPermissions" },
            run.Arguments);
        // No environment to set and no scratch to clean: Claude is configured entirely by flags.
        Assert.Empty(run.Environment);
        Assert.Null(run.ScratchDir);
        // It answers on stdout, so nothing should be looking for a result file.
        Assert.Null(run.ResultPath);
    }

    [Fact]
    public void ClaudeFreeformRunKeepsItsExactArgv()
    {
        var run = new ClaudeAgentProvider().BuildFreeformRun("C:/repo", Scratch());

        Assert.Equal("claude", run.FileName);
        Assert.Equal(new[] { "-p", "--model", "claude-opus-5-5", "--permission-mode", "bypassPermissions" }, run.Arguments);
        Assert.Empty(run.Environment);
    }

    /// <summary>
    /// The setting has to reach the argv, and it is the one thing about it nothing else checks: a
    /// model that is merely stored is a Settings panel naming a model no run ever uses, and the only
    /// place that surfaces is the model name stamped on a finished analysis.
    /// </summary>
    [Fact]
    public void ClaudeRunsOnTheModelItWasGiven()
    {
        var analysis = new ClaudeAgentProvider("claude-fable-5-1").BuildAnalysisRun("C:/repo", "{}", Scratch());
        var freeform = new ClaudeAgentProvider("claude-fable-5-1").BuildFreeformRun("C:/repo", Scratch());

        Assert.Equal("claude-fable-5-1", ArgAfter(analysis, "--model"));
        Assert.Equal("claude-fable-5-1", ArgAfter(freeform, "--model"));
    }

    /// <summary>
    /// Effort has to reach the argv for the same reason the model does, and more urgently: Claude
    /// Code's own default moves with the model (Opus 5 ran at high, Opus 5.5 at medium), so a setting
    /// that is merely stored would let a model change quietly lower how hard every run thinks.
    /// </summary>
    [Fact]
    public void ClaudeRunsAtTheEffortItWasGiven()
    {
        var analysis = new ClaudeAgentProvider("claude-opus-5-5", "high").BuildAnalysisRun("C:/repo", "{}", Scratch());
        var freeform = new ClaudeAgentProvider("claude-opus-5-5", "high").BuildFreeformRun("C:/repo", Scratch());

        Assert.Equal("high", ArgAfter(analysis, "--effort"));
        Assert.Equal("high", ArgAfter(freeform, "--effort"));
    }

    /// <summary>Blank effort means "the CLI's default", not a bare flag swallowing the next one.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankEffortIsLeftToTheCli(string? effort)
    {
        var run = new ClaudeAgentProvider("claude-opus-5-5", effort).BuildAnalysisRun("C:/repo", "{}", Scratch());
        Assert.DoesNotContain("--effort", run.Arguments);
    }

    /// <summary>The factory reads the same setting the heartbeat does, for the pipeline and the Classroom alike.</summary>
    [Fact]
    public void TheConfiguredEffortReachesBothClaudeRuns()
    {
        var factory = FactoryWith(
            ("agentProvider", "claude"),
            ("workerModel", "claude-opus-5-5"),
            ("workerReasoningEffort", "xhigh"));

        Assert.Equal("xhigh", ArgAfter(factory.For(AgentRoles.Applier).BuildFreeformRun("C:/repo", Scratch()), "--effort"));
        Assert.Equal("xhigh", ArgAfter(factory.For(AgentRoles.Analysis).BuildAnalysisRun("C:/repo", "{}", Scratch()), "--effort"));
    }

    /// <summary>
    /// Unset is medium, not the CLI's default — which for Claude Code moves with the model, so leaving
    /// it there would let a model change quietly move how hard the Classroom thinks.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnUnsetEffortRunsAtMedium(string? stored)
    {
        var settings = new List<(string, string)> { ("agentProvider", "claude"), ("workerModel", "claude-opus-5-5") };
        if (stored != null) settings.Add(("workerReasoningEffort", stored));
        var factory = FactoryWith(settings.ToArray());

        Assert.Equal(AgentProviderFactory.DefaultEffort, factory.Role(AgentRoles.Analysis).Effort);
        Assert.Equal("medium", ArgAfter(factory.For(AgentRoles.Analysis).BuildAnalysisRun("C:/repo", "{}", Scratch()), "--effort"));
    }

    /// <summary>Codex takes the same setting, in its own spelling, on both of the dashboard's runs.</summary>
    [Fact]
    public void CodexRunsAtTheConfiguredEffort()
    {
        var factory = FactoryWith(("agentProvider", "codex"), ("workerReasoningEffort", "xhigh"));

        Assert.Equal("model_reasoning_effort=xhigh", ArgAfter(factory.For(AgentRoles.Analysis).BuildAnalysisRun("C:/repo", "{}", Scratch()), "-c"));
        Assert.Equal("model_reasoning_effort=xhigh", ArgAfter(factory.For(AgentRoles.Applier).BuildFreeformRun("C:/repo", Scratch()), "-c"));
    }

    /// <summary>
    /// Blank is not a model. An empty string in config.json would otherwise reach the CLI as a bare
    /// `--model` followed by the next flag, which fails the run over a field nobody set.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ClaudeFallsBackToItsDefaultModel(string? configured)
    {
        var run = new ClaudeAgentProvider(configured).BuildAnalysisRun("C:/repo", "{}", Scratch());
        Assert.Equal(ClaudeAgentProvider.DefaultModel, ArgAfter(run, "--model"));
    }

    /// <summary>
    /// The three places that name Claude's default — this class, the Settings catalogue, and the
    /// heartbeat's own table in <c>data/providers.ps1</c> — have to agree. When they drift, the
    /// panel shows one model and the run uses another, silently.
    /// </summary>
    [Fact]
    public void ClaudesDefaultMatchesTheHeartbeatsTable()
    {
        var ps1 = Path.Combine(RepoRoot(), "data", "providers.ps1");
        // Anchored to Claude's own block: every provider in that file declares a
        // DefaultWorkerModel, and an unanchored match reads whichever one is written first.
        var declared = Regex.Match(
            File.ReadAllText(ps1),
            @"Key\s*=\s*'claude'.*?DefaultWorkerModel\s*=\s*'([^']+)'",
            RegexOptions.Singleline);

        Assert.True(declared.Success, $"No Claude DefaultWorkerModel found in {ps1}");
        Assert.Equal(ClaudeAgentProvider.DefaultModel, declared.Groups[1].Value);
    }

    /// <summary>
    /// A factory reading one hand-written config, so the resolution rules can be exercised without
    /// a running dashboard. <see cref="ProjectRegistry"/> is what both paths read through.
    /// </summary>
    private static AgentProviderFactory FactoryWith(params (string Key, string Value)[] settings)
    {
        var root = Path.Combine(Path.GetTempPath(), "whetstone-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        var config = new JsonObject();
        foreach (var (key, value) in settings) config[key] = value;
        File.WriteAllText(Path.Combine(root, "config.json"), config.ToJsonString());
        return new AgentProviderFactory(new ProjectRegistry(new DashboardPaths(root)));
    }

    /// <summary>
    /// The model an analysis run would actually be launched with, whichever flag its CLI spells it
    /// as — Claude takes <c>--model</c>, codex and gemini take <c>-m</c>. Read off the argv rather
    /// than off the provider, because the argv is what the process gets.
    /// </summary>
    private static string? ModelOf(AgentProvider p)
    {
        var run = p.BuildAnalysisRun("C:/repo", "{}", Scratch());
        return ArgAfter(run, "--model") ?? ArgAfter(run, "-m");
    }

    /// <summary>
    /// The whole point of the separate setting: the Classroom can run a different model from the
    /// pipeline. A worker's output is checked by QA and by a human at review; an analysis is read
    /// once and believed, which is why it is the run worth paying more for.
    /// </summary>
    [Fact]
    public void TheAnalysisCanRunOnItsOwnModel()
    {
        var factory = FactoryWith(
            ("agentProvider", "claude"),
            ("workerModel", "claude-opus-5"),
            ("analysisModel", "claude-fable-5-1"));

        Assert.Equal("claude-opus-5", ModelOf(factory.For(AgentRoles.Applier)));
        Assert.Equal("claude-fable-5-1", ModelOf(factory.For(AgentRoles.Analysis)));
    }

    /// <summary>
    /// Unset means "follow the worker model" rather than "run the provider default". Stored as an
    /// absence on purpose: a copy would pin the analysis to whatever the worker model was the day it
    /// was saved, so raising the worker model later would leave the analysis behind on the old one.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnUnsetAnalysisModelFollowsTheWorkerModel(string? stored)
    {
        var settings = new List<(string, string)> { ("agentProvider", "claude"), ("workerModel", "claude-fable-5-1") };
        if (stored != null) settings.Add(("analysisModel", stored));

        Assert.Equal("claude-fable-5-1", ModelOf(FactoryWith(settings.ToArray()).For(AgentRoles.Analysis)));
    }

    /// <summary>
    /// The provider guard applies to the override too. A config left naming Claude's model after a
    /// switch to codex would otherwise fail every analysis with an error about a model name, which
    /// says nothing about the setting that caused it. It falls through to the worker model, the
    /// next thing the legacy settings named.
    /// </summary>
    [Fact]
    public void AnAnalysisModelBelongingToAnotherProviderIsIgnored()
    {
        var factory = FactoryWith(
            ("agentProvider", "codex"),
            ("workerModel", "gpt-5.6-terra"),
            ("analysisModel", "claude-fable-5-1"));

        Assert.Equal("gpt-5.6-terra", ModelOf(factory.For(AgentRoles.Analysis)));
    }

    // ---- Roles ----------------------------------------------------------------

    /// <summary>
    /// The point of roles: two runs on two CLIs at once. Each takes its own provider, model and
    /// effort, and neither borrows from the other.
    /// </summary>
    [Fact]
    public void EachRoleRunsOnItsOwnProvider()
    {
        var factory = FactoryWithJson("""
            { "agentRoles": {
                "analysis": { "provider": "codex",  "model": "gpt-5.6-sol", "effort": "high" },
                "applier":  { "provider": "claude", "model": "opus",        "effort": "low" } } }
            """);

        var analysis = factory.For(AgentRoles.Analysis);
        var applier = factory.For(AgentRoles.Applier);
        Assert.IsType<CodexAgentProvider>(analysis);
        Assert.IsType<ClaudeAgentProvider>(applier);
        Assert.Equal("gpt-5.6-sol", ModelOf(analysis));
        Assert.Equal("opus", ModelOf(applier));
        Assert.Equal("model_reasoning_effort=high", ArgAfter(analysis.BuildAnalysisRun("C:/repo", "{}", Scratch()), "-c"));
        Assert.Equal("low", ArgAfter(applier.BuildFreeformRun("C:/repo", Scratch()), "--effort"));
    }

    /// <summary>A role entry outranks every legacy setting, including the fleet-wide provider.</summary>
    [Fact]
    public void ARoleEntryOutranksTheLegacySettings()
    {
        var factory = FactoryWithJson("""
            { "agentProvider": "claude", "workerModel": "claude-opus-5", "workerReasoningEffort": "xhigh",
              "agentRoles": { "analysis": { "provider": "gemini", "model": "flash" } } }
            """);

        var role = factory.Role(AgentRoles.Analysis);
        Assert.Equal(new AgentRole("gemini", "flash", "xhigh"), role);
        // A role with no entry still reads the legacy settings.
        Assert.Equal(new AgentRole("claude", "claude-opus-5", "xhigh"), factory.Role(AgentRoles.Applier));
    }

    /// <summary>
    /// A role's model that belongs to another provider is the provider's default, never the
    /// mismatched name; and reflection's default is the provider's reflection model, not its worker.
    /// </summary>
    [Fact]
    public void AMismatchedRoleModelFallsBackToThatProvidersDefault()
    {
        var factory = FactoryWithJson("""
            { "agentRoles": {
                "analysis":   { "provider": "codex", "model": "claude-opus-5-5" },
                "reflection": { "provider": "codex", "model": "" } } }
            """);

        Assert.Equal(CodexAgentProvider.DefaultModel, factory.Role(AgentRoles.Analysis).Model);
        Assert.Equal(CodexAgentProvider.DefaultReflectionModel, factory.Role(AgentRoles.Reflection).Model);
    }

    /// <summary>
    /// The rate-limit meter reads Claude's subscription, so it has something to say while any one
    /// role runs on Claude, and nothing once none does.
    /// </summary>
    [Fact]
    public void TheMeterFollowsWhetherAnyRoleUsesClaude()
    {
        var allCodex = "{ \"agentRoles\": {" + string.Join(",",
            AgentRoles.All.Select(r => $"\"{r}\": {{ \"provider\": \"codex\" }}")) + "} }";
        Assert.False(FactoryWithJson(allCodex).UsesClaude);

        var oneClaude = allCodex.Replace("\"qa-test\": { \"provider\": \"codex\" }", "\"qa-test\": { \"provider\": \"claude\" }");
        Assert.True(FactoryWithJson(oneClaude).UsesClaude);
    }

    /// <summary>
    /// The heartbeat's own list of the roles it runs has to match this one. A role missing there
    /// would leave its CLI unchecked at startup and its processes unreaped.
    /// </summary>
    [Fact]
    public void TheHeartbeatsRoleListMatches()
    {
        var ps1 = File.ReadAllText(Path.Combine(RepoRoot(), "data", "heartbeat-common.ps1"));
        var declared = Regex.Match(ps1, @"\$AgentRoleKeys\s*=\s*@\(([^)]*)\)");
        Assert.True(declared.Success, "No $AgentRoleKeys in heartbeat-common.ps1");
        var keys = Regex.Matches(declared.Groups[1].Value, "'([^']+)'").Select(m => m.Groups[1].Value);
        Assert.Equal(AgentRoles.HeartbeatRoles, keys);
    }

    private static AgentProviderFactory FactoryWithJson(string json)
    {
        var root = Path.Combine(Path.GetTempPath(), "whetstone-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "config.json"), json);
        return new AgentProviderFactory(new ProjectRegistry(new DashboardPaths(root)));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Whetstone.csproj"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    /// <summary>The value a flag was given, or null when the flag is absent.</summary>
    private static string? ArgAfter(AgentRun run, string flag)
    {
        var i = run.Arguments.IndexOf(flag);
        return i >= 0 && i + 1 < run.Arguments.Count ? run.Arguments[i + 1] : null;
    }

    [Fact]
    public void ClaudeReadsStructuredOutputAndUsage()
    {
        var stdout = """
        {"structured_output":{"headline":"It was the cache"},
         "usage":{"input_tokens":1200,"output_tokens":3400,
                  "cache_read_input_tokens":98000,"cache_creation_input_tokens":5000},
         "modelUsage":{"claude-opus-5":{}}}
        """;

        var outcome = new ClaudeAgentProvider().ReadAnalysis(new AgentRun { FileName = "claude" }, stdout);

        Assert.Null(outcome.Error);
        Assert.Equal("It was the cache", outcome.Structured!.Value.GetProperty("headline").GetString());
        Assert.Equal(1200, outcome.InputTokens);
        Assert.Equal(3400, outcome.OutputTokens);
        Assert.Equal(98000, outcome.CacheReadTokens);
        Assert.Equal(5000, outcome.CacheCreationTokens);
        Assert.Equal("claude-opus-5", outcome.Model);
    }

    /// Ticket 73: the CLI's own Haiku side calls sat first in modelUsage, and the Opus analysis was
    /// labelled Haiku.
    [Fact]
    public void ClaudeNamesTheModelThatDidTheWorkNotTheFirstOneListed()
    {
        var stdout = """
        {"structured_output":{"headline":"It was the cache"},
         "modelUsage":{
           "claude-haiku-4-5-20251001":{"inputTokens":900,"outputTokens":40,"cacheReadInputTokens":0},
           "claude-opus-5-5":{"inputTokens":60,"outputTokens":14000,"cacheReadInputTokens":1200000}}}
        """;

        var outcome = new ClaudeAgentProvider().ReadAnalysis(new AgentRun { FileName = "claude" }, stdout);

        Assert.Equal("claude-opus-5-5", outcome.Model);
    }

    [Fact]
    public void ClaudeReportsAnErroredRunRatherThanAnEmptyDocument()
    {
        var stdout = """{"is_error":true,"result":"rate limit reached"}""";

        var outcome = new ClaudeAgentProvider().ReadAnalysis(new AgentRun { FileName = "claude" }, stdout);

        Assert.Equal("rate limit reached", outcome.Error);
        Assert.Null(outcome.Structured);
    }

    [Fact]
    public void ClaudeSurvivesOutputThatIsNotJson()
    {
        var outcome = new ClaudeAgentProvider().ReadAnalysis(new AgentRun { FileName = "claude" }, "command not found");

        Assert.NotNull(outcome.Error);
        Assert.Null(outcome.Structured);
    }

    /// <summary>
    /// The run streams, so the document arrives as the last event of a conversation rather than as
    /// the whole of stdout. Everything before the closing envelope — session banners, the model's
    /// own chatter, every tool call — must be walked past rather than parsed.
    /// </summary>
    [Fact]
    public void ClaudeReadsTheDocumentOutOfAStream()
    {
        var stdout = string.Join('\n',
            """{"type":"system","subtype":"init","tools":["Read","Bash"]}""",
            """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Read","input":{"file_path":"C:/repo/src/Cache.cs"}}]}}""",
            """{"type":"user","message":{"content":[{"type":"tool_result","content":"…"}]}}""",
            """{"type":"result","subtype":"success","is_error":false,"structured_output":{"headline":"It was the cache"},"usage":{"input_tokens":1200,"output_tokens":3400},"modelUsage":{"claude-opus-5":{}}}""",
            "");

        var outcome = new ClaudeAgentProvider().ReadAnalysis(new AgentRun { FileName = "claude" }, stdout);

        Assert.Null(outcome.Error);
        Assert.Equal("It was the cache", outcome.Structured!.Value.GetProperty("headline").GetString());
        Assert.Equal(1200, outcome.InputTokens);
        Assert.Equal("claude-opus-5", outcome.Model);
    }

    /// <summary>A stream that ends in a failure still reports the sentence, not "no document".</summary>
    [Fact]
    public void ClaudeReportsAStreamThatEndedInError()
    {
        var stdout = string.Join('\n',
            """{"type":"system","subtype":"init"}""",
            """{"type":"result","is_error":true,"result":"rate limit reached"}""");

        var outcome = new ClaudeAgentProvider().ReadAnalysis(new AgentRun { FileName = "claude" }, stdout);

        Assert.Equal("rate limit reached", outcome.Error);
        Assert.Null(outcome.Structured);
    }

    /// <summary>
    /// The failure that has no sentence. A run that exhausted its <c>StructuredOutput</c> retries
    /// ends with <c>result</c> absent, and the old answer - "the run reported an error" - told the
    /// reader nothing they had not already worked out from the exit code.
    /// </summary>
    [Fact]
    public void ClaudeNamesTheSubtypeAndSessionWhenThereIsNoSentence()
    {
        var stdout = string.Join('\n',
            """{"type":"system","subtype":"init"}""",
            """{"type":"result","subtype":"error_during_execution","is_error":true,"session_id":"7536d706"}""");

        var outcome = new ClaudeAgentProvider().ReadAnalysis(new AgentRun { FileName = "claude" }, stdout);

        Assert.Contains("error_during_execution", outcome.Error);
        // The transcript holding the rejected calls is findable by this and nothing else.
        Assert.Contains("7536d706", outcome.Error);
        Assert.Null(outcome.Structured);
    }

    /// <summary>The id is worth having even when the envelope did manage a sentence.</summary>
    [Fact]
    public void ClaudeKeepsTheSessionAlongsideAReportedReason()
    {
        var outcome = new ClaudeAgentProvider().ReadAnalysis(new AgentRun { FileName = "claude" },
            """{"type":"result","is_error":true,"result":"rate limit reached","session_id":"abc123"}""");

        Assert.Equal("rate limit reached (session abc123)", outcome.Error);
    }

    [Fact]
    public void ClaudeReadsAToolCallAsOneStep()
    {
        var provider = new ClaudeAgentProvider();

        var read = provider.ReadProgress(
            """{"type":"assistant","message":{"content":[{"type":"text","text":"Let me look."},{"type":"tool_use","name":"Read","input":{"file_path":"C:/repo/src/Cache.cs"}}]}}""");
        var bash = provider.ReadProgress(
            """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Bash","input":{"command":"git log\n  -20"}}]}}""");

        // The path is cut to its leaf and the command is flattened to one line: this ends up in a
        // caption two inches wide, and a wrapped absolute path says less than a file name.
        Assert.Equal("Read Cache.cs", read!.Value.ToString());
        Assert.Equal("Bash git log -20", bash!.Value.ToString());
    }

    /// <summary>
    /// Everything that is not a tool call is silence. A run's step count is meant to be comparable
    /// with the ones before it, so counting thinking blocks or session banners would make the
    /// denominator a measure of how chatty the model was.
    /// </summary>
    [Theory]
    [InlineData("""{"type":"system","subtype":"init"}""")]
    [InlineData("""{"type":"assistant","message":{"content":[{"type":"text","text":"Nearly there."}]}}""")]
    [InlineData("""{"type":"result","structured_output":{}}""")]
    [InlineData("not json at all")]
    [InlineData("")]
    public void ClaudeReportsNoStepForAnythingThatIsNotAToolCall(string line)
    {
        Assert.Null(new ClaudeAgentProvider().ReadProgress(line));
    }

    // ---- Codex --------------------------------------------------------------

    /// <summary>
    /// The analysis run must be read-only by ENFORCEMENT. `--sandbox read-only` is the whole promise
    /// the Classroom makes about touching a real repo, and `--ask-for-approval never` is only safe
    /// alongside it because this run has no MCP servers to stall on.
    /// </summary>
    [Fact]
    public void CodexAnalysisRunIsSandboxedReadOnly()
    {
        var run = new CodexAgentProvider().BuildAnalysisRun("C:/repo", """{"type":"object"}""", Scratch());

        Assert.Equal("codex", run.FileName);
        Assert.Contains("--sandbox", run.Arguments);
        Assert.Equal("read-only", run.Arguments[run.Arguments.IndexOf("--sandbox") + 1]);
        Assert.Equal("never", run.Arguments[run.Arguments.IndexOf("--ask-for-approval") + 1]);
        Assert.DoesNotContain("--dangerously-bypass-approvals-and-sandbox", run.Arguments);
        // The prompt goes on stdin.
        Assert.Equal("-", run.Arguments[^1]);
    }

    /// <summary>
    /// CODEX_HOME is how a run is stopped from inheriting the user's own MCP servers - Codex has no
    /// --strict-mcp-config, so replacing the directory IS the strict flag. The config written there
    /// must therefore declare no servers at all.
    /// </summary>
    [Fact]
    public void CodexRunGetsItsOwnHomeWithNoInheritedServers()
    {
        var scratch = Scratch();
        var run = new CodexAgentProvider().BuildAnalysisRun("C:/repo", """{"type":"object"}""", scratch);

        var home = Assert.Contains("CODEX_HOME", run.Environment);
        Assert.StartsWith(scratch, home);
        Assert.DoesNotContain("mcp_servers", File.ReadAllText(Path.Combine(home, "config.toml")));
        // The whole scratch tree is the caller's to delete: it can hold a copy of a login.
        Assert.Equal(scratch, run.ScratchDir);
    }

    /// <summary>The applier really does edit files, unattended - this is the one place the bypass belongs.</summary>
    [Fact]
    public void CodexFreeformRunBypassesApprovals()
    {
        var run = new CodexAgentProvider().BuildFreeformRun("C:/repo", Scratch());

        Assert.Contains("--dangerously-bypass-approvals-and-sandbox", run.Arguments);
        Assert.Equal("-", run.Arguments[^1]);
    }

    [Fact]
    public void CodexReadsUsageFromTheStreamAndTheDocumentFromTheFile()
    {
        var scratch = Scratch();
        var run = new CodexAgentProvider().BuildAnalysisRun("C:/repo", """{"type":"object"}""", scratch);
        File.WriteAllText(run.ResultPath!, """{"headline":"It was the cache"}""");

        var stdout = string.Join('\n',
            """{"type":"thread.started","thread_id":"t_1"}""",
            """{"type":"item.completed","item":{"id":"i1","type":"agent_message","text":"done"}}""",
            """{"type":"turn.completed","usage":{"input_tokens":1200,"cached_input_tokens":98000,"output_tokens":3400}}""");

        var outcome = new CodexAgentProvider().ReadAnalysis(run, stdout);

        Assert.Null(outcome.Error);
        Assert.Equal("It was the cache", outcome.Structured!.Value.GetProperty("headline").GetString());
        Assert.Equal(1200, outcome.InputTokens);
        Assert.Equal(3400, outcome.OutputTokens);
        // Codex's cached_input_tokens are the cache READ figure; it reports no cache-write number,
        // which stays zero rather than being invented from the input count.
        Assert.Equal(98000, outcome.CacheReadTokens);
        Assert.Equal(0, outcome.CacheCreationTokens);
    }

    [Fact]
    public void CodexReportsAFailedTurnAsAnError()
    {
        var scratch = Scratch();
        var run = new CodexAgentProvider().BuildAnalysisRun("C:/repo", """{"type":"object"}""", scratch);
        var stdout = """{"type":"turn.failed","error":{"message":"model overloaded"}}""";

        var outcome = new CodexAgentProvider().ReadAnalysis(run, stdout);

        Assert.Equal("model overloaded", outcome.Error);
        Assert.Null(outcome.Structured);
    }

    /// <summary>
    /// A run killed before it wrote anything must report that, not throw. The Classroom shows this
    /// message verbatim, so "no document" has to be a normal outcome rather than an exception.
    /// </summary>
    [Fact]
    public void CodexReportsAMissingDocumentRatherThanThrowing()
    {
        var run = new CodexAgentProvider().BuildAnalysisRun("C:/repo", """{"type":"object"}""", Scratch());

        var outcome = new CodexAgentProvider().ReadAnalysis(run, "");

        Assert.NotNull(outcome.Error);
        Assert.Null(outcome.Structured);
    }

    /// <summary>Interleaved noise on the stream must not stop the accounting being found.</summary>
    [Fact]
    public void CodexIgnoresNonJsonLinesOnTheStream()
    {
        var run = new CodexAgentProvider().BuildAnalysisRun("C:/repo", """{"type":"object"}""", Scratch());
        File.WriteAllText(run.ResultPath!, """{"headline":"ok"}""");
        var stdout = string.Join('\n',
            "warning: something on stderr got interleaved",
            "",
            """{"type":"turn.completed","usage":{"input_tokens":7,"output_tokens":9}}""");

        var outcome = new CodexAgentProvider().ReadAnalysis(run, stdout);

        Assert.Null(outcome.Error);
        Assert.Equal(7, outcome.InputTokens);
        Assert.Equal(9, outcome.OutputTokens);
    }

    /// <summary>The schema reaches Codex as a file, because that is the only way it takes one.</summary>
    [Fact]
    public void CodexWritesTheSchemaWhereItToldTheCliToLook()
    {
        var run = new CodexAgentProvider().BuildAnalysisRun("C:/repo", """{"type":"object","x":1}""", Scratch());

        var schemaPath = run.Arguments[run.Arguments.IndexOf("--output-schema") + 1];
        Assert.True(File.Exists(schemaPath));
        Assert.Equal(1, JsonDocument.Parse(File.ReadAllText(schemaPath)).RootElement.GetProperty("x").GetInt32());
    }

    /// <summary>
    /// Codex says the same things in its own vocabulary, and both providers must come out of
    /// <see cref="AgentProvider.ReadProgress"/> speaking one. Taken at <c>item.started</c>: a feed
    /// of what the run is doing now is worth nothing if each line appears once the thing is over.
    /// </summary>
    [Fact]
    public void CodexReadsAThreadItemAsOneStep()
    {
        var provider = new CodexAgentProvider();

        var started = provider.ReadProgress(
            """{"type":"item.started","item":{"type":"command_execution","command":"git diff --stat"}}""");
        var finished = provider.ReadProgress(
            """{"type":"item.completed","item":{"type":"command_execution","command":"git diff --stat"}}""");
        var thinking = provider.ReadProgress(
            """{"type":"item.started","item":{"type":"reasoning"}}""");

        Assert.Equal("Bash git diff --stat", started!.Value.ToString());
        // Already counted when it started; counting it twice would double every run's step total.
        Assert.Null(finished);
        Assert.Null(thinking);
    }

    // ---- Gemini -------------------------------------------------------------
    //
    // Nobody has run this CLI from here. These tests are therefore worth more than the other two
    // providers', not less: they are the only thing that has exercised the mapping at all, and what
    // they pin is the part that stays true whatever the CLI turns out to do in detail - the
    // read-only guarantee, the shape of the settings directory, and the normalisation onto the
    // canonical event that every caller downstream reads.

    /// <summary>
    /// The read-only guarantee, which lives somewhere different here than for the other two: this
    /// CLI's <c>--allowed-tools</c> is an auto-approve list rather than a restriction, so the run's
    /// own settings file is what makes writing impossible.
    /// </summary>
    [Fact]
    public void GeminiAnalysisRunCannotWrite()
    {
        var scratch = Scratch();
        var run = new GeminiAgentProvider().BuildAnalysisRun("C:/repo", """{"type":"object"}""", scratch);

        Assert.Equal("gemini", run.FileName);
        Assert.Equal("stream-json", run.Arguments[run.Arguments.IndexOf("--output-format") + 1]);
        // Auto-approve is safe only because of the allowlist below; the two are one decision.
        Assert.Equal("yolo", run.Arguments[run.Arguments.IndexOf("--approval-mode") + 1]);

        var home = Assert.Contains("GEMINI_CLI_HOME", run.Environment);
        var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(home, ".gemini", "settings.json"))).RootElement;

        var core = settings.GetProperty("tools").GetProperty("core").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("read_file", core);
        Assert.Contains("run_shell_command", core);
        Assert.DoesNotContain("write_file", core);
        Assert.DoesNotContain("replace", core);

        // And the backstop: a tool named in both lists is excluded, so this holds even where the
        // allowlist is ignored.
        var exclude = settings.GetProperty("tools").GetProperty("exclude").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("write_file", exclude);
        Assert.Contains("replace", exclude);
    }

    /// <summary>
    /// GEMINI_CLI_HOME names the PARENT of the <c>.gemini</c> directory. Point it at the directory
    /// itself and the CLI silently makes a <c>.gemini</c> inside it, finds none of the credentials
    /// copied there, and the run fails on sign-in rather than on anything that names this bug.
    /// </summary>
    [Fact]
    public void GeminiHomeIsTheParentOfTheConfigDirectory()
    {
        var scratch = Scratch();
        var run = new GeminiAgentProvider().BuildAnalysisRun("C:/repo", """{"type":"object"}""", scratch);

        var home = run.Environment["GEMINI_CLI_HOME"];
        Assert.StartsWith(scratch, home);
        Assert.True(Directory.Exists(Path.Combine(home, ".gemini")));
        Assert.True(File.Exists(Path.Combine(home, ".gemini", "settings.json")));
        // The whole scratch tree is the caller's to delete: it can hold a copy of a login.
        Assert.Equal(scratch, run.ScratchDir);
    }

    /// <summary>
    /// The same reason Codex gets a redirected home: this CLI has no --mcp-config, so a directory of
    /// our own IS the "only these servers" flag - and neither C# caller wants any.
    /// </summary>
    [Fact]
    public void GeminiRunInheritsNoMcpServers()
    {
        var run = new GeminiAgentProvider().BuildAnalysisRun("C:/repo", """{"type":"object"}""", Scratch());

        var settings = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(run.Environment["GEMINI_CLI_HOME"], ".gemini", "settings.json"))).RootElement;

        Assert.Equal(JsonValueKind.Object, settings.GetProperty("mcpServers").ValueKind);
        Assert.Empty(settings.GetProperty("mcpServers").EnumerateObject());
    }

    /// <summary>The applier really does edit files, unattended - and gets no tool restrictions.</summary>
    [Fact]
    public void GeminiFreeformRunKeepsItsTools()
    {
        var run = new GeminiAgentProvider().BuildFreeformRun("C:/repo", Scratch());

        Assert.Equal("yolo", run.Arguments[run.Arguments.IndexOf("--approval-mode") + 1]);
        var settings = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(run.Environment["GEMINI_CLI_HOME"], ".gemini", "settings.json"))).RootElement;
        Assert.False(settings.TryGetProperty("tools", out _));
    }

    /// <summary>
    /// With no schema flag the shape has to be asked for in the prompt. This is the one provider
    /// where that request is all there is, so it must actually carry the schema.
    /// </summary>
    [Fact]
    public void GeminiCarriesTheSchemaInThePrompt()
    {
        var decorated = new GeminiAgentProvider()
            .DecorateAnalysisPrompt("Analyse the diff.", """{ "type": "object", "x": 1 }""");

        Assert.StartsWith("Analyse the diff.", decorated);
        // Compacted, so it is one line of the prompt rather than a page of it.
        Assert.Contains("""{"type":"object","x":1}""", decorated);
        // Codex's flag is a guarantee at the API level rather than a tool the model has to call, so
        // there is nothing left for its prompt to say.
        Assert.Equal("Analyse the diff.", new CodexAgentProvider().DecorateAnalysisPrompt("Analyse the diff.", "{}"));
    }

    /// <summary>
    /// Claude's flag validates the answer but never describes it, and a rejection names only the
    /// property that was wrong. A run that has to infer the nesting sends one section as the whole
    /// document, is refused, and sends the next one - so the root object is named in the prompt.
    /// </summary>
    [Fact]
    public void ClaudeNamesTheRootObjectInThePrompt()
    {
        var decorated = new ClaudeAgentProvider().DecorateAnalysisPrompt("Analyse the diff.", "{}");

        Assert.StartsWith("Analyse the diff.", decorated);
        Assert.Contains("\"headline\"", decorated);
        Assert.Contains("\"sections\"", decorated);
        // The schema itself stays on the flag: repeating 2KB of it here would be paid for on every
        // run to say what the CLI is already enforcing.
        Assert.DoesNotContain("\"type\": \"object\"", decorated);
    }

    /// <summary>
    /// The accounting is summed across models rather than read off the first, because the default
    /// <c>auto</c> alias routes one session between a fast model and a capable one - and the model
    /// reported is whichever actually did the work.
    /// </summary>
    [Fact]
    public void GeminiReadsTheDocumentAndSumsTheAccountingAcrossModels()
    {
        var run = new GeminiAgentProvider().BuildAnalysisRun("C:/repo", """{"type":"object"}""", Scratch());
        var stdout = string.Join('\n',
            """{"type":"init","session_id":"s1","model":"auto"}""",
            """{"type":"tool_use","tool_name":"run_shell_command","parameters":{"command":"git diff"}}""",
            """{"type":"message","role":"assistant","content":"{\"headline\":\"It was the cache\"}"}""",
            """{"type":"result","status":"success","stats":{"models":{"gemini-3.1-pro":{"tokens":{"prompt":1200,"candidates":3000,"thoughts":400,"cached":98000}},"gemini-3.7-flash":{"tokens":{"prompt":300,"candidates":100,"cached":0}}}}}""");

        var outcome = new GeminiAgentProvider().ReadAnalysis(run, stdout);

        Assert.Null(outcome.Error);
        Assert.Equal("It was the cache", outcome.Structured!.Value.GetProperty("headline").GetString());
        Assert.Equal(1500, outcome.InputTokens);
        // Thinking tokens are billed as output and reported apart from the answer's own.
        Assert.Equal(3500, outcome.OutputTokens);
        Assert.Equal(98000, outcome.CacheReadTokens);
        // No cache-WRITE figure is reported, so it stays zero rather than being invented.
        Assert.Equal(0, outcome.CacheCreationTokens);
        Assert.Equal("gemini-3.1-pro", outcome.Model);
    }

    /// <summary>
    /// The buffered format has to work too: which one the run used is a one-flag decision, and a run
    /// that answered in the other should still produce its document rather than an error about a
    /// shape.
    /// </summary>
    [Fact]
    public void GeminiReadsTheSingleDocumentFormatAsWell()
    {
        var run = new GeminiAgentProvider().BuildAnalysisRun("C:/repo", """{"type":"object"}""", Scratch());
        var stdout = """
        {"response":"{\"headline\":\"ok\"}",
         "stats":{"models":{"gemini-3.1-pro":{"tokens":{"prompt":10,"candidates":20,"cached":5}}}}}
        """;

        var outcome = new GeminiAgentProvider().ReadAnalysis(run, stdout);

        Assert.Null(outcome.Error);
        Assert.Equal("ok", outcome.Structured!.Value.GetProperty("headline").GetString());
        Assert.Equal(10, outcome.InputTokens);
        Assert.Equal(5, outcome.CacheReadTokens);
    }

    /// <summary>
    /// The prompt asks for bare JSON; a model that answers with a fence or a sentence of preamble
    /// has still done the work, and losing the document over its manners would be the wrong trade.
    /// </summary>
    [Theory]
    [InlineData("{\\\"headline\\\":\\\"ok\\\"}")]
    [InlineData("```json\\n{\\\"headline\\\":\\\"ok\\\"}\\n```")]
    [InlineData("Here is the analysis:\\n\\n{\\\"headline\\\":\\\"ok\\\"}\\n\\nLet me know if you need more.")]
    public void GeminiRecoversTheDocumentFromAWrappedAnswer(string escapedAnswer)
    {
        var run = new GeminiAgentProvider().BuildAnalysisRun("C:/repo", """{"type":"object"}""", Scratch());
        var stdout = $$"""{"type":"result","status":"success","response":"{{escapedAnswer}}"}""";

        var outcome = new GeminiAgentProvider().ReadAnalysis(run, stdout);

        Assert.Null(outcome.Error);
        Assert.Equal("ok", outcome.Structured!.Value.GetProperty("headline").GetString());
    }

    [Fact]
    public void GeminiReportsAFailedRunRatherThanAnEmptyDocument()
    {
        var run = new GeminiAgentProvider().BuildAnalysisRun("C:/repo", """{"type":"object"}""", Scratch());

        var failed = new GeminiAgentProvider().ReadAnalysis(run,
            """{"type":"error","message":"API key invalid or expired"}""");
        Assert.Equal("API key invalid or expired", failed.Error);
        Assert.Null(failed.Structured);

        // A run killed before it said anything must report that, not throw: the Classroom shows
        // this message verbatim, so "no document" has to be a normal outcome.
        var silent = new GeminiAgentProvider().ReadAnalysis(run, "");
        Assert.NotNull(silent.Error);
        Assert.Null(silent.Structured);

        // And prose where a document was asked for is a missing document, not a crash.
        var chatty = new GeminiAgentProvider().ReadAnalysis(run,
            """{"type":"result","status":"success","response":"I could not find the file."}""");
        Assert.NotNull(chatty.Error);
        Assert.Null(chatty.Structured);
    }

    /// <summary>Interleaved noise on the stream must not stop the accounting being found.</summary>
    [Fact]
    public void GeminiIgnoresNonJsonLinesOnTheStream()
    {
        var run = new GeminiAgentProvider().BuildAnalysisRun("C:/repo", """{"type":"object"}""", Scratch());
        var stdout = string.Join('\n',
            "warning: something on stderr got interleaved",
            "",
            """{"type":"message","role":"assistant","content":"{\"headline\":\"ok\"}"}""",
            """{"type":"result","status":"success","stats":{"input_tokens":7,"output_tokens":9}}""");

        var outcome = new GeminiAgentProvider().ReadAnalysis(run, stdout);

        Assert.Null(outcome.Error);
        Assert.Equal(7, outcome.InputTokens);
        Assert.Equal(9, outcome.OutputTokens);
    }

    /// <summary>
    /// Gemini says the same things in its own vocabulary, and all three providers must come out of
    /// <see cref="AgentProvider.ReadProgress"/> speaking one.
    /// </summary>
    [Fact]
    public void GeminiReadsAToolCallAsOneStep()
    {
        var provider = new GeminiAgentProvider();

        Assert.Equal("Bash git diff --stat", provider.ReadProgress(
            """{"type":"tool_use","tool_name":"run_shell_command","parameters":{"command":"git diff --stat"}}""")!.Value.ToString());
        // Its Read takes absolute_path where Claude's takes file_path; the caption says the file
        // either way.
        Assert.Equal("Read TicketService.cs", provider.ReadProgress(
            """{"type":"tool_use","tool_name":"read_file","parameters":{"absolute_path":"C:/repo/src/TicketService.cs"}}""")!.Value.ToString());
        // The result of a call is not the call: counting both would double every run's step total.
        Assert.Null(provider.ReadProgress("""{"type":"tool_result","tool_id":"t1","status":"success"}"""));
        Assert.Null(provider.ReadProgress("""{"type":"init","session_id":"s1","model":"pro"}"""));
        Assert.Null(provider.ReadProgress("not json at all"));
    }

    /// <summary>
    /// An MCP tool arrives wearing this CLI's own <c>server__tool</c> namespace and has to come out
    /// in the <c>mcp__server__tool</c> shape the rest of the dashboard keys off - the heartbeat's
    /// mutation counter reads that prefix to decide whether a run changed the ticket at all.
    /// </summary>
    [Theory]
    [InlineData("tickets__add_comment", "mcp__tickets__add_comment")]
    [InlineData("mcp__tickets__add_comment", "mcp__tickets__add_comment")]
    [InlineData("search_file_content", "Grep")]
    [InlineData("replace", "Edit")]
    [InlineData("google_web_search", "WebSearch")]
    // Not a tool this knows. It should read as itself rather than be relabelled as something else.
    [InlineData("list_directory", "list_directory")]
    public void GeminiPutsToolNamesIntoTheCanonicalShape(string reported, string canonical) =>
        Assert.Equal(canonical, GeminiAgentProvider.CanonicalTool(reported));

    // ---- the model guard, now that there are three of them ------------------

    /// <summary>
    /// The rule is one-sided on purpose: a name is rejected only when it unmistakably belongs to
    /// ANOTHER provider, so a slug released after this was written still works with no edit here.
    /// The heartbeat applies the same rule in <c>Test-AgentModelBelongsTo</c>, and the two halves
    /// disagreeing would mean the dashboard storing a model the fleet then silently replaces.
    /// </summary>
    [Theory]
    [InlineData("claude", "claude-opus-5", true)]
    [InlineData("claude", "haiku", true)]
    [InlineData("claude", "gpt-5.6-sol", false)]
    [InlineData("claude", "pro", false)]
    [InlineData("codex", "gpt-5.6-sol", true)]
    [InlineData("codex", "claude-opus-5", false)]
    [InlineData("codex", "haiku", false)]
    [InlineData("codex", "flash", false)]
    [InlineData("gemini", "pro", true)]
    [InlineData("gemini", "flash-lite", true)]
    [InlineData("gemini", "gemini-3.1-pro-preview", true)]
    [InlineData("gemini", "claude-opus-5", false)]
    [InlineData("gemini", "gpt-5.6-sol", false)]
    // Nobody's in particular, so everybody's: this is what keeps a new model name working.
    [InlineData("gemini", "some-new-thing-9", true)]
    [InlineData("claude", "some-new-thing-9", true)]
    public void ModelNamesAreOnlyRejectedWhenTheyAreUnmistakablySomebodyElses(
        string provider, string model, bool belongs) =>
        Assert.Equal(belongs, AgentModels.BelongsTo(provider, model));
}
