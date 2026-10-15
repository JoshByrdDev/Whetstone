using System.Text.Json;
using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services.Tracker;

namespace Whetstone.Services;

/// <summary>Resolved, typed config for one monitored project.</summary>
public class ProjectConfig
{
    public string Name { get; init; } = "";
    public string DisplayName { get; init; } = "";
    /// <summary>Absolute path to the external repo this project works on — the ONLY outward reference.</summary>
    public string ProjectPath { get; init; } = "";
    /// <summary>Branch new feature branches fork from. Empty = inherit the global baseBranch (default "main").</summary>
    public string BaseBranch { get; init; } = "";

    /// <summary>
    /// Which version control the hub actually answers to: <see cref="RepoTypes.Git"/> (the default,
    /// and what an absent key means) or <see cref="RepoTypes.Tfvc"/>.
    /// <para>
    /// A tfvc project is still a git repo as far as the pipeline is concerned - worktrees, branches
    /// and diffs are all git, in a mirror that shares its working tree with a TFVC local workspace.
    /// What changes is the last step. There is no remote to push to and no pull request to raise;
    /// the ticket's branch is merged into the hub without committing, a human checks the result in
    /// from Team Explorer, and only then is the merge committed to git. So the PR dialog becomes a
    /// check-in dialog, and every git action that would talk to a remote or move the hub off its
    /// base is refused for this project (see <c>GitEndpoints</c>).
    /// </para>
    /// </summary>
    public string RepoType { get; init; } = RepoTypes.Git;

    /// <summary>True when delivery is a TFVC check-in rather than a pull request.</summary>
    public bool IsTfvc => RepoType == RepoTypes.Tfvc;

    /// <summary>
    /// Repo-relative folders a TFVC workspace maps, on a <see cref="IsTfvc"/> project. A file outside
    /// all of them is git-only - the mirror's own <c>.gitignore</c>, <c>CLAUDE.md</c>, <c>.claude\</c>
    /// - and is never a check-in item. Empty = the whole repo is mapped.
    /// <para>
    /// Configured rather than discovered: <c>tf workfold</c> answers per folder and only once the
    /// command line is pointed somewhere mapped, and a guess that included an unmapped folder would
    /// ask TFVC about files it has never heard of, which it reports as an error on every check-in.
    /// </para>
    /// </summary>
    public string[] TfvcFolders { get; init; } = Array.Empty<string>();

    /// <summary>Whether <paramref name="repoRelativePath"/> is a file TFVC holds, by <see cref="TfvcFolders"/>.</summary>
    public bool InTfvc(string repoRelativePath)
    {
        if (TfvcFolders.Length == 0) return true;
        var path = repoRelativePath.Replace('\\', '/').TrimStart('/');
        return TfvcFolders.Any(f =>
        {
            var folder = f.Replace('\\', '/').Trim('/');
            return folder.Length > 0 && path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>Prefix for auto-created branch names (e.g. "jb/"). Empty = inherit the global branchPrefix.</summary>
    public string BranchPrefix { get; init; } = "";

    /// <summary>
    /// Root directory the heartbeat creates per-ticket worktrees under. Empty = derive it from
    /// <see cref="ProjectPath"/> — see <see cref="ResolvedWorktreeRoot"/>.
    /// </summary>
    public string WorktreeRoot { get; init; } = "";

    /// <summary>
    /// Commands run once inside a freshly created worktree, in order, before any worker starts.
    /// A new worktree has none of the repo's gitignored build state (node_modules, restored
    /// packages), and a worker that discovers that fact halfway through fails confusingly.
    /// </summary>
    public string[] WorktreeSetup { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Repo-relative gitignored files copied from <see cref="ProjectPath"/> into a fresh worktree
    /// (.env, appsettings.Development.json). Git cannot supply these — they are untracked by
    /// design — so without this a worktree is missing exactly the local config the app needs.
    /// </summary>
    public string[] WorktreeCopyFiles { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Extra globs whose files are ranked as generated in the PR diff — the review-noise list.
    /// <para>
    /// Almost always unnecessary: <see cref="DiffSignificance"/> already recognises lockfiles, build
    /// output and the header conventions generators emit (<c>&lt;auto-generated</c>, <c>DO NOT
    /// EDIT</c>), which covers a scaffolded API client without being told. This is for the repo that
    /// generates something unmarked, in a folder that looks hand-written. <c>*</c> stops at a
    /// separator, <c>**</c> does not.
    /// </para>
    /// </summary>
    public string[] DiffNoisePaths { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Shared things a phase needs exclusive use of, keyed by skill
    /// (<c>{ "qa-test": ["app-stack"] }</c>).
    /// <para>
    /// This is what stops two workers destroying each other once more than one runs at a time: QA
    /// rebuilds the app tier on fixed ports from its own worktree, so two QA runs in the same
    /// project cannot both be right. A bare name is scoped to this project (<c>app-stack</c> becomes
    /// <c>myproject:app-stack</c>); a name containing a colon is taken literally, which is how a resource
    /// shared ACROSS projects is expressed (a single machine-wide docker daemon, say).
    /// </para>
    /// <para>
    /// Lease only what a phase would DESTROY, never everything it merely uses. The database is the
    /// case that makes the difference: every phase queries it and only QA writes to it, so leasing it
    /// would serialise the whole pipeline behind QA to prevent a collision that does not happen. What
    /// does happen is a phase tearing down infrastructure on its way to rebuilding its own share of
    /// it - which is a fault in the teardown, fixed by naming services, not by widening the lease.
    /// </para>
    /// </summary>
    public Dictionary<string, string[]> PhaseResources { get; init; } = new();

    /// <summary>
    /// What <see cref="PhaseResources"/> falls back to, per skill, when a project says nothing: QA
    /// and review-pr both take the project's app tier, because both rebuild it on the same fixed
    /// ports - review-pr from a detached worktree of somebody else's branch. With QA alone here, a
    /// project that listed only qa-test let a review boot beside a QA run: a port clash, and each
    /// reporting on whichever build won. Chosen because it is true of every project here and costs
    /// nothing while only one worker runs - the lease is always free - so the mechanism is exercised
    /// in production long before anything depends on it.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> DefaultPhaseResources =
        new Dictionary<string, string[]>
        {
            ["qa-test"] = new[] { "app-stack" },
            ["review-pr"] = new[] { "app-stack" },
        };

    /// <summary>
    /// The resources a worker running <paramref name="skill"/> must hold, fully qualified. Empty
    /// means the phase can always run.
    /// </summary>
    public string[] ResourcesFor(string skill)
    {
        if (!PhaseResources.TryGetValue(skill, out var names))
            if (!DefaultPhaseResources.TryGetValue(skill, out names))
                return Array.Empty<string>();

        return names.Select(n => n.Contains(':') ? n : $"{Name}:{n}").ToArray();
    }

    /// <summary>
    /// Where this project's durable notes live, relative to <see cref="ProjectPath"/> (absolute is
    /// also accepted). Empty = <c>.claude/memory</c>.
    /// <para>
    /// This is the repo-side knowledge store: one file per fact plus a <c>MEMORY.md</c> index, the
    /// same shape interactive Claude writes. It lives in the repo rather than in dashboard data
    /// because the facts are true of the repo, not of the pipeline — a human or an interactive
    /// session is just as wrong without them. Keeping it under <c>.claude</c> also means it rides
    /// into every worktree on the existing <see cref="WorktreeCopyFiles"/> entry.
    /// </para>
    /// </summary>
    public string MemoryDir { get; init; } = "";

    /// <summary>
    /// Absolute path to the notes store, or empty when this project has no repo to hold one.
    /// Always resolves against the HUB repo: a worker's writes into its own worktree copy would be
    /// thrown away with the worktree, so the orchestrator writes here instead.
    /// </summary>
    public string ResolvedMemoryDir
    {
        get
        {
            var rel = string.IsNullOrWhiteSpace(MemoryDir) ? ".claude/memory" : MemoryDir.Trim();
            if (Path.IsPathRooted(rel)) return Path.GetFullPath(rel);
            if (string.IsNullOrWhiteSpace(ProjectPath)) return "";
            return Path.GetFullPath(Path.Combine(ProjectPath.Trim().TrimEnd('\\', '/'), rel));
        }
    }

    /// <summary>The notes index a worker is pointed at, or empty when there is no store.</summary>
    public string ResolvedMemoryIndex =>
        ResolvedMemoryDir is { Length: > 0 } dir ? Path.Combine(dir, "MEMORY.md") : "";

    /// <summary>
    /// Heading the ticket's learning brief is shown under. Empty = <c>"Learn {DisplayName}"</c>,
    /// which is what makes the section read as "Learn Acme" without the word Acme existing
    /// anywhere in this codebase. Override it for a project whose name does not sit well in that
    /// sentence.
    /// </summary>
    public string LearningLabel { get; init; } = "";

    /// <summary>The learning brief heading actually shown, falling back to "Learn {DisplayName}".</summary>
    public string ResolvedLearningLabel =>
        string.IsNullOrWhiteSpace(LearningLabel) ? $"Learn {DisplayName}" : LearningLabel.Trim();

    /// <summary>
    /// URL template the Classroom uses to open a file in the reader's editor, with <c>{path}</c>
    /// (absolute, forward slashes) and <c>{line}</c> substituted. Empty = VS Code's scheme.
    /// <para>
    /// Configurable rather than hard-coded because the scheme is a property of the person reading,
    /// not of the project: JetBrains, Sublime and Zed all use their own, and a citation that opens
    /// nothing is worse than one that was honest about being copy-paste. Set it in project.json,
    /// e.g. <c>"ideUrl": "jetbrains://rider/navigate/reference?path={path}:{line}"</c>.
    /// </para>
    /// </summary>
    public string IdeUrl { get; init; } = "";

    /// <summary>The editor template actually used, defaulting to VS Code.</summary>
    public string ResolvedIdeUrl =>
        string.IsNullOrWhiteSpace(IdeUrl) ? "vscode://file/{path}:{line}" : IdeUrl.Trim();

    /// <summary>
    /// Which pipelines this project runs, and so which lanes its board draws: every project has
    /// <see cref="TicketTrack.Delivery"/>, and one where we also peer-review other people's branches
    /// adds <see cref="TicketTrack.Review"/>.
    /// <para>
    /// Presentation only, and deliberately so. Nothing in the pipeline consults this — not the
    /// claim, not the phase machine, not a skill — because a ticket's track is fixed when it is
    /// created and a setting that could contradict it would only ever be a second, quieter answer to
    /// a question already settled. What this decides is which half of the machine a person is
    /// SHOWN, which is why the one rule guarding it lives at the edit rather than at the read: see
    /// the refusal in <c>ConfigEndpoints</c>, which will not let a track be switched off while this
    /// project still has unfinished tickets riding on it.
    /// </para>
    /// </summary>
    public TicketTrack[] Tracks { get; init; } = { TicketTrack.Delivery };

    /// <summary>
    /// What a new ticket's <see cref="Ticket.SkipClassroomAnalysis"/> starts as. Off by default -
    /// every ticket gets the independent analysis before refinement - and a project whose work is
    /// mostly small enough not to be worth it turns this on. Only a default: each ticket still
    /// carries its own setting and can be switched either way.
    /// </summary>
    public bool SkipClassroomAnalysisByDefault { get; init; }

    /// <summary>
    /// Deliver a finished ticket by merging its branch into the base branch in the hub, rather than
    /// by raising a pull request. For a project whose team does not use PRs at all - the branch is
    /// merged to main by hand and that is the review. The PR dialog keeps its preflight and diff and
    /// swaps its final button for the merge; see <c>TicketGitService.MergeAsync</c>.
    /// <para>
    /// Git projects only. A <see cref="IsTfvc"/> project already has its own last step, the check-in.
    /// </para>
    /// </summary>
    public bool MergeInsteadOfPr { get; init; }

    /// <summary>
    /// How this project's steward works: its limits, its plan, what its merge checks and refuses.
    /// See <see cref="StewardConfig"/>.
    /// </summary>
    public StewardConfig Steward { get; init; } = new();

    /// <summary>
    /// The mode as written in the project's config (<c>stewardMode</c>). What the steward may
    /// actually do is <see cref="StewardMode"/>, which this is only the upper bound of.
    /// </summary>
    public StewardMode StewardModeSetting { get; init; } = StewardMode.Off;

    /// <summary>
    /// What this project's steward may do, after the limits the project itself imposes.
    /// <para>
    /// <see cref="Models.StewardMode.Full"/> means merging, and a merge is only local where the
    /// project delivers by merging into its base (<see cref="MergeInsteadOfPr"/>). Where delivery
    /// is a pull request the last step is a push, which leaves this machine; where it is a TFVC
    /// check-in the one writer is the human. On both the steward stops at
    /// <see cref="Models.StewardMode.Manage"/> however it is set.
    /// </para>
    /// </summary>
    public StewardMode StewardMode =>
        StewardModeSetting == Models.StewardMode.Full && (!MergeInsteadOfPr || IsTfvc)
            ? Models.StewardMode.Manage
            : StewardModeSetting;

    /// <summary>
    /// Which external issue tracker this project's tickets link to, and the credentials for it.
    /// One per project and no default: one project can be on Jira while the project beside it is on
    /// Azure Boards, and a project with neither works fine - its tickets simply own their titles.
    /// </summary>
    public TrackerConfig Tracker { get; init; } = new();

    /// <summary>
    /// How this project's local stack is started, stopped and recognised as running. Empty when the
    /// project has no such thing - which is the default, and renders nothing.
    /// </summary>
    public StackConfig Stack { get; init; } = new();

    /// <summary>
    /// The lease the stack control takes, fully qualified - by default the SAME
    /// <c>{project}:app-stack</c> the qa-test phase takes via <see cref="PhaseResources"/>.
    /// <para>
    /// That default is the whole point rather than a convenience. The app tier binds fixed ports, so
    /// a person pressing Start and an agent starting its QA run are the identical collision the lease
    /// already exists to prevent; a control that took some private key of its own would be a hole in
    /// exactly the guard it sits next to.
    /// </para>
    /// </summary>
    public string StackResourceKey
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(Stack.Resource) ? "app-stack" : Stack.Resource.Trim();
            return name.Contains(':') ? name : $"{Name}:{name}";
        }
    }

    /// <summary>
    /// Where this project's agent worktrees actually live: the configured root, else a sibling of
    /// the repo (<c>C:\Projects\Acme</c> → <c>C:\Projects\.worktrees\acme</c>).
    ///
    /// Deliberately OUTSIDE the repo. A worktree nested inside <see cref="ProjectPath"/> would show
    /// up as untracked files in the user's own checkout and get crawled by their editor's indexer —
    /// the isolation is the whole point, so it has to be isolated on the filesystem too.
    ///
    /// Empty when there is no project path to derive from (an unconfigured project).
    /// </summary>
    public string ResolvedWorktreeRoot
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(WorktreeRoot)) return Path.GetFullPath(WorktreeRoot.Trim());
            if (string.IsNullOrWhiteSpace(ProjectPath)) return "";
            var repo = Path.GetFullPath(ProjectPath.Trim().TrimEnd('\\', '/'));
            var parent = Path.GetDirectoryName(repo);
            return string.IsNullOrEmpty(parent) ? "" : Path.Combine(parent, ".worktrees", Name);
        }
    }
}

/// <summary>The values <see cref="ProjectConfig.RepoType"/> can hold.</summary>
public static class RepoTypes
{
    public const string Git = "git";
    public const string Tfvc = "tfvc";

    /// <summary>
    /// Anything that is not <c>tfvc</c> reads as git: absent, blank, or a typo. Git is what every
    /// project did before this setting existed, so a mistake here costs a project nothing it had.
    /// </summary>
    public static string Normalize(string? value) =>
        string.Equals(value?.Trim(), Tfvc, StringComparison.OrdinalIgnoreCase) ? Tfvc : Git;
}

/// <summary>One thing the stack control can open in a browser once the stack is up.</summary>
public record StackLink(string Label, string Url);

/// <summary>
/// How to run one project's local stack from the dashboard: the commands, and the URL that proves
/// it is actually serving.
///
/// Deliberately says nothing about docker. A project is startable if a command brings it up and a
/// URL answers afterwards; that is the whole contract, and it fits a compose file, a dev server or
/// a script equally. The one real constraint is that <see cref="Up"/> must RETURN, leaving
/// something running behind it (<c>docker compose up -d</c> does; a foreground <c>npm run dev</c>
/// does not) - supervising a foreground process is a different job with different failure modes,
/// and this config would only pretend to cover it.
/// </summary>
public class StackConfig
{
    /// <summary>Where the commands run, relative to <c>projectPath</c> (absolute also accepted).</summary>
    public string WorkingDir { get; init; } = "";

    /// <summary>Command that brings the stack up and returns. Empty = this project has no stack.</summary>
    public string Up { get; init; } = "";

    /// <summary>
    /// Command that takes it down. Empty = the control is start-and-open only.
    /// <para>
    /// Take down the app tier, not the machine's shared infrastructure. A project-wide
    /// <c>docker compose down</c> also stops the database, which every other phase queries without
    /// holding any lease - so Stop would interrupt three running agents to release a resource only
    /// one of them wanted. Name the services instead, exactly as the qa-test skill does.
    /// </para>
    /// </summary>
    public string Down { get; init; } = "";

    /// <summary>
    /// The URL polled to decide whether the stack is running. Any HTTP response counts - a 404 or a
    /// 401 from the proxy still proves it is listening and serving, and requiring a 200 would report
    /// a perfectly healthy stack as down whenever its root path is not a page.
    /// </summary>
    public string HealthUrl { get; init; } = "";

    /// <summary>How long a cold start is allowed to take before it is called a failure.</summary>
    public int StartupSeconds { get; init; } = 240;

    /// <summary>Lease name; bare names scope to this project. See <see cref="ProjectConfig.StackResourceKey"/>.</summary>
    public string Resource { get; init; } = "app-stack";

    /// <summary>What the Open button offers. Several because one stack can front several sites.</summary>
    public StackLink[] Open { get; init; } = Array.Empty<StackLink>();

    /// <summary>A stack can only be driven if there is something to run and something to poll.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Up) && !string.IsNullOrWhiteSpace(HealthUrl);

    /// <summary>Absolute working directory for the commands, or empty when there is no repo path.</summary>
    public string ResolvedWorkingDir(string projectPath)
    {
        var root = projectPath?.Trim().TrimEnd('\\', '/') ?? "";
        var rel = WorkingDir.Trim();
        if (Path.IsPathRooted(rel)) return Path.GetFullPath(rel);
        if (string.IsNullOrWhiteSpace(root)) return "";
        return string.IsNullOrWhiteSpace(rel) ? Path.GetFullPath(root) : Path.GetFullPath(Path.Combine(root, rel));
    }
}

/// <summary>
/// Loads the project registry from <c>data/</c>.
///
/// Two layers of config:
/// <list type="bullet">
/// <item><b>Global</b> — <c>config.defaults.json</c> (committed) merged with
/// <c>config.json</c> (local, gitignored): dashboard URL, notification settings, heartbeat
/// timing, off-hours. One set for the whole system.</item>
/// <item><b>Per-project</b> — <c>projects/&lt;key&gt;/project.defaults.json</c> (committed)
/// merged with <c>project.json</c> (local, gitignored): repo path, display name,
/// branch settings, tracker credentials, QA ports/login, stack.</item>
/// </list>
///
/// Every enabled project is a peer of every other: there is no current, default or
/// active one. A request that is about a project names it, and one that does not is
/// refused rather than pointed at whichever project happens to sort first.
///
/// Project lists are read live from disk so add/remove/enable takes effect without
/// a restart (background services that captured a value at startup still need one).
/// </summary>
public class ProjectRegistry
{
    private readonly DashboardPaths _paths;
    private readonly QaIsolation _isolation;

    public ProjectRegistry(DashboardPaths paths, QaIsolation? isolation = null)
    {
        _paths = paths;
        _isolation = isolation ?? QaIsolation.Off;
    }

    /// <summary>
    /// Enabled project names, read fresh from disk. In a QA copy, only the projects wholly inside its
    /// own data root: one that names a real repository is not the copy's to touch.
    /// </summary>
    public IReadOnlyList<string> Projects => _paths.DiscoverProjects()
        .Where(p => !_isolation.On || QaIsolation.Escapes(ReadProjectConfig(p), _paths.DataRoot) is null)
        .ToList();

    /// <summary>All project names including unregistered ones (management view).</summary>
    public IReadOnlyList<string> AllProjects => _paths.DiscoverAllProjects().ToList();

    /// <summary>Is this an enabled project? The one gate every project-scoped request goes through.</summary>
    public bool IsEnabled(string? project) =>
        !string.IsNullOrWhiteSpace(project) && Projects.Contains(project);

    /// <summary>Merge global committed defaults with the local override file (shallow, top-level).</summary>
    public Dictionary<string, JsonElement> GetGlobalConfig() =>
        MergeFiles(_paths.GlobalConfigDefaultsPath, _paths.GlobalConfigPath);

    /// <summary>Merge a project's committed defaults with its local override file (shallow, top-level).</summary>
    public Dictionary<string, JsonElement> GetMergedConfig(string project) =>
        MergeFiles(_paths.ProjectConfigDefaults(project), _paths.ProjectConfig(project));

    private static Dictionary<string, JsonElement> MergeFiles(params string[] paths)
    {
        var merged = new Dictionary<string, JsonElement>();
        foreach (var path in paths)
        {
            if (!File.Exists(path)) continue;
            try
            {
                foreach (var prop in JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(path)).EnumerateObject())
                    merged[prop.Name] = prop.Value;
            }
            catch { /* skip malformed config file */ }
        }
        return merged;
    }

    /// <summary>
    /// Config for one named project. There is deliberately no default: projects are peers, so a
    /// caller that does not know which one it means cannot be answered — only guessed at, which is
    /// how a request lands on the wrong project's repo without anything saying so.
    /// </summary>
    public ProjectConfig GetProjectConfig(string project)
    {
        var cfg = ReadProjectConfig(project);
        // The backstop behind the filter on Projects, for a caller that names a project directly:
        // a QA copy sees no path of a project that reaches outside it.
        if (_isolation.On && QaIsolation.Escapes(cfg, _paths.DataRoot) is not null)
            return new ProjectConfig { Name = cfg.Name, DisplayName = cfg.DisplayName };
        return cfg;
    }

    private ProjectConfig ReadProjectConfig(string project)
    {
        var name = project;
        var cfg = GetMergedConfig(name);

        return new ProjectConfig
        {
            Name = name,
            DisplayName = cfg.TryGetValue("displayName", out var d) ? d.GetString() ?? name : name,
            ProjectPath = cfg.TryGetValue("projectPath", out var p) ? p.GetString() ?? "" : "",
            BaseBranch = cfg.TryGetValue("baseBranch", out var bb) ? bb.GetString() ?? "" : "",
            RepoType = RepoTypes.Normalize(
                cfg.TryGetValue("repoType", out var rt) && rt.ValueKind == JsonValueKind.String ? rt.GetString() : null),
            TfvcFolders = GetStrArray(cfg, "tfvcFolders"),
            BranchPrefix = cfg.TryGetValue("branchPrefix", out var bp) ? bp.GetString() ?? "" : "",
            WorktreeRoot = cfg.TryGetValue("worktreeRoot", out var wr) ? wr.GetString() ?? "" : "",
            WorktreeSetup = GetStrArray(cfg, "worktreeSetup"),
            WorktreeCopyFiles = GetStrArray(cfg, "worktreeCopyFiles"),
            DiffNoisePaths = GetStrArray(cfg, "diffNoisePaths"),
            MemoryDir = cfg.TryGetValue("memoryDir", out var md) ? md.GetString() ?? "" : "",
            LearningLabel = cfg.TryGetValue("learningLabel", out var ll) ? ll.GetString() ?? "" : "",
            IdeUrl = cfg.TryGetValue("ideUrl", out var iu) ? iu.GetString() ?? "" : "",
            Tracks = GetTracks(cfg),
            SkipClassroomAnalysisByDefault = cfg.TryGetValue("skipClassroomAnalysisByDefault", out var sca)
                && sca.ValueKind == JsonValueKind.True,
            MergeInsteadOfPr = cfg.TryGetValue("mergeInsteadOfPr", out var mip) && mip.ValueKind == JsonValueKind.True,
            PhaseResources = GetStrArrayMap(cfg, "phaseResources"),
            Tracker = TrackerConfig.From(cfg),
            Stack = GetStack(cfg),
            Steward = StewardConfig.From(cfg),
            StewardModeSetting = StewardConfig.ParseMode(
                cfg.TryGetValue("stewardMode", out var sm) && sm.ValueKind == JsonValueKind.String ? sm.GetString() : null),
        };
    }

    /// <summary>Read the optional <c>stack</c> block; anything missing or malformed leaves it unconfigured.</summary>
    private static StackConfig GetStack(Dictionary<string, JsonElement> cfg)
    {
        if (!cfg.TryGetValue("stack", out var s) || s.ValueKind != JsonValueKind.Object)
            return new StackConfig();

        var open = new List<StackLink>();
        if (s.TryGetProperty("open", out var links) && links.ValueKind == JsonValueKind.Array)
        {
            foreach (var link in links.EnumerateArray())
            {
                if (link.ValueKind != JsonValueKind.Object) continue;
                var url = GetStr(link, "url");
                if (string.IsNullOrWhiteSpace(url)) continue;
                var label = GetStr(link, "label");
                open.Add(new StackLink(string.IsNullOrWhiteSpace(label) ? "Open" : label, url));
            }
        }

        return new StackConfig
        {
            WorkingDir = GetStr(s, "workingDir"),
            Up = GetStr(s, "up"),
            Down = GetStr(s, "down"),
            HealthUrl = GetStr(s, "healthUrl"),
            StartupSeconds = s.TryGetProperty("startupSeconds", out var ss) && ss.ValueKind == JsonValueKind.Number
                ? Math.Clamp(ss.GetInt32(), 5, 3600) : 240,
            Resource = GetStr(s, "resource"),
            Open = open.ToArray(),
        };
    }

    private static string GetStr(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>
    /// The <c>tracks</c> list, normalised by <see cref="EnabledTracks.Parse"/> — the same parser the
    /// settings save uses, so a file and a form can never disagree about what a name means.
    /// <para>
    /// Absent, malformed and empty all read as delivery-only. That is the default every project
    /// starts on, and it is also the only safe answer to a broken list: a project with NO track
    /// enabled can hold no ticket and draws a board with no lanes, so falling back to none would
    /// turn a typo in a config file into a project that looks deleted.
    /// </para>
    /// </summary>
    private static TicketTrack[] GetTracks(Dictionary<string, JsonElement> cfg)
    {
        var tracks = EnabledTracks.Parse(GetStrArray(cfg, "tracks")) ?? Array.Empty<TicketTrack>();
        return tracks.Length > 0 ? tracks : EnabledTracks.Default;
    }

    /// <summary>Read a <c>{ "key": ["a","b"] }</c> object out of config, skipping anything malformed.</summary>
    private static Dictionary<string, string[]> GetStrArrayMap(Dictionary<string, JsonElement> cfg, string key)
    {
        var map = new Dictionary<string, string[]>();
        if (!cfg.TryGetValue(key, out var v) || v.ValueKind != JsonValueKind.Object) return map;
        foreach (var prop in v.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Array) continue;
            map[prop.Name] = prop.Value.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToArray();
        }
        return map;
    }

    private static string[] GetStrArray(Dictionary<string, JsonElement> cfg, string key) =>
        cfg.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToArray()
            : Array.Empty<string>();
}
