using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Whetstone.Services;

/// <summary>Which agent CLI the dashboard shells out to.</summary>
public enum AgentProviderKind
{
    Claude,
    Codex,
    Gemini,
}

/// <summary>
/// One prepared CLI invocation: what to run, with what argv, in what environment, plus the scratch
/// files the provider needs carried from building the command to reading the answer back.
/// <para>
/// <see cref="ScratchDir"/> is deleted by the caller once the run is done. For Codex it is the
/// redirected <c>CODEX_HOME</c>, which holds a copy of a login — so it must not outlive the run.
/// </para>
/// </summary>
public sealed class AgentRun
{
    public required string FileName { get; init; }
    public List<string> Arguments { get; } = new();
    public Dictionary<string, string> Environment { get; } = new();

    /// <summary>Where a provider that answers into a file was told to write it. Null when it answers on stdout.</summary>
    public string? ResultPath { get; set; }

    /// <summary>Per-run directory to delete when the run finishes; null when the provider needed none.</summary>
    public string? ScratchDir { get; set; }
}

/// <summary>
/// What a structured, read-only run came back with, normalised across providers. Token counts are
/// zero when a provider does not report that class at all — Codex has no cache-creation figure.
/// </summary>
public sealed record AgentOutcome(
    JsonElement? Structured,
    string? Error,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens,
    string? Model);

/// <summary>
/// One thing a run just did, as the queue panel would say it: "Read AnalysisQueue.cs", "git log -20".
/// <para>
/// Deliberately not the event — a caller wanting to render the run's activity should not have to
/// know that Claude says <c>tool_use</c> and Codex says <c>item.started</c>.
/// </para>
/// </summary>
/// <param name="Tool">The canonical tool name, in Claude's vocabulary.</param>
/// <param name="Detail">What it was pointed at, already shortened for one line. Null when the tool took nothing worth naming.</param>
public readonly record struct AgentStep(string Tool, string? Detail)
{
    /// <summary>The whole step on one line, for a caption.</summary>
    public override string ToString() => Detail is null ? Tool : $"{Tool} {Detail}";
}

/// <summary>
/// Everything the C# half knows about talking to an agent CLI. The two one-shot callers — the
/// Classroom analysis and the improvement applier — build their process through here rather than
/// naming an executable, so adding a provider is one subclass instead of an edit in each of them.
/// <para>
/// The heartbeat's per-ticket runs are the PowerShell half's equivalent of this; see
/// <c>data/providers.ps1</c>. The two are deliberately separate: they share no process, and a shared
/// abstraction across the language boundary would be a config file pretending to be a type.
/// </para>
/// </summary>
public abstract class AgentProvider
{
    public abstract AgentProviderKind Kind { get; }
    public abstract string Executable { get; }
    public abstract string DisplayName { get; }

    /// <summary>
    /// Build the Classroom's read-only, schema-validated run. The read-only guarantee is enforced by
    /// the CLI rather than asked for in the prompt — how, is each provider's business.
    /// </summary>
    public abstract AgentRun BuildAnalysisRun(string workDir, string jsonSchema, string scratchDir);

    /// <summary>Build a free-form run that may edit files and answers in prose (the improvement applier).</summary>
    public abstract AgentRun BuildFreeformRun(string workDir, string scratchDir);

    /// <summary>Pull the document and the accounting out of whatever the CLI left behind.</summary>
    public abstract AgentOutcome ReadAnalysis(AgentRun run, string stdout);

    /// <summary>
    /// Read one line of the run's live output as a step, or null when the line says nothing a person
    /// waiting on the run would want to know.
    /// <para>
    /// Called on every stdout line as it arrives, which is the whole point: a run reports nothing
    /// about itself until this reads it, and a queue that cannot say what a run is doing can only
    /// offer a spinner. Must never throw — a stream line it does not recognise is normal, not an
    /// error, and a run must not fail over its own narration.
    /// </para>
    /// </summary>
    public virtual AgentStep? ReadProgress(string line) => null;

    /// <summary>
    /// The prompt as this provider needs it, given the schema the answer has to satisfy.
    /// <para>
    /// Unchanged by default, which is the case worth understanding: Codex constrains the answer with
    /// a FLAG, so the prompt it is handed is the prompt the caller wrote. A provider whose CLI has no
    /// such flag has nowhere else to say it, and says it here rather than making the caller ask which
    /// provider it is talking to.
    /// </para>
    /// <para>
    /// Claude is the third case and the one that is easy to get wrong: it HAS the flag, and still
    /// overrides this. Constraining an answer and telling the model what the answer looks like are
    /// different jobs, and <c>--json-schema</c> only does the first — see
    /// <see cref="ClaudeAgentProvider.DecorateAnalysisPrompt"/>.
    /// </para>
    /// </summary>
    public virtual string DecorateAnalysisPrompt(string prompt, string jsonSchema) => prompt;

    // ---- shared stream reading ----------------------------------------------

    /// <summary>A whole string as one JSON value, or null when it is not one.</summary>
    protected static JsonElement? TryParse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException) { return null; }
    }

    /// <summary>A named property, or null when the object has none (missing, or not an object).</summary>
    protected static JsonElement? Prop(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) ? v : null;

    /// <summary>A named number, or 0 — the same "absent counts as none" rule every provider uses.</summary>
    protected static long Long(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    /// <summary>A JSON document with its whitespace taken out, for passing on a command line.</summary>
    protected static string Compact(string json) =>
        JsonSerializer.Serialize(JsonDocument.Parse(json).RootElement);

    /// <summary>Parse one NDJSON line, or null for blank lines, prose, and anything malformed.</summary>
    protected static JsonElement? ParseEvent(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed[0] != '{') return null;
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            return doc.RootElement.Clone();
        }
        catch (JsonException) { return null; }
    }

    protected static string? Str(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;

    /// <summary>
    /// A tool call as one short line: the file, the command, the pattern — whatever the reader would
    /// have looked at. The names are Claude's tool vocabulary because both providers' events are
    /// mapped onto it, exactly as <c>Format-ToolCall</c> does for the heartbeat's own feed.
    /// </summary>
    protected static string? Describe(string tool, JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return null;

        string? Leaf(string field) => Str(input, field)?.Split('/', '\\').LastOrDefault();

        var detail = tool switch
        {
            "Read" or "Edit" or "Write" => Leaf("file_path") ?? Leaf("absolute_path") ?? Leaf("path"),
            "NotebookEdit" => Leaf("notebook_path"),
            "Bash" => Str(input, "command"),
            "Grep" => Str(input, "glob") is { Length: > 0 } g
                ? $"{Str(input, "pattern")}  ({g})"
                : Str(input, "pattern"),
            "Glob" => Str(input, "pattern"),
            "WebFetch" => Str(input, "url"),
            "WebSearch" => Str(input, "query"),
            "Task" => Str(input, "description"),
            _ => new[] { "file_path", "absolute_path", "path", "command", "pattern", "query", "url", "description" }
                .Select(f => Str(input, f))
                .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)),
        };

        return Condense(detail);
    }

    /// <summary>One line, one space between words, short enough for a sidebar caption.</summary>
    protected static string? Condense(string? s, int max = 72)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var flat = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
        return flat.Length <= max ? flat : flat[..(max - 1)].TrimEnd() + "…";
    }

    /// <summary>Best-effort removal of a run's scratch directory. Never throws: it is cleanup.</summary>
    public static void Cleanup(AgentRun run)
    {
        if (string.IsNullOrEmpty(run.ScratchDir)) return;
        try { if (Directory.Exists(run.ScratchDir)) Directory.Delete(run.ScratchDir, recursive: true); }
        catch { /* a leftover temp directory is not worth failing a run over */ }
    }
}

// ---------------------------------------------------------------------------

/// <summary>Claude Code. The incumbent — these are the exact invocations the dashboard has always used.</summary>
public sealed class ClaudeAgentProvider : AgentProvider
{
    /// <summary>
    /// Matches <c>DefaultWorkerModel</c> in <c>data/providers.ps1</c> and the Settings catalogue in
    /// <c>ConfigEndpoints</c>. All three have to say the same thing, or the panel shows one model
    /// while a run uses another.
    /// </summary>
    public const string DefaultModel = "claude-opus-5-5";

    private readonly string _model;
    private readonly string? _effort;
    private readonly string? _configDir;

    /// <param name="effort">
    /// Claude Code's <c>--effort</c>; blank leaves it to the CLI. Its default is per MODEL (Opus 5
    /// ran at high, Opus 5.5 at medium), so "the default" is not a stable meaning to leave a run on.
    /// </param>
    /// <param name="configDir">
    /// The config directory of the account to run as; blank is the CLI's own login. See
    /// <see cref="ClaudeAccounts"/>.
    /// </param>
    public ClaudeAgentProvider(string? model = null, string? effort = null, string? configDir = null)
    {
        _model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
        _effort = string.IsNullOrWhiteSpace(effort) ? null : effort.Trim();
        _configDir = string.IsNullOrWhiteSpace(configDir) ? null : configDir.Trim();
    }

    public override AgentProviderKind Kind => AgentProviderKind.Claude;
    public override string Executable => "claude";
    public override string DisplayName => "Anthropic Claude Code";

    public override AgentRun BuildAnalysisRun(string workDir, string jsonSchema, string scratchDir)
    {
        var run = new AgentRun { FileName = Executable };
        AddAccount(run);
        // ArgumentList rather than a single Arguments string: the schema is 2KB of quotes and
        // braces, and hand-escaping that for a Windows command line is a bug waiting to happen.
        run.Arguments.AddRange(new[]
        {
            "-p",
            // The configured model, passed for the same reason the PowerShell worker passes it: a
            // Settings panel showing "Opus 5" while the run silently takes the CLI's own default is
            // a setting that does nothing, and the only place that shows is the model name stamped
            // on a finished analysis.
            "--model", _model,
        });
        AddEffort(run);
        run.Arguments.AddRange(new[]
        {
            // Streamed rather than buffered, and only for one reason: with `json` the CLI says
            // nothing at all until it exits, so a run that takes four minutes is four minutes of a
            // queue panel with nothing true to show. The closing `result` event of the stream is
            // the same envelope `json` would have printed — see ReadAnalysis — so this costs
            // nothing on the way out and buys every step on the way through.
            "--output-format", "stream-json",
            // Required by the CLI to stream under -p, not a logging preference.
            "--verbose",
            "--json-schema", Compact(jsonSchema),
            // The read-only guarantee, enforced rather than requested: Edit, Write and
            // NotebookEdit are not in the session's tool set, so the run cannot modify the repo
            // even if it decides it would like to. Bash stays because the analysis is mostly
            // `git diff` and `git log`; the prompt is what keeps it to reads.
            "--tools", "Read,Grep,Glob,Bash",
            "--permission-mode", "bypassPermissions",
        });
        return run;
    }

    public override AgentRun BuildFreeformRun(string workDir, string scratchDir)
    {
        var run = new AgentRun { FileName = Executable };
        AddAccount(run);
        run.Arguments.AddRange(new[] { "-p", "--model", _model });
        AddEffort(run);
        run.Arguments.AddRange(new[] { "--permission-mode", "bypassPermissions" });
        return run;
    }

    private void AddAccount(AgentRun run)
    {
        if (_configDir != null) run.Environment["CLAUDE_CONFIG_DIR"] = _configDir;
    }

    private void AddEffort(AgentRun run)
    {
        if (_effort != null) run.Arguments.AddRange(new[] { "--effort", _effort });
    }

    /// <summary>
    /// Names the envelope the document travels in. Adds no constraint — <c>--json-schema</c> is
    /// already enforcing every one of them — and exists because enforcing a shape and TELLING the
    /// model the shape are different jobs, and the flag only does the first.
    ///
    /// <para>
    /// The flag turns the schema into a <c>StructuredOutput</c> tool and validates each call against
    /// it, but a rejection only ever names the property that was wrong; it never says what the right
    /// shape was. The prompt above spends a thousand words on "sections" and "blocks" and never once
    /// names the root, so a model that has to infer the nesting from the schema alone has one plain
    /// way to get it wrong — and the failure that follows is not random. It sends ONE SECTION as the
    /// whole document, is told <c>headline</c> is missing, and sends the next section. The CLI
    /// exhausts its retries and exits 1, and a run that had already read the diff and written the
    /// analysis is thrown away with nothing kept.
    /// </para>
    ///
    /// <para>
    /// The array line is the second half of the same failure: a model squeezing a large nested array
    /// into a tool argument reaches for a JSON string, which is a legal string and an illegal
    /// <c>blocks</c>.
    /// </para>
    /// </summary>
    public override string DecorateAnalysisPrompt(string prompt, string jsonSchema) =>
        $$"""
        {{prompt}}

        ---

        # HANDING BACK THE DOCUMENT

        One `StructuredOutput` call holding the WHOLE document. Not one call per section.

        The root object is `{"headline": ..., "sections": [...]}` - those two properties and no
        others. Every section named above is one entry in `sections`, shaped `{"title": ...,
        "blocks": [...]}`, and that section's blocks go in its own `blocks`. A `{"title": ...,
        "blocks": [...]}` object sent on its own is a SECTION, not a document, and is rejected.

        `sections` and `blocks` are arrays, passed as arrays. A JSON string that happens to contain
        an array is not an array and does not validate.

        A rejected call means the SHAPE is wrong. Re-read this section before sending another one:
        sending the same shape twice is how a finished analysis gets thrown away.
        """;

    /// <summary>
    /// With <c>--json-schema</c> the validated object arrives on its own <c>structured_output</c>
    /// field and <c>result</c> is empty, so there is no prose to strip and no fenced block to find —
    /// the parsing that would otherwise be the fragile part of this feature does not exist.
    /// </summary>
    public override AgentOutcome ReadAnalysis(AgentRun run, string stdout)
    {
        if (FindEnvelope(stdout) is not { } envelope)
            return Fail($"could not read the CLI's output as JSON. {Tail(stdout)}");

        if (envelope.TryGetProperty("is_error", out var isErr) && isErr.ValueKind == JsonValueKind.True)
            return Fail(Explain(envelope));

        if (!envelope.TryGetProperty("structured_output", out var structured))
            return Fail("the run finished without producing an analysis document");

        var usage = envelope.TryGetProperty("usage", out var u) ? u : default;
        long Tok(string name) => usage.ValueKind == JsonValueKind.Object
            && usage.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;

        var model = envelope.TryGetProperty("modelUsage", out var m) ? MainModel(m) : null;

        return new AgentOutcome(structured, null,
            Tok("input_tokens"), Tok("output_tokens"),
            Tok("cache_read_input_tokens"), Tok("cache_creation_input_tokens"), model);
    }

    /// <summary>
    /// The model that did the run's work, out of <c>modelUsage</c>.
    /// <para>
    /// Not simply the first key. The CLI makes small housekeeping calls of its own on Haiku, and
    /// those land in <c>modelUsage</c> beside the model that was asked for — sometimes ahead of it
    /// (ticket 73's transcript is Opus on every turn). Taking the
    /// first key labelled an Opus analysis as Haiku. The one that wrote the most is the one that
    /// wrote the document.
    /// </para>
    /// </summary>
    internal static string? MainModel(JsonElement modelUsage)
    {
        if (modelUsage.ValueKind != JsonValueKind.Object) return null;

        static long Count(JsonElement u, string name) => u.ValueKind == JsonValueKind.Object
            && u.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;

        return modelUsage.EnumerateObject()
            .Select((p, i) => (p.Name, i,
                Output: Count(p.Value, "outputTokens"),
                Total: Count(p.Value, "inputTokens") + Count(p.Value, "cacheReadInputTokens")
                     + Count(p.Value, "cacheCreationInputTokens")))
            .OrderByDescending(x => x.Output).ThenByDescending(x => x.Total).ThenBy(x => x.i)
            .Select(x => x.Name)
            .FirstOrDefault();
    }

    /// <summary>
    /// The one closing envelope, from either shape of output.
    /// <para>
    /// Both are read because they are the same object: <c>--output-format json</c> prints the
    /// stream's final <c>result</c> event on its own, and nothing else. Accepting both means the
    /// choice of format stays a one-flag decision in <see cref="BuildAnalysisRun"/> rather than a
    /// format this parser is welded to — and a run that somehow answers in the other one still
    /// produces its document instead of an error about a shape.
    /// </para>
    /// </summary>
    private static JsonElement? FindEnvelope(string stdout)
    {
        try
        {
            using var whole = JsonDocument.Parse(stdout);
            if (whole.RootElement.ValueKind == JsonValueKind.Object) return whole.RootElement.Clone();
        }
        catch (JsonException) { /* a stream, not a single document */ }

        // From the end: the result is the last thing said, and scanning backwards means a run that
        // printed a thousand events costs one parse rather than a thousand.
        var lines = stdout.Split('\n');
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (ParseEvent(lines[i]) is not { } evt) continue;
            if (Str(evt, "type") == "result" || evt.TryGetProperty("structured_output", out _)) return evt;
        }
        return null;
    }

    /// <summary>
    /// Claude narrates through <c>assistant</c> events. One step per event rather than per block:
    /// parallel tool calls arrive in one message, and the caption has room for the first of them.
    /// </summary>
    public override AgentStep? ReadProgress(string line)
    {
        if (ParseEvent(line) is not { } evt || Str(evt, "type") != "assistant") return null;
        if (!evt.TryGetProperty("message", out var message)
            || !message.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array) return null;

        foreach (var block in content.EnumerateArray())
        {
            if (Str(block, "type") != "tool_use") continue;
            if (Str(block, "name") is not { Length: > 0 } tool) continue;
            var input = block.TryGetProperty("input", out var i) ? i : default;
            return new AgentStep(tool, Describe(tool, input));
        }
        return null;
    }

    /// <summary>
    /// Why a run that answered <c>is_error</c> failed, in as much of it as the envelope holds.
    ///
    /// <para>
    /// <c>result</c> is empty on exactly the failures worth explaining: a run that exhausted its
    /// <c>StructuredOutput</c> retries carries its reason in <c>subtype</c> and nowhere else, so a
    /// reader told only "the run reported an error" has been told the one thing they already knew.
    /// </para>
    ///
    /// <para>
    /// The session id goes on either way, and is the more useful half. The CLI's own transcript is
    /// the only place the rejected tool calls survive - what the run actually tried to hand back,
    /// which is the whole of what a failed serialisation needs looking at - and that transcript is
    /// findable by session id and by nothing else.
    /// </para>
    /// </summary>
    private static string Explain(JsonElement envelope)
    {
        var said = envelope.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String
            ? r.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(said))
            said = Str(envelope, "subtype") is { Length: > 0 } subtype
                ? $"the run ended as '{subtype}'"
                : "the run reported an error";

        return Str(envelope, "session_id") is { Length: > 0 } session
            ? $"{said} (session {session})"
            : said;
    }

    private static AgentOutcome Fail(string error) => new(null, error, 0, 0, 0, 0, null);

    private static string Tail(string s, int max = 400) =>
        s.Length <= max ? s.Trim() : s[^max..].Trim();
}

// ---------------------------------------------------------------------------

/// <summary>
/// OpenAI Codex, driven through <c>codex exec</c>.
/// <para>
/// Two things differ from Claude in ways worth knowing before changing anything here. Codex reads
/// its configuration from a DIRECTORY rather than a flag, so every run gets a redirected
/// <c>CODEX_HOME</c> — which is also what keeps the user's own MCP servers out of a run that is
/// supposed to be read-only. And it answers a schema-constrained question into a FILE rather than on
/// stdout, so <see cref="AgentRun.ResultPath"/> is where the document actually turns up.
/// </para>
/// </summary>
public sealed class CodexAgentProvider : AgentProvider
{
    /// <summary>The flagship of the current generation; the Classroom document is worth it.</summary>
    public const string DefaultModel = "gpt-5.6-sol";

    private readonly string _model;
    private readonly string? _effort;

    /// <param name="effort">Codex's <c>model_reasoning_effort</c>; blank leaves it to the CLI.</param>
    public CodexAgentProvider(string? model = null, string? effort = null)
    {
        _model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
        _effort = string.IsNullOrWhiteSpace(effort) ? null : effort.Trim();
    }

    public override AgentProviderKind Kind => AgentProviderKind.Codex;
    public override string Executable => "codex";
    public override string DisplayName => "OpenAI Codex";

    public override AgentRun BuildAnalysisRun(string workDir, string jsonSchema, string scratchDir)
    {
        var home = PrepareHome(scratchDir);
        var schemaPath = Path.Combine(scratchDir, "schema.json");
        var resultPath = Path.Combine(scratchDir, "result.json");
        File.WriteAllText(schemaPath, jsonSchema, new UTF8Encoding(false));

        var run = new AgentRun { FileName = Executable, ResultPath = resultPath, ScratchDir = scratchDir };
        run.Arguments.AddRange(new[] { "exec", "--json", "-m", _model });
        AddEffort(run);
        run.Arguments.AddRange(new[]
        {
            "--cd", workDir,
            // The read-only guarantee, and a stronger one than a tool allowlist: the sandbox is the
            // OS refusing the write, not the model declining to try. Safe to pair with an approval
            // policy of "never" because this run has no MCP servers — the known non-interactive
            // stall (openai/codex#24135) is an MCP-approval problem and there is nothing here to
            // approve.
            "--sandbox", "read-only",
            "--ask-for-approval", "never",
            "--skip-git-repo-check",
            "--output-schema", schemaPath,
            "--output-last-message", resultPath,
            "-",   // prompt arrives on stdin
        });
        run.Environment["CODEX_HOME"] = home;
        return run;
    }

    public override AgentRun BuildFreeformRun(string workDir, string scratchDir)
    {
        var home = PrepareHome(scratchDir);
        var run = new AgentRun { FileName = Executable, ScratchDir = scratchDir };
        run.Arguments.AddRange(new[] { "exec", "-m", _model });
        AddEffort(run);
        run.Arguments.AddRange(new[]
        {
            "--cd", workDir,
            // This one really does edit files, and no human is watching it.
            "--dangerously-bypass-approvals-and-sandbox",
            "--skip-git-repo-check",
            "-",
        });
        run.Environment["CODEX_HOME"] = home;
        return run;
    }

    /// <summary>
    /// A <c>-c</c> override, as the heartbeat's workers pass it. Unquoted for the same reason as
    /// there: codex reads the value as TOML and takes a bare word it cannot parse as a string.
    /// </summary>
    private void AddEffort(AgentRun run)
    {
        if (_effort != null) run.Arguments.AddRange(new[] { "-c", $"model_reasoning_effort={_effort}" });
    }

    /// <summary>
    /// Codex narrates through thread items, taken at <c>item.started</c> — the moment the work
    /// begins, which is what a feed of "doing this now" means. Its read-only analysis run has no
    /// Read tool: it reads the repo by shelling out, so nearly every step here is a command.
    /// </summary>
    public override AgentStep? ReadProgress(string line)
    {
        if (ParseEvent(line) is not { } evt || Str(evt, "type") != "item.started") return null;
        if (!evt.TryGetProperty("item", out var item)) return null;

        switch (Str(item, "type"))
        {
            case "command_execution":
                return new AgentStep("Bash", Condense(Str(item, "command")));
            case "mcp_tool_call":
                // Reassembled into the name every consumer already understands.
                if (Str(item, "server") is { Length: > 0 } server && Str(item, "tool") is { Length: > 0 } tool)
                    return new AgentStep($"mcp__{server}__{tool}", null);
                return null;
            case "web_search":
                return new AgentStep("WebSearch", Condense(Str(item, "query")));
            default:
                return null;
        }
    }

    public override AgentOutcome ReadAnalysis(AgentRun run, string stdout)
    {
        // The accounting is on the JSONL stream; the document is in the file. Both halves are read
        // defensively — a run that failed leaves one or the other missing rather than malformed.
        long input = 0, output = 0, cacheRead = 0;
        string? streamError = null;

        foreach (var line in stdout.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] != '{') continue;
            JsonElement evt;
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                evt = doc.RootElement.Clone();
            }
            catch (JsonException) { continue; }

            var type = evt.TryGetProperty("type", out var t) ? t.GetString() : null;
            switch (type)
            {
                case "turn.completed":
                    if (evt.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
                    {
                        long Tok(string name) => u.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;
                        input = Tok("input_tokens");
                        output = Tok("output_tokens");
                        // Codex's cached_input_tokens are prompt tokens served from cache, which is
                        // what cache_read means on the other side. There is no cache-WRITE figure.
                        cacheRead = Tok("cached_input_tokens");
                    }
                    break;
                case "turn.failed":
                    streamError = evt.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var em)
                        ? em.GetString() : "the run failed";
                    break;
                case "error":
                    streamError = evt.TryGetProperty("message", out var msg) ? msg.GetString() : "the run failed";
                    break;
            }
        }

        if (streamError != null) return new AgentOutcome(null, streamError, input, output, cacheRead, 0, _model);

        if (run.ResultPath == null || !File.Exists(run.ResultPath))
            return new AgentOutcome(null, "the run finished without producing an analysis document", input, output, cacheRead, 0, _model);

        try
        {
            using var parsed = JsonDocument.Parse(File.ReadAllText(run.ResultPath));
            return new AgentOutcome(parsed.RootElement.Clone(), null, input, output, cacheRead, 0, _model);
        }
        catch (JsonException)
        {
            return new AgentOutcome(null, "the analysis document was not valid JSON", input, output, cacheRead, 0, _model);
        }
    }

    /// <summary>
    /// Give this run a config directory of its own, holding only what we put there.
    /// <para>
    /// Codex has no <c>--mcp-config</c>: its servers come from the user's <c>~/.codex/config.toml</c>,
    /// so replacing the whole directory IS the "only these servers" flag. Neither of the C# callers
    /// wants any MCP server at all, so the config we write is empty — which is the point.
    /// </para>
    /// <para>
    /// Redirecting <c>CODEX_HOME</c> also moves where Codex looks for its LOGIN, so <c>auth.json</c>
    /// is copied across. Nothing else is.
    /// </para>
    /// </summary>
    private static string PrepareHome(string scratchDir)
    {
        var home = Path.Combine(scratchDir, "codex-home");
        Directory.CreateDirectory(home);

        var source = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(source))
            source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

        var auth = Path.Combine(source, "auth.json");
        if (File.Exists(auth))
        {
            try { File.Copy(auth, Path.Combine(home, "auth.json"), overwrite: true); }
            catch { /* surfaced as a sign-in failure on the run itself, which says more than we could here */ }
        }

        File.WriteAllText(Path.Combine(home, "config.toml"),
            "# Written per run by the dashboard. Intentionally empty: this run gets no MCP servers.\n",
            new UTF8Encoding(false));
        return home;
    }
}

// ---------------------------------------------------------------------------

/// <summary>
/// Google's Gemini CLI, driven headlessly by piping the prompt in on stdin.
/// <para>
/// WRITTEN FROM THE DOCUMENTATION, NOT AGAINST A LIVE CLI. That is why the reading below accepts
/// more shapes than it should have to — both output formats, both spellings of the settings keys,
/// flat and nested token counts. Being wrong about which one a given version emits then costs a run
/// its accounting or its narration rather than its answer.
/// </para>
/// <para>
/// Three things differ from the other two providers in ways worth knowing before changing anything
/// here. It has NO SCHEMA FLAG, so the analysis run asks for its JSON in the prompt (see
/// <see cref="DecorateAnalysisPrompt"/>) and digs the document back out of the answer — the parsing
/// Claude's <c>--json-schema</c> spares us. Its configuration lives in a DIRECTORY, redirected per
/// run through <c>GEMINI_CLI_HOME</c> — which names the PARENT of the <c>.gemini</c> folder rather
/// than the folder itself. And its <c>--allowed-tools</c> flag is an auto-approve list rather than a
/// restriction, so the read-only guarantee is written into that directory's settings instead:
/// <c>tools.core</c> allows the reading tools and <c>tools.exclude</c> blocks the writing ones.
/// </para>
/// </summary>
public sealed class GeminiAgentProvider : AgentProvider
{
    /// <summary>
    /// The routing alias rather than a dated slug, on purpose: <c>pro</c> is whatever the current
    /// flagship is, so this default cannot go stale the way <c>gemini-3.1-pro-preview</c> will.
    /// </summary>
    public const string DefaultModel = "pro";

    /// <summary>
    /// The tools the read-only analysis run may have. Both spellings of each on purpose: the
    /// settings file has been through a rename (<c>coreTools</c> to <c>tools.core</c>) and the docs
    /// show the class names while the model sees the snake_case ones. Naming both costs a few bytes
    /// of config and means this works either side of that change.
    /// </summary>
    private static readonly string[] ReadingTools =
    {
        "read_file", "ReadFileTool",
        "read_many_files", "ReadManyFilesTool",
        "list_directory", "LSTool",
        "search_file_content", "GrepTool",
        "glob", "GlobTool",
        // The analysis is mostly `git diff` and `git log`, exactly as it is under Claude. The
        // prompt is what keeps it to reads; this list is what makes writing impossible.
        "run_shell_command", "ShellTool",
    };

    /// <summary>The tools that can change something, blocked outright on the analysis run.</summary>
    private static readonly string[] WritingTools =
    {
        "write_file", "WriteFileTool",
        "replace", "EditTool",
        // Not a repo edit, but it writes to the user's own GEMINI.md, which a run the docs promise
        // cannot modify anything has no business doing either.
        "save_memory", "MemoryTool",
    };

    private readonly string _model;

    public GeminiAgentProvider(string? model = null) =>
        _model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();

    public override AgentProviderKind Kind => AgentProviderKind.Gemini;
    public override string Executable => "gemini";
    public override string DisplayName => "Google Gemini CLI";

    public override AgentRun BuildAnalysisRun(string workDir, string jsonSchema, string scratchDir)
    {
        var run = new AgentRun { FileName = Executable, ScratchDir = scratchDir };
        run.Arguments.AddRange(new[]
        {
            // Streamed rather than buffered for the same reason as Claude: with a single closing
            // document the CLI says nothing at all until it exits, and a four-minute run is then
            // four minutes of a queue panel with nothing true to show. ReadAnalysis accepts either
            // format, so this stays a one-flag decision.
            "--output-format", "stream-json",
            "-m", _model,
            // Auto-approve, paired with a tool set that contains nothing worth refusing. The
            // read-only guarantee here is the ALLOWLIST in settings.json, not this flag — and
            // without the flag a headless run has no way to answer the confirmation its first
            // `git diff` would raise, which is a stall rather than a refusal.
            "--approval-mode", "yolo",
            // No prompt flag: the CLI reads stdin when it is not a terminal, and stdin is where the
            // caller writes. --prompt is deprecated anyway.
        });
        run.Environment["GEMINI_CLI_HOME"] = PrepareHome(scratchDir, readOnly: true);
        AddSharedEnvironment(run);
        return run;
    }

    public override AgentRun BuildFreeformRun(string workDir, string scratchDir)
    {
        var run = new AgentRun { FileName = Executable, ScratchDir = scratchDir };
        // This one really does edit files, and no human is watching it — so it gets the tool set the
        // CLI ships with and nothing is excluded.
        run.Arguments.AddRange(new[] { "-m", _model, "--approval-mode", "yolo" });
        run.Environment["GEMINI_CLI_HOME"] = PrepareHome(scratchDir, readOnly: false);
        AddSharedEnvironment(run);
        return run;
    }

    /// <summary>
    /// The schema, carried in the prompt because this CLI has no flag for it.
    /// <para>
    /// Worth being plain about what that is worth: this is a REQUEST, where Claude's
    /// <c>--json-schema</c> and Codex's <c>--output-schema</c> are guarantees. A run that answers
    /// with prose around its object is recovered by <see cref="ExtractJson"/>; one that answers in
    /// the wrong SHAPE fails validation downstream and is reported as a run that produced no
    /// document, which is the truth.
    /// </para>
    /// </summary>
    public override string DecorateAnalysisPrompt(string prompt, string jsonSchema) =>
        $"""
        {prompt}

        ---

        # OUTPUT FORMAT

        Your final message must be ONE JSON object and nothing else: no explanation before it, no
        summary after it, no markdown code fence around it. It must validate against this JSON
        Schema:

        {Compact(jsonSchema)}
        """;

    /// <summary>
    /// Gemini narrates through <c>tool_use</c> events, which is the closest of the three CLIs to
    /// Claude's own shape — the work is in the NAMES, which are its own and are mapped onto Claude's
    /// vocabulary by <see cref="CanonicalTool"/>.
    /// </summary>
    public override AgentStep? ReadProgress(string line)
    {
        if (ParseEvent(line) is not { } evt || Str(evt, "type") != "tool_use") return null;

        var raw = Str(evt, "tool_name") ?? Str(evt, "name");
        if (raw is not { Length: > 0 }) return null;

        var input = Prop(evt, "parameters") ?? Prop(evt, "args") ?? Prop(evt, "input") ?? default;
        var tool = CanonicalTool(raw);
        return new AgentStep(tool, Describe(tool, input));
    }

    /// <summary>
    /// The document and the accounting, out of whichever output format turned up.
    /// <para>
    /// Both are read because a run that answered in the other one should still produce its analysis
    /// rather than an error about a shape — and because the closing <c>result</c> event is not
    /// documented to repeat the final message, the last assistant message is kept as the fallback
    /// answer rather than assumed to be redundant.
    /// </para>
    /// </summary>
    public override AgentOutcome ReadAnalysis(AgentRun run, string stdout)
    {
        string? answer = null;
        string? error = null;
        string? model = null;
        JsonElement stats = default;

        // Shape 1: --output-format json, one object for the whole run.
        if (TryParse(stdout) is { ValueKind: JsonValueKind.Object } whole)
        {
            answer = Str(whole, "response");
            error = ErrorMessage(whole);
            if (whole.TryGetProperty("stats", out var s)) stats = s;
        }
        else
        {
            // Shape 2: --output-format stream-json, one event per line. Read forwards rather than
            // backwards: the answer may be the last assistant message rather than anything in the
            // closing event, so the whole stream is worth the one pass.
            foreach (var line in stdout.Split('\n'))
            {
                if (ParseEvent(line) is not { } evt) continue;
                switch (Str(evt, "type"))
                {
                    case "init":
                        model ??= Str(evt, "model");
                        break;
                    case "message" or "assistant":
                        if (MessageText(evt) is { Length: > 0 } said) answer = said;
                        break;
                    case "error":
                        error ??= ErrorMessage(evt) ?? "the run failed";
                        break;
                    case "result":
                        if ((Str(evt, "response") ?? Str(evt, "content")) is { Length: > 0 } final)
                            answer = final;
                        if (evt.TryGetProperty("stats", out var st)) stats = st;
                        if (ErrorMessage(evt) is { } failed) error = failed;
                        else if (Str(evt, "status") is { Length: > 0 } status
                                 && !status.Equals("success", StringComparison.OrdinalIgnoreCase))
                            error ??= $"the run ended with status '{status}'";
                        break;
                }
            }
        }

        var (input, output, cacheRead, statsModel) = ReadTokens(stats);
        model = statsModel ?? model ?? _model;

        if (error != null) return new AgentOutcome(null, error, input, output, cacheRead, 0, model);

        if (ExtractJson(answer) is not { } document)
            return new AgentOutcome(null, "the run finished without producing an analysis document",
                input, output, cacheRead, 0, model);

        return new AgentOutcome(document, null, input, output, cacheRead, 0, model);
    }

    // ---- reading the CLI's shapes -------------------------------------------

    /// <summary>
    /// The CLI's own tool names, in Claude's vocabulary — the one every consumer of
    /// <see cref="AgentStep"/> already understands.
    /// <para>
    /// An MCP tool arrives here fully qualified as <c>server__tool</c> and is reassembled into
    /// <c>mcp__server__tool</c>, which is the shape the rest of the dashboard keys off. Anything
    /// else is passed through under its own name: an unrecognised tool should read as itself in the
    /// feed rather than be quietly relabelled as something it is not.
    /// </para>
    /// </summary>
    public static string CanonicalTool(string name)
    {
        if (name.StartsWith("mcp__", StringComparison.Ordinal)) return name;

        var built = name switch
        {
            "read_file" or "ReadFileTool" or "read_many_files" or "ReadManyFilesTool" => "Read",
            "write_file" or "WriteFileTool" => "Write",
            "replace" or "EditTool" => "Edit",
            "run_shell_command" or "ShellTool" => "Bash",
            "search_file_content" or "GrepTool" or "ripGrep" => "Grep",
            "glob" or "GlobTool" or "findFiles" => "Glob",
            "google_web_search" or "WebSearchTool" => "WebSearch",
            "web_fetch" or "WebFetchTool" => "WebFetch",
            _ => null,
        };
        if (built != null) return built;

        // No built-in tool name contains a double underscore, so one that does is an MCP tool
        // wearing this CLI's `server__tool` namespace.
        var sep = name.IndexOf("__", StringComparison.Ordinal);
        return sep > 0 ? $"mcp__{name[..sep]}__{name[(sep + 2)..]}" : name;
    }

    /// <summary>
    /// The text of one message event, whether the CLI put it in a string or in Gemini's own
    /// <c>parts</c>-style array.
    /// </summary>
    private static string? MessageText(JsonElement evt)
    {
        if (Str(evt, "role") is { } role && !role.Equals("assistant", StringComparison.OrdinalIgnoreCase))
            return null;

        if (Str(evt, "content") is { Length: > 0 } flat) return flat;
        if (Str(evt, "text") is { Length: > 0 } text) return text;

        if (Prop(evt, "content") is { ValueKind: JsonValueKind.Array } parts)
        {
            var joined = string.Concat(parts.EnumerateArray()
                .Select(p => p.ValueKind == JsonValueKind.String ? p.GetString() : Str(p, "text")));
            if (joined.Length > 0) return joined;
        }
        return null;
    }

    /// <summary>The message off an error, from either the nested or the flat spelling.</summary>
    private static string? ErrorMessage(JsonElement obj)
    {
        if (Prop(obj, "error") is { } err)
        {
            if (err.ValueKind == JsonValueKind.String) return err.GetString();
            if (err.ValueKind == JsonValueKind.Object) return Str(err, "message") ?? "the run failed";
        }
        return Str(obj, "type") == "error" ? Str(obj, "message") : null;
    }

    /// <summary>
    /// The accounting, from either shape of <c>stats</c>.
    /// <para>
    /// The documented one is nested per model — <c>stats.models["gemini-..."].tokens</c> — and there
    /// may be more than one of them, because the default <c>auto</c> alias routes a single session
    /// between a fast model and a capable one. The totals are therefore SUMMED across models and the
    /// model reported is whichever did most of the work, which is the honest answer to "what ran
    /// this". A flatter <c>input_tokens</c>/<c>output_tokens</c> pair is accepted too.
    /// </para>
    /// </summary>
    private static (long Input, long Output, long CacheRead, string? Model) ReadTokens(JsonElement stats)
    {
        if (stats.ValueKind != JsonValueKind.Object) return (0, 0, 0, null);

        if (Prop(stats, "models") is { ValueKind: JsonValueKind.Object } models)
        {
            long input = 0, output = 0, cacheRead = 0, most = -1;
            string? model = null;

            foreach (var entry in models.EnumerateObject())
            {
                var tokens = Prop(entry.Value, "tokens") ?? entry.Value;
                var prompt = Long(tokens, "prompt");
                // Thinking tokens are billed as output and reported apart from the answer's own, so
                // leaving them out would under-report every run on a reasoning model.
                var answered = Long(tokens, "candidates") + Long(tokens, "thoughts");
                input += prompt;
                output += answered;
                cacheRead += Long(tokens, "cached");

                if (prompt + answered > most) { most = prompt + answered; model = entry.Name; }
            }
            return (input, output, cacheRead, model);
        }

        return (Long(stats, "input_tokens"), Long(stats, "output_tokens"),
                Long(stats, "cached_tokens") + Long(stats, "cached_input_tokens"), null);
    }

    /// <summary>
    /// The JSON object out of an answer that was ASKED to be nothing else. Tried three ways,
    /// cheapest first: the whole message, a fenced block, then the outermost braces in the prose.
    /// The last of those is what recovers a run that could not resist a sentence of introduction.
    /// </summary>
    private static JsonElement? ExtractJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim();

        if (TryParse(s) is { ValueKind: JsonValueKind.Object } whole) return whole;

        var fence = Regex.Match(s, @"```(?:json)?\s*(\{.*\})\s*```", RegexOptions.Singleline);
        if (fence.Success && TryParse(fence.Groups[1].Value) is { ValueKind: JsonValueKind.Object } fenced)
            return fenced;

        var open = s.IndexOf('{');
        var close = s.LastIndexOf('}');
        if (open >= 0 && close > open && TryParse(s[open..(close + 1)]) is { ValueKind: JsonValueKind.Object } loose)
            return loose;

        return null;
    }

    // ---- the per-run configuration directory --------------------------------

    /// <summary>Environment both runs share.</summary>
    private static void AddSharedEnvironment(AgentRun run)
    {
        // The CLI asks whether a folder is trusted before it will use its tools there, and a
        // headless run has nobody to ask. Every directory these two callers point it at is one the
        // dashboard chose — its own repo, or a read-only copy of a project already registered with
        // it — so the question is answered here rather than left to hang.
        run.Environment["GEMINI_CLI_TRUST_WORKSPACE"] = "true";
        // The answer is parsed as JSON. Escape codes in it would not be.
        run.Environment["NO_COLOR"] = "1";
    }

    /// <summary>Where the CLI keeps config and credentials when we have not redirected it.</summary>
    private static string UserGeminiDir()
    {
        var home = Environment.GetEnvironmentVariable("GEMINI_CLI_HOME");
        if (string.IsNullOrWhiteSpace(home))
            home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".gemini");
    }

    /// <summary>
    /// Give this run a configuration directory of its own, holding only what we put there.
    /// <para>
    /// Same reasoning as Codex's <c>CODEX_HOME</c> and the same trade: the CLI has no
    /// <c>--mcp-config</c>, its servers otherwise come from the user's own settings, so replacing
    /// the whole directory IS the "only these servers" flag. Neither C# caller wants any MCP server
    /// at all, so the map written here is empty — which is the point.
    /// </para>
    /// <para>
    /// The redirect also moves where the CLI looks for its LOGIN, so the cached OAuth files are
    /// copied across, along with the <c>security</c> block that records WHICH sign-in method the
    /// user chose. Nothing else is: inheriting the rest would be exactly the ambient-configuration
    /// leak this exists to prevent. An API key needs none of it — that arrives in the inherited
    /// environment as <c>GEMINI_API_KEY</c>.
    /// </para>
    /// <para>Returns the value for <c>GEMINI_CLI_HOME</c>: the PARENT of the directory written.</para>
    /// </summary>
    private static string PrepareHome(string scratchDir, bool readOnly)
    {
        var home = Path.Combine(scratchDir, "gemini-home");
        var dir = Path.Combine(home, ".gemini");
        Directory.CreateDirectory(dir);

        var source = UserGeminiDir();
        foreach (var name in new[] { "oauth_creds.json", "google_accounts.json" })
        {
            var from = Path.Combine(source, name);
            if (!File.Exists(from)) continue;
            try { File.Copy(from, Path.Combine(dir, name), overwrite: true); }
            catch { /* surfaced as a sign-in failure on the run itself, which says more than we could here */ }
        }

        var settings = new JsonObject
        {
            // Written explicitly rather than left out: an absent key is a question about defaults,
            // an empty map is an answer.
            ["mcpServers"] = new JsonObject(),
        };

        if (CarriedSecurity(source) is { } security) settings["security"] = security;

        if (readOnly)
        {
            // Allowlist AND blocklist. The allowlist is the intent; the blocklist is the backstop,
            // and it is the stronger of the two because a tool named in both is excluded — so if
            // the allowlist is ignored, the writing tools are still gone.
            settings["tools"] = new JsonObject
            {
                ["core"] = Strings(ReadingTools),
                ["exclude"] = Strings(WritingTools),
            };
            // The same pair under the names this file used before the settings were nested. Both
            // spellings are written because we cannot know which version is installed, and the one
            // that does not recognise a key ignores it.
            settings["coreTools"] = Strings(ReadingTools);
            settings["excludeTools"] = Strings(WritingTools);
        }

        File.WriteAllText(Path.Combine(dir, "settings.json"),
            settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
        return home;
    }

    /// <summary>
    /// The user's chosen sign-in method, lifted out of their own settings file.
    /// <para>
    /// Without it a redirected run has the credentials but not the record of what KIND they are,
    /// and settling that is an interactive question — which a headless run cannot be asked.
    /// </para>
    /// </summary>
    private static JsonNode? CarriedSecurity(string sourceDir)
    {
        var path = Path.Combine(sourceDir, "settings.json");
        if (!File.Exists(path)) return null;
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) is JsonObject o && o["security"] is { } security
                ? security.DeepClone()
                : null;
        }
        catch { return null; }   // their file, their problem: this run just goes without.
    }

    private static JsonArray Strings(string[] values)
    {
        var array = new JsonArray();
        foreach (var v in values) array.Add(v);
        return array;
    }
}

// ---------------------------------------------------------------------------

/// <summary>
/// Which model names belong to which provider, in one table.
/// <para>
/// The question every caller actually asks is "would this CLI reject this name?", and with three
/// providers that is no longer a single pattern per provider — it is "does this name unambiguously
/// belong to one of the OTHERS". Hence one table read from both sides rather than a foreign-model
/// pattern per provider, which with three of them would be three overlapping unions to keep in step.
/// </para>
/// <para>
/// Deliberately one-sided, exactly as it was with two: a name matching NOTHING here belongs to
/// nobody in particular and is passed through, so a model slug released after this was written still
/// works without an edit. The dashboard's Settings endpoint and the heartbeat's
/// <c>Test-AgentModelBelongsTo</c> apply the same rule to the same names, so the two halves cannot
/// disagree about which models a provider will take.
/// </para>
/// </summary>
public static class AgentModels
{
    private static readonly (string Provider, Regex Own)[] Signatures =
    {
        ("claude", Signature("^(claude|opus|sonnet|haiku|fable)")),
        ("codex", Signature("^gpt")),
        // pro / flash / auto are the CLI's own routing aliases, and no other provider's models
        // begin with them.
        ("gemini", Signature("^(gemini|pro|flash|auto)")),
    };

    private static Regex Signature(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Could this model name plausibly be one of <paramref name="provider"/>'s? False only when it
    /// is unmistakably another provider's.
    /// </summary>
    public static bool BelongsTo(string provider, string model)
    {
        var name = model.Trim();
        if (name.Length == 0) return false;
        foreach (var (owner, own) in Signatures)
        {
            if (string.Equals(owner, provider, StringComparison.OrdinalIgnoreCase)) continue;
            if (own.IsMatch(name)) return false;
        }
        return true;
    }
}

// ---------------------------------------------------------------------------

/// <summary>
/// Which provider the dashboard is currently pointed at, read fresh from global config each time.
/// <para>
/// Not cached on purpose. The provider is a Settings toggle, and a value captured at startup would
/// mean the setting silently does nothing until the dashboard is restarted — which, here, also kills
/// every in-flight worker.
/// </para>
/// </summary>
public class AgentProviderFactory
{
    private readonly ProjectRegistry _registry;
    private readonly QaIsolation _isolation;

    public AgentProviderFactory(ProjectRegistry registry, QaIsolation? isolation = null) =>
        (_registry, _isolation) = (registry, isolation ?? QaIsolation.Off);

    /// <summary>
    /// The configured provider. An unrecognised name falls back to Claude, which is the incumbent:
    /// a typo in config.json must not silently stop the dashboard working.
    /// </summary>
    public AgentProviderKind CurrentKind => Configured().Trim().ToLowerInvariant() switch
    {
        "codex" => AgentProviderKind.Codex,
        "gemini" => AgentProviderKind.Gemini,
        _ => AgentProviderKind.Claude,
    };

    /// <summary>The pipeline's provider, on the configured worker model.</summary>
    public AgentProvider Resolve() => Build(Str("workerModel"));

    /// <summary>
    /// The provider for a Classroom analysis.
    /// <para>
    /// Separate from <see cref="Resolve"/> because the two jobs are not alike. A worker writes code
    /// against a spec and its output is checked by QA and by a human at review; an analysis is read
    /// once, believed, and never verified by anything downstream — which makes it the run where
    /// paying for a stronger model can be worth it even when the pipeline is not.
    /// </para>
    /// <para>
    /// Falls back to the worker model when unset, and unset is stored as an ABSENCE rather than a
    /// copy — the same rule <c>phaseModels</c> follows. A copy would pin the analysis to whatever
    /// the worker model happened to be the day it was saved, so changing the worker model later
    /// would leave the analysis silently behind on the old one.
    /// </para>
    /// </summary>
    public AgentProvider ResolveForAnalysis() =>
        Build(Str("analysisModel") is { } over && over.Trim().Length > 0 ? over : Str("workerModel"));

    /// <summary>
    /// How hard every run thinks when Settings has never been told. Never "whatever the CLI
    /// defaults to": Claude Code's default moves with the model (Opus 5 ran at high, Opus 5.5 at
    /// medium), so a blank would quietly mean something else after each model change. The
    /// heartbeat reads the same setting with the same fallback, in heartbeat-common.ps1.
    /// </summary>
    public const string DefaultEffort = "medium";

    /// <summary>The one effort setting, which governs the pipeline, the Classroom and the applier alike.</summary>
    public string Effort =>
        Str("workerReasoningEffort") is { } e && e.Trim().Length > 0 ? e.Trim() : DefaultEffort;

    // A QA copy gets a provider that builds no command, whatever asked for one.
    private AgentProvider Build(string? model) => _isolation.On ? new RefusedAgentProvider() : CurrentKind switch
    {
        AgentProviderKind.Codex => new CodexAgentProvider(Honour("codex", model, CodexAgentProvider.DefaultModel), Effort),
        // No effort dial on this CLI: thinking is a property of the model picked.
        AgentProviderKind.Gemini => new GeminiAgentProvider(Honour("gemini", model, GeminiAgentProvider.DefaultModel)),
        // Claude reads the same setting as the other two. It used to be the exception - the two C#
        // runs took whatever the CLI defaulted to - which made `workerModel` a field that governed
        // the heartbeat's workers and silently did nothing to an analysis. The panel said Opus 5 and
        // the finished document said otherwise, which is the worst of both. Effort follows the same
        // rule, for the same reason.
        _ => new ClaudeAgentProvider(Honour("claude", model, ClaudeAgentProvider.DefaultModel), Effort,
            ClaudeAccounts.Active(_registry.GetGlobalConfig()).ConfigDir),
    };

    private string Configured() => Str("agentProvider") ?? "claude";

    /// <summary>
    /// Honour a configured model only when this provider could plausibly take it. Switching provider
    /// and forgetting the model would otherwise hand <c>claude-opus-5</c> to codex and fail every
    /// run with an error that says nothing about the real cause — the same guard the PowerShell half
    /// applies in <c>Resolve-AgentModel</c>, over the same table.
    /// </summary>
    private static string Honour(string provider, string? configured, string fallback)
    {
        var model = configured?.Trim();
        if (string.IsNullOrEmpty(model)) return fallback;
        return AgentModels.BelongsTo(provider, model) ? model : fallback;
    }

    private string? Str(string key) =>
        _registry.GetGlobalConfig().TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
