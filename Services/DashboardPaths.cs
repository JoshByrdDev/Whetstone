namespace Whetstone.Services;

/// <summary>
/// Single source of truth for every filesystem path the dashboard uses.
/// All state lives under a self-contained <c>data/</c> root inside this repo —
/// the dashboard never resolves paths relative to the projects it monitors.
/// </summary>
public class DashboardPaths
{
    public string DataRoot { get; }

    public DashboardPaths(IWebHostEnvironment env)
    {
        DataRoot = ResolveDataRoot(env);
        Directory.CreateDirectory(DataRoot);
    }

    /// <summary>
    /// An explicit root, for a caller that already knows where its data lives — tests, mostly.
    /// <see cref="ResolveDataRoot"/> deliberately falls back to walking up from the binary, which
    /// finds the real repo from a test run and would have it writing into the live data folder.
    /// </summary>
    public DashboardPaths(string dataRoot)
    {
        DataRoot = Path.GetFullPath(dataRoot);
        Directory.CreateDirectory(DataRoot);
    }

    private static string ResolveDataRoot(IWebHostEnvironment env)
    {
        // 1. Explicit override (deployment / tests)
        var overridePath = Environment.GetEnvironmentVariable("DASHBOARD_DATA_ROOT");
        if (!string.IsNullOrWhiteSpace(overridePath))
            return Path.GetFullPath(overridePath);

        // 2. Walk up from the content root and the binary location to find the
        //    repo root (the folder containing Whetstone.csproj). This works
        //    for both `dotnet run` (cwd = repo root) and running the built exe
        //    from bin/Debug/net9.0.
        foreach (var start in new[] { env.ContentRootPath, AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Whetstone.csproj")))
                    return Path.Combine(dir.FullName, "data");
                dir = dir.Parent;
            }
        }

        // 3. Fallback: content root
        return Path.Combine(env.ContentRootPath, "data");
    }

    public string ProjectsDir => Path.Combine(DataRoot, "projects");

    /// <summary>Global settings the user edits (gitignored — holds notification secrets + active project).</summary>
    public string GlobalConfigPath => Path.Combine(DataRoot, "config.json");
    /// <summary>Committed non-secret global defaults (dashboard URL, heartbeat timing, off-hours).</summary>
    public string GlobalConfigDefaultsPath => Path.Combine(DataRoot, "config.defaults.json");

    /// <summary>The local ticketing SQLite database (gitignored — runtime state).</summary>
    public string DatabaseFile => Path.Combine(DataRoot, "dashboard.db");

    /// <summary>Stored QA screenshot shots, one folder per ticket (gitignored — runtime artifacts).</summary>
    public string QaShotsDir => Path.Combine(DataRoot, "qa-shots");
    public string QaShotTicketDir(int ticketId) => Path.Combine(QaShotsDir, ticketId.ToString());

    /// <summary>
    /// The Node script that voices a walkthrough, and its node_modules and model cache (the script and
    /// package.json are tracked; node_modules and models/ are not).
    /// </summary>
    public string NarratorDir => Path.Combine(DataRoot, "narrator");

    /// <summary>
    /// Every voiced walkthrough line, named by a hash of voice and spoken text - so a line two decks
    /// share, or a deck regenerated with one sentence changed, is synthesised once (gitignored).
    /// </summary>
    public string WalkthroughClipsDir => Path.Combine(DataRoot, "walkthrough-clips");

    /// <summary>
    /// Each unit report's own copy of the screenshots it cites, one folder per report. Copied, not
    /// pointed at: a ticket's shots are deleted on every re-QA (gitignored).
    /// </summary>
    public string UnitReportsDir => Path.Combine(DataRoot, "unit-reports");
    public string UnitReportShotsDir(int reportId) => Path.Combine(UnitReportsDir, reportId.ToString(), "shots");

    /// <summary>
    /// Files the ticket's owner attached to a progress note, one folder per ticket (gitignored —
    /// runtime artifacts). Kept apart from the issue cache below because these are not a cache: there
    /// is no upstream to re-fetch them from, so this folder is the only copy.
    /// </summary>
    public string TicketFilesDir => Path.Combine(DataRoot, "ticket-files");
    public string TicketFilesTicketDir(int ticketId) => Path.Combine(TicketFilesDir, ticketId.ToString());

    /// <summary>
    /// Linked-issue attachment bytes cached locally, one folder per ticket (gitignored — runtime
    /// artifacts). The worker has no tracker credentials, so it reads attachments as local files
    /// from here.
    /// </summary>
    public string IssueAttachmentsDir => Path.Combine(DataRoot, "issue-attachments");
    public string IssueAttachmentTicketDir(int ticketId) => Path.Combine(IssueAttachmentsDir, ticketId.ToString());

    /// <summary>
    /// The folder this cache used to live in, back when Jira was the only tracker there could be.
    /// Kept only so <see cref="MigrateLegacyAttachmentCache"/> can find it.
    /// </summary>
    private string LegacyIssueAttachmentsDir => Path.Combine(DataRoot, "jira-attachments");

    /// <summary>
    /// Move the old <c>jira-attachments</c> cache to its tracker-neutral name, once, on startup.
    /// <para>
    /// Strictly this is optional - the folder is a cache, and losing it costs one re-download the
    /// next time a phase asks for context. It is done anyway because of what the cache is FOR: a
    /// ticket already mid-flight is told which attachments exist by listing this folder, and a
    /// ticket that quietly lost its design PDF between one phase and the next is exactly the
    /// failure the listing was added to prevent.
    /// </para>
    /// </summary>
    public void MigrateLegacyAttachmentCache()
    {
        try
        {
            if (!Directory.Exists(LegacyIssueAttachmentsDir) || Directory.Exists(IssueAttachmentsDir)) return;
            Directory.Move(LegacyIssueAttachmentsDir, IssueAttachmentsDir);
        }
        catch { /* a cache that could not be moved is a cache that gets refilled */ }
    }

    /// <summary>
    /// Where an attachment inherited from an ancestor issue lands: a subfolder of the ticket's own,
    /// named for the issue it came from. Kept apart from the ticket's own attachments so a reader is
    /// never told the epic's mockup was attached to their ticket, and so "which of my attachments
    /// failed to download" stays an answerable question.
    /// </summary>
    public string IssueAttachmentAncestorDir(int ticketId, string issueKey)
        => Path.Combine(IssueAttachmentTicketDir(ticketId), "from-" + SafeSegment(issueKey));

    /// <summary>Recovers the issue key from a folder <see cref="IssueAttachmentAncestorDir"/> made.</summary>
    public static string? AncestorKeyFromDir(string dirName)
        => dirName.StartsWith("from-", StringComparison.OrdinalIgnoreCase) ? dirName[5..] : null;

    private static string SafeSegment(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return string.IsNullOrWhiteSpace(s) ? "unknown" : s;
    }

    /// <summary>The one global heartbeat activity log — events across every project (gitignored).</summary>
    public string GlobalEventsFile => Path.Combine(DataRoot, "events.jsonl");

    // ---- The one global heartbeat (project-agnostic orchestrator) ----
    public string GlobalHeartbeatScript => Path.Combine(DataRoot, "heartbeat.ps1");
    /// <summary>One agent, one ticket, one process — what the orchestrator launches per slot.</summary>
    public string GlobalWorkerScript => Path.Combine(DataRoot, "worker.ps1");
    /// <summary>Per-run scratch the fleet writes to: staged claims and per-ticket MCP configs (gitignored).</summary>
    public string RunDir => Path.Combine(DataRoot, "run");
    public string GlobalHeartbeatPrompt => Path.Combine(DataRoot, "heartbeat-prompt.md");
    public string GlobalMcpConfig => Path.Combine(DataRoot, "mcp-config.json");
    public string GlobalLockFile => Path.Combine(DataRoot, "heartbeat.lock");
    public string GlobalStopFile => Path.Combine(DataRoot, "heartbeat.stop");
    public string GlobalWakeFile => Path.Combine(DataRoot, "heartbeat.wake");
    public string GlobalLogsDir => Path.Combine(DataRoot, "logs");

    /// <summary>Committed, project-agnostic skill + operational-file templates used to scaffold a new project.</summary>
    public string SkillTemplatesDir => Path.Combine(DataRoot, "skill-templates");
    public string SkillTemplate(string skill) => Path.Combine(SkillTemplatesDir, skill, "skill.md");
    public string ReflectPromptTemplate => Path.Combine(SkillTemplatesDir, "reflect-prompt.md");

    public string ProjectDir(string project) => Path.Combine(ProjectsDir, project);
    public string ProjectConfig(string project) => Path.Combine(ProjectDir(project), "project.json");
    public string ProjectConfigDefaults(string project) => Path.Combine(ProjectDir(project), "project.defaults.json");

    // Per-project data files. The heartbeat, lock/stop signals, logs and events are now GLOBAL
    // (one project-agnostic orchestrator) — see the Global* members above; there are no per-project
    // heartbeat/lock/events paths any more.
    public string SkillsDir(string project) => Path.Combine(ProjectDir(project), "skills");
    public string SkillFile(string project, string skill) => Path.Combine(SkillsDir(project), skill, "skill.md");
    public string ImprovementsFile(string project) => Path.Combine(ProjectDir(project), "improvement-suggestions.md");
    public string ReflectPromptFile(string project) => Path.Combine(ProjectDir(project), "reflect-prompt.md");

    /// <summary>Marker file that unregisters a project without deleting its data (reversible).</summary>
    public string DisabledMarker(string project) => Path.Combine(ProjectDir(project), ".disabled");
    public bool IsDisabled(string project) => File.Exists(DisabledMarker(project));

    /// <summary>Active (enabled) project names — folders with a config that are NOT unregistered.</summary>
    public IEnumerable<string> DiscoverProjects() =>
        DiscoverAllProjects().Where(name => !IsDisabled(name));

    /// <summary>Every configured project folder, including unregistered ones (for management UI).</summary>
    public IEnumerable<string> DiscoverAllProjects()
    {
        if (!Directory.Exists(ProjectsDir)) yield break;
        foreach (var dir in Directory.GetDirectories(ProjectsDir).OrderBy(d => d))
        {
            var name = Path.GetFileName(dir);
            if (File.Exists(ProjectConfig(name)) || File.Exists(ProjectConfigDefaults(name)))
                yield return name;
        }
    }
}
