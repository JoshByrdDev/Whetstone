using System.Diagnostics;
using System.Text;

namespace Whetstone.Services;

/// <summary>
/// Applies a project's <b>approved</b> improvement suggestions. In the dashboard-owned model the
/// suggestions, lessons, skills, prompts and config all live under the dashboard's own <c>data/</c>
/// tree (NOT the target repo's <c>.claude/</c>), so this runs a headless agent at the dashboard repo
/// root with a self-contained prompt that edits those files in place. It never touches the target
/// repo, and it does not commit — changes are left for the user to review.
/// </summary>
public class ImprovementApplier
{
    private readonly DashboardPaths _paths;
    private readonly ProjectRegistry _registry;
    private readonly AgentProviderFactory _providers;
    private readonly ClaudeUsageService _usage;
    private AgentRun? _run;
    private Process? _process;
    private readonly StringBuilder _output = new();
    private bool _running;
    private bool? _success;
    private string? _error;

    public ImprovementApplier(DashboardPaths paths, ProjectRegistry registry,
        AgentProviderFactory providers, ClaudeUsageService usage)
    {
        _paths = paths;
        _registry = registry;
        _providers = providers;
        _usage = usage;
    }

    public bool IsRunning => _running;

    public object GetStatus() => new
    {
        running = _running,
        output = _output.ToString(),
        success = _success,
        error = _error
    };

    public void Reset()
    {
        _output.Clear();
        _success = null;
        _error = null;
    }

    public async Task<(bool success, string message)> StartAsync(string project)
    {
        if (_running)
            return (false, "Apply is already running");

        // A spent token does not make the CLI fail fast - it retries the 401 for minutes and then
        // exits 1 - so the login is renewed before anything is spawned. See EnsureSignedInAsync.
        if (await _usage.EnsureSignedInAsync() is { } authError)
            return (false, authError);

        var suggestionsFile = _paths.ImprovementsFile(project);
        if (!File.Exists(suggestionsFile))
            return (false, "No improvement-suggestions.md for this project yet.");

        // Cheap pre-check: nothing to do unless at least one item is approved (checked).
        if (!File.ReadLines(suggestionsFile).Any(l => l.TrimStart().StartsWith("- [x]")))
            return (false, "No approved (checked) suggestions to apply.");

        var displayName = _registry.GetProjectConfig(project).DisplayName;
        var prompt = BuildPrompt(project, displayName);

        // Run at the dashboard repo root (parent of data/) so the agent sees the dashboard's own
        // CLAUDE.md/git and can edit the data/ files referenced (by absolute path) in the prompt.
        var repoRoot = Directory.GetParent(_paths.DataRoot)?.FullName ?? _paths.DataRoot;

        _output.Clear();
        _success = null;
        _error = null;
        _running = true;

        // One apply runs at a time (guarded above), so one scratch directory is enough. It is
        // recreated per run and torn down on exit, because for some providers it holds a copy of a
        // login rather than just config.
        var scratch = Path.Combine(_paths.RunDir, "improvements");
        try { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); } catch { /* best effort */ }
        Directory.CreateDirectory(scratch);

        try
        {
            var provider = _providers.Resolve();
            _run = provider.BuildFreeformRun(repoRoot, scratch);

            _process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = CliPath.Resolve(_run.FileName),
                    // Prompt is fed on stdin (avoids escaping a large multi-line argument).
                    WorkingDirectory = repoRoot,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    // Same reason as the Classroom run: the CLI speaks UTF-8 both ways, and the
                    // console's code page is not a fact about the CLI.
                    StandardOutputEncoding = ProcessText.Utf8,
                    StandardErrorEncoding = ProcessText.Utf8,
                    StandardInputEncoding = ProcessText.Utf8,
                },
                EnableRaisingEvents = true
            };
            foreach (var arg in _run.Arguments) _process.StartInfo.ArgumentList.Add(arg);
            foreach (var (k, v) in _run.Environment) _process.StartInfo.Environment[k] = v;

            _process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null) _output.AppendLine(e.Data);
            };
            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) _output.AppendLine(e.Data);
            };
            _process.Exited += (_, _) =>
            {
                _success = _process?.ExitCode == 0;
                if (_success != true)
                    _error = "Process exited with non-zero exit code";
                _running = false;
                if (_run != null) AgentProvider.Cleanup(_run);
            };

            _process.Start();
            _process.StandardInput.Write(prompt);
            _process.StandardInput.Close();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            return (true, "Apply started");
        }
        catch (Exception ex)
        {
            _running = false;
            _error = ex.Message;
            if (_run != null) AgentProvider.Cleanup(_run);
            return (false, $"Failed to start: {ex.Message}");
        }
    }

    /// <summary>
    /// A self-contained instruction set for the apply run, wired to this project's dashboard-owned
    /// files by absolute path. Replaces the old target-repo <c>.claude/skills/apply-improvements</c>.
    /// </summary>
    private string BuildPrompt(string project, string displayName)
    {
        var suggestionsFile = _paths.ImprovementsFile(project);
        var memoryDir = _registry.GetProjectConfig(project).ResolvedMemoryDir;
        var skillsDir = _paths.SkillsDir(project);
        var reflectPrompt = _paths.ReflectPromptFile(project);
        var globalPrompt = _paths.GlobalHeartbeatPrompt;

        string P(string path) => path.Replace('\\', '/');

        return $$"""
        You are running in heartbeat-mode automation for the "{{displayName}}" project ({{project}}) in the
        Claude Developer Dashboard. No human is watching. Apply this project's APPROVED improvement
        suggestions. You may edit the dashboard's own files and the target repo's NOTES STORE (named
        below) - nothing else in the target repo, and no git commit anywhere.

        All paths below are absolute. Edit them directly.

        - Suggestions (source):     {{P(suggestionsFile)}}
        - Repo notes store:         {{P(memoryDir)}}  (one file per fact + MEMORY.md index)
        - This project's skills:    {{P(skillsDir)}}/<skill>/skill.md
        - This project's reflect prompt: {{P(reflectPrompt)}}
        - Global heartbeat prompt (shared by all projects): {{P(globalPrompt)}}

        ## Steps

        1. Read the suggestions file. Process ONLY items marked approved: `- [x]`. Leave every `- [ ]`
           (pending) item exactly as-is.

           Each item carries one of five tags, and they route to exactly two destinations:

           - `[environment]`, `[codebase]`, `[lesson]` - a FACT about the repo or the machine.
             Goes to the notes store. (`environment`/`codebase` are what the reflection writes now;
             `lesson` is the older name for the same thing. Untagged also means this.)
           - `[process]`, `[system]` - a change to how the PIPELINE behaves. Edits a dashboard-owned
             skill, prompt or config. (`process` is current, `system` is the older name.)

           The test behind the split: would a human developer, or an interactive Claude session in
           this repo, also be wrong without it? Yes means it is a fact and belongs in the notes
           store. Only-the-unattended-pipeline-would-be-wrong means it is a pipeline change.

        2. KNOWLEDGE track - for each approved `[environment]`, `[codebase]` or `[lesson]` item:
           - FOLDING INTO AN EXISTING NOTE IS THE PREFERRED OUTCOME, not the fallback. An item only
             reached this queue because the automated matcher could not place it, so a note that
             covers it usually does exist under a name the matcher did not recognise. Before writing
             anything, read `MEMORY.md`, then open the two or three notes whose subject is closest
             and read them in full. The index line summarises a note's title, not its contents - the
             fact you are about to add may already be a bullet inside one of them.
           - If a note covers this subject, rewrite THAT note to include the fact, and update its
             `description:` and `last-confirmed:` to match. Preserve every specific detail already
             there - file paths, line numbers, endpoint and column names, exact error strings. They
             were expensive to learn and losing one is worse than an untidy note.
           - Only when no note covers it, write a new `<short-kebab-key>.md` matching the shape of
             the files already there (YAML frontmatter with `name`, `description` and a `metadata`
             block carrying `learned` and `last-confirmed`, then the fact, then a `**How to apply:**`
             line where it helps), and add one line to `MEMORY.md`.
           - Machine-written index lines go under the `## Unsorted (written by reflection)` heading
             at the bottom. Do not append below the last human section, which is how that index came
             to have two halves that read differently.
           - Never delete an existing note in this track, and never replace one with something
             shorter that drops detail.
           - This directory is the ONE exception to "do not touch the target repo" below. Write
             nothing else there: no source, no config, no commits.

        3. PIPELINE track - for each approved `[process]` or `[system]` item:
           - Determine which dashboard-owned file it changes: a skill under the skills dir, the global
             heartbeat prompt, this project's reflect prompt, or config. Read that file, make the
             MINIMAL precise change described, and verify it still makes sense.
           - Only edit files under the dashboard's data/ tree (the paths above). Never edit application
             source, and never edit the target repo outside its notes store. If a suggestion would
             require changes outside these files, or is too ambiguous to action safely, leave it in
             the suggestions file unchecked with a one-line note on why, instead of changing code.
           - THE GLOBAL HEARTBEAT PROMPT IS SHARED. Every project's workers read it, so anything
             written there must be true of a repo you have never seen: no project name, no repo
             layout, no framework, no service or container name, no example drawn from this
             project's code. A lesson that cannot be stated that generally belongs in this
             project's own skill file instead - which is always the safe destination when you are
             unsure which of the two a suggestion wants.

        4. Remove every processed `- [x]` item (both tracks) from the suggestions file. If a `###`
           section is left with no items, remove that section too. Leave all `- [ ]` items.

        5. Do NOT create a branch or commit - leave the edited dashboard files uncommitted for review.

        6. Output a short summary: notes written, system changes made (list each file), items skipped
           (duplicate/ambiguous), and items still pending.
        """;
    }
}
